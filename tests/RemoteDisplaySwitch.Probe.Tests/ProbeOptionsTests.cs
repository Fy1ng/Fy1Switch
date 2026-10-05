using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RemoteDisplaySwitch.Probe.Tests;

[TestClass]
public class ProbeOptionsTests
{
    private static readonly string TargetKey = new('a', 64);

    [TestMethod]
    [DataRow("inventory")]
    [DataRow("discover")]
    public void ReadOnlyCommandsNeedNoWriteFlag(string command)
    {
        Assert.IsTrue(ProbeOptions.TryParse([command], out var options));
        Assert.IsFalse(options!.WritesPower);
        Assert.IsFalse(ProbeOptions.TryParse([command, "--allow-power-write"], out _));
    }

    [TestMethod]
    [DataRow("on")]
    [DataRow("cycle")]
    public void WritesRequireExplicitFlagAndExactTarget(string command)
    {
        Assert.IsFalse(ProbeOptions.TryParse([command, TargetKey], out _));
        Assert.IsFalse(ProbeOptions.TryParse([command, "G4Q", "--allow-power-write"], out _));
        Assert.IsTrue(ProbeOptions.TryParse([command, TargetKey, "--allow-power-write"], out var options));
        Assert.IsTrue(options!.WritesPower);
    }

    [TestMethod]
    public void UnknownAndDuplicateOptionsAreRejected()
    {
        Assert.IsFalse(ProbeOptions.TryParse(["off", TargetKey, "--allow-power-write"], out _));
        Assert.IsFalse(ProbeOptions.TryParse(["cycle", TargetKey, "--allow-power-write", "--allow-power-write"], out _));
        Assert.IsFalse(ProbeOptions.TryParse(["read", TargetKey, "--worker", "--worker"], out _));
        Assert.IsFalse(ProbeOptions.TryParse(["read", new string('z', 64)], out _));
    }

    [TestMethod]
    public void WorkerRetainsWriteGuardAndTarget()
    {
        Assert.IsFalse(ProbeOptions.TryParse(["cycle", TargetKey, "--worker"], out _));
        Assert.IsTrue(ProbeOptions.TryParse(["cycle", TargetKey.ToUpperInvariant(), "--allow-power-write", "--worker"], out var options));
        Assert.AreEqual(TargetKey, options!.TargetKey);
        Assert.IsTrue(options.Worker);
    }
}
