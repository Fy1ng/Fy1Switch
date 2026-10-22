using Microsoft.VisualStudio.TestTools.UnitTesting;
using PowerDisplay.Common.Models;

namespace Fy1Switch.Probe.Tests;

[TestClass]
public class PowerCycleRunnerTests
{
    [TestMethod]
    public async Task SuccessWritesOnlyDpmAndOnWithBoundedDelay()
    {
        var writes = new List<int>();
        var result = await PowerCycleRunner.RunAsync(value =>
        {
            writes.Add(value);
            return Task.FromResult(MonitorOperationResult.Success());
        }, duration =>
        {
            Assert.AreEqual(TimeSpan.FromSeconds(5), duration);
            return Task.CompletedTask;
        });
        CollectionAssert.AreEqual(new[] { 4, 1 }, writes);
        Assert.IsTrue(result.Off.IsSuccess && result.On.IsSuccess);
    }

    [TestMethod]
    public async Task FailedOffStillAttemptsOnWithoutWaiting()
    {
        var writes = new List<int>();
        var result = await PowerCycleRunner.RunAsync(value =>
        {
            writes.Add(value);
            return Task.FromResult(value == 4 ? MonitorOperationResult.Failure("refused", 50) : MonitorOperationResult.Success());
        }, _ => throw new AssertFailedException("No wait after failed OFF."));
        CollectionAssert.AreEqual(new[] { 4, 1 }, writes);
        Assert.IsFalse(result.Off.IsSuccess);
        Assert.IsTrue(result.On.IsSuccess);
    }

    [TestMethod]
    public async Task ThrowingOffStillAttemptsOn()
    {
        var writes = new List<int>();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => PowerCycleRunner.RunAsync(value =>
        {
            writes.Add(value);
            return value == 4 ? throw new InvalidOperationException() : Task.FromResult(MonitorOperationResult.Success());
        }, _ => Task.CompletedTask));
        CollectionAssert.AreEqual(new[] { 4, 1 }, writes);
    }

    [TestMethod]
    public async Task InterruptedWaitStillAttemptsOn()
    {
        var writes = new List<int>();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => PowerCycleRunner.RunAsync(value =>
        {
            writes.Add(value);
            return Task.FromResult(MonitorOperationResult.Success());
        }, _ => throw new OperationCanceledException()));
        CollectionAssert.AreEqual(new[] { 4, 1 }, writes);
    }

    [TestMethod]
    public async Task FailedRestorationIsNotReportedAsSuccess()
    {
        var result = await PowerCycleRunner.RunAsync(value => Task.FromResult(
            value == 1 ? MonitorOperationResult.Failure("gone", 50) : MonitorOperationResult.Success()), _ => Task.CompletedTask);
        Assert.IsFalse(result.On.IsSuccess);
        Assert.AreEqual(50, result.On.ErrorCode);
    }
}
