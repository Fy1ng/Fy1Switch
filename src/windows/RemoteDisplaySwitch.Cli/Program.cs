using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Runtime;

namespace RemoteDisplaySwitch.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length == 0 || args[0] == "--help")
        {
            Console.WriteLine("RemoteDisplaySwitch.Cli status | discover | bind <targetKey> <fingerprint> [name] | unbind | refresh | on/off/toggle <targetId> <operationId> | operation <operationId> | startup on/off [--pipe name] [--host-path path]");
            return 0;
        }
        try
        {
            var arguments = args.ToList();
            string ReadOption(string name, string fallback)
            {
                var index = arguments.IndexOf(name);
                if (index < 0) return fallback;
                if (index + 1 >= arguments.Count) throw new ControlException("invalid_request");
                var value = arguments[index + 1]; arguments.RemoveRange(index, 2); return value;
            }
            var pipe = ReadOption("--pipe", PipeIdentity.DefaultHostPipe);
            var hostPath = ReadOption("--host-path", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "RemoteDisplaySwitch.Host.exe")));
            var id = Guid.NewGuid();
            var request = arguments[0] switch
            {
                "status" or "discover" or "refresh" or "unbind" or "shutdown" or "show" when arguments.Count == 1 => new LocalRequest(id, arguments[0]),
                "bind" when arguments.Count is 3 or 4 => new LocalRequest(id, "bind", TargetKey: arguments[1], TopologyFingerprint: arguments[2], Name: arguments.Count == 4 ? arguments[3] : "显示器"),
                "on" or "off" or "toggle" when arguments.Count == 3 => new LocalRequest(id, "set", TargetId: Guid.Parse(arguments[1]), Intent: Enum.Parse<PowerIntent>(arguments[0], true), OperationId: Guid.Parse(arguments[2])),
                "operation" when arguments.Count == 2 => new LocalRequest(id, "operation", OperationId: Guid.Parse(arguments[1])),
                "startup" when arguments.Count == 2 && arguments[1] is "on" or "off" => new LocalRequest(id, "startup", Enabled: arguments[1] == "on"),
                _ => throw new ControlException("invalid_request"),
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(125));
            var reply = await LocalPipeClient.SendAsync(pipe, hostPath, request, deadline.Token);
            Console.WriteLine(WireJson.Serialize(reply));
            return reply.Success && reply.Operation?.Outcome is not (OperationOutcome.Failed or OperationOutcome.Unknown) ? 0 : 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is ControlException control ? control.Code : exception is TimeoutException or OperationCanceledException ? "request_timeout_result_unknown" : "connection_or_argument_error");
            return 2;
        }
    }
}
