using System.Net;

namespace RemoteDisplaySwitch.Discovery;

public sealed partial class LanDiscovery : IDisposable, IAsyncDisposable
{
    private readonly IDnsSdAdapter adapter;
    private readonly uint interfaceIndex;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> pending = [];
    private readonly List<DiscoveryDiagnostic> diagnostics = [];
    private CancellationTokenSource? lifetime;
    private CancellationTokenRegistration cancellation;
    private IAsyncDisposable? registration;
    private IAsyncDisposable? browse;
    private ITimer? timer;
    private long generation;
    private long revision;
    private string self = "";
    private bool disposed;
    private IReadOnlyList<DiscoveredPeer> candidates = Array.AsReadOnly(Array.Empty<DiscoveredPeer>());
    private sealed record Entry(long Revision, DateTimeOffset Expires, IReadOnlyList<DiscoveredPeer> Peers);

    public LanDiscovery(uint interfaceIndex) : this(interfaceIndex, new WindowsDnsSdAdapter(), TimeProvider.System) { }
    public LanDiscovery(uint interfaceIndex, IDnsSdAdapter adapter, TimeProvider? timeProvider = null)
    {
        if (interfaceIndex == 0) throw new ArgumentOutOfRangeException(nameof(interfaceIndex), "An explicit interface is required.");
        this.interfaceIndex = interfaceIndex;
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        clock = timeProvider ?? TimeProvider.System;
        if (adapter is WindowsDnsSdAdapter windows) windows.Diagnostic += NativeDiagnostic;
    }

    public event EventHandler? CandidatesChanged;
    public IReadOnlyList<DiscoveredPeer> Candidates { get { lock (sync) return candidates; } }
    public IReadOnlyList<DiscoveryDiagnostic> Diagnostics { get { lock (sync) return diagnostics.ToArray(); } }
    public bool IsRunning { get { lock (sync) return lifetime is { IsCancellationRequested: false }; } }

    private void NativeDiagnostic(DiscoveryDiagnostic diagnostic) => Record(null, diagnostic);

    public async Task StartAsync(int port, string sessionId, string displayName, IPAddress localAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (!DiscoveryMetadata.ValidSession(sessionId)) throw new ArgumentException("Expected a lowercase canonical UUID.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Any(character => char.IsControl(character) || character is '.' or '\\') || System.Text.Encoding.UTF8.GetByteCount(displayName) > 63)
            throw new ArgumentException("Expected a nonempty DNS-SD label of at most 63 UTF-8 bytes without dots or backslashes.", nameof(displayName));
        if (!DiscoveryMetadata.UsableAddress(localAddress)) throw new ArgumentException("Expected a unicast local address.", nameof(localAddress));
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            long current;
            lock (sync)
            {
                current = ++generation;
                self = sessionId;
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }
            try
            {
                registration = await adapter.RegisterAsync(new(sessionId, displayName, localAddress, port, interfaceIndex), lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                browse = adapter.Browse(interfaceIndex, announcement => Announce(current, announcement), diagnostic => Record(current, diagnostic));
                timer = clock.CreateTimer(_ => Expire(current), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                cancellation = cancellationToken.Register(() => { _ = StopAfterCancellationAsync(current); });
            }
            catch (Exception error)
            {
                Record(current, Diagnostic("Start", error));
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { lifecycle.Release(); }
    }

    private async Task StopAfterCancellationAsync(long current)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (current == generation) await StopCoreAsync().ConfigureAwait(false); }
        catch (Exception error) { Record(null, Diagnostic("Stop", error)); }
        finally { lifecycle.Release(); }
    }

    private void Record(long? current, DiscoveryDiagnostic diagnostic)
    {
        lock (sync)
        {
            if (current.HasValue && current != generation) return;
            if (diagnostics.Count == 64) diagnostics.RemoveAt(0);
            diagnostics.Add(diagnostic);
        }
    }

    private static DiscoveryDiagnostic Diagnostic(string operation, Exception error) => error is DnsSdException native
        ? new(native.Operation, native.NativeStatus, native.Message) : new(operation, null, error.Message);

    public async Task StopAsync()
    {
        lock (sync) lifetime?.Cancel();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Task[] tasks;
        lock (sync)
        {
            ++generation;
            lifetime?.Cancel();
            timer?.Dispose();
            timer = null;
            entries.Clear();
            Publish(generation);
            tasks = pending.ToArray();
        }
        cancellation.Unregister();
        List<Exception> errors = [];
        if (browse is not null)
            try { await browse.DisposeAsync().ConfigureAwait(false); browse = null; } catch (Exception error) { errors.Add(error); }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (registration is not null)
            try { await registration.DisposeAsync().ConfigureAwait(false); registration = null; } catch (Exception error) { errors.Add(error); }
        lock (sync) { lifetime?.Dispose(); lifetime = null; }
        foreach (var error in errors) Record(null, Diagnostic("Stop", error));
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        lock (sync) lifetime?.Cancel();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
            if (adapter is WindowsDnsSdAdapter windows) windows.Diagnostic -= NativeDiagnostic;
        }
        finally { lifecycle.Release(); }
    }
}
