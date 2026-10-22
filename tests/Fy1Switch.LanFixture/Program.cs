using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Fy1Switch.Contracts;
using Fy1Switch.Pairing;
using Fy1Switch.Discovery;
using Fy1Switch.Remote;
using Fy1Switch.Runtime;

if (args.Length < 2 || !Path.IsPathFullyQualified(args[1])) return 2;
var mode = args[0]; var directory = args[1]; Directory.CreateDirectory(directory);
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(4));
if (mode == "discovery")
{
    var choice = LocalNetworkChoices.Get().First(choice => choice.Recommended);
    var address = IPAddress.Parse(choice.Address);
    var network = NetworkInterface.GetAllNetworkInterfaces().Single(adapter => adapter.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(address)));
    var index = (uint)network.GetIPProperties().GetIPv4Properties()!.Index;
    await using var discovery = new LanDiscovery(index);
    using var listener = new TcpListener(address, 0); listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    await discovery.StartAsync(((IPEndPoint)listener.LocalEndpoint).Port, Guid.NewGuid().ToString(), "Fy1Switch-fixture", address, timeout.Token);
    Console.WriteLine("NATIVE_DISCOVERY_STARTED");
    await Task.Delay(1500); await discovery.StopAsync(); Console.WriteLine("NATIVE_DISCOVERY_STOPPED");
    return 0;
}
await using var transport = new SimulationTransport();
await using var controller = new DesktopController(transport, new NoStartup(), directory, true);
var snapshot = await controller.DiscoverAsync(); var bound = await controller.BindAsync(snapshot.Candidates[0].TargetKey, snapshot.TopologyFingerprint, "fixture");
using var security = new DeviceSecurity(Path.Combine(directory, "identity"), new UserSecretProtector());
using var portFinder = new TcpListener(IPAddress.Loopback, 0); portFinder.Start(); var port = ((IPEndPoint)portFinder.LocalEndpoint).Port; portFinder.Stop();
var endpoint = $"https://127.0.0.1:{port}";
await using var server = new HttpsControlServer(new RemoteGateway(controller, security, bound.DeviceId), security);
await server.StartAsync(new(true, "127.0.0.1", port, endpoint));
Task<PairBundle> Bundle() => Task.FromResult(security.BeginPairing(bound.DeviceId, endpoint));
if (mode == "forward")
{
    await using var host = await PairingHost.StartAsync(IPAddress.Loopback, Bundle, cancellationToken: lifetime.Token);
    await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), WireJson.Serialize(new { host = "127.0.0.1", port = host.Port, sessionId = host.SessionId, code = host.Code, expectedDeviceId = bound.DeviceId, targetId = bound.Binding!.TargetId }));
    Console.WriteLine("SHORT_CODE_FIXTURE_READY");
    var completed = await host.Completion.WaitAsync(lifetime.Token);
    await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), WireJson.Serialize(new { success = completed }));
    while (!File.Exists(Path.Combine(directory, "stop"))) await Task.Delay(100, lifetime.Token);
    File.Delete(Path.Combine(directory, "fixture.json"));
    return completed ? 0 : 1;
}
if (mode == "reverse")
{
    using var controlPort = new TcpListener(IPAddress.Loopback, 0); controlPort.Start(); var controlNumber = ((IPEndPoint)controlPort.LocalEndpoint).Port; controlPort.Stop();
    using var control = new HttpListener(); var controlUrl = $"http://127.0.0.1:{controlNumber}/"; control.Prefixes.Add(controlUrl); control.Start();
    await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), WireJson.Serialize(new { expectedDeviceId = bound.DeviceId, targetId = bound.Binding!.TargetId, reverseControlUrl = controlUrl }));
    var request = await control.GetContextAsync().WaitAsync(lifetime.Token);
    using var reader = new StreamReader(request.Request.InputStream);
    var text = await reader.ReadToEndAsync(lifetime.Token);
    if (text.Length > 16384) return 2;
    var peer = WireJson.Parse<Peer>(text);
    using var client = new PairingClient();
    var completed = await client.ConnectAsync(IPAddress.Loopback, peer.Port, peer.SessionId, peer.Code, Bundle, lifetime.Token);
    request.Response.StatusCode = completed ? 200 : 400; request.Response.Close();
    await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), WireJson.Serialize(new { success = completed }));
    while (!File.Exists(Path.Combine(directory, "stop"))) await Task.Delay(100, lifetime.Token);
    File.Delete(Path.Combine(directory, "fixture.json"));
    return completed ? 0 : 1;
}
return 2;

internal sealed record Peer(int Port, string SessionId, string Code);
internal sealed class NoStartup : IStartupRegistration { public bool Enabled => false; public void SetEnabled(bool enabled) => throw new InvalidOperationException(); }
internal sealed class SimulationTransport : IWorkerTransport
{
    public bool IsRunning => true;
    public Task<WorkerReply> SendAsync(WorkerRequest request, CancellationToken cancellationToken = default) => Task.FromResult(request.Command == "discover"
        ? new WorkerReply(request.RequestId, true, Discovery: new(new string('b',64), [new(new string('a',64),"fixture","simulation")], DateTimeOffset.UtcNow))
        : new WorkerReply(request.RequestId, true, Observation: new(DisplayPower.On,1,DateTimeOffset.UtcNow,"simulation")));
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
