using Fy1Switch.WolGateway;

namespace Fy1Switch.WolGateway.Tests;

[TestClass]
public sealed class SshExecutorTests
{
    [TestMethod]
    public void SshUsesPinnedHostAndLiteralArgumentsWithoutShellOrFallback()
    {
        using var data = new TestStore();
        var info = OpenSshExecutor.BuildStartInfo(data.Settings.Ssh, data.Settings.Devices[0], true);
        Assert.IsFalse(info.UseShellExecute);
        var arguments = info.ArgumentList.ToArray();
        foreach (var option in new[] { "BatchMode=yes", "StrictHostKeyChecking=yes", "IdentitiesOnly=yes", "IdentityAgent=none", "UpdateHostKeys=no",
            "PasswordAuthentication=no", "ClearAllForwardings=yes", "ConnectionAttempts=1", "ProxyCommand=none", "ProxyJump=none", "-b", "-B", "wg0" })
            CollectionAssert.Contains(arguments, option);
        Assert.AreEqual("wake " + data.DeviceId + " " + OpenSshExecutor.DeviceDigest(data.Settings.Devices[0]), arguments.Last());
        Assert.IsFalse(arguments.Last().Contains(data.Settings.Devices[0].MacAddress));
        Assert.IsFalse(info.Environment.ContainsKey("SSH_AUTH_SOCK"));
    }

    [DataTestMethod]
    [DataRow(255, "Permission denied (publickey).", "failed", "ssh_auth_failed")]
    [DataRow(255, "Host key verification failed.", "failed", "ssh_host_key_failed")]
    [DataRow(255, "ssh: connect to host 10.1.2.3 port 22: Connection timed out", "failed", "ssh_unreachable")]
    [DataRow(255, "Connection refused", "failed", "ssh_unreachable")]
    [DataRow(255, "Connection closed by remote host", "unknown", "ssh_failed")]
    [DataRow(66, "", "failed", "remote_configuration_invalid")]
    [DataRow(70, "", "unknown", "etherwake_failed")]
    public void FailureClassificationDoesNotClaimWireGuardDownOrSafeRetry(int exitCode, string stderr, string status, string error)
    {
        using var data = new TestStore();
        var result = OpenSshExecutor.Classify(exitCode, "", stderr, data.Settings.Devices[0], true);
        Assert.AreEqual(status, result.Status);
        Assert.AreEqual(error, result.ErrorCode);
    }

    [TestMethod]
    public void ExitZeroRequiresExactAcknowledgementAndMappingDigest()
    {
        using var data = new TestStore();
        var device = data.Settings.Devices[0];
        var good = $"FY1SWITCH_WOL_V1 packet_sent {device.DeviceId} {OpenSshExecutor.DeviceDigest(device)}\n";
        Assert.AreEqual("packet_sent", OpenSshExecutor.Classify(0, good, "", device, true).Status);
        foreach (var bad in new[] { "", "OK", good.Replace(device.DeviceId.ToString(), data.OtherDeviceId.ToString()), good + "unexpected\n" })
            Assert.AreEqual("unknown", OpenSshExecutor.Classify(0, bad, "", device, true).Status);
        var changed = device with { Interface = "br-other" };
        Assert.AreNotEqual(OpenSshExecutor.DeviceDigest(device), OpenSshExecutor.DeviceDigest(changed));
    }
}
