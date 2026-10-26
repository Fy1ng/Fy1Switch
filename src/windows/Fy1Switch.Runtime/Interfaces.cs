using Fy1Switch.Contracts;

namespace Fy1Switch.Runtime;

public interface IWorkerTransport : IAsyncDisposable
{
    bool IsRunning { get; }
    Task<WorkerReply> SendAsync(WorkerRequest request, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface IStartupRegistration
{
    bool Enabled { get; }
    void SetEnabled(bool enabled);
}

public interface IDesktopController
{
    event Action? Changed;
    bool Simulation { get; }
    string DataDirectory { get; }
    Task<HostSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<HostSnapshot> BindAsync(string targetKey, string topologyFingerprint, string name, CancellationToken cancellationToken = default);
    Task<HostSnapshot> UnbindAsync(CancellationToken cancellationToken = default);
    Task<HostSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task<DisplayControlsSnapshot> GetDisplayControlsAsync(Guid targetId, CancellationToken cancellationToken = default);
    Task<DisplayControlWriteResult> SetDisplayControlAsync(Guid targetId, DisplayControl control, int value, CancellationToken cancellationToken = default);
    Task<MonitorPreferences> GetMonitorPreferencesAsync(string targetKey, CancellationToken cancellationToken = default);
    Task<MonitorPreferences> SetMonitorPreferencesAsync(string targetKey, MonitorPreferences preferences, CancellationToken cancellationToken = default);
    Task<OperationRecord> ExecuteAsync(Guid targetId, PowerIntent intent, Guid operationId, CancellationToken cancellationToken = default);
    Task<OperationRecord?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<HostSnapshot> SetStartupAsync(bool enabled, CancellationToken cancellationToken = default);
    void NotifyTopologyChanged();
}

public sealed class ControlException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
