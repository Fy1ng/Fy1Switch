using System.Runtime.InteropServices;

namespace Fy1Switch.Discovery;

internal static class DnsNative
{
    internal const uint Pending = 9506;
    internal const uint Cancelled = 1223;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void Callback(uint status, nint context, nint result);

    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryRequest
    {
        internal uint Version, InterfaceIndex;
        internal nint QueryName, Callback, Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RegisterRequest
    {
        internal uint Version, InterfaceIndex;
        internal nint Instance, Callback, Context, Credentials;
        internal int UnicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Instance
    {
        internal nint Name, Host, IPv4, IPv6;
        internal ushort Port, Priority, Weight;
        internal uint PropertyCount;
        internal nint Keys, Values;
        internal uint InterfaceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RecordHeader
    {
        internal nint Next, Name;
        internal ushort Type, DataLength;
        internal uint Flags, Ttl, Reserved;
    }

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    internal static extern uint DnsServiceBrowse(nint request, nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    internal static extern uint DnsServiceBrowseCancel(nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    internal static extern uint DnsServiceResolve(nint request, nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    internal static extern uint DnsServiceResolveCancel(nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    internal static extern uint DnsServiceRegister(nint request, nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    internal static extern uint DnsServiceDeRegister(nint request, nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    internal static extern uint DnsServiceRegisterCancel(nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern nint DnsServiceConstructInstance(string serviceName, string hostName, nint ipv4, nint ipv6,
        ushort port, ushort priority, ushort weight, uint count, nint keys, nint values);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    internal static extern void DnsServiceFreeInstance(nint instance);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    internal static extern void DnsRecordListFree(nint records, int freeType);
}

internal sealed class NativeMemory : IDisposable
{
    private readonly List<nint> allocations = [];
    internal nint Allocate(int size)
    {
        var address = Marshal.AllocHGlobal(size);
        allocations.Add(address);
        Marshal.Copy(new byte[size], 0, address, size);
        return address;
    }
    internal nint String(string text)
    {
        var address = Marshal.StringToHGlobalUni(text);
        allocations.Add(address);
        return address;
    }
    internal nint Strings(params string[] values)
    {
        var address = Allocate(values.Length * nint.Size);
        for (var index = 0; index < values.Length; index++) Marshal.WriteIntPtr(address, index * nint.Size, String(values[index]));
        return address;
    }
    internal nint Structure<T>(T value) where T : struct
    {
        var address = Allocate(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, address, false);
        return address;
    }
    public void Dispose()
    {
        foreach (var address in allocations) Marshal.FreeHGlobal(address);
        allocations.Clear();
    }
}
