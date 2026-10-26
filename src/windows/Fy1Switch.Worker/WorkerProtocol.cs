using System.Text.Json;
using Fy1Switch.Contracts;

namespace Fy1Switch.Worker;

internal sealed class WorkerProtocol(IDisplayProvider provider)
{
    public async Task RunAsync(StreamReader reader, StreamWriter writer, CancellationToken cancellationToken = default)
    {
        while (await WireJson.ReadLineAsync(reader, cancellationToken) is { } line)
        {
            WorkerRequest? request = null;
            WorkerReply reply;
            var requestId = Guid.Empty;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
                if (root.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String)
                    id.TryGetGuid(out requestId);
                var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root.EnumerateObject().Any(property => !properties.Add(property.Name)) ||
                    requestId == Guid.Empty || !root.TryGetProperty("version", out var version) ||
                    !version.TryGetInt32(out var versionNumber)) throw new JsonException();
                if (versionNumber != 1)
                {
                    reply = new(requestId, false, "unsupported_version");
                }
                else
                {
                    request = WireJson.Parse<WorkerRequest>(line);
                    reply = await HandleAsync(request, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                reply = new(requestId, false, "invalid_request");
            }

            await writer.WriteLineAsync(WireJson.Serialize(reply).AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            if (request?.Command == "shutdown" && reply.Success) return;
        }
    }

    internal async Task<WorkerReply> HandleAsync(WorkerRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Version != 1) return new(request.RequestId, false, "unsupported_version");
        if (request.RequestId == Guid.Empty) return new(request.RequestId, false, "invalid_request");
        if (request.Command == "shutdown") return new(request.RequestId, true);
        if (request.Command is not ("discover" or "read" or "set" or "read-controls" or "set-control")) return new(request.RequestId, false, "unknown_command");
        var writing = request.Command is "set" or "set-control";
        if (request.Command is "read" or "set" or "read-controls" or "set-control" && (!IsHash(request.TargetKey) || !IsHash(request.TopologyFingerprint)))
            return new(request.RequestId, false, "invalid_request", WriteAccepted: writing ? false : null,
                ControlWrite: request.Command == "set-control" ? new(false, null, false, "invalid_request") : null);
        if (request.Command == "set" && request.Intent is not (PowerIntent.On or PowerIntent.Off))
            return new(request.RequestId, false, "invalid_intent", WriteAccepted: false);
        if (request.Command == "set-control" && (request.Control is null || !Enum.IsDefined(request.Control.Value) || request.Value is null))
            return new(request.RequestId, false, "invalid_request", WriteAccepted: false, ControlWrite: new(false, null, false, "invalid_request"));

        try
        {
            switch (request.Command)
            {
                case "discover":
                    return new(request.RequestId, true, Discovery: await provider.DiscoverAsync(cancellationToken));
                case "read":
                    var observation = await provider.ReadAsync(request.TargetKey!, request.TopologyFingerprint!, cancellationToken);
                    return new(request.RequestId, observation.State != DisplayPower.Unknown,
                        observation.State == DisplayPower.Unknown ? "power_read_failed" : null, Observation: observation);
                case "read-controls":
                    return new(request.RequestId, true, Controls: await provider.ReadControlsAsync(request.TargetKey!, request.TopologyFingerprint!, cancellationToken));
                case "set-control":
                    var controlResult = await provider.SetControlAsync(request.TargetKey!, request.TopologyFingerprint!, request.Control!.Value, request.Value!.Value, cancellationToken);
                    return new(request.RequestId, controlResult.WriteAccepted == true, controlResult.ErrorCode,
                        WriteAccepted: controlResult.WriteAccepted, ControlWrite: controlResult);
                default:
                    var result = await provider.SetAsync(request.TargetKey!, request.TopologyFingerprint!, request.Intent!.Value, cancellationToken);
                    return new(request.RequestId, result.WriteAccepted,
                        !result.WriteAccepted ? "power_write_failed" : result.Observation.State == DisplayPower.Unknown ? "power_read_failed" : null,
                        Observation: result.Observation, WriteAccepted: result.WriteAccepted);
            }
        }
        catch (DisplayProviderException exception)
        {
            return new(request.RequestId, false, exception.ErrorCode, WriteAccepted: writing ? exception.WriteAccepted : null,
                ControlWrite: request.Command == "set-control" ? new(exception.WriteAccepted, null, false, exception.ErrorCode) : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            SafeWorkerLog.Write("provider_failed");
            return new(request.RequestId, false, "provider_failed");
        }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
