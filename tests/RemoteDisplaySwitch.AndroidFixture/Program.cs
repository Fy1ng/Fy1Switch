using System.Net;
using System.Net.Sockets;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Remote;
using RemoteDisplaySwitch.Runtime;

if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1])) return 2;
var directory = Path.GetFullPath(args[0]);
Directory.CreateDirectory(directory);
await using var worker = new WorkerSupervisor(args[1], Path.Combine(directory, "worker"), true);
await using var controller = new DesktopController(worker, new NoStartup(), directory, true);
var discovery = await controller.DiscoverAsync();
var candidate = discovery.Candidates.Single();
var bound = await controller.BindAsync(candidate.TargetKey, discovery.TopologyFingerprint, "Android integration simulation");
using var security = new DeviceSecurity(Path.Combine(directory, "remote"), new UserSecretProtector());
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var settings = new RemoteSettings(true, "127.0.0.1", port, $"https://127.0.0.1:{port}");
var gateway = new RemoteGateway(controller, security, bound.DeviceId);
await using var server = new HttpsControlServer(gateway, security);
await server.StartAsync(settings);
var bundle = security.BeginPairing(bound.DeviceId, settings.PublicEndpoint);
var filename = Path.Combine(directory, "fixture.json");
await File.WriteAllTextAsync(filename, WireJson.Serialize(new { bundle, targetId = bound.Binding!.TargetId }));
Console.WriteLine("ANDROID_FIXTURE_READY_SIMULATION_ONLY");
var end = DateTimeOffset.UtcNow.AddMinutes(4);
while (DateTimeOffset.UtcNow < end && !File.Exists(Path.Combine(directory, "stop"))) await Task.Delay(250);
File.Delete(filename);
return 0;

internal sealed class NoStartup : IStartupRegistration
{
    public bool Enabled => false;
    public void SetEnabled(bool enabled) => throw new InvalidOperationException("simulation_only");
}
