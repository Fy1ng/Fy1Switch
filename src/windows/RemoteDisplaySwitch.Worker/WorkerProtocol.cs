using System.Text.Json;
using RemoteDisplaySwitch.Contracts;

namespace RemoteDisplaySwitch.Worker;

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
        if (request.Command is not ("discover" or "read" or "set")) return new(request.RequestId, false, "unknown_command");
        if (request.Command is "read" or "set" && (!IsHash(request.TargetKey) || !IsHash(request.TopologyFingerprint)))
            return new(request.RequestId, false, "invalid_request", WriteAccepted: request.Command == "set" ? false : null);
        if (request.Command == "set" && request.Intent is not (PowerIntent.On or PowerIntent.Off))
            return new(request.RequestId, false, "invalid_intent", WriteAccepted: false);

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
                default:
                    var result = await provider.SetAsync(request.TargetKey!, request.TopologyFingerprint!, request.Intent!.Value, cancellationToken);
                    return new(request.RequestId, result.WriteAccepted,
                        !result.WriteAccepted ? "power_write_failed" : result.Observation.State == DisplayPower.Unknown ? "power_read_failed" : null,
                        Observation: result.Observation, WriteAccepted: result.WriteAccepted);
            }
        }
        catch (DisplayProviderException exception)
        {
            return new(request.RequestId, false, exception.ErrorCode, WriteAccepted: request.Command == "set" ? exception.WriteAccepted : null);
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
