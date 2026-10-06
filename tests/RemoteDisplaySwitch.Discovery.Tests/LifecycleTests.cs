using System.Collections.Concurrent;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RemoteDisplaySwitch.Discovery.Tests;

[TestClass]
public class LifecycleTests
{
    private static Task Start(LanDiscovery discovery, CancellationToken token = default) =>
        discovery.StartAsync(43210, MetadataTests.Self, "Windows", IPAddress.Parse("192.0.2.1"), token);

    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [TestMethod]
    public async Task RegistersSelectedInterfaceAndStopsBothDirections()
    {
        var adapter = new FakeAdapter();
        await using var discovery = new LanDiscovery(7, adapter);
        await Start(discovery);
        Assert.AreEqual(7U, adapter.Advertisement!.InterfaceIndex);
        Assert.AreEqual(MetadataTests.Self, adapter.Advertisement.SessionId);
        Assert.AreEqual(43210, adapter.Advertisement.Port);
        adapter.Emit(120);
        await Until(() => discovery.Candidates.Count == 2);
        await discovery.StopAsync();
        Assert.AreEqual(0, discovery.Candidates.Count);
        Assert.IsFalse(discovery.IsRunning);
        Assert.AreEqual(1, adapter.BrowseLease.Disposals);
        Assert.AreEqual(1, adapter.RegisterLease.Disposals);
        await discovery.StopAsync();
        Assert.AreEqual(1, adapter.RegisterLease.Disposals);
    }

    [TestMethod]
    public async Task DeduplicatesRefreshUpdatesPortAndExpiresWithoutSleepingForTtl()
    {
        var adapter = new FakeAdapter();
        var clock = new ManualClock();
        await using var discovery = new LanDiscovery(7, adapter, clock);
        await Start(discovery);
        adapter.Emit(30);
        await Until(() => discovery.Candidates.Count == 2);
        adapter.Emit(30);
        await Until(() => adapter.Resolves >= 2);
        Assert.AreEqual(2, discovery.Candidates.Count);
        adapter.Metadata = MetadataTests.Metadata(port: 50000);
        adapter.Emit(30);
        await Until(() => discovery.Candidates[0].Port == 50000);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.AreEqual(2, discovery.Candidates.Count);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, discovery.Candidates.Count);
    }

    [TestMethod]
    public async Task GoodbyeDeletesAndLateResolveCannotResurrect()
    {
        var adapter = new FakeAdapter { DelayResolve = true };
        await using var discovery = new LanDiscovery(7, adapter);
        await Start(discovery);
        adapter.Emit(30);
        await Until(() => adapter.Delayed.Count == 1);
        adapter.Emit(0);
        adapter.Delayed.TryDequeue(out var pending);
        pending!.SetResult(MetadataTests.Metadata());
        await discovery.StopAsync();
        Assert.AreEqual(0, discovery.Candidates.Count);
    }

    [TestMethod]
    public async Task OldGenerationBrowseCallbackIsIgnoredAfterRestart()
    {
        var adapter = new FakeAdapter();
        await using var discovery = new LanDiscovery(7, adapter);
        await Start(discovery);
        var oldCallback = adapter.Announcement!;
        await Start(discovery);
        oldCallback(new(MetadataTests.Name, 120));
        Assert.AreEqual(0, adapter.Resolves);
        adapter.Emit(120);
        await Until(() => discovery.Candidates.Count == 2);
        Assert.AreEqual(1, adapter.Resolves);
    }

    [TestMethod]
    public async Task StopCancelsAndDrainsOutstandingResolve()
    {
        var adapter = new FakeAdapter { DelayResolve = true };
        await using var discovery = new LanDiscovery(7, adapter);
        await Start(discovery);
        adapter.Emit(120);
        await Until(() => adapter.Delayed.Count == 1);
        await discovery.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(adapter.ResolveToken.IsCancellationRequested);
        Assert.AreEqual(0, discovery.Candidates.Count);
    }

    [TestMethod]
    public async Task CancellationStopsSessionAndDisposePreventsRestart()
    {
        var adapter = new FakeAdapter();
        var discovery = new LanDiscovery(7, adapter);
        using var cancellation = new CancellationTokenSource();
        await Start(discovery, cancellation.Token);
        cancellation.Cancel();
        await Until(() => adapter.RegisterLease.Disposals == 1);
        await discovery.DisposeAsync();
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => Start(discovery));
    }

    [TestMethod]
    public async Task BrowseFailureRollsBackAdvertisementAndKeepsNativeStatus()
    {
        var adapter = new FakeAdapter { BrowseError = new DnsSdException("DnsServiceBrowse", 5) };
        await using var discovery = new LanDiscovery(7, adapter);
        await Assert.ThrowsExceptionAsync<DnsSdException>(() => Start(discovery));
        Assert.AreEqual(1, adapter.RegisterLease.Disposals);
        Assert.IsFalse(discovery.IsRunning);
        Assert.AreEqual(5U, discovery.Diagnostics.Single().NativeStatus);
    }

    [TestMethod]
    public async Task TeardownFailureStillReleasesOtherDirectionAndCanRetry()
    {
        var adapter = new FakeAdapter();
        await using var discovery = new LanDiscovery(7, adapter);
        await Start(discovery);
        adapter.BrowseLease.Error = new DnsSdException("DnsServiceBrowseCancel", 87);
        await Assert.ThrowsExceptionAsync<AggregateException>(() => discovery.StopAsync());
        Assert.AreEqual(1, adapter.RegisterLease.Disposals);
        Assert.AreEqual(87U, discovery.Diagnostics.Last().NativeStatus);
        adapter.BrowseLease.Error = null;
        await discovery.StopAsync();
    }

    [TestMethod]
    public async Task InvalidInputsNeverInvokeAdapter()
    {
        var adapter = new FakeAdapter();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new LanDiscovery(0, adapter));
        await using var discovery = new LanDiscovery(7, adapter);
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => discovery.StartAsync(0, MetadataTests.Self, "Win", IPAddress.Loopback));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => discovery.StartAsync(1, "invalid", "Win", IPAddress.Loopback));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => discovery.StartAsync(1, MetadataTests.Self, "bad.name", IPAddress.Loopback));
        Assert.IsNull(adapter.Advertisement);
    }

    [TestMethod]
    public async Task OlderResolveCannotOverwriteNewerAnnouncement()
    {
        var adapter = new FakeAdapter { DelayResolve = true };
        await using var discovery = new LanDiscovery(7, adapter);
        await Start(discovery);
        adapter.Emit(120);
        await Until(() => adapter.Delayed.Count == 1);
        adapter.Delayed.TryDequeue(out var older);
        adapter.Emit(120);
        await Until(() => adapter.Delayed.Count == 1);
        adapter.Delayed.TryDequeue(out var newer);
        newer!.SetResult(MetadataTests.Metadata(port: 50123));
        await Until(() => discovery.Candidates.Count == 2);
        older!.SetResult(MetadataTests.Metadata(port: 43210));
        await Until(() => adapter.CompletedResolves == 2);
        Assert.AreEqual(50123, discovery.Candidates[0].Port);
    }

    [TestMethod]
    public async Task AliasInstancesDeduplicateBySessionAddressAndInterface()
    {
        var adapter = new FakeAdapter();
        await using var discovery = new LanDiscovery(7, adapter);
        await Start(discovery);
        adapter.Emit(120);
        await Until(() => discovery.Candidates.Count == 2);
        adapter.Announcement!(new("Other._rds-pair._tcp.local.", 120));
        await Until(() => discovery.Candidates[0].Name == "Other");
        Assert.AreEqual(2, discovery.Candidates.Count);
        adapter.Announcement!(new("Other._rds-pair._tcp.local.", 0));
        Assert.AreEqual("Phone", discovery.Candidates[0].Name);
        adapter.Emit(0);
        Assert.AreEqual(0, discovery.Candidates.Count);
    }

    [TestMethod]
    public async Task RegistrationFailureAndResolveFailureRemainDiagnosable()
    {
        var adapter = new FakeAdapter { RegisterError = new DnsSdException("DnsServiceRegister", 87) };
        await using var discovery = new LanDiscovery(7, adapter);
        await Assert.ThrowsExceptionAsync<DnsSdException>(() => Start(discovery));
        Assert.AreEqual(87U, discovery.Diagnostics[0].NativeStatus);
        adapter.RegisterError = null;
        adapter.ResolveError = new DnsSdException("DnsServiceResolve", 9003);
        await Start(discovery);
        adapter.Emit(120);
        await Until(() => discovery.Diagnostics.Any(item => item.NativeStatus == 9003));
        Assert.AreEqual(0, discovery.Candidates.Count);
    }

    [TestMethod]
    public async Task EventSubscriberMayStopWithoutBlockingResolveCallback()
    {
        var adapter = new FakeAdapter();
        await using var discovery = new LanDiscovery(7, adapter);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.CandidatesChanged += (_, _) =>
        {
            if (discovery.Candidates.Count == 0) return;
            discovery.Stop();
            stopped.TrySetResult();
        };
        await Start(discovery);
        adapter.Emit(120);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(discovery.IsRunning);
    }

    [TestMethod]
    public async Task CancellationRacingSuccessfulRegistrationRetainsLeaseForRollback()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new FakeAdapter { AfterRegister = cancellation.Cancel };
        await using var discovery = new LanDiscovery(7, adapter);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => Start(discovery, cancellation.Token));
        Assert.AreEqual(1, adapter.RegisterLease.Disposals);
        Assert.IsNull(adapter.Announcement);
        Assert.IsFalse(discovery.IsRunning);
    }
}

internal sealed class FakeAdapter : IDnsSdAdapter
{
    internal LocalAdvertisement? Advertisement;
    internal Action<ServiceAnnouncement>? Announcement;
    internal FakeLease RegisterLease = new();
    internal FakeLease BrowseLease = new();
    internal ServiceMetadata Metadata = MetadataTests.Metadata();
    internal Exception? BrowseError;
    internal Exception? RegisterError;
    internal Exception? ResolveError;
    internal Action? AfterRegister;
    internal bool DelayResolve;
    internal int Resolves;
    internal int CompletedResolves;
    internal CancellationToken ResolveToken;
    internal ConcurrentQueue<TaskCompletionSource<ServiceMetadata?>> Delayed = new();

    public Task<IAsyncDisposable> RegisterAsync(LocalAdvertisement advertisement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RegisterError is not null) throw RegisterError;
        Advertisement = advertisement;
        RegisterLease = new();
        AfterRegister?.Invoke();
        return Task.FromResult<IAsyncDisposable>(RegisterLease);
    }
    public IAsyncDisposable Browse(uint interfaceIndex, Action<ServiceAnnouncement> onAnnouncement, Action<DiscoveryDiagnostic> onError)
    {
        if (BrowseError is not null) throw BrowseError;
        Assert.AreEqual(7U, interfaceIndex);
        Announcement = onAnnouncement;
        BrowseLease = new();
        return BrowseLease;
    }
    public async Task<ServiceMetadata?> ResolveAsync(string instanceName, uint interfaceIndex, CancellationToken cancellationToken)
    {
        Assert.AreEqual(7U, interfaceIndex);
        ResolveToken = cancellationToken;
        Interlocked.Increment(ref Resolves);
        if (ResolveError is not null) throw ResolveError;
        if (!DelayResolve) return Metadata with { InstanceName = instanceName };
        var completion = new TaskCompletionSource<ServiceMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Delayed.Enqueue(completion);
        var result = await completion.Task.WaitAsync(cancellationToken);
        Interlocked.Increment(ref CompletedResolves);
        return result;
    }
    internal void Emit(uint ttl) => Announcement!(new(MetadataTests.Name, ttl));
}

internal sealed class FakeLease : IAsyncDisposable
{
    internal int Disposals;
    internal Exception? Error;
    public ValueTask DisposeAsync()
    {
        if (Error is not null) throw Error;
        Interlocked.Increment(ref Disposals);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UnixEpoch;
    private readonly List<ManualTimer> timers = [];
    public override DateTimeOffset GetUtcNow() => now;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state);
        timers.Add(timer);
        return timer;
    }
    internal void Advance(TimeSpan amount)
    {
        now += amount;
        foreach (var timer in timers.ToArray()) timer.Fire();
    }
    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        internal void Fire() { if (!disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
        public void Dispose() => disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
