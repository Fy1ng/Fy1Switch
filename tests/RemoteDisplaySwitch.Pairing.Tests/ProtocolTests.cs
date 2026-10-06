using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Pairing;

namespace RemoteDisplaySwitch.Pairing.Tests;

[TestClass]
public sealed class ProtocolTests
{
    internal static PairBundle Bundle() => new(1, Guid.Parse("4d2d58cc-2e22-4087-83c3-710af7484034"),
        "https://127.0.0.1:12345", "fixture-pin-not-a-real-certificate", "fixture-bootstrap-not-six-digits", DateTimeOffset.Parse("2030-01-01T00:00:00Z"), "https://100.70.1.2:12345");

    internal static (PairingChannel Client, PairingChannel Server) Channels(string role = "windows", string code = "000001")
    {
        var session = PairingProtocol.NewSessionId();
        using var client = new SrpClientHandshake(session, code, role);
        using var server = new SrpServerHandshake(client.Hello, session, code, PairingProtocol.OppositeRole(role));
        var proof = client.AcceptChallenge(server.Challenge);
        var accepted = server.AcceptProof(proof);
        return (client.AcceptVerified(accepted.Verified), accepted.Channel);
    }

    [DataTestMethod]
    [DataRow("windows", "000000")]
    [DataRow("windows", "000001")]
    [DataRow("android", "012345")]
    [DataRow("android", "999999")]
    public void BothRolesExchangeOnlyOneBundleAndResult(string clientRole, string code)
    {
        var channels = Channels(clientRole, code);
        using var client = channels.Client;
        using var server = channels.Server;
        var windows = clientRole == "windows" ? client : server;
        var android = clientRole == "android" ? client : server;
        Assert.AreEqual(client.Transcript, server.Transcript);
        var encrypted = windows.Encrypt(Bundle());
        Assert.AreEqual(Bundle(), android.Decrypt<PairBundle>(encrypted));
        Assert.IsTrue(windows.Decrypt<PairingResult>(android.Encrypt(new PairingResult(true))).Success);
        Assert.ThrowsException<PairingProtocolException>(() => windows.Encrypt(Bundle()));
        Assert.ThrowsException<PairingProtocolException>(() => android.Decrypt<PairBundle>(encrypted));
        Assert.AreEqual(384, PairingProtocol.Decode(client.Transcript.A).Length);
        Assert.AreEqual(384, PairingProtocol.Decode(client.Transcript.B).Length);
        Assert.AreEqual(32, PairingProtocol.Decode(client.Transcript.M1).Length);
        Assert.AreEqual(32, PairingProtocol.Decode(client.Transcript.M2).Length);
    }

    [DataTestMethod]
    [DataRow("12345")]
    [DataRow("1234567")]
    [DataRow("１２３４５６")]
    [DataRow(" 12345")]
    [DataRow("12345\n")]
    public void InvalidCodesRejected(string code) => Assert.ThrowsException<PairingProtocolException>(() => new SrpClientHandshake(PairingProtocol.NewSessionId(), code));

    [TestMethod]
    public void WrongCodeFailsAndProofObjectCannotRetry()
    {
        var session = PairingProtocol.NewSessionId();
        using var client = new SrpClientHandshake(session, "000001");
        using var server = new SrpServerHandshake(client.Hello, session, "000002", "android");
        var proof = client.AcceptChallenge(server.Challenge);
        Assert.ThrowsException<PairingProtocolException>(() => server.AcceptProof(proof));
        Assert.ThrowsException<PairingProtocolException>(() => server.AcceptProof(proof));
    }

    [DataTestMethod]
    [DataRow("zero")]
    [DataRow("n")]
    [DataRow("above")]
    [DataRow("short")]
    [DataRow("whitespace")]
    public void InvalidPublicValuesRejectedOnBothSides(string kind)
    {
        var invalid = kind switch
        {
            "zero" => Convert.ToBase64String(new byte[384]),
            "n" => Convert.ToBase64String(PairingProtocol.Padded(Srp6StandardGroups.rfc5054_3072.N, 384)),
            "above" => Convert.ToBase64String(Enumerable.Repeat((byte)255, 384).ToArray()),
            "short" => "AQ==",
            _ => " " + Convert.ToBase64String(new byte[384])
        };
        var session = PairingProtocol.NewSessionId();
        using var client = new SrpClientHandshake(session, "012345");
        Assert.ThrowsException<PairingProtocolException>(() => new SrpServerHandshake(client.Hello with { A = invalid }, session, "012345", "android"));
        using var server = new SrpServerHandshake(client.Hello, session, "012345", "android");
        Assert.ThrowsException<PairingProtocolException>(() => client.AcceptChallenge(server.Challenge with { B = invalid }));
        Assert.ThrowsException<PairingProtocolException>(() => client.AcceptChallenge(server.Challenge));
    }

    [TestMethod]
    public void WrongM1AndM2Rejected()
    {
        var session = PairingProtocol.NewSessionId();
        using var client = new SrpClientHandshake(session, "000001");
        using var server = new SrpServerHandshake(client.Hello, session, "000001", "android");
        var proof = client.AcceptChallenge(server.Challenge);
        Assert.ThrowsException<PairingProtocolException>(() => server.AcceptProof(proof with { M1 = Flip(proof.M1!) }));
        using var client2 = new SrpClientHandshake(session, "000001");
        using var server2 = new SrpServerHandshake(client2.Hello, session, "000001", "android");
        var accepted = server2.AcceptProof(client2.AcceptChallenge(server2.Challenge));
        using var channel = accepted.Channel;
        Assert.ThrowsException<PairingProtocolException>(() => client2.AcceptVerified(accepted.Verified with { M2 = Flip(accepted.Verified.M2!) }));
        Assert.ThrowsException<PairingProtocolException>(() => client2.AcceptVerified(accepted.Verified));
    }

    [DataTestMethod]
    [DataRow("session")]
    [DataRow("client-session")]
    [DataRow("role")]
    [DataRow("version")]
    [DataRow("type")]
    public void HelloAndChallengeContextsFailClosed(string alteration)
    {
        var session = PairingProtocol.NewSessionId();
        using var client = new SrpClientHandshake(session, "000001");
        var hello = alteration switch
        {
            "session" => client.Hello with { ServerSession = PairingProtocol.NewSessionId() },
            "client-session" => client.Hello with { ClientSession = "BAD-UUID" },
            "role" => client.Hello with { ClientRole = "android" },
            "version" => client.Hello with { Version = 2 },
            _ => client.Hello with { Type = "unknown" }
        };
        Assert.ThrowsException<PairingProtocolException>(() => new SrpServerHandshake(hello, session, "000001", "android"));
        using var server = new SrpServerHandshake(client.Hello, session, "000001", "android");
        var challenge = alteration switch
        {
            "session" => server.Challenge with { ServerSession = PairingProtocol.NewSessionId() },
            "client-session" => server.Challenge with { ClientSession = PairingProtocol.NewSessionId() },
            "role" => server.Challenge with { ServerRole = "windows" },
            "version" => server.Challenge with { Version = 2 },
            _ => server.Challenge with { Type = "unknown" }
        };
        Assert.ThrowsException<PairingProtocolException>(() => client.AcceptChallenge(challenge));
    }

    [DataTestMethod]
    [DataRow("ciphertext")]
    [DataRow("tag")]
    [DataRow("nonce")]
    [DataRow("purpose")]
    [DataRow("version")]
    public void GcmTamperingRejectsAndConsumesPayload(string alteration)
    {
        var channels = Channels();
        using var windows = channels.Client;
        using var android = channels.Server;
        var original = windows.Encrypt(Bundle());
        var tampered = alteration switch
        {
            "ciphertext" => original with { Ciphertext = Flip(original.Ciphertext!) },
            "tag" => original with { Tag = Flip(original.Tag!) },
            "nonce" => original with { Nonce = Flip(original.Nonce!) },
            "purpose" => original with { Purpose = PairingProtocol.AndroidResult },
            _ => original with { Version = 2 }
        };
        Reject(() => android.Decrypt<PairBundle>(tampered));
        Assert.ThrowsException<PairingProtocolException>(() => android.Decrypt<PairBundle>(original));
    }

    [TestMethod]
    public void TranscriptBindsSessionAndRolesAndHasNoTrailingLf()
    {
        var channels = Channels();
        using var client = channels.Client;
        using var server = channels.Server;
        var transcript = client.Transcript;
        var text = Encoding.UTF8.GetString(transcript.ToUtf8());
        Assert.AreEqual(9, text.Count(character => character == '\n'));
        Assert.IsFalse(text.EndsWith('\n'));
        Assert.AreEqual("RDS-Pair-v1", text.Split('\n')[0]);
        var changed = transcript with { ServerSession = PairingProtocol.NewSessionId() };
        Assert.IsFalse(transcript.Hash().SequenceEqual(changed.Hash()));
        var key = new byte[32];
        using var sender = new PairingChannel(key, transcript, "windows");
        using var receiver = new PairingChannel(key, changed, "android");
        Reject(() => receiver.Decrypt<PairBundle>(sender.Encrypt(Bundle())));
        using var sender2 = new PairingChannel(key, transcript, "windows");
        using var receiver2 = new PairingChannel(key, transcript with { ClientRole = "android", ServerRole = "windows" }, "android");
        Reject(() => receiver2.Decrypt<PairBundle>(sender2.Encrypt(Bundle())));
    }

    [TestMethod]
    public void Base64AndJsonAreCanonicalAndStrict()
    {
        Assert.ThrowsException<PairingProtocolException>(() => PairingProtocol.Decode("AB==", 1));
        Assert.ThrowsException<PairingProtocolException>(() => PairingProtocol.Decode("AA", 1));
        Assert.ThrowsException<PairingProtocolException>(() => PairingProtocol.Decode(" AA==", 1));
        Assert.ThrowsException<PairingProtocolException>(() => PairingProtocol.ValidateSession("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"));
        using var client = new SrpClientHandshake(PairingProtocol.NewSessionId(), "000000");
        var wire = Encoding.UTF8.GetString(PairingProtocol.Serialize(client.Hello));
        Assert.IsTrue(wire.Contains("\"a\":"));
        Assert.AreEqual(client.Hello, PairingProtocol.Parse(Encoding.UTF8.GetBytes(wire)));
        foreach (var invalid in new[] { wire.Replace("\"version\":1", "\"version\":1,\"version\":1"), wire.Replace("\"a\":", "\"A\":"), wire.Replace("\"version\":1,", ""), "{}", "[]", "{\"type\":\"wat\"}", wire[..^1] + ",\"extra\":1}" })
            Assert.ThrowsException<PairingProtocolException>(() => PairingProtocol.Parse(Encoding.UTF8.GetBytes(invalid)));
        CollectionAssert.AreEqual(new byte[31].Concat(new byte[] { 1 }).ToArray(), PairingProtocol.Padded(BigInteger.One, 32));
    }

    [TestMethod]
    public void ClientHelloBeforeSaltInteroperatesWithUnmodifiedBcServer()
    {
        var session = PairingProtocol.NewSessionId();
        using var client = new SrpClientHandshake(session, "000001");
        var salt = RandomNumberGenerator.GetBytes(32);
        var generator = new Srp6VerifierGenerator();
        generator.Init(Srp6StandardGroups.rfc5054_3072, new Sha256Digest());
        var verifier = generator.GenerateVerifier(salt, Encoding.UTF8.GetBytes("RDS-Pair-v1/" + session), "000001"u8.ToArray());
        var server = new Srp6Server();
        server.Init(Srp6StandardGroups.rfc5054_3072, verifier, new Sha256Digest(), new SecureRandom());
        var publicB = server.GenerateServerCredentials();
        server.CalculateSecret(new BigInteger(1, PairingProtocol.Decode(client.Hello.A, 384)));
        var challenge = new PairingFrame { Type = "challenge", ClientSession = client.Hello.ClientSession, ServerSession = session,
            ServerRole = "android", Salt = Convert.ToBase64String(salt), B = Convert.ToBase64String(PairingProtocol.Padded(publicB, 384)) };
        var proof = client.AcceptChallenge(challenge);
        Assert.IsTrue(server.VerifyClientEvidenceMessage(new BigInteger(1, PairingProtocol.Decode(proof.M1, 32))));
        var verified = new PairingFrame { Type = "verified", ClientSession = client.Hello.ClientSession, ServerSession = session,
            M2 = Convert.ToBase64String(PairingProtocol.Padded(server.CalculateServerEvidenceMessage(), 32)) };
        using var clientChannel = client.AcceptVerified(verified);
        var sessionKey = PairingProtocol.Padded(server.CalculateSessionKey(), 32);
        using var serverChannel = new PairingChannel(sessionKey, clientChannel.Transcript, "android");
        CryptographicOperations.ZeroMemory(sessionKey);
        Assert.AreEqual(Bundle(), serverChannel.Decrypt<PairBundle>(clientChannel.Encrypt(Bundle())));
    }

    internal static string Flip(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        bytes[0] ^= 1;
        return Convert.ToBase64String(bytes);
    }

    [TestMethod]
    public void AndroidCannotSendResultBeforeReceivingBundle()
    {
        var channels = Channels();
        using var windows = channels.Client;
        using var android = channels.Server;
        Assert.ThrowsException<PairingProtocolException>(() => android.Encrypt(new PairingResult(true)));
    }

    [TestMethod]
    public void FixedTranscriptAndGcmVectorMatchesIndependentNodeOpenSsl()
    {
        var transcript = new PairingTranscript("11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222",
            "windows", "android", Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray()),
            Convert.ToBase64String(PairingProtocol.Padded(BigInteger.One, 384)),
            Convert.ToBase64String(PairingProtocol.Padded(BigInteger.Two, 384)),
            Convert.ToBase64String(Enumerable.Repeat((byte)4, 32).ToArray()),
            Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        Assert.AreEqual("1cd2d910904b3e189e5ef4436e93515585152803c140b40c71733d34d5550604", Convert.ToHexStringLower(transcript.Hash()));
        var sessionKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using var windows = new PairingChannel(sessionKey, transcript, "windows");
        CryptographicOperations.ZeroMemory(sessionKey);
        windows.Encrypt(Bundle());
        var encrypted = new PairingFrame
        {
            Type = "encrypted", Purpose = "android-result", Nonce = "AAECAwQFBgcICQoL",
            Ciphertext = "7cXZDcnqJEHGb5d2nCLsqg==", Tag = "fsyrH4WV3lTXFxubE2OeuQ=="
        };
        Assert.IsTrue(windows.Decrypt<PairingResult>(encrypted).Success);
    }

    [DataTestMethod]
    [DataRow("proof")]
    [DataRow("verified")]
    public void EvidenceFramesCannotChangeSessionOrAddRole(string phase)
    {
        var session = PairingProtocol.NewSessionId();
        using var client = new SrpClientHandshake(session, "000001");
        using var server = new SrpServerHandshake(client.Hello, session, "000001", "android");
        var proof = client.AcceptChallenge(server.Challenge);
        if (phase == "proof")
        {
            Assert.ThrowsException<PairingProtocolException>(() => server.AcceptProof(proof with { ServerSession = PairingProtocol.NewSessionId() }));
        }
        else
        {
            var accepted = server.AcceptProof(proof);
            using var channel = accepted.Channel;
            Assert.ThrowsException<PairingProtocolException>(() => client.AcceptVerified(accepted.Verified with { ServerRole = "android" }));
        }
    }

    internal static void Reject(Action action)
    {
        try { action(); Assert.Fail("Expected protocol/cryptographic rejection."); }
        catch (Exception exception) when (exception is PairingProtocolException or CryptographicException or System.Text.Json.JsonException) { }
    }
}
