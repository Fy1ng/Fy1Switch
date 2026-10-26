using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Contracts;
using Fy1Switch.Runtime;

namespace Fy1Switch.Runtime.Tests;

[TestClass]
public sealed class WireProtocolTests
{
    [TestMethod]
    public async Task OversizedMessageIsRejectedAtDefaultLimit()
    {
        using var reader = Reader(new string('x', 65537) + "\n");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => WireJson.ReadLineAsync(reader, CancellationToken.None));
    }

    [TestMethod]
    public async Task MessageExactlyAtLimitIsAcceptedAndNextMessageRemainsSeparate()
    {
        var message = new string('x', 65536);
        using var reader = Reader(message + "\r\nnext\n");
        Assert.AreEqual(message, await WireJson.ReadLineAsync(reader, CancellationToken.None));
        Assert.AreEqual("next", await WireJson.ReadLineAsync(reader, CancellationToken.None));
        Assert.IsNull(await WireJson.ReadLineAsync(reader, CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow("{\"command\":\"status\"}")]
    [DataRow("{\"command\":\"status\"}\r")]
    public async Task UnterminatedMessageIsRejectedEvenIfJsonWouldBeValid(string text)
    {
        using var reader = Reader(text);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => WireJson.ReadLineAsync(reader, CancellationToken.None));
    }

    [TestMethod]
    public async Task CleanEofReturnsNull()
    {
        using var reader = Reader("");
        Assert.IsNull(await WireJson.ReadLineAsync(reader, CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(int.MaxValue)]
    public async Task UnsupportedRequestVersionIsPreservedByWireJsonAndRejectedBeforeDispatch(int version)
    {
        var request = new LocalRequest(Guid.NewGuid(), "status", Version: version);
        using var reader = Reader(WireJson.Serialize(request) + "\n");
        var decoded = WireJson.Parse<LocalRequest>((await WireJson.ReadLineAsync(reader, CancellationToken.None))!);
        Assert.AreEqual(request, decoded);

        var reply = await LocalDispatcher.DispatchAsync(null!, decoded);

        Assert.IsFalse(reply.Success);
        Assert.AreEqual(request.RequestId, reply.RequestId);
        Assert.AreEqual("protocol_version_or_id_invalid", reply.ErrorCode);
        Assert.IsNull(reply.Snapshot);
    }

    [TestMethod]
    public async Task EmptyRequestIdIsRejectedBeforeDispatch()
    {
        var reply = await LocalDispatcher.DispatchAsync(null!, new(Guid.Empty, "status"));
        Assert.IsFalse(reply.Success);
        Assert.AreEqual("protocol_version_or_id_invalid", reply.ErrorCode);
    }

    [TestMethod]
    public void WorkerRequestRoundTripKeepsExplicitIntentTargetAndVersion()
    {
        var request = new WorkerRequest(Guid.NewGuid(), "set", FakeWorkerTransport.TargetKey, FakeWorkerTransport.Fingerprint, PowerIntent.Off);
        Assert.AreEqual(request, WireJson.Parse<WorkerRequest>(WireJson.Serialize(request)));
    }

    [TestMethod]
    public void ExistingWorkerJsonRemainsReadableWithoutDisplayControlFields()
    {
        var id = Guid.NewGuid();
        var request = WireJson.Parse<WorkerRequest>($"{{\"requestId\":\"{id}\",\"command\":\"read\",\"version\":1}}");
        Assert.AreEqual(id, request.RequestId);
        Assert.IsNull(request.Control);
        Assert.IsNull(request.Value);
        var reply = WireJson.Parse<WorkerReply>($"{{\"requestId\":\"{id}\",\"success\":true,\"version\":1}}");
        Assert.IsNull(reply.Controls);
        Assert.IsNull(reply.ControlWrite);
    }

    [TestMethod]
    public void DisplayControlMessagesKeepFeatureValueAndUnconfirmedAcceptance()
    {
        var request = new WorkerRequest(Guid.NewGuid(), "set-control", Control: DisplayControl.InputSource, Value: 0x11);
        Assert.AreEqual(request, WireJson.Parse<WorkerRequest>(WireJson.Serialize(request)));
        var reply = new WorkerReply(request.RequestId, true, ControlWrite: new(true, null, false, "control_read_failed"));
        Assert.AreEqual(reply, WireJson.Parse<WorkerReply>(WireJson.Serialize(reply)));
    }

    private static StreamReader Reader(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
}
