using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace RemoteDisplaySwitch.Pairing;

internal sealed class ClearableClient : Srp6Client
{
    private BigInteger? ephemeral;
    protected override BigInteger SelectPrivateValue() => ephemeral ??= base.SelectPrivateValue();
    internal BigInteger ExpectedM2() => Srp6Utilities.CalculateM2(new Sha256Digest(), N, pubA, M1, S);
    internal void Clear()
    {
        ephemeral = null;
        privA = pubA = B = x = u = S = M1 = M2 = Key = null!;
        digest.Reset();
    }
}

internal sealed class ClearableServer : Srp6Server
{
    internal BigInteger ExpectedM1() => Srp6Utilities.CalculateM1(new Sha256Digest(), N, A, pubB, S);
    internal void Clear()
    {
        v = A = privB = pubB = u = S = M1 = M2 = Key = null!;
        digest.Reset();
    }
}

public sealed class SrpClientHandshake : IDisposable
{
    private readonly ClearableClient client = new();
    private readonly byte[] password;
    private readonly byte[] identity;
    private PairingFrame? challenge;
    private PairingFrame? proof;
    private int stage;
    public PairingFrame Hello { get; }

    public SrpClientHandshake(string serverSession, string sixDigitCode, string localRole = "windows", string? clientSession = null)
    {
        PairingProtocol.ValidateSession(serverSession);
        PairingProtocol.ValidateCode(sixDigitCode);
        PairingProtocol.OppositeRole(localRole);
        clientSession ??= PairingProtocol.NewSessionId();
        PairingProtocol.ValidateSession(clientSession);
        password = Encoding.UTF8.GetBytes(sixDigitCode);
        identity = Encoding.UTF8.GetBytes("RDS-Pair-v1/" + serverSession);
        client.Init(Srp6StandardGroups.rfc5054_3072, new Sha256Digest(), new SecureRandom());
        var publicA = client.GenerateClientCredentials([], identity, password);
        Hello = new PairingFrame { Type = "hello", ClientSession = clientSession, ServerSession = serverSession,
            ClientRole = localRole, A = PairingProtocol.Encode(publicA, 384) };
    }

    public PairingFrame AcceptChallenge(PairingFrame incoming)
    {
        if (stage != 0) throw new PairingProtocolException();
        stage = -1;
        try
        {
            PairingProtocol.Check(incoming, "challenge", Hello.ClientSession!, Hello.ServerSession!);
            if (incoming.ServerRole != PairingProtocol.OppositeRole(Hello.ClientRole!)) throw new PairingProtocolException();
            var salt = PairingProtocol.Decode(incoming.Salt, 32);
            var publicB = PairingProtocol.PublicValue(incoming.B);
            client.GenerateClientCredentials(salt, identity, password);
            client.CalculateSecret(publicB);
            challenge = incoming;
            proof = new PairingFrame { Type = "proof", ClientSession = Hello.ClientSession, ServerSession = Hello.ServerSession,
                M1 = PairingProtocol.Encode(client.CalculateClientEvidenceMessage(), 32) };
            stage = 1;
            return proof;
        }
        catch { Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    public PairingChannel AcceptVerified(PairingFrame incoming)
    {
        if (stage != 1) throw new PairingProtocolException();
        stage = -1;
        try
        {
            PairingProtocol.Check(incoming, "verified", Hello.ClientSession!, Hello.ServerSession!);
            PairingProtocol.Evidence(client.ExpectedM2(), incoming.M2);
            if (!client.VerifyServerEvidenceMessage(new BigInteger(1, PairingProtocol.Decode(incoming.M2, 32)))) throw new PairingProtocolException();
            var transcript = new PairingTranscript(Hello.ClientSession!, Hello.ServerSession!, Hello.ClientRole!, challenge!.ServerRole!,
                challenge.Salt!, Hello.A!, challenge.B!, proof!.M1!, incoming.M2!);
            var key = PairingProtocol.Padded(client.CalculateSessionKey(), 32);
            try { return new PairingChannel(key, transcript, Hello.ClientRole!); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        stage = -1;
        CryptographicOperations.ZeroMemory(password);
        CryptographicOperations.ZeroMemory(identity);
        client.Clear();
        challenge = proof = null;
    }
}

public sealed class SrpServerHandshake : IDisposable
{
    private readonly ClearableServer server = new();
    private readonly PairingFrame hello;
    private int stage;
    public PairingFrame Challenge { get; }

    public SrpServerHandshake(PairingFrame hello, string serverSession, string sixDigitCode, string localRole = "windows")
    {
        PairingProtocol.ValidateSession(serverSession);
        PairingProtocol.ValidateSession(hello.ClientSession);
        PairingProtocol.ValidateCode(sixDigitCode);
        PairingProtocol.Check(hello, "hello", hello.ClientSession!, serverSession);
        if (hello.ClientRole != PairingProtocol.OppositeRole(localRole)) throw new PairingProtocolException();
        var publicA = PairingProtocol.PublicValue(hello.A);
        this.hello = hello;
        var salt = RandomNumberGenerator.GetBytes(32);
        var password = Encoding.UTF8.GetBytes(sixDigitCode);
        var identity = Encoding.UTF8.GetBytes("RDS-Pair-v1/" + serverSession);
        try
        {
            var generator = new Srp6VerifierGenerator();
            generator.Init(Srp6StandardGroups.rfc5054_3072, new Sha256Digest());
            var verifier = generator.GenerateVerifier(salt, identity, password);
            server.Init(Srp6StandardGroups.rfc5054_3072, verifier, new Sha256Digest(), new SecureRandom());
            var publicB = server.GenerateServerCredentials();
            server.CalculateSecret(publicA);
            Challenge = new PairingFrame { Type = "challenge", ClientSession = hello.ClientSession, ServerSession = serverSession,
                ServerRole = localRole, Salt = Convert.ToBase64String(salt), B = PairingProtocol.Encode(publicB, 384) };
        }
        finally { CryptographicOperations.ZeroMemory(password); CryptographicOperations.ZeroMemory(identity); }
    }

    public (PairingFrame Verified, PairingChannel Channel) AcceptProof(PairingFrame incoming)
    {
        if (stage != 0) throw new PairingProtocolException();
        stage = -1;
        try
        {
            PairingProtocol.Check(incoming, "proof", hello.ClientSession!, hello.ServerSession!);
            PairingProtocol.Evidence(server.ExpectedM1(), incoming.M1);
            if (!server.VerifyClientEvidenceMessage(new BigInteger(1, PairingProtocol.Decode(incoming.M1, 32)))) throw new PairingProtocolException();
            var verified = new PairingFrame { Type = "verified", ClientSession = hello.ClientSession, ServerSession = hello.ServerSession,
                M2 = PairingProtocol.Encode(server.CalculateServerEvidenceMessage(), 32) };
            var transcript = new PairingTranscript(hello.ClientSession!, hello.ServerSession!, hello.ClientRole!, Challenge.ServerRole!,
                Challenge.Salt!, hello.A!, Challenge.B!, incoming.M1!, verified.M2!);
            var key = PairingProtocol.Padded(server.CalculateSessionKey(), 32);
            try { return (verified, new PairingChannel(key, transcript, Challenge.ServerRole!)); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { Dispose(); }
    }

    public void Dispose() { stage = -1; server.Clear(); }
}
