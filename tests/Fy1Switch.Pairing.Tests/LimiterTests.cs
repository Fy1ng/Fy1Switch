using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Pairing;

namespace Fy1Switch.Pairing.Tests;

[TestClass]
public sealed class LimiterTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan amount) => ticks += amount.Ticks;
    }

    [TestMethod]
    public void FivePerCodeTenPerProcessAndRenewalDoesNotReset()
    {
        var clock = new Clock();
        var limiter = new PairingAttemptLimiter(clock);
        for (var sessionIndex = 0; sessionIndex < 2; sessionIndex++)
        {
            using var lease = new PairingCodeLease(limiter, clock);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                Assert.IsTrue(lease.TryBegin(out var code));
                PairingProtocol.ValidateCode(code);
                lease.EndAttempt();
            }
            Assert.IsFalse(lease.TryBegin(out _));
            Assert.AreEqual("", lease.Code);
        }
        using var denied = new PairingCodeLease(limiter, clock);
        Assert.IsFalse(denied.TryBegin(out _));
        clock.Advance(TimeSpan.FromMinutes(10));
        using var next = new PairingCodeLease(limiter, clock);
        Assert.IsTrue(next.TryBegin(out _));
        next.EndAttempt();
    }

    [TestMethod]
    public void BusyProofConsumptionExpiryAndDisposalAreTerminal()
    {
        var clock = new Clock();
        var limiter = new PairingAttemptLimiter(clock);
        using var lease = new PairingCodeLease(limiter, clock);
        Assert.IsTrue(lease.TryBegin(out _));
        Assert.IsFalse(lease.TryBegin(out _));
        Assert.IsTrue(lease.ConsumeProof());
        Assert.IsFalse(lease.ConsumeProof());
        lease.EndAttempt();
        Assert.IsFalse(lease.TryBegin(out _));
        Assert.AreEqual("", lease.Code);
        using var expiring = new PairingCodeLease(limiter, clock);
        Assert.IsTrue(expiring.TryBegin(out _));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsFalse(expiring.ConsumeProof());
        Assert.IsFalse(expiring.TryBegin(out _));
        using var disposed = new PairingCodeLease(limiter, clock);
        disposed.Dispose();
        Assert.IsFalse(disposed.TryBegin(out _));
    }

    [TestMethod]
    public void SharedAdmissionIsAtomicAndConsumedSessionCannotReplay()
    {
        var limiter = new PairingAttemptLimiter();
        var admitted = 0;
        Parallel.For(0, 100, _ => { if (limiter.TryAdmit(PairingProtocol.NewSessionId())) Interlocked.Increment(ref admitted); });
        Assert.AreEqual(10, admitted);
        var fresh = new PairingAttemptLimiter();
        var session = PairingProtocol.NewSessionId();
        Assert.IsTrue(fresh.TryBeginAttempt(session));
        Assert.IsFalse(fresh.TryBeginAttempt(session));
        Assert.IsTrue(fresh.TryConsume(session));
        fresh.EndAttempt(session);
        Assert.IsFalse(fresh.TryAdmit(session));
    }
}
