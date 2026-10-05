using System.Net.WebSockets;
using System.Text.Json;
using RemoteDisplaySwitch.Contracts;

namespace RemoteDisplaySwitch.Relay;

public sealed record RelayResult(int StatusCode, object Body)
{
    public static RelayResult Error(int status, string code) => new(status, new { errorCode = code });
}

public sealed class RelayRouter
{
    public const int MessageLimit = 64 * 1024;
    public const int PendingLimit = 16;
    private readonly Dictionary<Guid, DeviceRoute> routes;
    private readonly RelaySettings settings;

    public RelayRouter(RelaySettings settings)
    {
        this.settings = settings;
        routes = settings.DeviceIds.ToDictionary(deviceId => deviceId, deviceId => new DeviceRoute());
    }

    public RelayConnection? Reserve(Guid deviceId)
    {
        if (!routes.TryGetValue(deviceId, out var route)) return null;
        lock (route.Gate)
        {
            if (route.Connection is not null) return null;
            return route.Connection = new RelayConnection(route);
        }
    }

    public async Task<RelayResult> ForwardAsync(Guid deviceId, RelayEnvelope envelope, CancellationToken requestAborted)
    {
        var now = DateTimeOffset.UtcNow;
        var deadline = now + settings.RequestTimeout;
        if (envelope.Request is { } command && command.ExpiresAt < deadline) deadline = command.ExpiresAt;
        if (deadline <= now) return RelayResult.Error(504, "deadline_expired");
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, RelayJson.Options);
        if (payload.Length > MessageLimit) return RelayResult.Error(413, "message_too_large");
        var route = routes[deviceId];
        RelayConnection connection;
        var pending = new TaskCompletionSource<RelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (route.Gate)
        {
            foreach (var expired in route.RecentRequests.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
                route.RecentRequests.Remove(expired);
            if (envelope.Request is not null && route.RecentRequests.ContainsKey(envelope.Id))
                return RelayResult.Error(409, "request_replayed");
            if (route.Connection is not { Ready: true } active) return RelayResult.Error(503, "device_offline");
            connection = active;
            if (connection.Pending.Count >= PendingLimit) return RelayResult.Error(429, "too_many_pending");
            if (envelope.Request is not null && route.RecentRequests.Count >= 4096)
                return RelayResult.Error(429, "replay_window_full");
            if (!connection.Pending.TryAdd(envelope.Id, pending)) return RelayResult.Error(409, "request_replayed");
            if (envelope.Request is not null) route.RecentRequests.Add(envelope.Id, now.AddSeconds(120));
        }

        using var timeout = new CancellationTokenSource();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, connection.Closed, timeout.Token);
        try
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return RelayResult.Error(504, "deadline_expired");
            timeout.CancelAfter(remaining);
            await connection.SendAsync(payload, deadline, cancellation.Token);
            var response = await pending.Task.WaitAsync(cancellation.Token);
            if (DateTimeOffset.UtcNow >= deadline) return RelayResult.Error(504, "deadline_expired");
            return ValidateResponse(deviceId, envelope, response);
        }
        catch (OperationCanceledException)
        {
            if (requestAborted.IsCancellationRequested) throw;
            return DateTimeOffset.UtcNow >= deadline || timeout.IsCancellationRequested
                ? RelayResult.Error(504, "deadline_expired")
                : RelayResult.Error(503, "device_disconnected");
        }
        catch (WebSocketException) { return RelayResult.Error(503, "device_disconnected"); }
        finally
        {
            lock (route.Gate) connection.Pending.Remove(envelope.Id);
        }
    }

    private static RelayResult ValidateResponse(Guid deviceId, RelayEnvelope envelope, RelayResponse response)
    {
        if (response.StatusCode is < 200 or > 599 or 204 or 205 or 304 ||
            (response.Reply is not null && response.Pair is not null))
            return RelayResult.Error(502, "invalid_device_response");
        if (envelope.Request is { } command && response.Reply is { } reply && response.Pair is null)
        {
            if (reply.RequestId != command.RequestId || reply.Version != 1 ||
                (reply.Status is { } status && status.DeviceId != deviceId))
                return RelayResult.Error(502, "invalid_device_response");
            return new RelayResult(response.StatusCode, reply);
        }
        if (envelope.Kind == "pair" && response.Pair is { } pair && response.Reply is null && pair.DeviceId == deviceId)
            return new RelayResult(response.StatusCode, pair);
        if (response.StatusCode >= 400 && response.Reply is null && response.Pair is null && !string.IsNullOrEmpty(response.ErrorCode))
            return RelayResult.Error(response.StatusCode, response.ErrorCode);
        return RelayResult.Error(502, "invalid_device_response");
    }

    public sealed class DeviceRoute
    {
        public object Gate { get; } = new();
        public RelayConnection? Connection { get; set; }
        public Dictionary<Guid, DateTimeOffset> RecentRequests { get; } = new();
    }

    public sealed class RelayConnection(DeviceRoute route) : IDisposable
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly CancellationTokenSource closed = new();
        private readonly TaskCompletionSource<WebSocket> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocket? socket;
        public Dictionary<Guid, TaskCompletionSource<RelayResponse>> Pending { get; } = new();
        public CancellationToken Closed => closed.Token;
        public bool Ready => !closed.IsCancellationRequested;

        public async Task RunAsync(WebSocket webSocket, CancellationToken requestAborted)
        {
            lock (route.Gate)
            {
                socket = webSocket;
                accepted.TrySetResult(webSocket);
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, closed.Token);
            var buffer = new byte[MessageLimit + 1];
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var length = 0;
                    ValueWebSocketReceiveResult received;
                    do
                    {
                        received = await webSocket.ReceiveAsync(buffer.AsMemory(length), cancellation.Token);
                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            await CloseAsync(WebSocketCloseStatus.NormalClosure);
                            return;
                        }
                        if (received.MessageType != WebSocketMessageType.Text)
                        {
                            await CloseAsync(WebSocketCloseStatus.InvalidMessageType);
                            return;
                        }
                        length += received.Count;
                        if (length > MessageLimit)
                        {
                            await CloseAsync(WebSocketCloseStatus.MessageTooBig);
                            return;
                        }
                    } while (!received.EndOfMessage);

                    RelayResponse response;
                    try
                    {
                        response = JsonSerializer.Deserialize<RelayResponse>(buffer.AsSpan(0, length), RelayJson.Options) ?? throw new JsonException();
                    }
                    catch (JsonException)
                    {
                        await CloseAsync(WebSocketCloseStatus.InvalidPayloadData);
                        return;
                    }
                    lock (route.Gate)
                    {
                        if (Pending.TryGetValue(response.Id, out var pending)) pending.TrySetResult(response);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally { Dispose(); }
        }

        public async Task SendAsync(byte[] payload, DateTimeOffset deadline, CancellationToken cancellationToken)
        {
            var webSocket = await accepted.Task.WaitAsync(cancellationToken);
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline) throw new OperationCanceledException(cancellationToken);
                try { await webSocket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, cancellationToken); }
                catch (Exception error) when (error is OperationCanceledException or WebSocketException)
                {
                    Dispose();
                    throw;
                }
            }
            finally { sendLock.Release(); }
        }

        private async Task CloseAsync(WebSocketCloseStatus status)
        {
            closed.Cancel();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await sendLock.WaitAsync(timeout.Token);
                try { await socket!.CloseAsync(status, "relay_connection_closed", timeout.Token); }
                finally { sendLock.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }

        public void Dispose()
        {
            lock (route.Gate)
            {
                closed.Cancel();
                Pending.Clear();
                socket?.Abort();
                if (ReferenceEquals(route.Connection, this)) route.Connection = null;
            }
        }
    }
}
