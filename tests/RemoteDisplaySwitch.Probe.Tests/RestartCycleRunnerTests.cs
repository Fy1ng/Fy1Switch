using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RemoteDisplaySwitch.Probe.Tests;

[TestClass]
public class RestartCycleRunnerTests
{
    [TestMethod]
    public async Task ColdRecoveryRequiresStoppedAcknowledgedWriterAndFreshOn()
    {
        var events = new List<string>();
        var result = await RestartCycleRunner.RunAsync(() =>
        {
            events.Add("off-and-stop");
            return Task.FromResult(new ProbeProcessResult(0, true, true));
        }, duration =>
        {
            events.Add("wait");
            Assert.AreEqual(TimeSpan.FromSeconds(60), duration);
            return Task.CompletedTask;
        }, () =>
        {
            events.Add("fresh-on");
            return Task.FromResult(0);
        }, TimeSpan.FromSeconds(60));
        CollectionAssert.AreEqual(new[] { "off-and-stop", "wait", "fresh-on" }, events);
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    [DataRow(0, false, false)]
    [DataRow(0, true, false)]
    [DataRow(3, true, true)]
    public async Task UnprovenWriterStillRestoresWithoutClaimingColdSuccess(int exitCode, bool acknowledged, bool stopped)
    {
        var restored = false;
        var result = await RestartCycleRunner.RunAsync(
            () => Task.FromResult(new ProbeProcessResult(exitCode, acknowledged, stopped)),
            _ => throw new AssertFailedException("Do not prolong an uncertain OFF."),
            () => { restored = true; return Task.FromResult(0); },
            TimeSpan.FromSeconds(5));
        Assert.IsTrue(restored);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task ThrowingWriterStillStartsFreshRestoration()
    {
        var restored = false;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => RestartCycleRunner.RunAsync(
            () => throw new InvalidOperationException(),
            _ => Task.CompletedTask,
            () => { restored = true; return Task.FromResult(0); },
            TimeSpan.FromSeconds(5)));
        Assert.IsTrue(restored);
    }

    [TestMethod]
    public async Task CancelledWaitStillStartsFreshRestoration()
    {
        var restored = false;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => RestartCycleRunner.RunAsync(
            () => Task.FromResult(new ProbeProcessResult(0, true, true)),
            _ => throw new OperationCanceledException(),
            () => { restored = true; return Task.FromResult(0); },
            TimeSpan.FromSeconds(5)));
        Assert.IsTrue(restored);
    }

    [TestMethod]
    public async Task FreshRestorationFailureIsNotSuccess()
    {
        var result = await RestartCycleRunner.RunAsync(
            () => Task.FromResult(new ProbeProcessResult(0, true, true)),
            _ => Task.CompletedTask,
            () => Task.FromResult(8),
            TimeSpan.FromSeconds(5));
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(8, result.RestoreExitCode);
    }

    [TestMethod]
    public async Task InvalidDurationDoesNotWriteOrRestore()
    {
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => RestartCycleRunner.RunAsync(
            () => throw new AssertFailedException("No OFF before valid parameters."),
            _ => throw new AssertFailedException(),
            () => throw new AssertFailedException("No ON before valid parameters."),
            TimeSpan.FromSeconds(301)));
    }
}
