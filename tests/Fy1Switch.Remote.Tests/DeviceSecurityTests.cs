using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Contracts;
using Fy1Switch.Remote;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class DeviceSecurityTests
{
    [TestMethod]
    public void PairingIsSingleUseAndBundleHasFiveMinuteLifetime()
    {
        using var fixture = new RemoteFixture();
        var bundle = fixture.Security.BeginPairing(fixture.DeviceId, "https://localhost:48152/");
        Assert.AreEqual(1, bundle.Version);
        Assert.AreEqual(fixture.DeviceId, bundle.DeviceId);
        Assert.AreEqual("https://localhost:48152", bundle.Endpoint);
        Assert.AreEqual(fixture.Security.CertificatePin, bundle.CertificatePin);
        Assert.AreEqual(fixture.Clock.Now.AddMinutes(5), bundle.ExpiresAt);
        var request = new PairRequest(bundle.Code, "  Test client  ");
        var reply = fixture.Gateway.Pair(request);
        Assert.IsNotNull(reply);
        Assert.AreNotEqual(Guid.Empty, reply.ClientId);
        Assert.AreEqual(64, reply.Token.Length);
        Assert.IsTrue(fixture.Security.Authenticate(reply.Token));
        Assert.IsNull(fixture.Gateway.Pair(request));
        Assert.AreEqual("Test client", fixture.Security.Clients.Single().Name);
    }

    [DataTestMethod]
    [DataRow(299999, true)]
    [DataRow(300000, false)]
    [DataRow(300001, false)]
    public void PairingExpiryUsesInjectedClock(int milliseconds, bool accepted)
    {
        using var fixture = new RemoteFixture();
        var bundle = fixture.Security.BeginPairing(fixture.DeviceId, "https://localhost:48152");
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Assert.AreEqual(accepted, fixture.Gateway.Pair(new(bundle.Code, "Test client")) is not null);
        Assert.AreEqual(accepted ? 1 : 0, fixture.Security.Clients.Count);
    }

    [TestMethod]
    public void WrongCodeDoesNotConsumeValidCode()
    {
        using var fixture = new RemoteFixture();
        var bundle = fixture.Security.BeginPairing(fixture.DeviceId, "https://localhost:48152");
        var wrongCode = (bundle.Code[0] == '0' ? "1" : "0") + bundle.Code[1..];
        Assert.IsNull(fixture.Gateway.Pair(new(wrongCode, "Test client")));
        Assert.AreEqual(0, fixture.Security.Clients.Count);
        Assert.IsNotNull(fixture.Gateway.Pair(new(bundle.Code, "Test client")));
    }

    [TestMethod]
    public void ReplacingOrCancellingPairingInvalidatesPreviousCode()
    {
        using var fixture = new RemoteFixture();
        var first = fixture.Security.BeginPairing(fixture.DeviceId, "https://localhost:48152");
        var second = fixture.Security.BeginPairing(fixture.DeviceId, "https://localhost:48152");
        Assert.IsNull(fixture.Gateway.Pair(new(first.Code, "Test client")));
        fixture.Security.CancelPairing();
        Assert.IsNull(fixture.Gateway.Pair(new(second.Code, "Test client")));
        Assert.AreEqual(0, fixture.Security.Clients.Count);
    }

    [TestMethod]
    public async Task ConcurrentPairAttemptsIssueExactlyOneToken()
    {
        using var fixture = new RemoteFixture();
        var bundle = fixture.Security.BeginPairing(fixture.DeviceId, "https://localhost:48152");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 12).Select(async index =>
        {
            await start.Task;
            return fixture.Gateway.Pair(new(bundle.Code, $"Test client {index}"));
        }).ToArray();
        start.SetResult();
        var replies = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, replies.Count(reply => reply is not null));
        Assert.AreEqual(1, fixture.Security.Clients.Count);
    }

    [TestMethod]
    public void ThirtyTwoClientLimitPersistsAndRevocationFreesOneSlot()
    {
        using var fixture = new RemoteFixture();
        var clients = Enumerable.Range(0, 32).Select(_ => fixture.Pair()).ToArray();
        Assert.AreEqual(32, clients.Select(client => client.ClientId).Distinct().Count());
        using var restarted = new DeviceSecurity(fixture.Directory.Path, fixture.Protector, fixture.Clock.Read);
        var bundle = restarted.BeginPairing(fixture.DeviceId, "https://localhost:48152");
        Assert.IsNull(restarted.CompletePairing(fixture.DeviceId, new(bundle.Code, "Client 33")));
        Assert.AreEqual(32, restarted.Clients.Count);
        restarted.Revoke(clients[0].ClientId);
        Assert.IsFalse(restarted.Authenticate(clients[0].Token));
        Assert.IsTrue(restarted.Authenticate(clients[1].Token));
        Assert.IsNotNull(restarted.CompletePairing(fixture.DeviceId, new(bundle.Code, "Replacement client")));
        Assert.AreEqual(32, restarted.Clients.Count);
    }

    [TestMethod]
    public void RestartPreservesPinAndTokenHashButNotRevokedAccessOrPendingCode()
    {
        using var directory = new TestDirectory();
        var protector = new TestOnlyEncodingProtector(directory);
        var clock = new TestClock();
        var deviceId = Guid.NewGuid();
        string pin;
        PairReply client;
        PairBundle pending;
        using (var original = new DeviceSecurity(directory.Path, protector, clock.Read))
        {
            pin = original.CertificatePin;
            var bundle = original.BeginPairing(deviceId, "https://localhost:48152");
            client = original.CompletePairing(deviceId, new(bundle.Code, "Persistent client"))!;
            Assert.IsNotNull(client);
            pending = original.BeginPairing(deviceId, "https://localhost:48152");
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.Token)));
            Assert.AreEqual(expectedHash, original.Clients.Single().TokenHash);
            foreach (var file in Directory.EnumerateFiles(directory.Path))
                Assert.IsFalse(File.ReadAllText(file).Contains(client.Token, StringComparison.Ordinal), "A persisted file contains the bearer token.");
        }
        using (var restarted = new DeviceSecurity(directory.Path, protector, clock.Read))
        {
            Assert.AreEqual(pin, restarted.CertificatePin);
            Assert.IsTrue(restarted.Authenticate(client.Token));
            Assert.IsNull(restarted.CompletePairing(deviceId, new(pending.Code, "Stale pairing")));
            restarted.Revoke(client.ClientId);
            Assert.IsFalse(restarted.Authenticate(client.Token));
        }
        using var afterRevocation = new DeviceSecurity(directory.Path, protector, clock.Read);
        Assert.AreEqual(pin, afterRevocation.CertificatePin);
        Assert.IsFalse(afterRevocation.Authenticate(client.Token));
        Assert.AreEqual(0, afterRevocation.Clients.Count);
        Assert.AreEqual(1, protector.ProtectCalls);
        Assert.AreEqual(2, protector.UnprotectCalls);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not-a-token")]
    [DataRow("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void InvalidBearerTokensAreRejected(string? token)
    {
        using var fixture = new RemoteFixture();
        fixture.Pair();
        Assert.IsFalse(fixture.Security.Authenticate(token));
    }
}
