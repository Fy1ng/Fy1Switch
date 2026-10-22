using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Contracts;
using Fy1Switch.Runtime;

namespace Fy1Switch.Runtime.Tests;

[TestClass]
public sealed class StateStoreTests
{
    [TestMethod]
    public void NewStoreHasStableDeviceIdAndNoInventedBindingOrOperations()
    {
        using var directory = new TestDirectory();
        Guid deviceId;
        using (var store = new StateStore(directory.Path))
        {
            deviceId = store.DeviceId;
            Assert.AreNotEqual(Guid.Empty, deviceId);
            Assert.IsNull(store.LoadBinding());
            Assert.IsNull(store.FindOperation(Guid.NewGuid()));
        }
        using var reopened = new StateStore(directory.Path);
        Assert.AreEqual(deviceId, reopened.DeviceId);
        Assert.IsNull(reopened.LoadBinding());
        using var otherDirectory = new TestDirectory();
        using var otherStore = new StateStore(otherDirectory.Path);
        Assert.AreNotEqual(deviceId, otherStore.DeviceId);
    }

    [TestMethod]
    public void BindingIncludingNeedsConfirmationRoundTripsAndCanBeDeleted()
    {
        using var directory = new TestDirectory();
        var binding = new MonitorBinding(Guid.NewGuid(), FakeWorkerTransport.TargetKey, "Saved monitor", "fake-profile", FakeWorkerTransport.Fingerprint, NeedsConfirmation: true);
        using (var store = new StateStore(directory.Path)) store.SaveBinding(binding);
        using (var reopened = new StateStore(directory.Path))
        {
            Assert.AreEqual(binding, reopened.LoadBinding());
            reopened.SaveBinding(null);
        }
        using var afterDeletion = new StateStore(directory.Path);
        Assert.IsNull(afterDeletion.LoadBinding());
    }

    [TestMethod]
    public void SavingOperationUpdatesOnlyItsOwnIdAndPersistsLatestRecord()
    {
        using var directory = new TestDirectory();
        var pending = Record(OperationOutcome.Pending);
        var independent = Record(OperationOutcome.Failed);
        var completed = pending with { Outcome = OperationOutcome.Confirmed, Observation = FakeWorkerTransport.Observe(DisplayPower.Off), UpdatedAt = DateTimeOffset.UtcNow };
        using (var store = new StateStore(directory.Path))
        {
            store.SaveOperation(pending);
            store.SaveOperation(independent);
            store.SaveOperation(completed);
            Assert.AreEqual(completed, store.FindOperation(pending.OperationId));
            Assert.AreEqual(independent, store.FindOperation(independent.OperationId));
        }
        using var reopened = new StateStore(directory.Path);
        Assert.AreEqual(completed, reopened.FindOperation(completed.OperationId));
        Assert.AreEqual(independent, reopened.FindOperation(independent.OperationId));
    }

    [DataTestMethod]
    [DataRow(OperationOutcome.Confirmed)]
    [DataRow(OperationOutcome.AcceptedUnconfirmed)]
    [DataRow(OperationOutcome.Failed)]
    [DataRow(OperationOutcome.Unknown)]
    public void RestartPreservesTerminalRecordsExactly(OperationOutcome outcome)
    {
        using var directory = new TestDirectory();
        var record = Record(outcome) with { ErrorCode = "saved-error", Observation = FakeWorkerTransport.Observe(DisplayPower.Unknown) };
        using (var store = new StateStore(directory.Path)) store.SaveOperation(record);
        using var reopened = new StateStore(directory.Path);
        Assert.AreEqual(record, reopened.FindOperation(record.OperationId));
    }

    [TestMethod]
    public void RestartConvertsAllPendingRecordsToUnknownWithoutChangingTheirIdentityOrIntent()
    {
        using var directory = new TestDirectory();
        var unresolved = Record(OperationOutcome.Pending) with { Requested = PowerIntent.Toggle, Resolved = null };
        var issued = Record(OperationOutcome.Pending);
        var terminal = Record(OperationOutcome.Confirmed);
        using (var store = new StateStore(directory.Path))
        {
            store.SaveOperation(unresolved);
            store.SaveOperation(issued);
            store.SaveOperation(terminal);
        }
        OperationRecord[] recovered;
        using (var reopened = new StateStore(directory.Path))
        {
            recovered = new[] { unresolved, issued }.Select(original =>
            {
                var actual = reopened.FindOperation(original.OperationId);
                Assert.IsNotNull(actual);
                Assert.AreEqual(OperationOutcome.Unknown, actual.Outcome);
                Assert.AreEqual("host_restarted", actual.ErrorCode);
                Assert.IsTrue(actual.UpdatedAt > original.UpdatedAt);
                Assert.AreEqual(original with { Outcome = OperationOutcome.Unknown, ErrorCode = "host_restarted", UpdatedAt = actual.UpdatedAt }, actual);
                return actual;
            }).ToArray();
            Assert.AreEqual(terminal, reopened.FindOperation(terminal.OperationId));
        }
        using var secondRestart = new StateStore(directory.Path);
        foreach (var record in recovered) Assert.AreEqual(record, secondRestart.FindOperation(record.OperationId));
        Assert.AreEqual(terminal, secondRestart.FindOperation(terminal.OperationId));
    }

    private static OperationRecord Record(OperationOutcome outcome) => new(Guid.NewGuid(), Guid.NewGuid(), PowerIntent.Off, PowerIntent.Off, outcome, null, null, DateTimeOffset.UtcNow.AddHours(-1));
}
