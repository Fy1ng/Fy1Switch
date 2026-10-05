using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ManagedCommon;
using Microsoft.Win32.SafeHandles;

namespace RemoteDisplaySwitch.Worker;

internal static class Program
{
    public static Task<int> Main(string[] args) => WorkerApplication.RunAsync(args);
}

internal static class WorkerApplication
{
    public static async Task<int> RunAsync(string[] args, Func<WorkerOptions, IDisplayProvider>? providerFactory = null)
    {
        if (!WorkerOptions.TryParse(args, out var options))
        {
            SafeWorkerLog.Write("invalid_arguments");
            return 2;
        }

        SafeWorkerLog.Configure();
        try
        {
            using var pipe = new NamedPipeClientStream(".", options!.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(10000);
            if (!NativePipe.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != options.ParentPid)
            {
                SafeWorkerLog.Write("pipe_peer_mismatch");
                return 3;
            }

            using var provider = (providerFactory ?? CreateProvider)(options);
            var encoding = new UTF8Encoding(false, true);
            using var reader = new StreamReader(pipe, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, encoding, leaveOpen: true) { NewLine = "\n" };
            await new WorkerProtocol(provider).RunAsync(reader, writer);
            return 0;
        }
        catch (Exception)
        {
            SafeWorkerLog.Write("worker_connection_or_protocol_failed");
            return 4;
        }
    }

    internal static IDisplayProvider CreateProvider(WorkerOptions options) => options.Simulate
        ? new SimulationDisplayProvider()
        : new HardwareDisplayProvider(options.DataDirectory);
}

internal static class NativePipe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}

internal static class SafeWorkerLog
{
    public static void Configure() => Logger.Sink = message => Write(
        message.Contains("[error]", StringComparison.Ordinal) ? "powerdisplay_error" :
        message.Contains("[warning]", StringComparison.Ordinal) ? "powerdisplay_warning" : "powerdisplay_event");

    public static void Write(string code) => Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {code}");
}
