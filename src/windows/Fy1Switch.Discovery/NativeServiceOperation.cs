using System.Runtime.InteropServices;

namespace Fy1Switch.Discovery;

internal sealed class NativeServiceOperation
{
    private static readonly DnsNative.Callback Completion = OnComplete;
    private readonly TaskCompletionSource<(uint Status, nint Instance)> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly NativeMemory memory = new();
    private GCHandle root;
    private readonly nint cancel;

    private NativeServiceOperation()
    {
        cancel = memory.Allocate(nint.Size);
        root = GCHandle.Alloc(this);
    }

    internal static async Task<nint> RunAsync(string operation, uint interfaceIndex, string? name, nint instance, CancellationToken token,
        Action<DiscoveryDiagnostic>? report = null)
    {
        token.ThrowIfCancellationRequested();
        var state = new NativeServiceOperation();
        var callback = Marshal.GetFunctionPointerForDelegate(Completion);
        var context = GCHandle.ToIntPtr(state.root);
        var request = name is not null
            ? state.memory.Structure(new DnsNative.QueryRequest { Version = 1, InterfaceIndex = interfaceIndex, QueryName = state.memory.String(name), Callback = callback, Context = context })
            : state.memory.Structure(new DnsNative.RegisterRequest { Version = 1, InterfaceIndex = interfaceIndex, Instance = instance, Callback = callback, Context = context });
        CancellationTokenRegistration cancellation = default;
        nint result = 0;
        try
        {
            var status = operation switch
            {
                "DnsServiceResolve" => DnsNative.DnsServiceResolve(request, state.cancel),
                "DnsServiceRegister" => DnsNative.DnsServiceRegister(request, state.cancel),
                _ => DnsNative.DnsServiceDeRegister(request, 0)
            };
            if (status != DnsNative.Pending) throw new DnsSdException(operation, status);
            cancellation = token.Register(() =>
            {
                if (state.completion.Task.IsCompleted) return;
                var status = name is not null ? DnsNative.DnsServiceResolveCancel(state.cancel) : DnsNative.DnsServiceRegisterCancel(state.cancel);
                if (status != 0 && status != DnsNative.Cancelled && status != 1168)
                    report?.Invoke(new(operation + "Cancel", status, new DnsSdException(operation + "Cancel", status).Message));
            });
            var completed = await state.completion.Task.ConfigureAwait(false);
            result = completed.Instance;
            await cancellation.DisposeAsync().ConfigureAwait(false);
            if (completed.Status == DnsNative.Cancelled && token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (completed.Status != 0) throw new DnsSdException(operation, completed.Status);
            var owned = result;
            result = 0;
            return owned;
        }
        finally
        {
            await cancellation.DisposeAsync().ConfigureAwait(false);
            if (result != 0) DnsNative.DnsServiceFreeInstance(result);
            state.root.Free();
            state.memory.Dispose();
        }
    }

    private static void OnComplete(uint status, nint context, nint instance)
    {
        var state = (NativeServiceOperation)GCHandle.FromIntPtr(context).Target!;
        if (!state.completion.TrySetResult((status, instance)) && instance != 0)
            DnsNative.DnsServiceFreeInstance(instance);
    }
}
