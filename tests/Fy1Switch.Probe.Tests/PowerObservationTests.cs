using Microsoft.VisualStudio.TestTools.UnitTesting;
using PowerDisplay.Common.Drivers.DDC;
using Fy1Switch.PowerDisplay;
using DisplayMonitor = PowerDisplay.Common.Models.Monitor;

namespace Fy1Switch.Probe.Tests;

[TestClass]
public class PowerObservationTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    public async Task DiscreteReadingAcceptsZeroMaximum(int current)
    {
        var reader = new FakeReader(VcpReadAttempt.Success((uint)current, 0));
        using var controller = new DdcCiController(new MemoryVcpStore(), reader);
        var observation = await controller.ReadPowerObservationAsync(new DisplayMonitor { Handle = new IntPtr(123) });
        Assert.IsTrue(observation.Succeeded);
        Assert.AreEqual(current, observation.Value);
        Assert.AreEqual((byte)0xD6, reader.LastCode);
    }

    [TestMethod]
    public async Task FailedReadingIsUnknownNotOffOrDefaultOn()
    {
        using var controller = new DdcCiController(new MemoryVcpStore(), new FakeReader(VcpReadAttempt.Failure(50)));
        var observation = await controller.ReadPowerObservationAsync(new DisplayMonitor { Handle = new IntPtr(123) });
        Assert.IsFalse(observation.Succeeded);
        Assert.IsNull(observation.Value);
        Assert.AreEqual(50, observation.ErrorCode);
    }

    [TestMethod]
    public void UnexpectedValueIsNotClassifiedAsKnownState()
    {
        Assert.IsFalse(PowerReadObservation.FromNative(true, 77, 0).IsKnownPowerValue);
    }

    private sealed class FakeReader(VcpReadAttempt result) : IVcpFeatureReader
    {
        public byte LastCode { get; private set; }
        public VcpReadAttempt Read(IntPtr handle, byte code)
        {
            LastCode = code;
            return result;
        }
    }
}
