using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDisplaySwitch.Pairing;

public sealed class PairingChannel : IDisposable
{
    private readonly object gate = new();
    private readonly byte[] key;
    private readonly byte[] hash;
    private readonly string sendPurpose;
    private readonly string receivePurpose;
    private bool sent;
    private bool received;
    private bool disposed;
    public PairingTranscript Transcript { get; }

    public PairingChannel(ReadOnlySpan<byte> sessionKey, PairingTranscript transcript, string localRole)
    {
        if (sessionKey.Length != 32) throw new PairingProtocolException();
        PairingProtocol.OppositeRole(localRole);
        Transcript = transcript;
        hash = transcript.Hash();
        key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, key, hash, "RDS-Pair-v1/encryption"u8);
        sendPurpose = localRole == "windows" ? PairingProtocol.WindowsBundle : PairingProtocol.AndroidResult;
        receivePurpose = localRole == "windows" ? PairingProtocol.AndroidResult : PairingProtocol.WindowsBundle;
    }

    public PairingFrame Encrypt<T>(T payload) => EncryptBytes(JsonSerializer.SerializeToUtf8Bytes(payload, PairingProtocol.Json));

    private PairingFrame EncryptBytes(byte[] plaintext)
    {
        lock (gate)
        {
            try
            {
                if (disposed || sent || (sendPurpose == PairingProtocol.AndroidResult && !received)) throw new PairingProtocolException();
                sent = true;
                if (plaintext.Length > PairingProtocol.MaxFrameBytes) throw new PairingProtocolException();
                var nonce = RandomNumberGenerator.GetBytes(12);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[16];
                using var aes = new AesGcm(key, 16);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(sendPurpose));
                var frame = new PairingFrame { Type = "encrypted", Purpose = sendPurpose, Nonce = Convert.ToBase64String(nonce),
                    Ciphertext = Convert.ToBase64String(ciphertext), Tag = Convert.ToBase64String(tag) };
                PairingProtocol.Serialize(frame);
                return frame;
            }
            catch { Dispose(); throw; }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    public T Decrypt<T>(PairingFrame frame)
    {
        lock (gate)
        {
            byte[]? plaintext = null;
            try
            {
                if (disposed || received || (receivePurpose == PairingProtocol.AndroidResult && !sent)) throw new PairingProtocolException();
                received = true;
                PairingProtocol.Serialize(frame);
                if (frame.Type != "encrypted" || frame.Purpose != receivePurpose) throw new PairingProtocolException();
                var ciphertext = PairingProtocol.Decode(frame.Ciphertext);
                plaintext = new byte[ciphertext.Length];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(PairingProtocol.Decode(frame.Nonce, 12), ciphertext, PairingProtocol.Decode(frame.Tag, 16), plaintext, Aad(receivePurpose));
                return JsonSerializer.Deserialize<T>(plaintext, PairingProtocol.Json) ?? throw new PairingProtocolException();
            }
            catch { Dispose(); throw; }
            finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    private byte[] Aad(string purpose) => [.. hash, .. Encoding.UTF8.GetBytes(purpose)];
    public void Dispose()
    {
        lock (gate) { disposed = true; CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(hash); }
    }
}

public sealed record PairingResult([property: System.Text.Json.Serialization.JsonRequired] bool Success);
