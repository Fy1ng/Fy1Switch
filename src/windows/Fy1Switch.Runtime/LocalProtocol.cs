using System.IO.Pipes;
using System.Text;
using Fy1Switch.Contracts;

namespace Fy1Switch.Runtime;

public static class LocalDispatcher
{
    public static async Task<LocalReply> DispatchAsync(IDesktopController controller, LocalRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Version != 1 || request.RequestId == Guid.Empty) return new(request.RequestId, false, "protocol_version_or_id_invalid");
        try
        {
            return request.Command switch
            {
                "status" => new(request.RequestId, true, Snapshot: await controller.GetSnapshotAsync(cancellationToken)),
                "discover" => new(request.RequestId, true, Discovery: await controller.DiscoverAsync(cancellationToken)),
                "bind" when request.TargetKey is not null && request.TopologyFingerprint is not null => new(request.RequestId, true, Snapshot: await controller.BindAsync(request.TargetKey, request.TopologyFingerprint, request.Name ?? "显示器", cancellationToken)),
                "unbind" => new(request.RequestId, true, Snapshot: await controller.UnbindAsync(cancellationToken)),
                "refresh" => new(request.RequestId, true, Snapshot: await controller.RefreshAsync(cancellationToken)),
                "set" when request.TargetId is not null && request.Intent is not null && request.OperationId is not null => new(request.RequestId, true, Operation: await controller.ExecuteAsync(request.TargetId.Value, request.Intent.Value, request.OperationId.Value, cancellationToken)),
                "operation" when request.OperationId is not null => new(request.RequestId, true, Operation: await controller.GetOperationAsync(request.OperationId.Value, cancellationToken)),
                "startup" when request.Enabled is not null => new(request.RequestId, true, Snapshot: await controller.SetStartupAsync(request.Enabled.Value, cancellationToken)),
                _ => new(request.RequestId, false, "invalid_request"),
            };
        }
        catch (ControlException exception) { return new(request.RequestId, false, exception.Code); }
        catch (OperationCanceledException) { return new(request.RequestId, false, "request_cancelled"); }
        catch { return new(request.RequestId, false, "internal_error"); }
    }
}

public sealed class LocalPipeServer(string name, IDesktopController controller, Action<string>? lifecycleRequest = null)
{
    private readonly SemaphoreSlim _slots = new(8, 8);
    private NamedPipeServerStream CreatePipe() => new(name, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var handlers = new List<Task>();
        NamedPipeServerStream? pending = CreatePipe();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await pending.WaitForConnectionAsync(cancellationToken);
                var connected = pending;
                pending = CreatePipe();
                if (!_slots.Wait(0)) { connected.Dispose(); continue; }
                handlers.RemoveAll(task => task.IsCompleted);
                handlers.Add(ServeAsync(connected, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { pending?.Dispose(); await Task.WhenAll(handlers); }
    }
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
            {
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var json = await WireJson.ReadLineAsync(reader, readDeadline.Token);
                if (json is null) return;
                var request = WireJson.Parse<LocalRequest>(json);
                using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                operationDeadline.CancelAfter(TimeSpan.FromSeconds(120));
                var isLifecycle = request.Version == 1 && request.RequestId != Guid.Empty && lifecycleRequest is not null && request.Command is "shutdown" or "show";
                var response = isLifecycle ? new LocalReply(request.RequestId, true) : await LocalDispatcher.DispatchAsync(controller, request, operationDeadline.Token);
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                await writer.WriteLineAsync(WireJson.Serialize(response).AsMemory(), writeDeadline.Token);
                if (isLifecycle) lifecycleRequest!(request.Command);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or System.Text.Json.JsonException) { }
        finally { _slots.Release(); }
    }
}

public static class LocalPipeClient
{
    public static async Task<LocalReply> SendAsync(string pipeName, string expectedHostPath, LocalRequest request, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, cancellationToken);
        PipeIdentity.RequireServerPath(pipe, expectedHostPath);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(WireJson.Serialize(request).AsMemory(), cancellationToken);
        var json = await WireJson.ReadLineAsync(reader, cancellationToken) ?? throw new ControlException("host_disconnected");
        var reply = WireJson.Parse<LocalReply>(json);
        if (reply.RequestId != request.RequestId || reply.Version != 1) throw new ControlException("host_protocol_error");
        return reply;
    }
}
