using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Runtime;

[assembly: DoNotParallelize]

namespace RemoteDisplaySwitch.Runtime.Tests;

internal sealed class TestDirectory : IDisposable
{
    private readonly string temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
    private readonly string directoryName = $"RemoteDisplaySwitch.Runtime.Tests-{Guid.NewGuid():N}";
    public string Path { get; }

    public TestDirectory()
    {
        Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(temporaryRoot, directoryName));
        if (Directory.Exists(Path)) throw new IOException("Test directory already exists.");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        var expectedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(temporaryRoot, directoryName));
        if (!string.Equals(System.IO.Path.GetFullPath(Path), expectedPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(System.IO.Path.GetDirectoryName(Path), System.IO.Path.TrimEndingDirectorySeparator(temporaryRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove a directory outside the owned temporary path.");
        if (!Directory.Exists(Path)) return;
        if ((File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to follow a test directory reparse point.");
        using (var pool = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(Path, "state.db") }.ToString()))
            SqliteConnection.ClearPool(pool);
        Directory.Delete(Path, recursive: true);
    }
}

internal sealed class FakeStartup : IStartupRegistration
{
    public bool Enabled { get; private set; }
    public List<bool> Changes { get; } = [];

    public void SetEnabled(bool enabled)
    {
        Changes.Add(enabled);
        Enabled = enabled;
    }
}

internal sealed class FakeWorkerTransport : IWorkerTransport
{
    public const string TargetKey = "fake-physical-target";
    public const string Fingerprint = "fake-topology-v1";
    private readonly ConcurrentQueue<WorkerRequest> requests = new();
    private int activeRequests;
    private int maximumConcurrentRequests;

    public bool IsRunning { get; set; }
    public bool Disposed { get; private set; }
    public int MaximumConcurrentRequests => Volatile.Read(ref maximumConcurrentRequests);
    public WorkerRequest[] Requests => requests.ToArray();
    public WorkerRequest[] Writes => Requests.Where(request => request.Command == "set").ToArray();
    public PowerObservation Reading { get; set; } = Observe(DisplayPower.Unknown);
    public DiscoverySnapshot Discovery { get; set; } = new(Fingerprint, [new(TargetKey, "Fake monitor", "fake-profile")], DateTimeOffset.UtcNow);
    public Func<WorkerRequest, CancellationToken, Task<WorkerReply>>? Handler { get; set; }

    public static PowerObservation Observe(DisplayPower state) => new(state, state switch { DisplayPower.On => 1, DisplayPower.Off => 4, _ => null }, DateTimeOffset.UtcNow, "fake-authoritative-read");

    public async Task<WorkerReply> SendAsync(WorkerRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        requests.Enqueue(request);
        var active = Interlocked.Increment(ref activeRequests);
        int previous;
        do
        {
            previous = Volatile.Read(ref maximumConcurrentRequests);
            if (previous >= active) break;
        } while (Interlocked.CompareExchange(ref maximumConcurrentRequests, active, previous) != previous);
        IsRunning = true;
        try { return Handler is null ? Reply(request) : await Handler(request, cancellationToken); }
        finally { Interlocked.Decrement(ref activeRequests); }
    }

    public WorkerReply Reply(WorkerRequest request)
    {
        if (request.Command == "discover") return new(request.RequestId, true, Discovery: Discovery);
        if (request.Command == "read") return new(request.RequestId, true, Observation: Reading);
        if (request.Command == "set" && request.Intent is PowerIntent.On or PowerIntent.Off)
        {
            Reading = Observe(request.Intent == PowerIntent.On ? DisplayPower.On : DisplayPower.Off);
            return new(request.RequestId, true, Observation: Reading, WriteAccepted: true);
        }
        throw new InvalidOperationException($"Unexpected fake worker command: {request.Command}");
    }

    public Task StopAsync()
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        IsRunning = false;
        return ValueTask.CompletedTask;
    }
}

internal sealed class RequestGate
{
    public static TimeSpan Deadline { get; } = TimeSpan.FromSeconds(10);
    private readonly TaskCompletionSource<WorkerRequest> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<WorkerRequest> Entered => entered.Task.WaitAsync(Deadline);

    public async Task WaitAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        entered.TrySetResult(request);
        await released.Task.WaitAsync(Deadline, cancellationToken);
    }

    public void Release() => released.TrySetResult();
}

internal sealed class ControllerFixture : IAsyncDisposable
{
    public TestDirectory Directory { get; } = new();
    public FakeWorkerTransport Worker { get; private set; } = new();
    public FakeStartup Startup { get; } = new();
    public DesktopController Controller { get; private set; }

    public ControllerFixture() => Controller = CreateController();
    private DesktopController CreateController() => new(Worker, Startup, Directory.Path, simulation: false);

    public async Task<MonitorBinding> BindAsync()
    {
        var discovery = await Controller.DiscoverAsync();
        var snapshot = await Controller.BindAsync(discovery.Candidates.Single().TargetKey, discovery.TopologyFingerprint, "Test monitor");
        Assert.IsNotNull(snapshot.Binding);
        return snapshot.Binding;
    }

    public async Task RestartAsync()
    {
        await Controller.DisposeAsync();
        Worker = new();
        Controller = CreateController();
    }

    public async ValueTask DisposeAsync()
    {
        try { await Controller.DisposeAsync(); }
        finally { Directory.Dispose(); }
    }
}
