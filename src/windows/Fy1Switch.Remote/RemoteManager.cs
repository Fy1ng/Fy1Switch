using Fy1Switch.Contracts;
using Fy1Switch.Runtime;

namespace Fy1Switch.Remote;

public sealed class RemoteManager : IAsyncDisposable
{
    private readonly RemoteSettingsStore _store;
    private readonly RemoteGateway _gateway;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private HttpsControlServer? _server;
    private RelayConnection? _relay;
    public DeviceSecurity Security { get; }
    public RemoteSettings Settings { get; private set; }
    public bool Running => _server is not null;
    public string RelayStatus => _relay?.Status ?? "disabled";
    public string TailscaleStatus => _server?.TailscaleStatus ?? "disabled";
    public string? LastError { get; private set; }
    public RemoteManager(IDesktopController controller, Guid deviceId, string directory, ISecretProtector? protector = null)
    {
        protector ??= new UserSecretProtector();
        _store = new(directory, protector);
        Security = new(directory, protector);
        _gateway = new(controller, Security, deviceId);
        Settings = _store.Load();
    }
    public PairBundle BeginPairing()
    {
        if (!Running) throw new InvalidOperationException("remote_disabled");
        return Security.BeginPairing(_gateway.DeviceId, Settings.PublicEndpoint) with { TailscaleEndpoint = _gateway.TailscaleEndpoint };
    }
    public string? ProtectedRelayToken(string? token) => string.IsNullOrWhiteSpace(token) ? null : _store.ProtectToken(token.Trim());
    public async Task ApplyAsync(RemoteSettings settings, bool persist = true)
    {
        settings.Validate();
        await _changes.WaitAsync();
        try
        {
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
                        var token = _store.RelayToken(settings);
                        if (string.IsNullOrWhiteSpace(token) || token.Length < 32) throw new ArgumentException("relay_token_missing");
                        _relay = new(_gateway, settings.RelayUrl!, token); _relay.Start();
                    }
                }
                if (persist) _store.Save(settings);
            }
            catch
            {
                LastError = "remote_start_failed";
                if (_relay is not null) { await _relay.DisposeAsync(); _relay = null; }
                if (_server is not null) { await _server.DisposeAsync(); _server = null; }
                throw;
            }
        }
        finally { _changes.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _changes.WaitAsync();
        try
        {
            if (_relay is not null) await _relay.DisposeAsync();
            if (_server is not null) await _server.DisposeAsync();
            Security.Dispose();
        }
        finally { _changes.Release(); }
    }
}
