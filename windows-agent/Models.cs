using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FyDisplayAgent;

public sealed class AgentConfig
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = $"fy-pc-{Environment.MachineName.ToLowerInvariant()}";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; } = 27120;
    [JsonPropertyName("monitor_id")] public string MonitorId { get; set; } = "";
    [JsonPropertyName("monitor_number")] public int MonitorNumber { get; set; } = 1;
    [JsonPropertyName("off_power_state")] public string OffPowerState { get; set; } = "0x04";
    [JsonPropertyName("on_power_state")] public string OnPowerState { get; set; } = "0x01";
    [JsonPropertyName("last_power_state")] public string LastPowerState { get; set; } = "";
    [JsonPropertyName("relay_url")] public string RelayUrl { get; set; } = "";
    [JsonPropertyName("relay_poll_seconds")] public int RelayPollSeconds { get; set; } = 25;

    // Kept only so v0.2 config files deserialize cleanly. It is no longer used.
    [JsonPropertyName("powertoys_cli")] public string LegacyPowerToysCli { get; set; } = "";
}

public sealed class ApiResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("display_state")] public string? DisplayState { get; set; }
    [JsonPropertyName("power_state")] public string? PowerState { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class RelayCommand
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
}

public sealed class RelayResult
{
    [JsonPropertyName("command_id")] public string CommandId { get; set; } = "";
    [JsonPropertyName("result")] public ApiResponse Result { get; set; } = new();
}

public sealed class ConfigStore
{
    public string PathOnDisk { get; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();

    public ConfigStore(string path) => PathOnDisk = path;

    public (AgentConfig Config, bool Created) LoadOrCreate()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk)!);
        if (!File.Exists(PathOnDisk))
        {
            var cfg = Defaults(new AgentConfig());
            Save(cfg);
            return (cfg, true);
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(PathOnDisk), JsonOptions) ?? new AgentConfig();
            cfg = Defaults(cfg);
            Save(cfg);
            return (cfg, false);
        }
        catch
        {
            var backup = PathOnDisk + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(PathOnDisk, backup, true); } catch { }
            var cfg = Defaults(new AgentConfig());
            Save(cfg);
            return (cfg, true);
        }
    }

    public void Save(AgentConfig cfg)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk)!);
            File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(cfg, JsonOptions));
        }
    }

    private static AgentConfig Defaults(AgentConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.DeviceId)) cfg.DeviceId = $"fy-pc-{Environment.MachineName.ToLowerInvariant()}";
        if (string.IsNullOrWhiteSpace(cfg.Token)) cfg.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        if (cfg.Port is < 1 or > 65535) cfg.Port = 27120;
        if (cfg.MonitorNumber < 1) cfg.MonitorNumber = 1;
        if (string.IsNullOrWhiteSpace(cfg.OffPowerState)) cfg.OffPowerState = "0x04";
        if (string.IsNullOrWhiteSpace(cfg.OnPowerState)) cfg.OnPowerState = "0x01";
        if (cfg.RelayPollSeconds is < 1 or > 30) cfg.RelayPollSeconds = 25;
        cfg.RelayUrl = cfg.RelayUrl?.Trim().TrimEnd('/') ?? "";
        return cfg;
    }
}
