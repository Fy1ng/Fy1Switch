using Microsoft.VisualStudio.TestTools.UnitTesting;
using PowerDisplay.Common.Models;

namespace RemoteDisplaySwitch.Probe.Tests;

[TestClass]
public class DurationAndRestartTests
{
    private static readonly string TargetKey = new('a', 64);

    [TestMethod]
    [DataRow("cycle")]
    [DataRow("restart-cycle")]
    public void CycleCommandsDefaultToFiveSeconds(string command)
    {
        Assert.IsTrue(ProbeOptions.TryParse([command, TargetKey, "--allow-power-write"], out var options));
        Assert.AreEqual(command, options!.Command);
        Assert.AreEqual(TargetKey, options.TargetKey);
        Assert.AreEqual(5, options.OffSeconds);
        Assert.IsTrue(options.WritesPower);
        Assert.IsTrue(options.AllowPowerWrite);
    }

    [TestMethod]
    [DataRow("cycle", "1", 1)]
    [DataRow("cycle", "300", 300)]
    [DataRow("restart-cycle", "1", 1)]
    [DataRow("restart-cycle", "300", 300)]
    public void DurationEndpointsPreserveWorkerAndOrderingOptions(string command, string seconds, int expectedSeconds)
    {
        Assert.IsTrue(ProbeOptions.TryParse(
            [command, TargetKey.ToUpperInvariant(), "--worker", "--off-seconds", seconds, "--gdi-order-experiment", "--allow-power-write"],
            out var options));
        Assert.AreEqual(expectedSeconds, options!.OffSeconds);
        Assert.AreEqual(TargetKey, options.TargetKey);
        Assert.IsTrue(options.WritesPower);
        Assert.IsTrue(options.AllowPowerWrite);
        Assert.IsTrue(options.Worker);
        Assert.IsTrue(options.GdiOrderExperiment);
    }

    [TestMethod]
    [DataRow("cycle")]
    [DataRow("restart-cycle")]
    public void CycleCommandsRetainTargetAndWriteGuards(string command)
    {
        string[][] invalidArguments =
        [
            [command, "--allow-power-write"],
            [command, "", "--allow-power-write"],
            [command, new string('a', 63), "--allow-power-write"],
            [command, new string('a', 65), "--allow-power-write"],
            [command, new string('z', 64), "--allow-power-write"],
            [command, TargetKey],
            [command, TargetKey, "--worker", "--gdi-order-experiment", "--off-seconds", "300"],
        ];

        foreach (var arguments in invalidArguments)
        {
            Assert.IsFalse(ProbeOptions.TryParse(arguments, out _), string.Join(" ", arguments));
        }
    }

    [TestMethod]
    [DataRow("cycle")]
    [DataRow("restart-cycle")]
    public void InvalidAndDuplicateDurationOptionsAreRejected(string command)
    {
        string[][] invalidOptions =
        [
            ["--off-seconds"],
            ["--off-seconds", "--worker"],
            ["--off-seconds", ""],
            ["--off-seconds", "abc"],
            ["--off-seconds", "1.5"],
            ["--off-seconds", "-1"],
            ["--off-seconds", "0"],
            ["--off-seconds", "301"],
            ["--off-seconds", "2147483648"],
            ["--off-seconds", "5", "--off-seconds", "5"],
            ["--off-seconds", "5", "--worker", "--worker"],
            ["--off-seconds", "5", "--gdi-order-experiment", "--gdi-order-experiment"],
            ["--off-seconds", "5", "--allow-power-write"],
        ];

        foreach (var options in invalidOptions)
        {
            Assert.IsFalse(ProbeOptions.TryParse(
                [command, TargetKey, "--allow-power-write", .. options], out _), string.Join(" ", options));
        }
    }

    [TestMethod]
    public void OtherCommandsRejectDuration()
    {
        string[][] invalidArguments =
        [
            ["inventory", "--off-seconds", "5"],
            ["topology", "--off-seconds", "5"],
            ["discover", "--off-seconds", "5"],
            ["read", TargetKey, "--off-seconds", "5"],
            ["on", TargetKey, "--allow-power-write", "--off-seconds", "5"],
        ];

        foreach (var arguments in invalidArguments)
        {
            Assert.IsFalse(ProbeOptions.TryParse(arguments, out _), string.Join(" ", arguments));
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(1)]
    [DataRow(300)]
    public async Task RunnerUsesDefaultOrExplicitDurationBetweenOffAndOn(int? offSeconds)
    {
        var writes = new List<int>();
        var delayCalls = 0;
        var result = await PowerCycleRunner.RunAsync(value =>
        {
            writes.Add(value);
            return Task.FromResult(MonitorOperationResult.Success());
        }, duration =>
        {
            delayCalls++;
            CollectionAssert.AreEqual(new[] { 4 }, writes);
            Assert.AreEqual(TimeSpan.FromSeconds(offSeconds ?? 5), duration);
            return Task.CompletedTask;
        }, offSeconds.HasValue ? TimeSpan.FromSeconds(offSeconds.Value) : null);

        CollectionAssert.AreEqual(new[] { 4, 1 }, writes);
        Assert.AreEqual(1, delayCalls);
        Assert.IsTrue(result.Off.IsSuccess && result.On.IsSuccess);
    }

    [TestMethod]
    [DataRow(-TimeSpan.TicksPerSecond)]
    [DataRow(0L)]
    [DataRow(TimeSpan.TicksPerSecond - 1)]
    [DataRow(300 * TimeSpan.TicksPerSecond + 1)]
    [DataRow(long.MinValue)]
    [DataRow(long.MaxValue)]
    public async Task InvalidDurationIsRejectedBeforeAnyWriteOrDelay(long ticks)
    {
        var writes = new List<int>();
        var delayCalls = 0;
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => PowerCycleRunner.RunAsync(value =>
        {
            writes.Add(value);
            return Task.FromResult(MonitorOperationResult.Success());
        }, _ =>
        {
            delayCalls++;
            return Task.CompletedTask;
        }, TimeSpan.FromTicks(ticks)));

        Assert.AreEqual(0, writes.Count);
        Assert.AreEqual(0, delayCalls);
    }

    [TestMethod]
    public async Task FailedOffWithLongDurationRestoresOnWithoutWaiting()
    {
        var writes = new List<int>();
        var result = await PowerCycleRunner.RunAsync(value =>
        {
            writes.Add(value);
            return Task.FromResult(value == 4 ? MonitorOperationResult.Failure("refused", 50) : MonitorOperationResult.Success());
        }, _ => throw new AssertFailedException("No wait after failed OFF."), TimeSpan.FromSeconds(300));

        CollectionAssert.AreEqual(new[] { 4, 1 }, writes);
        Assert.IsFalse(result.Off.IsSuccess);
        Assert.IsTrue(result.On.IsSuccess);
    }

    [TestMethod]
    public async Task FaultedLongDelayRestoresOnAndPropagatesException()
    {
        var writes = new List<int>();
        var failure = new InvalidOperationException("Interrupted test delay.");
        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => PowerCycleRunner.RunAsync(value =>
        {
            writes.Add(value);
            return Task.FromResult(MonitorOperationResult.Success());
        }, duration =>
        {
            Assert.AreEqual(TimeSpan.FromSeconds(300), duration);
            return Task.FromException(failure);
        }, TimeSpan.FromSeconds(300)));

        CollectionAssert.AreEqual(new[] { 4, 1 }, writes);
        Assert.AreSame(failure, thrown);
    }
}
