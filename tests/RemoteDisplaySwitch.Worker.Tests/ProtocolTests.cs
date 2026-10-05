using System.Text;
using ManagedCommon;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Worker;

namespace RemoteDisplaySwitch.Worker.Tests;

[TestClass]
public sealed class ProtocolTests
{
    [TestMethod]
    public async Task SimulationDiscoversReadsAndExplicitlySetsWithoutHardware()
    {
        using var provider = new SimulationDisplayProvider();
        var requests = new[]
        {
            ProtocolFixture.Request("discover"), ProtocolFixture.Request("read"),
            ProtocolFixture.Request("set", PowerIntent.Off), ProtocolFixture.Request("read"),
            ProtocolFixture.Request("set", PowerIntent.On), ProtocolFixture.Request("shutdown"),
            ProtocolFixture.Request("set", PowerIntent.Off),
        };
        var replies = await ProtocolFixture.RunAsync(ProtocolFixture.Lines(requests), provider);
        Assert.AreEqual(6, replies.Length);
        CollectionAssert.AreEqual(requests.Take(6).Select(request => request.RequestId).ToArray(), replies.Select(reply => reply.RequestId).ToArray());
        Assert.IsTrue(replies.All(reply => reply.Success && reply.Version == 1));
        Assert.AreEqual(new string('b', 64), replies[0].Discovery!.TopologyFingerprint);
        Assert.AreEqual(new MonitorCandidate(new string('a', 64), "模拟显示器", "simulation"), replies[0].Discovery!.Candidates.Single());
        CollectionAssert.AreEqual(new[] { DisplayPower.On, DisplayPower.Off, DisplayPower.Off, DisplayPower.On }, replies.Skip(1).Take(4).Select(reply => reply.Observation!.State).ToArray());
        Assert.IsTrue(replies.Skip(1).Take(4).All(reply => reply.Observation!.Source == "simulation"));
        Assert.AreEqual(true, replies[2].WriteAccepted);
        Assert.AreEqual(4, replies[2].Observation!.RawValue);
        Assert.AreEqual(1, replies[4].Observation!.RawValue);
    }

    [TestMethod]
    public async Task ToggleUnknownIntentAndTargetAreRejectedWithoutChangingSimulation()
    {
        using var provider = new SimulationDisplayProvider();
        var protocol = new WorkerProtocol(provider);
        await protocol.HandleAsync(ProtocolFixture.Request("discover"));
        foreach (var intent in new PowerIntent?[] { PowerIntent.Toggle, (PowerIntent)99, null })
        {
            var reply = await protocol.HandleAsync(ProtocolFixture.Request("set", intent));
            Assert.AreEqual("invalid_intent", reply.ErrorCode);
            Assert.AreEqual(false, reply.WriteAccepted);
        }

        var targetReply = await protocol.HandleAsync(ProtocolFixture.Request("set", PowerIntent.Off) with { TargetKey = new string('c', 64) });
        Assert.AreEqual("target_mismatch", targetReply.ErrorCode);
        var fingerprintReply = await protocol.HandleAsync(ProtocolFixture.Request("set", PowerIntent.Off) with { TopologyFingerprint = new string('c', 64) });
        Assert.AreEqual("topology_changed", fingerprintReply.ErrorCode);
        Assert.AreEqual(DisplayPower.On, (await protocol.HandleAsync(ProtocolFixture.Request("read"))).Observation!.State);
    }

    [TestMethod]
    public async Task SimulationRequiresDiscoveryAndDoesNotCreateADataDirectory()
    {
        using var directory = new TestDirectory();
        var nonexistent = Path.Combine(directory.Path, "not-created");
        using var provider = WorkerApplication.CreateProvider(new("pipe", Environment.ProcessId, nonexistent, true));
        Assert.IsInstanceOfType<SimulationDisplayProvider>(provider);
        Assert.AreEqual("discovery_required", (await new WorkerProtocol(provider).HandleAsync(ProtocolFixture.Request("read"))).ErrorCode);
        Assert.IsFalse(Directory.Exists(nonexistent));
    }

    [TestMethod]
    public async Task MalformedVersionIntentAndDuplicateEnvelopesDoNotReachProvider()
    {
        using var provider = new FakeProvider();
        var request = ProtocolFixture.Request("discover");
        var id = request.RequestId.ToString();
        var malformed = new[]
        {
            "{", "null", "[]", "{}",
            $$"""{"requestId":"{{id}}","command":"discover"}""",
            $$"""{"requestId":"{{id}}","command":"discover","version":"1"}""",
            $$"""{"requestId":"{{id}}","command":"discover","version":1,"Version":1}""",
            $$"""{"requestId":"{{id}}","command":"set","version":1,"intent":"nonsense"}""",
        };
        var replies = await ProtocolFixture.RunAsync(string.Join('\n', malformed) + "\n" +
            ProtocolFixture.Lines(request with { Version = 2 }, request with { Version = 0 }, request with { Command = "unknown" }), provider);
        Assert.AreEqual(11, replies.Length);
        Assert.IsTrue(replies.Take(8).All(reply => reply.ErrorCode == "invalid_request"));
        Assert.AreEqual(id, replies[7].RequestId.ToString());
        Assert.AreEqual("unsupported_version", replies[8].ErrorCode);
        Assert.AreEqual("unsupported_version", replies[9].ErrorCode);
        Assert.AreEqual("unknown_command", replies[10].ErrorCode);
        Assert.AreEqual(0, provider.Calls);
    }

    [TestMethod]
    public async Task InvalidRequestsNeverCallProvider()
    {
        using var provider = new FakeProvider();
        var protocol = new WorkerProtocol(provider);
        foreach (var request in new[]
        {
            ProtocolFixture.Request("set", PowerIntent.Toggle),
            ProtocolFixture.Request("read") with { TargetKey = null },
            ProtocolFixture.Request("set", PowerIntent.Off) with { TopologyFingerprint = "bad" },
            ProtocolFixture.Request("discover") with { RequestId = Guid.Empty },
            ProtocolFixture.Request("shutdown") with { Version = 2 },
        })
            Assert.IsFalse((await protocol.HandleAsync(request)).Success);
        Assert.AreEqual(0, provider.Calls);
    }

    [TestMethod]
    public async Task RequestsAreSerialAndCleanEofStopsLoop()
    {
        using var provider = new FakeProvider();
        var requests = Enumerable.Range(0, 20).Select(_ => ProtocolFixture.Request("discover")).ToArray();
        var replies = await ProtocolFixture.RunAsync(ProtocolFixture.Lines(requests), provider);
        Assert.AreEqual(20, replies.Length);
        Assert.AreEqual(20, provider.Calls);
        Assert.AreEqual(1, provider.MaximumConcurrentCalls);
        Assert.AreEqual(0, (await ProtocolFixture.RunAsync("", provider)).Length);
    }

    [TestMethod]
    public async Task OversizedAndIncompleteLinesFailBeforeDispatch()
    {
        using var provider = new FakeProvider();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => ProtocolFixture.RunAsync(new string('x', 65537) + "\n", provider));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => ProtocolFixture.RunAsync(WireJson.Serialize(ProtocolFixture.Request("discover")), provider));
        Assert.AreEqual(0, provider.Calls);
    }

    [TestMethod]
    public async Task InvalidUtf8FailsBeforeDispatch()
    {
        using var provider = new FakeProvider();
        using var source = new MemoryStream([0xFF, 0x0A]);
        using var output = new MemoryStream();
        using var reader = new StreamReader(source, new UTF8Encoding(false, true));
        using var writer = new StreamWriter(output);
        await Assert.ThrowsExceptionAsync<DecoderFallbackException>(() => new WorkerProtocol(provider).RunAsync(reader, writer));
        Assert.AreEqual(0, provider.Calls);
    }

    [TestMethod]
    public async Task LogsAndFailureRepliesNeverContainRawDevicePaths()
    {
        using var provider = new FakeProvider { Failure = new IOException(FakeBackend.PhysicalId) };
        var oldError = Console.Error;
        var oldSink = Logger.Sink;
        using var log = new StringWriter();
        try
        {
            Console.SetError(log);
            SafeWorkerLog.Configure();
            Logger.LogError(FakeBackend.PhysicalId + " DISPLAY\\unredacted path handle=0x1234");
            var replies = await ProtocolFixture.RunAsync(ProtocolFixture.Lines(ProtocolFixture.Request("discover")), provider);
            Assert.AreEqual("provider_failed", replies.Single().ErrorCode);
            Assert.IsFalse(WireJson.Serialize(replies).Contains("DISPLAY", StringComparison.Ordinal));
            Assert.IsFalse(log.ToString().Contains("DISPLAY", StringComparison.Ordinal));
            Assert.IsFalse(log.ToString().Contains("physical-test", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetError(oldError);
            Logger.Sink = oldSink;
        }
    }
}
