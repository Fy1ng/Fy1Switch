using System.Net.WebSockets;
using System.Text;
using Fy1Switch.Contracts;

namespace Fy1Switch.Remote;

public sealed class RelayConnection(RemoteGateway gateway, string relayUrl, string token) : IAsyncDisposable
{
    internal Func<ClientWebSocket> SocketFactory { get; init; } = () => new ClientWebSocket();
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    public string Status { get; private set; } = "disconnected";
    public void Start() => _run = RunAsync(_stop.Token);
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var origin = RemoteSettings.RequireUrl(relayUrl, "https");
        var endpoint = new UriBuilder(origin) { Scheme = "wss", Path = $"/v1/devices/{gateway.DeviceId}/connect" }.Uri;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = SocketFactory();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                Status = "connecting";
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(endpoint, connect.Token);
                Status = "connected";
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var text = await ReceiveAsync(socket, cancellationToken);
                    if (text is null) break;
                    var envelope = WireJson.Parse<RelayEnvelope>(text);
                    RelayResponse result;
                    if (envelope.Id == Guid.Empty) result = new(envelope.Id, 400, ErrorCode: "invalid_request");
                    else if (envelope.Kind == "pair" && envelope.Pair is not null)
                    {
                        var reply = gateway.Pair(envelope.Pair);
                        result = new(envelope.Id, reply is null ? 401 : 200, Pair: reply, ErrorCode: reply is null ? "pairing_rejected" : null);
                    }
                    else if (envelope.Kind == "control" && envelope.Request is not null)
                    {
                        var reply = await gateway.ControlAsync(envelope.DeviceToken, envelope.Request, cancellationToken);
                        result = new(envelope.Id, reply.StatusCode, Reply: reply.Reply);
                    }
                    else result = new(envelope.Id, 400, ErrorCode: "invalid_request");
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    send.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.SendAsync(Encoding.UTF8.GetBytes(WireJson.Serialize(result)), WebSocketMessageType.Text, true, send.Token);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { Status = "unavailable"; }
            Status = "disconnected";
            socket.Abort();
            try { await Task.Delay(TimeSpan.FromSeconds(5 + Random.Shared.Next(0, 5)), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
        Status = "disconnected";
    }
    private static async Task<string?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > 65536) throw new InvalidDataException("relay_message_invalid");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return new UTF8Encoding(false, true).GetString(stream.ToArray());
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_run is not null) await _run;
        _stop.Dispose();
    }
}
