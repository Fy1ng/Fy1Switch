using PowerDisplay.Common.Models;

namespace RemoteDisplaySwitch.Probe;

public sealed record PowerCycleResult(MonitorOperationResult Off, MonitorOperationResult On);

public static class PowerCycleRunner
{
    public static async Task<PowerCycleResult> RunAsync(
        Func<int, Task<MonitorOperationResult>> write,
        Func<TimeSpan, Task> delay)
    {
        MonitorOperationResult off;
        MonitorOperationResult on;
        try
        {
            off = await write(4);
            if (off.IsSuccess)
            {
                await delay(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            on = await write(1);
        }

        return new(off, on);
    }
}
