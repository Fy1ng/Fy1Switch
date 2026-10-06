using System.Security.Cryptography;

namespace RemoteDisplaySwitch.Pairing;

public static class PairingTransport
{
    public static async Task WriteAsync(Stream stream, PairingFrame frame, CancellationToken cancellationToken = default)
    {
        var bytes = PairingProtocol.Serialize(frame);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PairingFrame> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = new byte[PairingProtocol.MaxFrameBytes];
        var length = 0;
        try
        {
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Pairing peer disconnected.");
                if (bytes[length] == (byte)'\n') return PairingProtocol.Parse(bytes.AsSpan(0, length));
                length++;
            }
            throw new PairingProtocolException();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
