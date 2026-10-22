using PowerDisplay.Common.Models;

namespace Fy1Switch.Probe;

public sealed record PowerCycleResult(MonitorOperationResult Off, MonitorOperationResult On);

public static class PowerCycleRunner
{
    public static async Task<PowerCycleResult> RunAsync(
        Func<int, Task<MonitorOperationResult>> write,
        Func<TimeSpan, Task> delay,
        TimeSpan? offDuration = null)
    {
        var duration = offDuration ?? TimeSpan.FromSeconds(5);
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromSeconds(300))
        {
            throw new ArgumentOutOfRangeException(nameof(offDuration));
        }

        MonitorOperationResult off;
        MonitorOperationResult on;
        try
        {
            off = await write(4);
            if (off.IsSuccess)
            {
                await delay(duration);
            }
        }
        finally
        {
            on = await write(1);
        }

        return new(off, on);
    }
}
