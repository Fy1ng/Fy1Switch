using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Fy1Switch.Contracts;

namespace Fy1Switch.Runtime;

public sealed class WorkerSupervisor(string workerPath, string dataDirectory, bool simulation) : IWorkerTransport
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _initialized;
    public bool IsRunning { get { try { return _process is { HasExited: false }; } catch { return false; } } }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning && _pipe?.IsConnected == true) return;
        await StopCoreAsync();
        if (!File.Exists(workerPath)) throw new ControlException("worker_missing");
        Directory.CreateDirectory(dataDirectory);
        var pipeName = "Fy1Switch.Worker." + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.GetFullPath(workerPath)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Path.GetFullPath(dataDirectory));
        if (simulation) start.ArgumentList.Add("--simulate");
        _process = Process.Start(start) ?? throw new ControlException("worker_start_failed");
        _ = CaptureLogAsync(_process.StandardError);
        _ = CaptureLogAsync(_process.StandardOutput);
        using var connected = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connected.CancelAfter(TimeSpan.FromSeconds(10));
        await _pipe.WaitForConnectionAsync(connected.Token);
        PipeIdentity.RequireClientProcess(_pipe, _process.Id);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    }
    private async Task CaptureLogAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var safeLine = line.Length > 4096 ? line[..4096] : line;
                var logPath = Path.Combine(dataDirectory, "worker.log");
                lock (_logGate)
                {
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024) File.Move(logPath, logPath + ".previous", true);
                    File.AppendAllText(logPath, safeLine + Environment.NewLine, Encoding.UTF8);
                }
            }
        }
        catch { }
    }
    private readonly object _logGate = new();
    private async Task<WorkerReply> ExchangeAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.Command == "discover" ? 90 : 10));
        await _writer!.WriteLineAsync(WireJson.Serialize(request).AsMemory(), deadline.Token);
        var line = await WireJson.ReadLineAsync(_reader!, deadline.Token) ?? throw new ControlException("worker_disconnected");
        var reply = WireJson.Parse<WorkerReply>(line);
        if (reply.Version != 1 || reply.RequestId != request.RequestId) throw new ControlException("worker_protocol_error");
        return reply;
    }
    public async Task<WorkerReply> SendAsync(WorkerRequest request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StartAsync(cancellationToken);
            if (!_initialized && request.Command is not ("discover" or "shutdown"))
            {
                var initialized = await ExchangeAsync(new(Guid.NewGuid(), "discover"), cancellationToken);
                if (!initialized.Success) return new(request.RequestId, false, initialized.ErrorCode, WriteAccepted: false);
                _initialized = true;
            }
            var reply = await ExchangeAsync(request, cancellationToken);
            if (request.Command == "discover") _initialized = reply.Success;
            return reply;
        }
        catch (OperationCanceledException)
        {
            await StopCoreAsync();
            throw new ControlException("worker_timeout");
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally { _gate.Release(); }
    }
    private async Task StopCoreAsync()
    {
        _initialized = false;
        _writer?.Dispose(); _reader?.Dispose(); _pipe?.Dispose();
        _writer = null; _reader = null; _pipe = null;
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                }
            }
            finally { process.Dispose(); _process = null; }
        }
    }
    public async Task StopAsync() { await _gate.WaitAsync(); try { await StopCoreAsync(); } finally { _gate.Release(); } }
    public async ValueTask DisposeAsync() => await StopAsync();
}
