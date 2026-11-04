using System.Text;
using Fy1Switch.WolGateway;

namespace Fy1Switch.WolGateway.Tests;

[TestClass]
public sealed class AuthorizationTests
{
    [TestMethod]
    public void RegistrationIsOneTimeAndTokenIsNotStored()
    {
        using var data = new TestStore();
        var code = data.Store.GenerateCode([data.DeviceId]);
        var reply = data.Store.Register(new(1, code.Code, "Phone"));
        Assert.AreEqual(reply.ClientId, data.Store.Authenticate(reply.AccessToken));
        Assert.AreEqual(43, reply.AccessToken.Length);
        Assert.AreEqual(data.DeviceId, reply.Devices.Single().DeviceId);
        Assert.ThrowsException<GatewayException>(() => data.Store.Register(new(1, code.Code, "Other")));
        foreach (var path in Directory.GetFiles(data.DirectoryPath, "wol.db*"))
        {
            var content = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.IsFalse(content.Contains(reply.AccessToken, StringComparison.Ordinal));
            Assert.IsFalse(content.Contains(code.Code, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void CodeExpiresAndFiveWrongSecretsDisableIt()
    {
        using var data = new TestStore();
        var expired = data.Store.GenerateCode([data.DeviceId]);
        data.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.ThrowsException<GatewayException>(() => data.Store.Register(new(1, expired.Code, "Phone")));
        var code = data.Store.GenerateCode([data.DeviceId]);
        var wrong = code.Code[..33] + new string('A', 32);
        for (var i = 0; i < 5; i++) Assert.ThrowsException<GatewayException>(() => data.Store.Register(new(1, wrong, "Phone")));
        Assert.ThrowsException<GatewayException>(() => data.Store.Register(new(1, code.Code, "Phone")));
    }

    [TestMethod]
    public void RegistrationGlobalBudgetSurvivesRegenerationAndRestart()
    {
        using var data = new TestStore();
        for (var i = 0; i < 10; i++) Assert.ThrowsException<GatewayException>(() => data.Store.Register(new(1, "invalid", "Phone")));
        var code = data.Store.GenerateCode([data.DeviceId]);
        var restarted = new GatewayStore(data.Settings, data.Clock);
        var error = Assert.ThrowsException<GatewayException>(() => restarted.Register(new(1, code.Code, "Phone")));
        Assert.AreEqual(429, error.StatusCode);
        data.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsNotNull(restarted.Register(new(1, restarted.GenerateCode([data.DeviceId]).Code, "Phone")));
    }

    [TestMethod]
    public void DeviceWhitelistAndIndependentPhoneRevocation()
    {
        using var data = new TestStore();
        var first = data.Register(data.DeviceId);
        var second = data.Register(data.DeviceId, data.OtherDeviceId);
        Assert.AreNotEqual(first.AccessToken, second.AccessToken);
        Assert.AreEqual(403, Assert.ThrowsException<GatewayException>(() => data.Store.Authorize(first.ClientId, data.OtherDeviceId)).StatusCode);
        var admin = new GatewayStore(data.Settings, data.Clock);
        admin.Revoke(first.ClientId);
        Assert.AreEqual(401, Assert.ThrowsException<GatewayException>(() => data.Store.Authenticate(first.AccessToken)).StatusCode);
        Assert.AreEqual(401, Assert.ThrowsException<GatewayException>(() => data.Store.Authorize(first.ClientId, data.DeviceId)).StatusCode);
        Assert.AreEqual(second.ClientId, data.Store.Authenticate(second.AccessToken));
        Assert.AreEqual(2, data.Store.Devices(second.ClientId).Devices.Count);
    }

    [TestMethod]
    public void ClientRateBudgetPersistsAndRollsOver()
    {
        using var data = new TestStore(clientLimit: 5);
        var client = data.Register().ClientId;
        for (var i = 0; i < 5; i++) data.Store.AdmitClientRequest(client);
        var restarted = new GatewayStore(data.Settings, data.Clock);
        Assert.AreEqual(429, Assert.ThrowsException<GatewayException>(() => restarted.AdmitClientRequest(client)).StatusCode);
        data.Clock.Advance(TimeSpan.FromMinutes(1));
        restarted.AdmitClientRequest(client);
    }
}
