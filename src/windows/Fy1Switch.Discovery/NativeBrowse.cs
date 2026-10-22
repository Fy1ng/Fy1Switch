using System.Runtime.InteropServices;

namespace Fy1Switch.Discovery;

internal sealed class NativeBrowse : IAsyncDisposable
{
    private static readonly DnsNative.Callback Callback = OnBrowse;
    private readonly NativeMemory memory = new();
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Action<ServiceAnnouncement> announcement;
    private readonly Action<DiscoveryDiagnostic> error;
    private GCHandle root;
    private readonly nint cancel;
    private bool disposed;

    internal NativeBrowse(uint index, Action<ServiceAnnouncement> announcement, Action<DiscoveryDiagnostic> error)
    {
        this.announcement = announcement;
        this.error = error;
        root = GCHandle.Alloc(this);
        cancel = memory.Allocate(nint.Size);
        try
        {
            var request = memory.Structure(new DnsNative.QueryRequest
            {
                Version = 1, InterfaceIndex = index, QueryName = memory.String(DiscoveryMetadata.ServiceType),
                Callback = Marshal.GetFunctionPointerForDelegate(Callback), Context = GCHandle.ToIntPtr(root)
            });
            var status = DnsNative.DnsServiceBrowse(request, cancel);
            if (status != DnsNative.Pending) throw new DnsSdException("DnsServiceBrowse", status);
        }
        catch { root.Free(); memory.Dispose(); throw; }
    }

    internal static IReadOnlyList<ServiceAnnouncement> ReadRecords(nint records)
    {
        List<ServiceAnnouncement> result = [];
        var count = 0;
        for (var record = records; record != 0 && count++ < 4096;)
        {
            var header = Marshal.PtrToStructure<DnsNative.RecordHeader>(record);
            if (header.Type == 12)
            {
                var owner = ReadName(header.Name, header.Flags)?.TrimEnd('.') + ".";
                if (string.Equals(owner, DiscoveryMetadata.ServiceType, StringComparison.OrdinalIgnoreCase))
                {
                    var target = ReadName(Marshal.ReadIntPtr(record, Marshal.SizeOf<DnsNative.RecordHeader>()), header.Flags);
                    if (!string.IsNullOrEmpty(target)) result.Add(new(target.TrimEnd('.') + ".", (header.Flags & 4) != 0 ? 0 : header.Ttl));
                }
            }
            record = header.Next;
        }
        return result;
    }

    private static string? ReadName(nint value, uint flags) => ((flags >> 3) & 3) switch
    {
        2 => Marshal.PtrToStringUTF8(value),
        3 => Marshal.PtrToStringAnsi(value),
        _ => Marshal.PtrToStringUni(value)
    };

    private static void OnBrowse(uint status, nint context, nint records)
    {
        var state = (NativeBrowse)GCHandle.FromIntPtr(context).Target!;
        try
        {
            if (status == 0)
                foreach (var item in ReadRecords(records)) state.announcement(item);
            else if (status != DnsNative.Cancelled)
                state.error(new("DnsServiceBrowse.Callback", status, new DnsSdException("DnsServiceBrowse.Callback", status).Message));
        }
        catch (Exception exception)
        {
            try { state.error(new("DnsServiceBrowse.Callback", null, exception.Message)); } catch { }
        }
        finally
        {
            if (records != 0) DnsNative.DnsRecordListFree(records, 1);
            if (status == DnsNative.Cancelled) state.completed.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            if (!completed.Task.IsCompleted)
            {
                var status = DnsNative.DnsServiceBrowseCancel(cancel);
                if (status != 0) throw new DnsSdException("DnsServiceBrowseCancel", status);
            }
            await completed.Task.ConfigureAwait(false);
            root.Free();
            memory.Dispose();
            disposed = true;
        }
        finally { gate.Release(); }
    }
}
