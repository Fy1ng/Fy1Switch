using System.Threading;
using System.Threading.Tasks;
using Monitor = PowerDisplay.Common.Models.Monitor;
using RemoteDisplaySwitch.PowerDisplay;

namespace PowerDisplay.Common.Drivers.DDC;

public partial class DdcCiController
{
    public Task<PowerReadObservation> ReadPowerObservationAsync(Monitor monitor, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var reading = _vcpReader.Read(monitor.Handle, NativeConstants.VcpCodePowerMode);
            return PowerReadObservation.FromNative(reading.IsSuccess, reading.Current, reading.ErrorCode);
        }, cancellationToken);
    }
}
