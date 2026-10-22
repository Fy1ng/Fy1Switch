using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Fy1Switch.Discovery;

public sealed class WindowsDnsSdAdapter : IDnsSdAdapter
{
    public event Action<DiscoveryDiagnostic>? Diagnostic;

    private void Report(DiscoveryDiagnostic diagnostic)
    {
        foreach (var handler in Diagnostic?.GetInvocationList() ?? [])
            try { ((Action<DiscoveryDiagnostic>)handler)(diagnostic); } catch { }
    }

    public async Task<IAsyncDisposable> RegisterAsync(LocalAdvertisement advertisement, CancellationToken cancellationToken)
    {
        EnsureInterface(advertisement.InterfaceIndex);
        DiscoveryMetadata.ValidateInterface(advertisement.Address, advertisement.InterfaceIndex);
        using var memory = new NativeMemory();
        var bytes = advertisement.Address.GetAddressBytes();
        var address = memory.Allocate(bytes.Length);
        Marshal.Copy(bytes, 0, address, bytes.Length);
        var ipv4 = advertisement.Address.AddressFamily == AddressFamily.InterNetwork;
        var instance = DnsNative.DnsServiceConstructInstance(advertisement.DisplayName + "." + DiscoveryMetadata.ServiceType,
            Environment.MachineName + ".local.", ipv4 ? address : 0, ipv4 ? 0 : address, checked((ushort)advertisement.Port), 0, 0, 3,
            memory.Strings("v", "role", "sid"), memory.Strings("1", "windows", advertisement.SessionId));
        if (instance == 0) throw new DnsSdException("DnsServiceConstructInstance", 8);
        nint registered;
        try
        {
            var data = Marshal.PtrToStructure<DnsNative.Instance>(instance);
            data.InterfaceIndex = advertisement.InterfaceIndex;
            Marshal.StructureToPtr(data, instance, false);
            registered = await NativeServiceOperation.RunAsync("DnsServiceRegister", advertisement.InterfaceIndex, null, instance, cancellationToken, Report).ConfigureAwait(false);
            if (registered == 0)
            {
                registered = instance;
                instance = 0;
            }
        }
        finally { if (instance != 0) DnsNative.DnsServiceFreeInstance(instance); }
        return new Registration(registered, advertisement.InterfaceIndex);
    }

    public IAsyncDisposable Browse(uint interfaceIndex, Action<ServiceAnnouncement> onAnnouncement, Action<DiscoveryDiagnostic> onError)
    {
        EnsureInterface(interfaceIndex);
        return new NativeBrowse(interfaceIndex, onAnnouncement, onError);
    }

    public async Task<ServiceMetadata?> ResolveAsync(string instanceName, uint interfaceIndex, CancellationToken cancellationToken)
    {
        EnsureInterface(interfaceIndex);
        if (!instanceName.EndsWith("." + DiscoveryMetadata.ServiceType, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only local pairing services can be resolved.", nameof(instanceName));
        var instance = await NativeServiceOperation.RunAsync("DnsServiceResolve", interfaceIndex, instanceName, 0, cancellationToken, Report).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return instance == 0 ? null : ReadInstance(instance, interfaceIndex);
        }
        finally { if (instance != 0) DnsNative.DnsServiceFreeInstance(instance); }
    }

    internal static ServiceMetadata? ReadInstance(nint instance, uint selectedInterface)
    {
        var data = Marshal.PtrToStructure<DnsNative.Instance>(instance);
        if (data.PropertyCount > 128 || data.InterfaceIndex != 0 && data.InterfaceIndex != selectedInterface ||
            data.PropertyCount > 0 && (data.Keys == 0 || data.Values == 0)) return null;
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < data.PropertyCount; index++)
        {
            var key = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data.Keys, index * nint.Size));
            var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data.Values, index * nint.Size));
            if (key is null || value is null || !properties.TryAdd(key, value)) return null;
        }
        List<IPAddress> addresses = [];
        if (data.IPv4 != 0)
        {
            var bytes = new byte[4];
            Marshal.Copy(data.IPv4, bytes, 0, bytes.Length);
            addresses.Add(new IPAddress(bytes));
        }
        if (data.IPv6 != 0)
        {
            var bytes = new byte[16];
            Marshal.Copy(data.IPv6, bytes, 0, bytes.Length);
            var address = new IPAddress(bytes);
            addresses.Add(address.IsIPv6LinkLocal ? new IPAddress(bytes, selectedInterface) : address);
        }
        var name = Marshal.PtrToStringUni(data.Name)?.TrimEnd('.') + ".";
        return new(name, data.Port, selectedInterface, properties, addresses);
    }

    private static void EnsureInterface(uint index)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException("Windows 10 or later is required.");
        if (index == 0) throw new ArgumentOutOfRangeException(nameof(index), "All-interface discovery is forbidden.");
    }

    private sealed class Registration(nint instance, uint index) : IAsyncDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (instance == 0) return;
                var response = await NativeServiceOperation.RunAsync("DnsServiceDeRegister", index, null, instance, CancellationToken.None).ConfigureAwait(false);
                if (response != 0) DnsNative.DnsServiceFreeInstance(response);
                DnsNative.DnsServiceFreeInstance(instance);
                instance = 0;
            }
            finally { gate.Release(); }
        }
    }
}
