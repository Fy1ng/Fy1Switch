using System.Net;
using System.Net.Sockets;
using RemoteDisplaySwitch.Contracts;

namespace RemoteDisplaySwitch.Pairing;

public sealed class PairingHost : IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly TcpListener listener;
    private readonly PairingCodeLease lease;
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<TcpClient> sockets = [];
    private readonly HashSet<Task> attempts = [];
    private readonly Func<CancellationToken, Task<PairBundle>> provideBundle;
    private readonly PairingTimeouts timeouts;
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task acceptTask;
    private readonly Task expiryTask;
    private CancellationTokenRegistration externalStop;
    private bool stopped;
    private bool authenticated;
    public string SessionId { get { lock (gate) return stopped ? "" : lease.SessionId; } }
    public string Code => lease.Code;
    public int Port { get; }
    public DateTimeOffset ExpiresAt => lease.ExpiresAt;
    public string Status { get; private set; } = "listening";
    public event Action<string>? StateChanged;
    public Task<bool> Completion => completion.Task;

    private PairingHost(IPAddress bindAddress, Func<CancellationToken, Task<PairBundle>> provideBundle,
        PairingAttemptLimiter? limiter, TimeSpan? lifetime, PairingTimeouts? timeouts, CancellationToken cancellationToken)
    {
        this.provideBundle = provideBundle;
        this.timeouts = timeouts ?? new PairingTimeouts();
        lease = new PairingCodeLease(limiter, lifetime: lifetime);
        listener = new TcpListener(bindAddress, 0);
        try { listener.Start(1); }
        catch { lease.Dispose(); stop.Dispose(); throw; }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptAsync();
        expiryTask = ExpireAsync(lifetime ?? TimeSpan.FromMinutes(5));
        externalStop = cancellationToken.Register(Stop);
    }

    public static Task<PairingHost> StartAsync(IPAddress bindAddress, Func<Task<PairBundle>> provideBundle,
        string localRole = "windows", PairingAttemptLimiter? limiter = null, CancellationToken cancellationToken = default)
        => StartAsync(bindAddress, _ => provideBundle(), localRole, limiter, cancellationToken: cancellationToken);

    public static Task<PairingHost> StartAsync(IPAddress bindAddress, Func<CancellationToken, Task<PairBundle>> provideBundle,
        string localRole = "windows", PairingAttemptLimiter? limiter = null, TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default, PairingTimeouts? timeouts = null)
    {
        if (localRole != "windows") throw new ArgumentException("The host service sends Windows bundles. Use SRP/transport APIs for Android fixtures.", nameof(localRole));
        ArgumentNullException.ThrowIfNull(bindAddress);
        ArgumentNullException.ThrowIfNull(provideBundle);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PairingHost(bindAddress, provideBundle, limiter, lifetime, timeouts, cancellationToken));
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var socket = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (stopped || !lease.TryBegin(out var password))
                    {
                        socket.Dispose();
                        if (!authenticated && lease.IsClosed) Finish(false, "exhausted");
                        continue;
                    }
                    sockets.Add(socket);
                    var attempt = HandleAsync(socket, password);
                    attempts.Add(attempt);
                    _ = attempt.ContinueWith(finished => { lock (gate) attempts.Remove(finished); }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            lock (gate) { if (!stopped && !authenticated) Finish(false, "failed"); }
        }
    }

    private async Task HandleAsync(TcpClient socket, string password)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        total.CancelAfter(timeouts.Total);
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        handshakeTimeout.CancelAfter(timeouts.Handshake);
        var proofAccepted = false;
        var rejected = false;
        try
        {
            SetState("authenticating");
            var stream = socket.GetStream();
            var hello = await PairingTransport.ReadAsync(stream, handshakeTimeout.Token).ConfigureAwait(false);
            using var handshake = new SrpServerHandshake(hello, lease.SessionId, password);
            password = "";
            await PairingTransport.WriteAsync(stream, handshake.Challenge, handshakeTimeout.Token).ConfigureAwait(false);
            var proof = await PairingTransport.ReadAsync(stream, handshakeTimeout.Token).ConfigureAwait(false);
            var accepted = handshake.AcceptProof(proof);
            using var channel = accepted.Channel;
            lock (gate)
            {
                if (stopped || !lease.ConsumeProof()) throw new PairingProtocolException();
                authenticated = proofAccepted = true;
                listener.Stop();
                handshakeTimeout.Token.ThrowIfCancellationRequested();
            }
            await PairingTransport.WriteAsync(stream, accepted.Verified, handshakeTimeout.Token).ConfigureAwait(false);
            SetState("sending-bundle");
            var bundle = await Task.Run(() => provideBundle(total.Token), total.Token).WaitAsync(total.Token).ConfigureAwait(false);
            await PairingTransport.WriteAsync(stream, channel.Encrypt(bundle), total.Token).ConfigureAwait(false);
            SetState("awaiting-android");
            var result = channel.Decrypt<PairingResult>(await PairingTransport.ReadAsync(stream, total.Token).ConfigureAwait(false));
            total.Token.ThrowIfCancellationRequested();
            Finish(result.Success, result.Success ? "completed" : "failed");
        }
        catch (Exception)
        {
            if (proofAccepted) Finish(false, "failed");
            else rejected = true;
        }
        finally
        {
            password = "";
            lock (gate)
            {
                sockets.Remove(socket);
                lease.EndAttempt();
                socket.Dispose();
                if (!stopped && !proofAccepted && lease.IsClosed) Finish(false, lease.IsExpired ? "expired" : "exhausted");
                else if (rejected) SetState("rejected");
            }
        }
    }

    private async Task ExpireAsync(TimeSpan lifetime)
    {
        try
        {
            await Task.Delay(lifetime, stop.Token).ConfigureAwait(false);
            lock (gate) { if (!authenticated) Finish(false, "expired"); }
        }
        catch (OperationCanceledException) { }
    }

    private void SetState(string status)
    {
        lock (gate)
        {
            if (stopped) return;
            Status = status;
            Notify(status);
        }
    }

    private void Notify(string status)
    {
        foreach (var handler in StateChanged?.GetInvocationList() ?? [])
        {
            try { ((Action<string>)handler)(status); }
            catch { }
        }
    }

    private void Finish(bool success, string status)
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            Status = status;
            lease.Dispose();
            stop.Cancel();
            listener.Stop();
            foreach (var socket in sockets.ToArray()) socket.Dispose();
            completion.TrySetResult(success);
            Notify(status);
        }
    }

    public void Stop() => Finish(false, "stopped");
    public void Dispose() { Stop(); externalStop.Dispose(); }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.WhenAll(acceptTask, expiryTask).ConfigureAwait(false);
        Task[] pending;
        lock (gate) pending = [.. attempts];
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
}
