using Fy1Switch.WolGateway;

namespace Fy1Switch.WolGateway.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [DataTestMethod]
    [DataRow("02:00:00:00:00:01", true)]
    [DataRow("AA:BB:CC:DD:EE:FF", true)]
    [DataRow("00:00:00:00:00:00", false)]
    [DataRow("FF:FF:FF:FF:FF:FF", false)]
    [DataRow("01:00:5E:00:00:01", false)]
    [DataRow("02-00-00-00-00-01", false)]
    [DataRow("02:00:00:00:00:GG", false)]
    [DataRow("02:00:00:00:00:01;reboot", false)]
    public void MacValidation(string mac, bool valid) => Assert.AreEqual(valid, WolValidation.ValidMac(mac));

    [DataTestMethod]
    [DataRow("http://wol.example.test")]
    [DataRow("https://user:pass@wol.example.test")]
    [DataRow("https://wol.example.test/path")]
    [DataRow("https://wol.example.test?secret=1")]
    public void RejectUnsafeOrigins(string origin)
    {
        using var data = new TestStore();
        Assert.ThrowsException<InvalidOperationException>(() => (data.Settings with { PublicUrl = origin }).Validate());
    }

    [TestMethod]
    public void RejectDuplicateDevicesAndUnsafeSshSettings()
    {
        using var data = new TestStore();
        Assert.ThrowsException<InvalidOperationException>(() => (data.Settings with { Devices = [data.Settings.Devices[0], data.Settings.Devices[0]] }).Validate());
        foreach (var ssh in new[] { data.Settings.Ssh with { Host = "example.com" }, data.Settings.Ssh with { Host = "8.8.8.8" },
            data.Settings.Ssh with { User = "root;reboot" }, data.Settings.Ssh with { WireGuardInterface = "wg0;id" },
            data.Settings.Ssh with { IdentityFile = "relative.key" }, data.Settings.Ssh with { ExecutionTimeoutSeconds = 5 },
            data.Settings.Ssh with { KnownHostsFile = Path.Combine(data.DirectoryPath, "known hosts") }, data.Settings.Ssh with { BindAddress = "fd00::1" } })
            Assert.ThrowsException<InvalidOperationException>(() => (data.Settings with { Ssh = ssh }).Validate());
    }

    [TestMethod]
    public void ValidateFreshEnvelopeAndRejectWrongDeviceAndTime()
    {
        using var data = new TestStore();
        var r = data.Wake();
        WolValidation.Envelope(r.Version, r.DeviceId, r.DeviceId, r.RequestId, r.IssuedAt, r.ExpiresAt, data.Clock.GetUtcNow());
        foreach (var changed in new[] { r with { Version = 2 }, r with { DeviceId = data.OtherDeviceId }, r with { RequestId = Guid.Empty },
            r with { IssuedAt = r.IssuedAt.AddMinutes(1), ExpiresAt = r.ExpiresAt.AddMinutes(1) }, r with { ExpiresAt = r.IssuedAt.AddMinutes(3) },
            r with { IssuedAt = r.IssuedAt.AddMinutes(-2), ExpiresAt = r.ExpiresAt.AddMinutes(-2) } })
            Assert.ThrowsException<GatewayException>(() => WolValidation.Envelope(changed.Version, data.DeviceId, changed.DeviceId, changed.RequestId, changed.IssuedAt, changed.ExpiresAt, data.Clock.GetUtcNow()));
    }

    [TestMethod]
    public void ServiceLeaseRejectsSecondRuntimeWithoutBlockingAdmin()
    {
        using var data = new TestStore();
        using (var lease = new GatewayLease(data.Settings))
        {
            Assert.ThrowsException<InvalidOperationException>(() => new GatewayLease(data.Settings));
            Assert.IsNotNull(new GatewayStore(data.Settings, data.Clock).GenerateCode([data.DeviceId]));
        }
        using var restarted = new GatewayLease(data.Settings);
    }
}
