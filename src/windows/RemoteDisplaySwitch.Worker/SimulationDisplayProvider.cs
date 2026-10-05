using RemoteDisplaySwitch.Contracts;

namespace RemoteDisplaySwitch.Worker;

internal sealed class SimulationDisplayProvider : IDisplayProvider
{
    public static string TargetKey { get; } = new('a', 64);
    public static string Fingerprint { get; } = new('b', 64);
    private DisplayPower state = DisplayPower.On;
    private bool discovered;

    public Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        discovered = true;
        return Task.FromResult(new DiscoverySnapshot(Fingerprint,
            [new(TargetKey, "模拟显示器", "simulation")], DateTimeOffset.UtcNow));
    }

    public Task<PowerObservation> ReadAsync(string targetKey, string fingerprint, CancellationToken cancellationToken)
    {
        Validate(targetKey, fingerprint, cancellationToken);
        return Task.FromResult(Observe());
    }

    public Task<PowerWriteResult> SetAsync(string targetKey, string fingerprint, PowerIntent intent, CancellationToken cancellationToken)
    {
        Validate(targetKey, fingerprint, cancellationToken);
        state = intent switch
        {
            PowerIntent.On => DisplayPower.On,
            PowerIntent.Off => DisplayPower.Off,
            _ => throw new DisplayProviderException("invalid_intent", false),
        };
        return Task.FromResult(new PowerWriteResult(true, Observe()));
    }

    private void Validate(string targetKey, string fingerprint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!discovered) throw new DisplayProviderException("discovery_required", false);
        if (fingerprint != Fingerprint) throw new DisplayProviderException("topology_changed", false);
        if (targetKey != TargetKey) throw new DisplayProviderException("target_mismatch", false);
    }

    private PowerObservation Observe() => new(state, state == DisplayPower.On ? 1 : 4, DateTimeOffset.UtcNow, "simulation");

    public void Dispose() { }
}
