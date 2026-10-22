using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Contracts;
using Fy1Switch.Relay;

namespace Fy1Switch.Relay.Tests;

[TestClass]
public sealed class LifecycleTests
{
    [TestMethod]
    public async Task OfflineControlAndPairReturn503WithoutQueueing()
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var offline = await fixture.PostAsync("control", fixture.Command());
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
        using var offlinePair = await fixture.PostAsync("pair", new PairRequest("fixture", "fixture"));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, offlinePair.StatusCode);
        using var socket = await fixture.ConnectAsync();
        var fresh = fixture.Command();
        var post = fixture.PostAsync("control", fresh);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        Assert.AreEqual(fresh, envelope.Request);
        await RelayFixture.ReplyAsync(socket, new RelayResponse(envelope.Id, 200, new RemoteReply(fresh.RequestId, true)));
        using var response = await post;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task ExpiredRequestImmediatelyReturns504WithoutForwarding()
    {
        await using var fixture = await RelayFixture.StartAsync(timeout: 100);
        using var response = await fixture.PostAsync("control", fixture.Command(lifetime: -1));
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RequestDeadlineOrConfiguredTimeoutReturns504AndLateReplyIsIgnored(bool shortDeadline)
    {
        await using var fixture = await RelayFixture.StartAsync(timeout: shortDeadline ? 5 : 0.25);
        using var socket = await fixture.ConnectAsync();
        var command = fixture.Command(lifetime: shortDeadline ? 0.3 : 30);
        var watch = Stopwatch.StartNew();
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        using var response = await post;
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2));
        await RelayFixture.ReplyAsync(socket, new RelayResponse(envelope.Id, 200, new RemoteReply(command.RequestId, true)));
        using var replay = await fixture.PostAsync("control", command with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) });
        Assert.AreEqual(HttpStatusCode.Conflict, replay.StatusCode);
        var fresh = fixture.Command();
        var next = fixture.PostAsync("control", fresh);
        var nextEnvelope = await RelayFixture.ReceiveAsync(socket);
        Assert.AreEqual(fresh, nextEnvelope.Request);
        await RelayFixture.ReplyAsync(socket, new RelayResponse(nextEnvelope.Id, 200, new RemoteReply(fresh.RequestId, true)));
        using var nextResponse = await next;
        Assert.AreEqual(HttpStatusCode.OK, nextResponse.StatusCode);
    }

    [TestMethod]
    public async Task PairHasBoundedTimeoutAndNoInventedToken()
    {
        await using var fixture = await RelayFixture.StartAsync(timeout: 0.25);
        using var socket = await fixture.ConnectAsync();
        var post = fixture.PostAsync("pair", new PairRequest("fixture", "fixture"));
        await RelayFixture.ReceiveAsync(socket);
        using var response = await post;
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DuplicatePendingAndCompletedRequestsReturn409()
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var command = fixture.Command();
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        using var pendingReplay = await fixture.PostAsync("control", command);
        Assert.AreEqual(HttpStatusCode.Conflict, pendingReplay.StatusCode);
        await RelayFixture.ReplyAsync(socket, new RelayResponse(envelope.Id, 200, new RemoteReply(command.RequestId, true)));
        using var response = await post;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var completedReplay = await fixture.PostAsync("control", command);
        Assert.AreEqual(HttpStatusCode.Conflict, completedReplay.StatusCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisconnectCancelsPendingAndReconnectNeverReplaysOldCommand(bool graceful)
    {
        await using var fixture = await RelayFixture.StartAsync(timeout: 100);
        using var socket = await fixture.ConnectAsync();
        var command = fixture.Command();
        var post = fixture.PostAsync("control", command);
        await RelayFixture.ReceiveAsync(socket);
        if (graceful) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture", CancellationToken.None);
        else socket.Abort();
        using var response = await post.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var reconnected = await ReconnectAsync(fixture);
        using var replay = await fixture.PostAsync("control", command);
        Assert.AreEqual(HttpStatusCode.Conflict, replay.StatusCode);
        var fresh = fixture.Command();
        var next = fixture.PostAsync("control", fresh);
        var envelope = await RelayFixture.ReceiveAsync(reconnected);
        Assert.AreEqual(fresh, envelope.Request);
        await RelayFixture.ReplyAsync(reconnected, new RelayResponse(envelope.Id, 200, new RemoteReply(fresh.RequestId, true)));
        using var nextResponse = await next;
        Assert.AreEqual(HttpStatusCode.OK, nextResponse.StatusCode);
    }

    [TestMethod]
    public async Task SixteenPendingShareOneSendLockAndSeventeenthIsRejected()
    {
        await using var fixture = await RelayFixture.StartAsync(timeout: 100);
        using var socket = await fixture.ConnectAsync();
        var commands = Enumerable.Range(0, 16).Select(_ => fixture.Command()).ToArray();
        var posts = commands.Select(command => fixture.PostAsync("control", command)).ToArray();
        var received = new List<RelayEnvelope>();
        for (var index = 0; index < 16; index++) received.Add(await RelayFixture.ReceiveAsync(socket));
        CollectionAssert.AreEquivalent(commands.Select(command => command.RequestId).ToArray(), received.Select(envelope => envelope.Id).ToArray());
        using var excess = await fixture.PostAsync("control", fixture.Command());
        Assert.AreEqual(HttpStatusCode.TooManyRequests, excess.StatusCode);
        using var excessPair = await fixture.PostAsync("pair", new PairRequest("fixture", "fixture"));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, excessPair.StatusCode);
        foreach (var envelope in received.AsEnumerable().Reverse())
            await RelayFixture.ReplyAsync(socket, new RelayResponse(envelope.Id, 200, new RemoteReply(envelope.Id, true)));
        foreach (var response in await Task.WhenAll(posts))
        {
            using (response) Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [TestMethod]
    public async Task ClientCancellationFreesPendingButDoesNotPermitReplay()
    {
        await using var fixture = await RelayFixture.StartAsync(timeout: 100);
        using var socket = await fixture.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var command = fixture.Command();
        var post = fixture.PostAsync("control", command, cancellationToken: cancellation.Token);
        await RelayFixture.ReceiveAsync(socket);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await post);
        using var replay = await fixture.PostAsync("control", command);
        Assert.AreEqual(HttpStatusCode.Conflict, replay.StatusCode);
        var fresh = fixture.Command();
        var next = fixture.PostAsync("control", fresh);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        await RelayFixture.ReplyAsync(socket, new RelayResponse(envelope.Id, 200, new RemoteReply(fresh.RequestId, true)));
        using var response = await next;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    [DataRow("oversize", 1009)]
    [DataRow("fragmented-oversize", 1009)]
    [DataRow("binary", 1003)]
    [DataRow("malformed", 1007)]
    public async Task InvalidWebSocketMessageClosesSocketAndCancelsPending(string invalid, int expectedClose)
    {
        await using var fixture = await RelayFixture.StartAsync(timeout: 100);
        using var socket = await fixture.ConnectAsync();
        var post = fixture.PostAsync("control", fixture.Command());
        await RelayFixture.ReceiveAsync(socket);
        var bytes = invalid.Contains("oversize", StringComparison.Ordinal) ? new byte[RelayRouter.MessageLimit + 1] : Encoding.UTF8.GetBytes("{");
        var type = invalid == "binary" ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
        if (invalid == "fragmented-oversize")
        {
            await socket.SendAsync(bytes.AsMemory(0, 40000), type, false, CancellationToken.None);
            await socket.SendAsync(bytes.AsMemory(40000), type, true, CancellationToken.None);
        }
        else await socket.SendAsync(bytes.AsMemory(), type, true, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var close = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[128]), timeout.Token);
        Assert.AreEqual(WebSocketMessageType.Close, close.MessageType);
        Assert.AreEqual(expectedClose, (int)close.CloseStatus!);
        using var response = await post.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static async Task<ClientWebSocket> ReconnectAsync(RelayFixture fixture)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (true)
        {
            var socket = fixture.NewSocket();
            try { await fixture.ConnectAsync(socket); return socket; }
            catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.Conflict && DateTimeOffset.UtcNow < deadline)
            {
                socket.Dispose();
                await Task.Delay(20);
            }
        }
    }
}
