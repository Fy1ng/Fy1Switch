using System.Security.Cryptography;
using System.Text;

namespace Fy1Switch.Pairing;

public sealed class PairingAttemptLimiter
{
    public static PairingAttemptLimiter Shared { get; } = new();
    private readonly object gate = new();
    private readonly Queue<long> admissions = new();
    private readonly Dictionary<string, int> perSession = new(StringComparer.Ordinal);
    private readonly HashSet<string> consumed = new(StringComparer.Ordinal);
    private readonly HashSet<string> active = new(StringComparer.Ordinal);
    private readonly TimeProvider time;

    public PairingAttemptLimiter(TimeProvider? timeProvider = null) => time = timeProvider ?? TimeProvider.System;

    public bool TryAdmit(string serverSession)
    {
        PairingProtocol.ValidateSession(serverSession);
        lock (gate)
        {
            var now = time.GetTimestamp();
            while (admissions.TryPeek(out var oldest) && time.GetElapsedTime(oldest, now) >= TimeSpan.FromMinutes(10)) admissions.Dequeue();
            var count = perSession.GetValueOrDefault(serverSession);
            if (consumed.Contains(serverSession) || count >= 5 || admissions.Count >= 10) return false;
            admissions.Enqueue(now);
            perSession[serverSession] = count + 1;
            return true;
        }
    }

    public bool TryConsume(string serverSession)
    {
        lock (gate) return perSession.ContainsKey(serverSession) && consumed.Add(serverSession);
    }

    public bool TryBeginAttempt(string serverSession)
    {
        lock (gate)
        {
            if (active.Contains(serverSession) || !TryAdmit(serverSession)) return false;
            active.Add(serverSession);
            return true;
        }
    }

    public void EndAttempt(string serverSession) { lock (gate) active.Remove(serverSession); }
}

public sealed class PairingCodeLease : IDisposable
{
    private readonly object gate = new();
    private readonly PairingAttemptLimiter limiter;
    private readonly TimeProvider time;
    private readonly long created;
    private readonly byte[] code;
    private readonly TimeSpan lifetime;
    private int attempts;
    private bool active;
    private bool closed;
    public string SessionId { get; } = PairingProtocol.NewSessionId();
    public DateTimeOffset ExpiresAt { get; }
    public string Code
    {
        get { lock (gate) { if (IsExpired) Close(); return closed ? "" : Encoding.ASCII.GetString(code); } }
    }
    public bool IsExpired => time.GetElapsedTime(created) >= lifetime;
    public bool IsClosed { get { lock (gate) return closed || IsExpired; } }

    public PairingCodeLease(PairingAttemptLimiter? limiter = null, TimeProvider? timeProvider = null, TimeSpan? lifetime = null)
    {
        this.limiter = limiter ?? PairingAttemptLimiter.Shared;
        time = timeProvider ?? TimeProvider.System;
        this.lifetime = lifetime ?? TimeSpan.FromMinutes(5);
        if (this.lifetime <= TimeSpan.Zero || this.lifetime > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        created = time.GetTimestamp();
        ExpiresAt = time.GetUtcNow() + this.lifetime;
        code = Encoding.ASCII.GetBytes(PairingProtocol.NewCode());
    }

    public bool TryBegin(out string password)
    {
        lock (gate)
        {
            password = "";
            if (closed || IsExpired || attempts >= 5) { Close(); return false; }
            if (active) return false;
            if (!limiter.TryBeginAttempt(SessionId)) { Close(); return false; }
            active = true;
            attempts++;
            password = Encoding.ASCII.GetString(code);
            return true;
        }
    }

    public bool ConsumeProof()
    {
        lock (gate)
        {
            if (!active || closed || IsExpired) { Close(); return false; }
            var accepted = limiter.TryConsume(SessionId);
            Close();
            return accepted;
        }
    }

    public void EndAttempt()
    {
        lock (gate) { active = false; limiter.EndAttempt(SessionId); if (attempts >= 5 || IsExpired) Close(); }
    }

    private void Close() { closed = true; CryptographicOperations.ZeroMemory(code); }
    public void Dispose() { lock (gate) Close(); }
}
