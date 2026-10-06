namespace RemoteDisplaySwitch.Pairing;

public sealed class PairingTimeouts
{
    public TimeSpan Handshake { get; }
    public TimeSpan Total { get; }

    public PairingTimeouts(TimeSpan? handshake = null, TimeSpan? total = null)
    {
        Handshake = handshake ?? TimeSpan.FromSeconds(15);
        Total = total ?? TimeSpan.FromSeconds(45);
        if (Handshake <= TimeSpan.Zero || Handshake > TimeSpan.FromSeconds(15) || Total < Handshake || Total > TimeSpan.FromSeconds(45))
            throw new ArgumentOutOfRangeException(nameof(handshake));
    }
}
