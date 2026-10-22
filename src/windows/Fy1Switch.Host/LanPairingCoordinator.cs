using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Fy1Switch.Contracts;
using Fy1Switch.Discovery;
using Fy1Switch.Pairing;
using Fy1Switch.Remote;

namespace Fy1Switch.Host;

internal sealed class LanPairingCoordinator(RemoteManager manager) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private PairingHost? _host;
    private PairingClient? _client;
    private LanDiscovery? _discovery;
    private CancellationTokenSource? _stop;
    private long _generation;
    public event Action? Changed;
    public string Status { get; private set; } = "stopped";
    public string DiscoveryError { get; private set; } = "";
    public string Code => _host?.Code ?? "";
    public DateTimeOffset? ExpiresAt => _host?.ExpiresAt;
    public bool Active => _stop is { IsCancellationRequested: false };
    public IReadOnlyList<DiscoveredPeer> Candidates => _discovery?.Candidates ?? Array.Empty<DiscoveredPeer>();

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
            if (!manager.Running) throw new InvalidOperationException("remote_disabled");
            if (!IPAddress.TryParse(new Uri(manager.Settings.PublicEndpoint).Host, out var address) || !LocalNetworkChoices.Eligible(address, false)) throw new InvalidOperationException("select_lan_address");
            if (manager.Settings.ListenAddress != address.ToString() && manager.Settings.ListenAddress != "0.0.0.0") throw new InvalidOperationException("listener_address_mismatch");
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network => network.OperationalStatus == OperationalStatus.Up && network.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(address))) ?? throw new InvalidOperationException("network_unavailable");
            var index = adapter.GetIPProperties().GetIPv4Properties()?.Index ?? throw new InvalidOperationException("network_unavailable");
            _stop = new CancellationTokenSource();
            var generation = ++_generation;
            _host = await PairingHost.StartAsync(address, ProvideBundleAsync, limiter: PairingAttemptLimiter.Shared, cancellationToken: _stop.Token);
            _host.StateChanged += state => { if (generation == Interlocked.Read(ref _generation)) { Status = state; Notify(); } };
            Status = "listening"; DiscoveryError = "";
            _discovery = new LanDiscovery((uint)index);
            _discovery.CandidatesChanged += (_, _) => { if (generation == Interlocked.Read(ref _generation)) Notify(); };
            try { await _discovery.StartAsync(_host.Port, _host.SessionId, "Fy1Switch-" + _host.SessionId[..8], address, _stop.Token); }
            catch (Exception exception)
            {
                DiscoveryError = exception is DnsSdException native ? "discovery_" + native.NativeStatus : "discovery_unavailable";
                await _host.DisposeAsync(); _host = null;
                _stop.Cancel();
                Status = "discovery_unavailable";
            }
            if (_host is { } active) _ = ObserveCompletionAsync(active, generation);
            Notify();
        }
        finally { _lifecycle.Release(); }
    }
    private async Task ObserveCompletionAsync(PairingHost host, long generation)
    {
        var completed = await host.Completion;
        if (generation != Interlocked.Read(ref _generation)) return;
        Status = completed ? "completed" : host.Status;
        try { if (_discovery is not null) await _discovery.StopAsync(); }
        catch { DiscoveryError = "discovery_stop_failed"; }
        Notify();
    }
    private Task<PairBundle> ProvideBundleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(manager.BeginPairing());
    }
    public async Task<bool> ConnectAsync(DiscoveredPeer peer, string code)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!Active || !Candidates.Any(candidate => candidate == peer)) throw new InvalidOperationException("peer_expired");
            var stop = _stop!;
            ++_generation;
            if (_host is not null) { await _host.DisposeAsync(); _host = null; }
            if (_discovery is not null) await _discovery.StopAsync();
            using var client = new PairingClient(PairingAttemptLimiter.Shared);
            _client = client;
            client.StateChanged += state => { Status = state; Notify(); };
            var result = await client.ConnectAsync(peer.Address, peer.Port, peer.SessionId, code, ProvideBundleAsync, stop.Token);
            Status = result ? "completed" : "failed";
            _client = null;
            Notify();
            return result;
        }
        finally { _client = null; _lifecycle.Release(); }
    }
    public void Cancel()
    {
        _stop?.Cancel(); _host?.Stop(); _client?.Stop();
    }
    public async Task StopAsync()
    {
        Cancel();
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); Status = "stopped"; Notify(); }
        finally { _lifecycle.Release(); }
    }
    private async Task StopCoreAsync()
    {
        ++_generation;
        _stop?.Cancel(); _client?.Stop(); _client = null;
        try { if (_discovery is not null) await _discovery.DisposeAsync(); }
        finally
        {
            _discovery = null;
            if (_host is not null) { await _host.DisposeAsync(); _host = null; }
            _stop?.Dispose(); _stop = null;
        }
    }
    private void Notify() { try { Changed?.Invoke(); } catch { } }
    public async ValueTask DisposeAsync() => await StopAsync();
}
