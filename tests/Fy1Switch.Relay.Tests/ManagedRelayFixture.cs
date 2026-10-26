using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fy1Switch.Contracts;
using Fy1Switch.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Relay.Tests;

internal sealed class ManagedRelayFixture : IAsyncDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "managed-" + Guid.NewGuid().ToString("N"));
    private readonly List<ClientWebSocket> sockets = [];
    private WebApplication? app;
    public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };
    public RelayRegistry Registry => app!.Services.GetRequiredService<RelayRegistry>();
    public RelayRouter Router => app!.Services.GetRequiredService<RelayRouter>();

    public static async Task<ManagedRelayFixture> StartAsync()
    {
        var fixture = new ManagedRelayFixture();
        try
        {
            Directory.CreateDirectory(fixture.directory);
            var passwordPath = Path.Combine(fixture.directory, "bootstrap.txt");
            await File.WriteAllTextAsync(passwordPath, "Fixture-Only-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "!a7");
            var configPath = Path.Combine(fixture.directory, "config.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                management = new { publicUrl = "https://relay.example.test", dataDirectory = Path.Combine(fixture.directory, "data"), adminPasswordFile = passwordPath },
                requestTimeoutSeconds = 2, requestsPerMinute = 120
            }));
            fixture.app = RelayApplication.Build(["--FY1SWITCH_RELAY_CONFIG", configPath, "--urls", "http://127.0.0.1:0", "--environment", "Production"]);
            await fixture.app.StartAsync();
            fixture.Client.BaseAddress = new Uri(fixture.app.Urls.Single());
            fixture.Client.DefaultRequestHeaders.Host = "relay.example.test";
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public async Task<RelayRegistrationReply> RegisterAsync(Guid? id = null)
    {
        var code = Registry.GenerateRegistrationCode();
        using var result = await PostAsync("/v1/relay/register", new RelayRegistrationRequest(id ?? Guid.NewGuid(), code.Code, "fixture"));
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        return JsonSerializer.Deserialize<RelayRegistrationReply>(await result.Content.ReadAsStringAsync(), RelayJson.Options)!;
    }

    public async Task<ClientWebSocket> ConnectAsync(RelayRegistrationReply registration)
    {
        var socket = new ClientWebSocket();
        sockets.Add(socket);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + registration.WindowsToken);
        socket.Options.SetRequestHeader("Host", "relay.example.test");
        await socket.ConnectAsync(new UriBuilder(Client.BaseAddress!) { Scheme = "ws", Path = $"/v1/devices/{registration.DeviceId}/connect" }.Uri, CancellationToken.None);
        return socket;
    }

    public Task<HttpResponseMessage> PostAsync(string path, object body, string? bearer = null, string? deviceToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent(JsonSerializer.Serialize(body, RelayJson.Options), Encoding.UTF8, "application/json") };
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        if (deviceToken is not null) request.Headers.Add("X-Device-Token", deviceToken);
        return SendAsync(request);
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    { using (request) return await Client.SendAsync(request); }

    public async ValueTask DisposeAsync()
    {
        foreach (var socket in sockets) { socket.Abort(); socket.Dispose(); }
        Client.Dispose();
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
