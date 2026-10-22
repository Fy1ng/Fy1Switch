namespace Fy1Switch.Probe;

public sealed record ProbeProcessResult(int ExitCode, bool OffAcknowledged, bool TerminatedAfterOff);
public sealed record RestartCycleResult(ProbeProcessResult Writer, int RestoreExitCode)
{
    public bool Succeeded => Writer.ExitCode == 0 && Writer.OffAcknowledged && Writer.TerminatedAfterOff && RestoreExitCode == 0;
}

public static class RestartCycleRunner
{
    public static async Task<RestartCycleResult> RunAsync(
        Func<Task<ProbeProcessResult>> stopWriterAfterOff,
        Func<TimeSpan, Task> delay,
        Func<Task<int>> restoreFromFreshProcess,
        TimeSpan offDuration)
    {
        if (offDuration < TimeSpan.FromSeconds(1) || offDuration > TimeSpan.FromSeconds(300))
        {
            throw new ArgumentOutOfRangeException(nameof(offDuration));
        }

        ProbeProcessResult writer;
        int restored;
        try
        {
            writer = await stopWriterAfterOff();
            if (writer.ExitCode == 0 && writer.OffAcknowledged && writer.TerminatedAfterOff)
            {
                await delay(offDuration);
            }
        }
        finally
        {
            restored = await restoreFromFreshProcess();
        }

        return new(writer, restored);
    }
}
