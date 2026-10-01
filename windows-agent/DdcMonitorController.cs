using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FyDisplayAgent;

public sealed record DdcMonitorInfo(
    string Key,
    string GdiDeviceName,
    string Description,
    int LogicalIndex,
    int PhysicalIndex,
    bool SupportsPowerRead,
    uint? PowerState,
    string? ProbeError)
{
    public override string ToString()
    {
        var physical = PhysicalIndex > 0 ? $" / physical {PhysicalIndex + 1}" : "";
        var power = SupportsPowerRead
            ? $" / Power OK 0x{PowerState.GetValueOrDefault():X2}"
            : $" / Power unsupported{(string.IsNullOrWhiteSpace(ProbeError) ? "" : $" ({ProbeError})")}";
        return $"{LogicalIndex}. {Description} [{GdiDeviceName}]{physical}{power}";
    }
}

/// <summary>
/// Minimal native DDC/CI controller for external monitors.
/// Uses the Windows Monitor Configuration API (Dxva2.dll) directly and has no PowerToys dependency.
/// </summary>
public sealed class DdcMonitorController
{
    public const byte VcpPowerMode = 0xD6;

    public IReadOnlyList<DdcMonitorInfo> EnumerateMonitors()
    {
        var result = new List<DdcMonitorInfo>();
        int logical = 0;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            logical++;
            var info = NativeMethods.GetInfo(hMonitor);
            var gdi = info?.DeviceName ?? $"DISPLAY{logical}";

            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                return true;

            var physicals = new NativeMethods.PhysicalMonitor[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
                return true;

            try
            {
                for (int i = 0; i < physicals.Length; i++)
                {
                    if (physicals[i].Handle == IntPtr.Zero) continue;
                    var description = string.IsNullOrWhiteSpace(physicals[i].Description)
                        ? $"Physical monitor {i + 1}"
                        : physicals[i].Description.Trim();

                    var (supported, state, error) = ProbePowerRead(physicals[i].Handle);
                    result.Add(new DdcMonitorInfo(
                        BuildKey(gdi, i),
                        gdi,
                        description,
                        logical,
                        i,
                        supported,
                        state,
                        error));
                }
            }
            finally
            {
                NativeMethods.DestroyPhysicalMonitors(count, physicals);
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    public uint GetPowerState(string? monitorId, int monitorNumber)
        => WithSelectedMonitor(monitorId, monitorNumber, (handle, selected) =>
        {
            if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, VcpPowerMode, IntPtr.Zero, out var current, out _))
                throw NativeError($"Could not read power state (VCP 0x{VcpPowerMode:X2}) from {selected.Description}");
            return current;
        }, preferPowerReadable: true);

    public void SetPowerState(string? monitorId, int monitorNumber, uint value)
        => WithSelectedMonitor(monitorId, monitorNumber, (handle, selected) =>
        {
            if (!NativeMethods.SetVCPFeature(handle, VcpPowerMode, value))
                throw NativeError($"Could not set power state 0x{value:X2} (VCP 0x{VcpPowerMode:X2}) on {selected.Description}");
            return 0;
        }, preferPowerReadable: false);

    public DdcMonitorInfo ResolveSelectedMonitor(string? monitorId, int monitorNumber)
    {
        var monitors = EnumerateMonitors();
        if (monitors.Count == 0)
            throw new InvalidOperationException("No external DDC/CI monitors were found. Check that DDC/CI is enabled in the monitor OSD.");

        if (!string.IsNullOrWhiteSpace(monitorId))
        {
            var byId = monitors.FirstOrDefault(m => string.Equals(m.Key, monitorId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
        }

        var readable = monitors.Where(m => m.SupportsPowerRead).ToList();
        if (readable.Count > 0)
        {
            var preferredIndex = Math.Clamp(monitorNumber, 1, readable.Count) - 1;
            return readable[preferredIndex];
        }

        var index = Math.Clamp(monitorNumber, 1, monitors.Count) - 1;
        return monitors[index];
    }

    private T WithSelectedMonitor<T>(
        string? monitorId,
        int monitorNumber,
        Func<IntPtr, DdcMonitorInfo, T> action,
        bool preferPowerReadable)
    {
        var candidates = CollectPhysicalCandidates();
        if (candidates.Count == 0)
            throw new InvalidOperationException("No external DDC/CI monitors were found. Enable DDC/CI in the monitor OSD and check the display cable/adapter.");

        try
        {
            Candidate? selected = null;

            if (!string.IsNullOrWhiteSpace(monitorId))
                selected = candidates.FirstOrDefault(c => string.Equals(c.Info.Key, monitorId, StringComparison.OrdinalIgnoreCase));

            if (selected is null && preferPowerReadable)
            {
                var readable = candidates.Where(c => c.Info.SupportsPowerRead).ToList();
                if (readable.Count > 0)
                    selected = readable[Math.Clamp(monitorNumber, 1, readable.Count) - 1];
            }

            if (selected is null)
                selected = candidates[Math.Clamp(monitorNumber, 1, candidates.Count) - 1];

            return action(selected.Handle, selected.Info);
        }
        finally
        {
            foreach (var group in candidates.GroupBy(c => c.OwnerArray))
            {
                var array = group.Key;
                if (array.Length > 0)
                    NativeMethods.DestroyPhysicalMonitors((uint)array.Length, array);
            }
        }
    }

    private List<Candidate> CollectPhysicalCandidates()
    {
        var logicalHandles = new List<(IntPtr HMonitor, string Gdi, int LogicalIndex)>();
        int logical = 0;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            logical++;
            var info = NativeMethods.GetInfo(hMonitor);
            logicalHandles.Add((hMonitor, info?.DeviceName ?? $"DISPLAY{logical}", logical));
            return true;
        }, IntPtr.Zero);

        var result = new List<Candidate>();
        foreach (var logicalMonitor in logicalHandles)
        {
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor.HMonitor, out var count) || count == 0)
                continue;

            var physicals = new NativeMethods.PhysicalMonitor[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(logicalMonitor.HMonitor, count, physicals))
                continue;

            bool anyAdded = false;
            for (int i = 0; i < physicals.Length; i++)
            {
                if (physicals[i].Handle == IntPtr.Zero) continue;
                var description = string.IsNullOrWhiteSpace(physicals[i].Description)
                    ? $"Physical monitor {i + 1}"
                    : physicals[i].Description.Trim();
                var (supported, state, error) = ProbePowerRead(physicals[i].Handle);
                var info = new DdcMonitorInfo(
                    BuildKey(logicalMonitor.Gdi, i),
                    logicalMonitor.Gdi,
                    description,
                    logicalMonitor.LogicalIndex,
                    i,
                    supported,
                    state,
                    error);
                result.Add(new Candidate(physicals[i].Handle, info, physicals));
                anyAdded = true;
            }

            if (!anyAdded)
                NativeMethods.DestroyPhysicalMonitors(count, physicals);
        }

        return result;
    }

    private static (bool Supported, uint? State, string? Error) ProbePowerRead(IntPtr handle)
    {
        Marshal.SetLastPInvokeError(0);
        if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, VcpPowerMode, IntPtr.Zero, out var current, out _))
            return (true, current, null);

        var error = Marshal.GetLastPInvokeError();
        if (error == 0)
            return (false, null, "read failed");
        return (false, null, $"error {error}");
    }

    private static string BuildKey(string gdiDeviceName, int physicalIndex)
        => $"{gdiDeviceName}#{physicalIndex}";

    private static Exception NativeError(string prefix)
    {
        var error = Marshal.GetLastPInvokeError();
        return error == 0
            ? new InvalidOperationException(prefix + ". The monitor may not support this DDC/CI command.")
            : new Win32Exception(error, $"{prefix}. Win32 error {error}");
    }

    private sealed record Candidate(IntPtr Handle, DdcMonitorInfo Info, NativeMethods.PhysicalMonitor[] OwnerArray);

    private static class NativeMethods
    {
        internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MonitorInfoEx
        {
            public uint Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PhysicalMonitor
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx info);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint numberOfPhysicalMonitors);

        [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint arraySize, [Out] PhysicalMonitor[] physicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyPhysicalMonitors(uint arraySize, [In] PhysicalMonitor[] physicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVCPFeatureAndVCPFeatureReply(
            IntPtr hMonitor,
            byte vcpCode,
            IntPtr pvct,
            out uint currentValue,
            out uint maximumValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint newValue);

        internal static MonitorInfoEx? GetInfo(IntPtr hMonitor)
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>(), DeviceName = string.Empty };
            return GetMonitorInfoW(hMonitor, ref info) ? info : null;
        }
    }
}
