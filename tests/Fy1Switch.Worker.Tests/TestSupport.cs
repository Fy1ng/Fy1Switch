using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PowerDisplay.Common.Models;
using Fy1Switch.Contracts;
using Fy1Switch.PowerDisplay;
using Fy1Switch.Worker;
using DisplayMonitor = PowerDisplay.Common.Models.Monitor;

[assembly: DoNotParallelize]

namespace Fy1Switch.Worker.Tests;

internal sealed class TestDirectory : IDisposable
{
    private readonly string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata"));
    public string Path { get; }

    public TestDirectory()
    {
        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (!System.IO.Path.GetFullPath(Path).StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException();
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

internal sealed class FakeBackend : IPowerDisplayBackend
{
    public const string PhysicalId = @"\\?\DISPLAY#SAC2563#physical-test#{guid}";
    public const string VirtualId = @"\\?\DISPLAY#MTT1337#virtual-test#{guid}";
    public static string TargetKey => TopologyPolicy.TargetKey(PhysicalId);
    public TopologyDiagnosticReport Topology { get; set; } = ValidTopology();
    public IReadOnlyList<DisplayMonitor> Monitors { get; set; } = [ValidMonitor()];
    public PowerReadObservation Reading { get; set; } = new(true, 1, null, DateTimeOffset.UtcNow);
    public bool WriteAccepted { get; set; } = true;
    public List<int> Writes { get; } = [];
    public int DiscoveryCalls { get; private set; }
    public int ReadCalls { get; private set; }
    public int CaptureCalls { get; private set; }
    public bool Disposed { get; private set; }
    public Action? OnDiscovery { get; set; }
    public Action? OnRead { get; set; }
    public Action? OnWrite { get; set; }
    public bool FailCapture { get; set; }
    public Dictionary<DisplayControl, int?> ControlValues { get; } = new()
    {
        [DisplayControl.Brightness] = 65, [DisplayControl.Contrast] = 50,
        [DisplayControl.Volume] = 25, [DisplayControl.InputSource] = 17,
        [DisplayControl.ColorTemperature] = 5, [DisplayControl.Rotation] = 0,
    };
    public List<(DisplayControl Control, int Value)> ControlWrites { get; } = [];
    public List<DisplayControl> ControlReads { get; } = [];
    public Action? OnControlRead { get; set; }
    public Action? OnControlWrite { get; set; }
    public bool ApplyControlWrite { get; set; } = true;

    public static DisplayMonitor ValidMonitor() => new()
    {
        Id = PhysicalId,
        Name = "Fixture",
        Handle = new IntPtr(1),
        CommunicationMethod = "DDC/CI",
        VcpCapabilitiesInfo = new VcpCapabilities
        {
            SupportedVcpCodes = new() { [0xD6] = new(0xD6, "Power", [1, 4]) },
        },
    };

    public static TopologyDiagnosticReport ValidTopology() => new(
        [new(0, TargetKey, "G4Q", "SAC2563", "source", 10, 8),
         new(1, TopologyPolicy.TargetKey(VirtualId), "VDD", "MTT1337", "source", 5, 0)],
        [new("source", 0, TopologyPolicy.TargetKey(VirtualId), "Monitor0", "Generic", 3),
         new("source", 1, TargetKey, "Monitor1", "Generic", 3)]);

    public void ChangeTopology() => Topology = Topology with
    {
        GdiDevices = [Topology.GdiDevices[0] with { Description = "changed" }, Topology.GdiDevices[1]],
    };

    public TopologyDiagnosticReport CaptureTopology()
    {
        CaptureCalls++;
        if (FailCapture) throw new IOException(PhysicalId);
        return Topology;
    }

    public Task<IReadOnlyList<DisplayMonitor>> DiscoverAsync(TopologyDiagnosticReport topology, CancellationToken cancellationToken)
    {
        DiscoveryCalls++;
        OnDiscovery?.Invoke();
        return Task.FromResult(Monitors);
    }

    public Task<PowerReadObservation> ReadAsync(DisplayMonitor monitor, CancellationToken cancellationToken)
    {
        ReadCalls++;
        OnRead?.Invoke();
        return Task.FromResult(Reading);
    }

    public Task<bool> WriteAsync(DisplayMonitor monitor, int value, CancellationToken cancellationToken)
    {
        Writes.Add(value);
        OnWrite?.Invoke();
        return Task.FromResult(WriteAccepted);
    }

    public Task<int?> ReadControlAsync(DisplayMonitor monitor, DisplayControl control, CancellationToken cancellationToken)
    {
        ControlReads.Add(control);
        OnControlRead?.Invoke();
        return Task.FromResult(ControlValues.GetValueOrDefault(control));
    }

    public Task<bool> WriteControlAsync(DisplayMonitor monitor, DisplayControl control, int value, CancellationToken cancellationToken)
    {
        ControlWrites.Add((control, value));
        OnControlWrite?.Invoke();
        if (WriteAccepted && ApplyControlWrite) ControlValues[control] = value;
        return Task.FromResult(WriteAccepted);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeProvider : IDisplayProvider
{
    public int Calls { get; private set; }
    public int MaximumConcurrentCalls { get; private set; }
    private int activeCalls;
    public bool Disposed { get; private set; }
    public Exception? Failure { get; set; }

    public async Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        Calls++;
        MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, ++activeCalls);
        await Task.Yield();
        activeCalls--;
        if (Failure is not null) throw Failure;
        return new(SimulationDisplayProvider.Fingerprint, [], DateTimeOffset.UtcNow);
    }

    public Task<PowerObservation> ReadAsync(string targetKey, string fingerprint, CancellationToken cancellationToken) =>
        throw new AssertFailedException("Unexpected read");

    public Task<PowerWriteResult> SetAsync(string targetKey, string fingerprint, PowerIntent intent, CancellationToken cancellationToken) =>
        throw new AssertFailedException("Unexpected set");

    public Task<DisplayControlsSnapshot> ReadControlsAsync(string targetKey, string fingerprint, CancellationToken cancellationToken) =>
        throw new AssertFailedException("Unexpected controls read");

    public Task<DisplayControlWriteResult> SetControlAsync(string targetKey, string fingerprint, DisplayControl control, int value, CancellationToken cancellationToken) =>
        throw new AssertFailedException("Unexpected control set");

    public void Dispose() => Disposed = true;
}

internal static class ProtocolFixture
{
    public static async Task<WorkerReply[]> RunAsync(string input, IDisplayProvider provider)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var destination = new MemoryStream();
        using var reader = new StreamReader(source, new UTF8Encoding(false, true));
        using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        await new WorkerProtocol(provider).RunAsync(reader, writer);
        return Encoding.UTF8.GetString(destination.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(WireJson.Parse<WorkerReply>).ToArray();
    }

    public static WorkerRequest Request(string command, PowerIntent? intent = null) => new(Guid.NewGuid(), command,
        SimulationDisplayProvider.TargetKey, SimulationDisplayProvider.Fingerprint, intent);

    public static string Lines(params WorkerRequest[] requests) => string.Concat(requests.Select(request => WireJson.Serialize(request) + "\n"));
}
