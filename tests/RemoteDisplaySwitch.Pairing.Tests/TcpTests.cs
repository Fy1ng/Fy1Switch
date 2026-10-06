using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Pairing;

namespace RemoteDisplaySwitch.Pairing.Tests;

[TestClass]
public sealed class TcpTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);

    private static async Task<PairingChannel> AndroidClientAsync(TcpClient socket, string session, string code, CancellationToken token)
    {
        using var client = new SrpClientHandshake(session, code, "android");
        var stream = socket.GetStream();
        await PairingTransport.WriteAsync(stream, client.Hello, token);
        var proof = client.AcceptChallenge(await PairingTransport.ReadAsync(stream, token));
        await PairingTransport.WriteAsync(stream, proof, token);
        return client.AcceptVerified(await PairingTransport.ReadAsync(stream, token));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WindowsHostWaitsForAuthenticatedAndroidResult(bool success)
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        var calls = 0;
        await using var host = await PairingHost.StartAsync(IPAddress.Loopback,
            () => { Interlocked.Increment(ref calls); return Task.FromResult(ProtocolTests.Bundle()); }, limiter: new PairingAttemptLimiter());
        var session = host.SessionId;
        var code = host.Code;
        Assert.AreEqual(0, calls);
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, host.Port, deadline.Token);
        using var android = await AndroidClientAsync(socket, session, code, deadline.Token);
        var bundle = android.Decrypt<PairBundle>(await PairingTransport.ReadAsync(socket.GetStream(), deadline.Token));
        Assert.AreEqual(ProtocolTests.Bundle(), bundle);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(host.Completion.IsCompleted);
        Assert.AreEqual("", host.Code);
        await PairingTransport.WriteAsync(socket.GetStream(), android.Encrypt(new PairingResult(success)), deadline.Token);
        Assert.AreEqual(success, await host.Completion.WaitAsync(deadline.Token));
        Assert.AreEqual(success ? "completed" : "failed", host.Status);
        Assert.AreEqual("", host.SessionId);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WindowsClientSendsBundleEvenAsSrpClient(bool success)
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var session = PairingProtocol.NewSessionId();
        var received = new TaskCompletionSource<PairBundle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = socket.GetStream();
            using var server = new SrpServerHandshake(await PairingTransport.ReadAsync(stream, deadline.Token), session, "000001", "android");
            await PairingTransport.WriteAsync(stream, server.Challenge, deadline.Token);
            var accepted = server.AcceptProof(await PairingTransport.ReadAsync(stream, deadline.Token));
            using var android = accepted.Channel;
            await PairingTransport.WriteAsync(stream, accepted.Verified, deadline.Token);
            received.SetResult(android.Decrypt<PairBundle>(await PairingTransport.ReadAsync(stream, deadline.Token)));
            await PairingTransport.WriteAsync(stream, android.Encrypt(new PairingResult(success)), deadline.Token);
        });
        var limiter = new PairingAttemptLimiter();
        using var client = new PairingClient(limiter);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.AreEqual(success, await client.ConnectAsync(IPAddress.Loopback, port, session, "000001", () => Task.FromResult(ProtocolTests.Bundle()), deadline.Token));
        Assert.AreEqual(ProtocolTests.Bundle(), await received.Task.WaitAsync(deadline.Token));
        await serverTask.WaitAsync(deadline.Token);
        Assert.IsFalse(await client.ConnectAsync(IPAddress.Loopback, port, session, "000001", () => throw new AssertFailedException("Consumed session must not call provider."), deadline.Token));
        using var replacement = new PairingClient(limiter);
        Assert.IsFalse(await replacement.ConnectAsync(IPAddress.Loopback, port, session, "000001", () => Task.FromResult(ProtocolTests.Bundle()), deadline.Token));
    }

    [TestMethod]
    public async Task InvalidM2NeverCallsWindowsBundleProvider()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var session = PairingProtocol.NewSessionId();
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = socket.GetStream();
            using var server = new SrpServerHandshake(await PairingTransport.ReadAsync(stream, deadline.Token), session, "000001", "android");
            await PairingTransport.WriteAsync(stream, server.Challenge, deadline.Token);
            var accepted = server.AcceptProof(await PairingTransport.ReadAsync(stream, deadline.Token));
            using var channel = accepted.Channel;
            await PairingTransport.WriteAsync(stream, accepted.Verified with { M2 = ProtocolTests.Flip(accepted.Verified.M2!) }, deadline.Token);
        });
        using var client = new PairingClient(new PairingAttemptLimiter());
        var calls = 0;
        Assert.IsFalse(await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, session, "000001",
            () => { calls++; return Task.FromResult(ProtocolTests.Bundle()); }, deadline.Token));
        await serverTask.WaitAsync(deadline.Token);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task HostRejectsConcurrentConnectionWithoutQueueingIt()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        await using var host = await PairingHost.StartAsync(IPAddress.Loopback, () => Task.FromResult(ProtocolTests.Bundle()), limiter: new PairingAttemptLimiter());
        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, host.Port, deadline.Token);
        using var handshake = new SrpClientHandshake(host.SessionId, host.Code, "android");
        await PairingTransport.WriteAsync(first.GetStream(), handshake.Hello, deadline.Token);
        var challenge = await PairingTransport.ReadAsync(first.GetStream(), deadline.Token);
        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, host.Port, deadline.Token);
        Assert.AreEqual(0, await second.GetStream().ReadAsync(new byte[1], deadline.Token));
        await PairingTransport.WriteAsync(first.GetStream(), handshake.AcceptChallenge(challenge), deadline.Token);
        using var channel = handshake.AcceptVerified(await PairingTransport.ReadAsync(first.GetStream(), deadline.Token));
        channel.Decrypt<PairBundle>(await PairingTransport.ReadAsync(first.GetStream(), deadline.Token));
        await PairingTransport.WriteAsync(first.GetStream(), channel.Encrypt(new PairingResult(true)), deadline.Token);
        Assert.IsTrue(await host.Completion.WaitAsync(deadline.Token));
    }

    [TestMethod]
    public async Task ValidProofThenDisconnectConsumesCodeWithoutSuccess()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        await using var host = await PairingHost.StartAsync(IPAddress.Loopback, () => Task.FromResult(ProtocolTests.Bundle()), limiter: new PairingAttemptLimiter());
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, host.Port, deadline.Token);
        using var android = await AndroidClientAsync(socket, host.SessionId, host.Code, deadline.Token);
        android.Decrypt<PairBundle>(await PairingTransport.ReadAsync(socket.GetStream(), deadline.Token));
        socket.Dispose();
        Assert.IsFalse(await host.Completion.WaitAsync(deadline.Token));
        Assert.AreEqual("", host.Code);
        Assert.AreNotEqual("completed", host.Status);
    }

    [TestMethod]
    public async Task StopClosesSocketAndClearsSessionWhileReading()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        await using var host = await PairingHost.StartAsync(IPAddress.Loopback, () => Task.FromResult(ProtocolTests.Bundle()), limiter: new PairingAttemptLimiter());
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, host.Port, deadline.Token);
        using var handshake = new SrpClientHandshake(host.SessionId, host.Code, "android");
        await PairingTransport.WriteAsync(socket.GetStream(), handshake.Hello, deadline.Token);
        await PairingTransport.ReadAsync(socket.GetStream(), deadline.Token);
        host.Stop();
        Assert.IsFalse(await host.Completion.WaitAsync(deadline.Token));
        Assert.AreEqual(0, await socket.GetStream().ReadAsync(new byte[1], deadline.Token));
        Assert.AreEqual("", host.Code);
        Assert.AreEqual("", host.SessionId);
    }

    [TestMethod]
    public async Task ExpiryAndHandshakeAndTotalDeadlinesCloseSockets()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        await using (var expiring = await PairingHost.StartAsync(IPAddress.Loopback, _ => Task.FromResult(ProtocolTests.Bundle()),
            limiter: new PairingAttemptLimiter(), lifetime: TimeSpan.FromMilliseconds(100)))
        {
            Assert.IsFalse(await expiring.Completion.WaitAsync(deadline.Token));
            Assert.AreEqual("expired", expiring.Status);
            Assert.AreEqual("", expiring.Code);
        }
        await using (var stalled = await PairingHost.StartAsync(IPAddress.Loopback, _ => Task.FromResult(ProtocolTests.Bundle()),
            limiter: new PairingAttemptLimiter(), timeouts: new PairingTimeouts(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))))
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, stalled.Port, deadline.Token);
            Assert.AreEqual(0, await socket.GetStream().ReadAsync(new byte[1], deadline.Token));
            Assert.IsFalse(stalled.Completion.IsCompleted);
        }
        await using (var noAck = await PairingHost.StartAsync(IPAddress.Loopback, _ => Task.FromResult(ProtocolTests.Bundle()),
            limiter: new PairingAttemptLimiter(), timeouts: new PairingTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3))))
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, noAck.Port, deadline.Token);
            using var android = await AndroidClientAsync(socket, noAck.SessionId, noAck.Code, deadline.Token);
            android.Decrypt<PairBundle>(await PairingTransport.ReadAsync(socket.GetStream(), deadline.Token));
            Assert.IsFalse(await noAck.Completion.WaitAsync(deadline.Token));
            Assert.AreEqual(0, await socket.GetStream().ReadAsync(new byte[1], deadline.Token));
        }
    }

    [TestMethod]
    public async Task OutboundDeadlineCancellationAndSharedLimitAreEnforced()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var limiter = new PairingAttemptLimiter();
        using var client = new PairingClient(limiter, new PairingTimeouts(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)));
        var connecting = client.ConnectAsync(IPAddress.Loopback, port, PairingProtocol.NewSessionId(), "000001", () => Task.FromResult(ProtocolTests.Bundle()), deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        Assert.IsFalse(await connecting.WaitAsync(deadline.Token));
        for (var attempt = 0; attempt < 9; attempt++) Assert.IsTrue(limiter.TryAdmit(PairingProtocol.NewSessionId()));
        Assert.IsFalse(await client.ConnectAsync(IPAddress.Loopback, port, PairingProtocol.NewSessionId(), "000001", () => Task.FromResult(ProtocolTests.Bundle()), deadline.Token));
        using var cancelled = new PairingClient(new PairingAttemptLimiter());
        var pending = cancelled.ConnectAsync(IPAddress.Loopback, port, PairingProtocol.NewSessionId(), "000001", () => Task.FromResult(ProtocolTests.Bundle()), deadline.Token);
        using var peer2 = await listener.AcceptTcpClientAsync(deadline.Token);
        cancelled.Stop();
        Assert.IsFalse(await pending.WaitAsync(deadline.Token));
        Assert.AreEqual("stopped", cancelled.Status);
    }

    [TestMethod]
    public async Task FrameSizeFragmentationInvalidUtf8AndDisconnectAreBounded()
    {
        using var client = new SrpClientHandshake(PairingProtocol.NewSessionId(), "000001");
        using var memory = new MemoryStream();
        await PairingTransport.WriteAsync(memory, client.Hello);
        memory.Position = 0;
        Assert.AreEqual(client.Hello, await PairingTransport.ReadAsync(memory));
        using var tooLarge = new MemoryStream(Enumerable.Repeat((byte)'x', PairingProtocol.MaxFrameBytes).ToArray());
        await Assert.ThrowsExceptionAsync<PairingProtocolException>(() => PairingTransport.ReadAsync(tooLarge));
        using var eof = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => PairingTransport.ReadAsync(eof));
        using var invalidUtf8 = new MemoryStream(new byte[] { 0xff, (byte)'\n' });
        await Assert.ThrowsExceptionAsync<PairingProtocolException>(() => PairingTransport.ReadAsync(invalidUtf8));
    }

    [TestMethod]
    public async Task WrongCodeOverTcpExhaustsExactlyFiveAdmissions()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        var calls = 0;
        await using var host = await PairingHost.StartAsync(IPAddress.Loopback,
            () => { calls++; return Task.FromResult(ProtocolTests.Bundle()); }, limiter: new PairingAttemptLimiter());
        var session = host.SessionId;
        var wrong = host.Code == "000000" ? "000001" : "000000";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, host.Port, deadline.Token);
            using var client = new SrpClientHandshake(session, wrong, "android");
            await PairingTransport.WriteAsync(socket.GetStream(), client.Hello, deadline.Token);
            var proof = client.AcceptChallenge(await PairingTransport.ReadAsync(socket.GetStream(), deadline.Token));
            await PairingTransport.WriteAsync(socket.GetStream(), proof, deadline.Token);
            Assert.AreEqual(0, await socket.GetStream().ReadAsync(new byte[1], deadline.Token));
        }
        Assert.IsFalse(await host.Completion.WaitAsync(deadline.Token));
        Assert.AreEqual("exhausted", host.Status);
        Assert.AreEqual(0, calls);
        Assert.AreEqual("", host.Code);
    }

    [TestMethod]
    public async Task TamperedAckNeverCompletesSuccessfully()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        await using var host = await PairingHost.StartAsync(IPAddress.Loopback, () => Task.FromResult(ProtocolTests.Bundle()), limiter: new PairingAttemptLimiter());
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, host.Port, deadline.Token);
        using var android = await AndroidClientAsync(socket, host.SessionId, host.Code, deadline.Token);
        android.Decrypt<PairBundle>(await PairingTransport.ReadAsync(socket.GetStream(), deadline.Token));
        var result = android.Encrypt(new PairingResult(true));
        await PairingTransport.WriteAsync(socket.GetStream(), result with { Tag = ProtocolTests.Flip(result.Tag!) }, deadline.Token);
        Assert.IsFalse(await host.Completion.WaitAsync(deadline.Token));
        Assert.AreEqual("failed", host.Status);
    }

    [TestMethod]
    public async Task UnresponsiveBundleCallbackCannotExtendTotalDeadline()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        var pending = new TaskCompletionSource<PairBundle>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await PairingHost.StartAsync(IPAddress.Loopback, _ => pending.Task,
            limiter: new PairingAttemptLimiter(), timeouts: new PairingTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)));
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, host.Port, deadline.Token);
        using var android = await AndroidClientAsync(socket, host.SessionId, host.Code, deadline.Token);
        Assert.IsFalse(await host.Completion.WaitAsync(deadline.Token));
        Assert.AreEqual("", host.Code);
        Assert.AreEqual(0, await socket.GetStream().ReadAsync(new byte[1], deadline.Token));
        pending.TrySetResult(ProtocolTests.Bundle());
    }
}
