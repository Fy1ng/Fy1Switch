using Fy1Switch.Contracts;
using Fy1Switch.Runtime;

namespace Fy1Switch.Remote;

public sealed class RemoteManager : IAsyncDisposable
{
    private readonly RemoteSettingsStore _store;
    private readonly RemoteGateway _gateway;
    private readonly RelayApiClient _relayApi;
    private readonly bool _ownsRelayApi;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private long _relayPairGeneration;
    private bool _relayCodeRegistered;
    private HttpsControlServer? _server;
    private RelayConnection? _relay;
    internal Func<RemoteGateway, string, string, RelayConnection> RelayClientFactory { get; init; } = (gateway, endpoint, token) => new(gateway, endpoint, token);
    public DeviceSecurity Security { get; }
    public RemoteSettings Settings { get; private set; }
    public bool Running => _server is not null;
    public string RelayStatus => _relay?.Status ?? "disabled";
    public string TailscaleStatus => _server?.TailscaleStatus ?? "disabled";
    public string? LastError { get; private set; }
    public RemoteManager(IDesktopController controller, Guid deviceId, string directory, ISecretProtector? protector = null, RelayApiClient? relayApi = null)
    {
        protector ??= new UserSecretProtector();
        _store = new(directory, protector);
        Security = new(directory, protector);
        _gateway = new(controller, Security, deviceId);
        _ownsRelayApi = relayApi is null;
        _relayApi = relayApi ?? new();
        _gateway.IssueRelayController = IssueRelayControllerAsync;
        Settings = _store.Load();
    }
    public PairBundle BeginPairing()
    {
        if (!Running) throw new InvalidOperationException("remote_disabled");
        return Security.BeginPairing(_gateway.DeviceId, Settings.PublicEndpoint) with { TailscaleEndpoint = _gateway.TailscaleEndpoint };
    }
    public string? ProtectedRelayToken(string? token) => string.IsNullOrWhiteSpace(token) ? null : _store.ProtectToken(token.Trim());
    public async Task RegisterRelayAsync(string endpoint, string code, CancellationToken cancellationToken = default)
        => await RegisterRelayAsync(Settings with { RelayUrl = endpoint.TrimEnd('/'), RelayEnabled = true }, code, cancellationToken);
    public async Task RegisterRelayAsync(RemoteSettings settings, string code, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        var endpoint = settings.RelayUrl ?? throw new ArgumentException("invalid_endpoint");
        RemoteSettings.RequireUrl(endpoint, "https");
        var reply = await _relayApi.RegisterAsync(endpoint, _gateway.DeviceId, code, Environment.MachineName, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ApplyAsync(settings with { RelayUrl = endpoint.TrimEnd('/'), ProtectedRelayToken = _store.ProtectToken(reply.WindowsToken), RelayEnabled = true });
    }
    public async Task<RelayCode> BeginRelayPairingAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _relayPairGeneration);
        Security.CancelRelayPairing();
        await _changes.WaitAsync(cancellationToken);
        try
        {
            if (generation != Interlocked.Read(ref _relayPairGeneration)) throw new OperationCanceledException();
            if (_relay is null || _gateway.RelayEndpoint is null) throw new InvalidOperationException("relay_not_registered");
            await CancelRegisteredRelayCodeAsync();
            await _relayApi.SynchronizeControllersAsync(Settings.RelayUrl!, _gateway.DeviceId, _store.RelayToken(Settings)!, Security.Clients.Select(client => client.ClientId).ToArray(), cancellationToken);
            if (generation != Interlocked.Read(ref _relayPairGeneration)) throw new OperationCanceledException();
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var code = Security.BeginRelayPairing();
                try
                {
                    await _relayApi.RegisterPairingCodeAsync(Settings.RelayUrl!, _gateway.DeviceId, _store.RelayToken(Settings)!, code, cancellationToken);
                    _relayCodeRegistered = true;
                    if (generation != Interlocked.Read(ref _relayPairGeneration))
                    {
                        Security.CancelRelayPairing();
                        await CancelRegisteredRelayCodeAsync();
                        throw new OperationCanceledException();
                    }
                    return code;
                }
                catch (RelayApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict && attempt < 3)
                {
                    Security.CancelRelayPairing();
                }
            }
            throw new InvalidOperationException("relay_pairing_failed");
        }
        catch { Security.CancelRelayPairing(); throw; }
        finally { _changes.Release(); }
    }
    public async Task CancelRelayPairingAsync()
    {
        Interlocked.Increment(ref _relayPairGeneration);
        Security.CancelRelayPairing();
        await _changes.WaitAsync();
        try { await CancelRegisteredRelayCodeAsync(); }
        finally { _changes.Release(); }
    }
    private async Task CancelRegisteredRelayCodeAsync()
    {
        if (!_relayCodeRegistered) return;
        _relayCodeRegistered = false;
        try
        {
            if (Settings.RelayUrl is { } endpoint && _store.RelayToken(Settings) is { } token)
                await _relayApi.CancelPairingCodeAsync(endpoint, _gateway.DeviceId, token);
        }
        catch (Exception exception) when (exception is RelayApiException or HttpRequestException or OperationCanceledException) { }
    }
    private async Task<RelayControllerReply> IssueRelayControllerAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (_relay is null || !settings.Enabled || !settings.UseRelay || settings.RelayUrl is not { } endpoint || _store.RelayToken(settings) is not { } token)
            throw new InvalidDataException("relay_not_registered");
        await _relayApi.SynchronizeControllersAsync(endpoint, _gateway.DeviceId, token, Security.Clients.Select(client => client.ClientId).ToArray(), cancellationToken);
        return await _relayApi.CreateControllerAsync(endpoint, _gateway.DeviceId, token, clientId, cancellationToken);
    }
    public async Task ApplyAsync(RemoteSettings settings, bool persist = true)
    {
        settings.Validate();
        var relayToken = settings.Enabled && settings.UseRelay ? _store.RelayToken(settings) : null;
        if (settings.Enabled && settings.UseRelay && (string.IsNullOrWhiteSpace(relayToken) || relayToken.Length < 32)) throw new ArgumentException("relay_token_missing");
        Interlocked.Increment(ref _relayPairGeneration);
        Security.CancelRelayPairing();
        await _changes.WaitAsync();
        try
        {
            await CancelRegisteredRelayCodeAsync();
            _gateway.RelayEndpoint = null;
            Security.CancelPairing();
            if (_relay is not null) { await _relay.DisposeAsync(); _relay = null; }
            if (_server is not null) { await _server.DisposeAsync(); _server = null; }
            Settings = settings;
            LastError = null;
            try
            {
                if (settings.Enabled)
                {
                    var server = new HttpsControlServer(_gateway, Security);
                    await server.StartAsync(settings);
                    _server = server;
                    if (settings.UseRelay)
                    {
                        _gateway.RelayEndpoint = settings.RelayUrl!.TrimEnd('/');
                        _relay = RelayClientFactory(_gateway, settings.RelayUrl!, relayToken!); _relay.Start();
                    }
                }
                if (persist) _store.Save(settings);
            }
            catch
            {
                LastError = "remote_start_failed";
                _gateway.RelayEndpoint = null;
                if (_relay is not null) { await _relay.DisposeAsync(); _relay = null; }
                if (_server is not null) { await _server.DisposeAsync(); _server = null; }
                throw;
            }
        }
        finally { _changes.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _relayPairGeneration);
        Security.CancelRelayPairing();
        await _changes.WaitAsync();
        try
        {
            await CancelRegisteredRelayCodeAsync();
            _gateway.RelayEndpoint = null;
            if (_relay is not null) await _relay.DisposeAsync();
            if (_server is not null) await _server.DisposeAsync();
            Security.Dispose();
            if (_ownsRelayApi) _relayApi.Dispose();
        }
        finally { _changes.Release(); }
    }
}
