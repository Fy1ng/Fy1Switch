using System.Net;
using System.Net.Sockets;
using RemoteDisplaySwitch.Contracts;

namespace RemoteDisplaySwitch.Pairing;

public sealed class PairingClient : IDisposable
{
    private readonly object gate = new();
    private readonly PairingAttemptLimiter limiter;
    private readonly PairingTimeouts timeouts;
    private readonly CancellationTokenSource stop = new();
    private TcpClient? socket;
    private bool disposed;
    public string Status { get; private set; } = "idle";
    public event Action<string>? StateChanged;

    public PairingClient(PairingAttemptLimiter? limiter = null, PairingTimeouts? timeouts = null)
    {
        this.limiter = limiter ?? PairingAttemptLimiter.Shared;
        this.timeouts = timeouts ?? new PairingTimeouts();
    }

    public Task<bool> ConnectAsync(IPAddress peer, int port, string peerSession, string sixDigitCode,
        Func<Task<PairBundle>> provideBundle, CancellationToken cancellationToken = default)
        => ConnectAsync(peer, port, peerSession, sixDigitCode, _ => provideBundle(), cancellationToken);

    public async Task<bool> ConnectAsync(IPAddress peer, int port, string peerSession, string sixDigitCode,
        Func<CancellationToken, Task<PairBundle>> provideBundle, CancellationToken cancellationToken = default)
    {
        PairingProtocol.ValidateSession(peerSession);
        PairingProtocol.ValidateCode(sixDigitCode);
        ArgumentNullException.ThrowIfNull(provideBundle);
        ArgumentNullException.ThrowIfNull(peer);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        cancellationToken.ThrowIfCancellationRequested();
        TcpClient connection;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (socket is not null || !limiter.TryBeginAttempt(peerSession)) return false;
            socket = connection = new TcpClient(peer.AddressFamily);
        }
        using var total = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        total.CancelAfter(timeouts.Total);
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        handshakeTimeout.CancelAfter(timeouts.Handshake);
        try
        {
            SetState("connecting");
            await connection.ConnectAsync(peer, port, handshakeTimeout.Token).ConfigureAwait(false);
            var stream = connection.GetStream();
            using var handshake = new SrpClientHandshake(peerSession, sixDigitCode);
            sixDigitCode = "";
            await PairingTransport.WriteAsync(stream, handshake.Hello, handshakeTimeout.Token).ConfigureAwait(false);
            SetState("authenticating");
            var proof = handshake.AcceptChallenge(await PairingTransport.ReadAsync(stream, handshakeTimeout.Token).ConfigureAwait(false));
            await PairingTransport.WriteAsync(stream, proof, handshakeTimeout.Token).ConfigureAwait(false);
            using var channel = handshake.AcceptVerified(await PairingTransport.ReadAsync(stream, handshakeTimeout.Token).ConfigureAwait(false));
            if (!limiter.TryConsume(peerSession)) throw new PairingProtocolException();
            handshakeTimeout.Token.ThrowIfCancellationRequested();
            SetState("sending-bundle");
            var bundle = await Task.Run(() => provideBundle(total.Token), total.Token).WaitAsync(total.Token).ConfigureAwait(false);
            await PairingTransport.WriteAsync(stream, channel.Encrypt(bundle), total.Token).ConfigureAwait(false);
            SetState("awaiting-android");
            var result = channel.Decrypt<PairingResult>(await PairingTransport.ReadAsync(stream, total.Token).ConfigureAwait(false));
            lock (gate)
            {
                total.Token.ThrowIfCancellationRequested();
                SetState(result.Success ? "completed" : "failed");
                return result.Success;
            }
        }
        catch (Exception)
        {
            SetState(total.IsCancellationRequested ? "cancelled" : "failed");
            return false;
        }
        finally
        {
            sixDigitCode = "";
            connection.Dispose();
            limiter.EndAttempt(peerSession);
            lock (gate) socket = null;
        }
    }

    private void SetState(string status)
    {
        lock (gate)
        {
            if (disposed) return;
            Status = status;
            foreach (var handler in StateChanged?.GetInvocationList() ?? [])
            {
                try { ((Action<string>)handler)(status); }
                catch { }
            }
        }
    }

    public void Stop() => Dispose();
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Status = "stopped";
            stop.Cancel();
            socket?.Dispose();
        }
    }
}
