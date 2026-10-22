using System.Collections.Concurrent;
using Fy1Switch.Contracts;
using Fy1Switch.Runtime;

namespace Fy1Switch.Remote.Tests;

internal sealed record ControllerCall(string Method, Guid? TargetId = null, PowerIntent? Intent = null, Guid? OperationId = null);

internal sealed class FakeDesktopController(Guid deviceId, TestDirectory directory, TestClock clock) : IDesktopController
{
    private readonly ConcurrentQueue<ControllerCall> calls = new();
    public const string PrivateTargetKey = "private-test-monitor-key";
    public const string PrivateFingerprint = "private-test-topology-fingerprint";
    public const string PrivateProfile = "private-test-monitor-profile";
    public event Action? Changed { add { } remove { } }
    public bool Simulation => true;
    public string DataDirectory => directory.Path;
    public ControllerCall[] Calls => calls.ToArray();
    public Func<CancellationToken, Task<HostSnapshot>>? SnapshotHandler { get; set; }
    public OperationRecord? Operation { get; set; }
    public HostSnapshot Snapshot { get; set; } = new(deviceId,
        new(Guid.NewGuid(), PrivateTargetKey, "Test display", PrivateProfile, PrivateFingerprint),
        new(DisplayPower.On, 1, clock.Now, "test-only-simulation"), true, false, true, null, true);

    public Task<HostSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        calls.Enqueue(new("status"));
        return SnapshotHandler?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot);
    }

    public Task<HostSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        calls.Enqueue(new("refresh"));
        return Task.FromResult(Snapshot);
    }

    public Task<OperationRecord> ExecuteAsync(Guid targetId, PowerIntent intent, Guid operationId, CancellationToken cancellationToken = default)
    {
        calls.Enqueue(new("set", targetId, intent, operationId));
        return Task.FromResult(Operation ?? new(operationId, targetId, intent, intent, OperationOutcome.Confirmed, null, Snapshot.Observation, clock.Now));
    }

    public Task<OperationRecord?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        calls.Enqueue(new("operation", OperationId: operationId));
        return Task.FromResult(Operation);
    }

    public Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default) => throw LocalOnly("discover");
    public Task<HostSnapshot> BindAsync(string targetKey, string topologyFingerprint, string name, CancellationToken cancellationToken = default) => throw LocalOnly("bind");
    public Task<HostSnapshot> UnbindAsync(CancellationToken cancellationToken = default) => throw LocalOnly("unbind");
    public Task<HostSnapshot> SetStartupAsync(bool enabled, CancellationToken cancellationToken = default) => throw LocalOnly("startup");
    public void NotifyTopologyChanged() => throw LocalOnly("topology");

    private InvalidOperationException LocalOnly(string method)
    {
        calls.Enqueue(new(method));
        return new("Local-only management must not be reached by a remote command.");
    }
}
