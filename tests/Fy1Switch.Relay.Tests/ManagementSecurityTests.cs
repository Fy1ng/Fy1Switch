using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Relay.Tests;

[TestClass]
public sealed class ManagementSecurityTests
{
    [TestMethod]
    public async Task ReverseProxyLoginRedirectUsesConfiguredHttpsOrigin()
    {
        await using var fixture = await WebUiFixture.StartAsync(https: false);
        using var response = await fixture.Client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.IsTrue(response.Headers.Location!.AbsoluteUri.StartsWith("https://localhost/login?", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("/Index")]
    [DataRow("/login/")]
    [DataRow("/favicon.svg")]
    public async Task AlternateBrowserPathsAlsoValidateHost(string path)
    {
        await using var fixture = await WebUiFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = "attacker.example";
        using var response = await fixture.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task NullAuthenticationFieldsFailWithoutServerErrors()
    {
        await using var fixture = await WebUiFixture.StartAsync();
        using var malformedLogin = await fixture.PostAsync("/api/account/login", new { password = (string?)null }, await fixture.CsrfAsync("/login"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, malformedLogin.StatusCode);
        var csrf = await fixture.LoginAsync();
        using var malformedChange = await fixture.PostAsync("/api/account/password", new { currentPassword = (string?)null, newPassword = (string?)null }, csrf);
        Assert.AreEqual(HttpStatusCode.BadRequest, malformedChange.StatusCode);
    }
}
