using System.Runtime.InteropServices;

namespace Fy1Switch.WolGateway;

public sealed class GatewayLease : IDisposable
{
    private readonly FileStream lease;
    public GatewayLease(GatewaySettings settings)
    {
        if (OperatingSystem.IsLinux() && GetEffectiveUserId() == 0)
            throw new InvalidOperationException("Run WOL Gateway with its dedicated non-root system account.");
        Directory.CreateDirectory(settings.DataDirectory);
        try { lease = new FileStream(Path.Combine(settings.DataDirectory, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("A WOL Gateway already owns this data directory. Run one instance only."); }
    }
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    public void Dispose() => lease.Dispose();
}
