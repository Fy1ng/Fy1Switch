using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace FyDisplayAgent;

public sealed class AgentService : IDisposable
{
    private readonly ConfigStore _store;
    private readonly DdcMonitorController _ddc = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private readonly object _configGate = new();
    private WebApplication? _app;
    private Task? _relayTask;
    private volatile AgentConfig _config;
    private volatile bool _relayOnline;
    private volatile string _lastRelayMessage = "disabled";

    public event EventHandler? StatusChanged;
    public AgentConfig Config => _config;
    public bool RelayOnline => _relayOnline;
    public string LastRelayMessage => _lastRelayMessage;

    public AgentService(AgentConfig config, ConfigStore store)
    {
        _config = config;
        _store = store;
    }

    public void Start()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(_config.Port));
        var app = builder.Build();

        app.MapGet("/v1/status", async (HttpRequest req) => await HandleHttpAsync(req, "status"));
        app.MapGet("/v1/display", async (HttpRequest req) => await HandleHttpAsync(req, "status"));
        app.MapPost("/v1/display/on", async (HttpRequest req) => await HandleHttpAsync(req, "on"));
        app.MapPost("/v1/display/off", async (HttpRequest req) => await HandleHttpAsync(req, "off"));
        app.MapPost("/v1/display/toggle", async (HttpRequest req) => await HandleHttpAsync(req, "toggle"));
        app.MapGet("/v1/monitors", async (HttpRequest req) =>
        {
            if (!Authorized(req)) return Results.Json(new { ok = false, message = "unauthorized" }, statusCode: 401);
            try
            {
                var monitors = await Task.Run(() => _ddc.EnumerateMonitors());
                return Results.Json(new { ok = true, monitors });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, message = ex.Message }, statusCode: 502);
            }
        });

        app.StartAsync(_cts.Token).GetAwaiter().GetResult();
        _app = app;
        _relayTask = Task.Run(() => RelayLoopAsync(_cts.Token));
    }

    public void UpdateConfig(AgentConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DeviceId)) throw new ArgumentException("Device ID cannot be empty.");
        if (string.IsNullOrWhiteSpace(config.Token)) throw new ArgumentException("Token cannot be empty.");
        if (config.MonitorNumber < 1) config.MonitorNumber = 1;
        config.RelayUrl = config.RelayUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(config.OnPowerState)) config.OnPowerState = "0x01";
        if (string.IsNullOrWhiteSpace(config.OffPowerState)) config.OffPowerState = "0x04";

        lock (_configGate)
        {
            if (string.IsNullOrWhiteSpace(config.LastPowerState)) config.LastPowerState = _config.LastPowerState;
            _store.Save(config);
            _config = config;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<DdcMonitorInfo>> EnumerateMonitorsAsync()
        => Task.Run<IReadOnlyList<DdcMonitorInfo>>(() => _ddc.EnumerateMonitors());

    private bool Authorized(HttpRequest req)
        => string.Equals(req.Headers.Authorization.ToString(), "Bearer " + _config.Token, StringComparison.Ordinal);

    private async Task<IResult> HandleHttpAsync(HttpRequest req, string action)
    {
        if (!Authorized(req))
            return Results.Json(new ApiResponse { Ok = false, Message = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
        var result = await ExecuteAsync(action);
        return Results.Json(result, statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status502BadGateway);
    }

    public async Task<ApiResponse> ExecuteAsync(string action)
    {
        var cfg = _config;
        try
        {
            return action switch
            {
                "status" => await StatusAsync(cfg),
                "on" => await SetAndRespondAsync(cfg, cfg.OnPowerState, "display power-on command sent"),
                "off" => await SetAndRespondAsync(cfg, cfg.OffPowerState, "display power-off command sent"),
                "toggle" => await ToggleAsync(cfg),
                _ => new ApiResponse { Ok = false, DeviceId = cfg.DeviceId, Message = "unsupported action" }
            };
        }
        catch (Exception ex)
        {
            return new ApiResponse { Ok = false, DeviceId = cfg.DeviceId, Message = ex.Message };
        }
    }

    private async Task<ApiResponse> StatusAsync(AgentConfig cfg)
    {
        var (state, cached, note) = await ReadStateWithFallbackAsync(cfg);
        return ResponseForState(cfg, state, cached ? note ?? "cached state" : note);
    }

    private async Task<ApiResponse> ToggleAsync(AgentConfig cfg)
    {
        var (state, _, _) = await ReadStateWithFallbackAsync(cfg);
        return string.Equals(state, NormalizeState(cfg.OnPowerState), StringComparison.OrdinalIgnoreCase)
            ? await SetAndRespondAsync(cfg, cfg.OffPowerState, "display power-off command sent")
            : await SetAndRespondAsync(cfg, cfg.OnPowerState, "display power-on command sent");
    }

    private async Task<ApiResponse> SetAndRespondAsync(AgentConfig cfg, string state, string message)
    {
        var value = ParseState(state);
        await Task.Run(() => _ddc.SetPowerState(cfg.MonitorId, cfg.MonitorNumber, value));
        var normalized = NormalizeState(state);
        PersistLastState(normalized);
        return ResponseForState(cfg, normalized, message);
    }

    private async Task<(string State, bool Cached, string? Note)> ReadStateWithFallbackAsync(AgentConfig cfg)
    {
        try
        {
            var current = await Task.Run(() => _ddc.GetPowerState(cfg.MonitorId, cfg.MonitorNumber));
            var normalized = $"0x{current:X2}".ToLowerInvariant();
            PersistLastState(normalized);
            return (normalized, false, null);
        }
        catch (Exception ex)
        {
            var cached = NormalizeState(cfg.LastPowerState);
            if (IsKnownState(cached))
                return (cached, true, "DDC/CI read unavailable; using last known state. " + ex.Message);
            throw;
        }
    }

    private void PersistLastState(string state)
    {
        lock (_configGate)
        {
            if (string.Equals(_config.LastPowerState, state, StringComparison.OrdinalIgnoreCase)) return;
            _config.LastPowerState = state;
            _store.Save(_config);
        }
    }

    private static ApiResponse ResponseForState(AgentConfig cfg, string state, string? message)
    {
        var normalized = NormalizeState(state);
        return new ApiResponse
        {
            Ok = true,
            DeviceId = cfg.DeviceId,
            DisplayState = string.Equals(normalized, NormalizeState(cfg.OnPowerState), StringComparison.Ordinal) ? "on" : "off",
            PowerState = normalized,
            Message = message
        };
    }

    private static uint ParseState(string state)
    {
        var normalized = state.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) normalized = normalized[2..];
        if (!uint.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out var value))
            throw new ArgumentException("Invalid DDC power-state value: " + state);
        return value;
    }

    private static string NormalizeState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return "";
        try { return $"0x{ParseState(state):X2}".ToLowerInvariant(); }
        catch { return state.Trim().ToLowerInvariant(); }
    }

    private static bool IsKnownState(string state)
        => state is "0x01" or "0x02" or "0x03" or "0x04" or "0x05";

    private async Task RelayLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _config;
            if (string.IsNullOrWhiteSpace(cfg.RelayUrl))
            {
                SetRelay(false, "disabled");
                await DelaySafe(1500, ct);
                continue;
            }

            try
            {
                var wait = Math.Clamp(cfg.RelayPollSeconds, 1, 30);
                var url = $"{cfg.RelayUrl.TrimEnd('/')}/v1/agent/poll?device_id={Uri.EscapeDataString(cfg.DeviceId)}&wait={wait}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
                req.Headers.UserAgent.ParseAdd("FyDisplayAgent/0.3");
                using var resp = await _http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.NoContent)
                {
                    SetRelay(true, "connected");
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Relay HTTP {(int)resp.StatusCode}: {body.Trim()}");
                var cmd = JsonSerializer.Deserialize<RelayCommand>(body) ?? throw new InvalidOperationException("Relay returned an invalid command.");
                if (string.IsNullOrWhiteSpace(cmd.Id) || string.IsNullOrWhiteSpace(cmd.Action))
                    throw new InvalidOperationException("Relay returned an incomplete command.");

                SetRelay(true, "connected");
                var result = await ExecuteAsync(cmd.Action);
                await PostRelayResultAsync(cfg, cmd.Id, result, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                SetRelay(false, ex.Message);
                await DelaySafe(3000, ct);
            }
        }
    }

    private async Task PostRelayResultAsync(AgentConfig cfg, string commandId, ApiResponse result, CancellationToken ct)
    {
        var url = $"{cfg.RelayUrl.TrimEnd('/')}/v1/agent/result?device_id={Uri.EscapeDataString(cfg.DeviceId)}";
        var json = JsonSerializer.Serialize(new RelayResult { CommandId = commandId, Result = result });
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Relay result HTTP {(int)resp.StatusCode}: {(await resp.Content.ReadAsStringAsync(ct)).Trim()}");
    }

    private void SetRelay(bool online, string message)
    {
        var changed = _relayOnline != online || _lastRelayMessage != message;
        _relayOnline = online;
        _lastRelayMessage = message;
        if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task DelaySafe(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _app?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch { }
        try { _relayTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        if (_app is not null)
        {
            try { _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        _http.Dispose();
        _cts.Dispose();
    }
}
