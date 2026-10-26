using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fy1Switch.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<PowerIntent>))]
public enum PowerIntent { On, Off, Toggle }
[JsonConverter(typeof(JsonStringEnumConverter<DisplayPower>))]
public enum DisplayPower { Unknown, On, Off }
[JsonConverter(typeof(JsonStringEnumConverter<OperationOutcome>))]
public enum OperationOutcome { Pending, Confirmed, AcceptedUnconfirmed, Failed, Unknown }

public sealed record PowerObservation(DisplayPower State, int? RawValue, DateTimeOffset ObservedAt, string Source);
public sealed record MonitorCandidate(string TargetKey, string Name, string Profile);
public sealed record DiscoverySnapshot(string TopologyFingerprint, IReadOnlyList<MonitorCandidate> Candidates, DateTimeOffset CapturedAt);
public sealed record MonitorBinding(Guid TargetId, string TargetKey, string Name, string Profile, string TopologyFingerprint, bool NeedsConfirmation = false);
public sealed record OperationRecord(Guid OperationId, Guid TargetId, PowerIntent Requested, PowerIntent? Resolved, OperationOutcome Outcome, string? ErrorCode, PowerObservation? Observation, DateTimeOffset UpdatedAt);
public sealed record HostSnapshot(Guid DeviceId, MonitorBinding? Binding, PowerObservation Observation, bool WorkerRunning, bool Busy, bool StartupEnabled, string? ErrorCode, bool Simulation);
public sealed record WorkerRequest(Guid RequestId, string Command, string? TargetKey = null, string? TopologyFingerprint = null, PowerIntent? Intent = null, int Version = 1, DisplayControl? Control = null, int? Value = null);
public sealed record WorkerReply(Guid RequestId, bool Success, string? ErrorCode = null, DiscoverySnapshot? Discovery = null, PowerObservation? Observation = null, bool? WriteAccepted = null, int Version = 1, DisplayControlsSnapshot? Controls = null, DisplayControlWriteResult? ControlWrite = null);
public sealed record LocalRequest(Guid RequestId, string Command, Guid? TargetId = null, string? TargetKey = null, string? TopologyFingerprint = null, string? Name = null, PowerIntent? Intent = null, Guid? OperationId = null, bool? Enabled = null, int Version = 1);
public sealed record LocalReply(Guid RequestId, bool Success, string? ErrorCode = null, HostSnapshot? Snapshot = null, DiscoverySnapshot? Discovery = null, OperationRecord? Operation = null, int Version = 1);

public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Empty message.");

    public static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken, int limit = 65536)
    {
        var text = new System.Text.StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character, cancellationToken) != 0)
        {
            if (character[0] == (char)10) return text.ToString();
            if (character[0] != (char)13) text.Append(character[0]);
            if (text.Length > limit) throw new InvalidDataException("Message exceeds limit.");
        }
        return text.Length == 0 ? null : throw new InvalidDataException("Incomplete message.");
    }
}
