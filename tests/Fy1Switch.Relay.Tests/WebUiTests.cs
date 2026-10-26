using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Relay;

namespace Fy1Switch.Relay.Tests;

[TestClass]
public sealed class WebUiTests
{
    [TestMethod]
    public async Task LoginPageBehindLoopbackHttpProxyStillEmitsSecureCookies()
    {
        await using var fixture = await WebUiFixture.StartAsync(https: false);
        using var page = await fixture.Client.GetAsync("/login");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        var cookies = page.Headers.GetValues("Set-Cookie").ToArray();
        Assert.IsTrue(cookies.Any(value => value.StartsWith("__Host-Fy1SwitchRelay.Csrf=", StringComparison.Ordinal)));
        Assert.IsTrue(cookies.All(value => value.Contains("secure", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task PasswordLoginRequiresCsrfAndIssuesSecureCookie()
    {
        await using var fixture = await WebUiFixture.StartAsync();
        using var anonymous = await fixture.Client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.IsTrue(anonymous.Headers.Location!.OriginalString.StartsWith("https://localhost/login", StringComparison.Ordinal));
        using var noCsrf = await fixture.Client.PostAsJsonAsync("/api/account/login", new { password = fixture.Password });
        Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        var csrf = await fixture.CsrfAsync("/login");
        using var wrong = await fixture.PostAsync("/api/account/login", new { password = "not-the-password" }, csrf);
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var login = await fixture.PostAsync("/api/account/login", new { password = fixture.Password }, csrf);
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-Fy1SwitchRelay=", StringComparison.Ordinal));
        StringAssert.Contains(cookie.ToLowerInvariant(), "secure");
        StringAssert.Contains(cookie.ToLowerInvariant(), "httponly");
        StringAssert.Contains(cookie.ToLowerInvariant(), "samesite=strict");
        using var home = await fixture.Client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, home.StatusCode);
        StringAssert.Contains(await home.Content.ReadAsStringAsync(), "Windows 接入码");
        StringAssert.Contains(home.Headers.GetValues("Content-Security-Policy").Single(), "frame-ancestors 'none'");
        using var devices = await fixture.Client.GetAsync("/api/admin/devices");
        Assert.AreEqual(HttpStatusCode.OK, devices.StatusCode);
        using var codeWithoutCsrf = await fixture.Client.PostAsJsonAsync("/api/admin/registration-code", new { });
        Assert.AreEqual(HttpStatusCode.BadRequest, codeWithoutCsrf.StatusCode);
        using var code = await fixture.PostAsync("/api/admin/registration-code", new { }, await fixture.CsrfAsync("/"));
        Assert.AreEqual(HttpStatusCode.OK, code.StatusCode);
        var json = await code.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(Regex.IsMatch(json.GetProperty("code").GetString()!, "^[0-9]{6}$"));
    }

    [TestMethod]
    public async Task BrowserHostOriginAndAnonymousAccessAreRejected()
    {
        await using var fixture = await WebUiFixture.StartAsync();
        using var devices = await fixture.Client.GetAsync("/api/admin/devices");
        Assert.AreEqual(HttpStatusCode.Unauthorized, devices.StatusCode);
        using var spoofedHost = new HttpRequestMessage(HttpMethod.Get, "/login");
        spoofedHost.Headers.Host = "attacker.example";
        using var hostResult = await fixture.Client.SendAsync(spoofedHost);
        Assert.AreEqual(HttpStatusCode.BadRequest, hostResult.StatusCode);
        using var spoofedOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/account/login") { Content = JsonContent.Create(new { password = fixture.Password }) };
        spoofedOrigin.Headers.Add("Origin", "https://attacker.example");
        using var originResult = await fixture.Client.SendAsync(spoofedOrigin);
        Assert.AreEqual(HttpStatusCode.BadRequest, originResult.StatusCode);
        using var passkeyOptions = await fixture.Client.PostAsJsonAsync("/api/account/passkeys/options", new { });
        Assert.AreEqual(HttpStatusCode.Unauthorized, passkeyOptions.StatusCode);
    }

    [TestMethod]
    public async Task PasskeyRegistrationAndLoginRejectMissingOrForgedCeremonies()
    {
        await using var fixture = await WebUiFixture.StartAsync();
        var csrf = await fixture.LoginAsync();
        using var noState = await fixture.PostAsync("/api/account/passkeys", new { credentialJson = "{}", name = "test key" }, csrf);
        Assert.AreEqual(HttpStatusCode.BadRequest, noState.StatusCode);
        using var options = await fixture.PostAsync("/api/account/passkeys/options", new { }, csrf);
        Assert.AreEqual(HttpStatusCode.OK, options.StatusCode);
        var json = await options.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("localhost", json.GetProperty("rp").GetProperty("id").GetString());
        Assert.AreEqual("required", json.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        using var forged = await fixture.PostAsync("/api/account/passkeys", new { credentialJson = "{}", name = "test key" }, csrf);
        Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);
        using var replay = await fixture.PostAsync("/api/account/passkeys", new { credentialJson = "{}", name = "test key" }, csrf);
        Assert.AreEqual(HttpStatusCode.BadRequest, replay.StatusCode);
        using var keys = await fixture.Client.GetAsync("/api/account/passkeys");
        Assert.AreEqual(0, (await keys.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        using var logout = await fixture.PostAsync("/api/account/logout", new { }, csrf);
        Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode);
        csrf = await fixture.CsrfAsync("/login");
        using var missingLoginState = await fixture.PostAsync("/api/account/passkey-login", new { credentialJson = "{}" }, csrf);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingLoginState.StatusCode);
    }

    [TestMethod]
    public async Task PasswordChangePersistsAndOldPasswordStopsWorking()
    {
        await using var fixture = await WebUiFixture.StartAsync();
        var csrf = await fixture.LoginAsync();
        const string replacement = "Changed-Relay-Password-825!";
        using var change = await fixture.PostAsync("/api/account/password", new { currentPassword = fixture.Password, newPassword = replacement }, csrf);
        Assert.AreEqual(HttpStatusCode.OK, change.StatusCode);
        using var logout = await fixture.PostAsync("/api/account/logout", new { }, csrf);
        Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode);
        csrf = await fixture.CsrfAsync("/login");
        using var oldLogin = await fixture.PostAsync("/api/account/login", new { password = fixture.Password }, csrf);
        Assert.AreEqual(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        using var newLogin = await fixture.PostAsync("/api/account/login", new { password = replacement }, csrf);
        Assert.AreEqual(HttpStatusCode.OK, newLogin.StatusCode);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.DirectoryPath, "data", "identity.db")));
    }

    [TestMethod]
    public void CeremonyStateIsBoundToOperationUserAndConsumedOnce()
    {
        var ceremonies = new RelayPasskeyCeremonies();
        var begin = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        Assert.IsTrue(ceremonies.Begin(begin, "register", "admin-id"));
        var header = begin.Response.Headers.SetCookie.ToString().Split(';')[0];
        var wrongUser = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        wrongUser.Request.Headers.Cookie = header;
        Assert.IsFalse(ceremonies.Consume(wrongUser, "register", "someone-else"));
        var replay = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        replay.Request.Headers.Cookie = header;
        Assert.IsFalse(ceremonies.Consume(replay, "register", "admin-id"));
    }
}

internal sealed class WebUiFixture : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;
    public string DirectoryPath { get; }
    public string Password { get; } = "Initial-Relay-Password-317!";
    public HttpClient Client { get; }

    private WebUiFixture(WebApplication app, X509Certificate2 certificate, string directory, HttpClient client)
    {
        this.app = app;
        this.certificate = certificate;
        DirectoryPath = directory;
        Client = client;
    }

    public static async Task<WebUiFixture> StartAsync(bool https = true)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "webui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var passwordPath = Path.Combine(directory, "admin-password.txt");
        await File.WriteAllTextAsync(passwordPath, "Initial-Relay-Password-317!");
        var configPath = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            management = new { publicUrl = "https://localhost", dataDirectory = Path.Combine(directory, "data"), adminPasswordFile = passwordPath },
            requestsPerMinute = 120,
        }));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        var app = RelayApplication.Build(["--FY1SWITCH_RELAY_CONFIG", configPath, "--urls", $"{(https ? "https" : "http")}://127.0.0.1:0", "--environment", "Production"], builder =>
            builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate)));
        await app.StartAsync();
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        var expectedCertificate = SHA256.HashData(certificate.RawData);
        handler.ServerCertificateCustomValidationCallback = (message, serverCertificate, _, _) =>
            message.RequestUri!.Host == "127.0.0.1" && serverCertificate is not null &&
            CryptographicOperations.FixedTimeEquals(expectedCertificate, SHA256.HashData(serverCertificate.RawData));
        var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Host = "localhost";
        return new(app, certificate, directory, client);
    }

    public async Task<string> CsrfAsync(string page)
    {
        using var response = await Client.GetAsync(page);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "<meta name=\"csrf-token\" content=\"([^\"]+)\"");
        Assert.IsTrue(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public async Task<string> LoginAsync()
    {
        using var login = await PostAsync("/api/account/login", new { password = Password }, await CsrfAsync("/login"));
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        return await CsrfAsync("/");
    }

    public async Task<HttpResponseMessage> PostAsync(string path, object body, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("__RequestVerificationToken", csrf);
        request.Headers.Add("Origin", "https://localhost");
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await app.StopAsync(timeout.Token);
        await app.DisposeAsync();
        certificate.Dispose();
        var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestResults"));
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(DirectoryPath)), testRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture cleanup must remain in its TestResults directory.");
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
