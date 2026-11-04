using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fy1Switch.Contracts;

public sealed record RemoteDisplayControls(Guid TargetId, IReadOnlyList<DisplayControlState> Controls,
    IReadOnlyList<VcpCodeDescriptor> VcpCodes, DateTimeOffset ObservedAt);

public sealed record DisplayControlOperation(Guid OperationId, Guid TargetId, DisplayControl Control, int RequestedValue,
    OperationOutcome Outcome, string? ErrorCode, DisplayControlWriteResult? Result, DateTimeOffset UpdatedAt);

// Remote input has a strict string-only representation without changing the local Worker protocol.
public sealed class RemoteControlConverter : JsonConverter<DisplayControl?>
{
    public override DisplayControl? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("invalid_control");
        var name = reader.GetString();
        if (!Enum.TryParse<DisplayControl>(name, out var value) || !Enum.IsDefined(value) || name != value.ToString())
            throw new JsonException("invalid_control");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, DisplayControl? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else if (Enum.IsDefined(value.Value)) writer.WriteStringValue(value.Value.ToString());
        else throw new JsonException("invalid_control");
    }
}
