using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FyDisplayAgent;

public sealed record DdcMonitorInfo(
    string Key,
    string GdiDeviceName,
    string Description,
    int LogicalIndex,
    int PhysicalIndex,
    bool HasCapabilities,
    bool AdvertisesPowerControl,
    string? CapabilitiesSummary,
    string? CapabilitiesError,
    bool SupportsPowerRead,
    uint? PowerState,
    string? ProbeError)
{
    public override string ToString()
    {
        var physical = PhysicalIndex > 0 ? $" / physical {PhysicalIndex + 1}" : "";
        var caps = AdvertisesPowerControl
            ? " / MCCS D6"
            : HasCapabilities
                ? " / MCCS"
                : " / no MCCS caps";
        var read = SupportsPowerRead
            ? $" / read 0x{PowerState.GetValueOrDefault():X2}"
            : $" / read unavailable{(string.IsNullOrWhiteSpace(ProbeError) ? "" : $" ({ProbeError})")}";
        return $"{LogicalIndex}. {Description} [{GdiDeviceName}]{physical}{caps}{read}";
    }
}

/// <summary>
/// Native DDC/CI controller for external monitors.
/// Uses Dxva2.dll directly. Monitor selection intentionally prefers a real MCCS capabilities
/// response over a successful VCP 0xD6 read because virtual displays can emulate reads while
/// rejecting writes in clone/mirror topologies.
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
                    result.Add(BuildMonitorInfo(gdi, logical, i, physicals[i]));
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
            Marshal.SetLastPInvokeError(0);
            if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, VcpPowerMode, IntPtr.Zero, out var current, out _))
                throw NativeError($"Could not read power state (VCP 0x{VcpPowerMode:X2}) from {selected.Key}");
            return current;
        });

    public void SetPowerState(string? monitorId, int monitorNumber, uint value)
        => WithSelectedMonitor(monitorId, monitorNumber, (handle, selected) =>
        {
            Marshal.SetLastPInvokeError(0);
            if (!NativeMethods.SetVCPFeature(handle, VcpPowerMode, value))
                throw NativeError($"Could not set power state 0x{value:X2} (VCP 0x{VcpPowerMode:X2}) on {selected.Key}");
            return 0;
        });

    public DdcMonitorInfo ResolveSelectedMonitor(string? monitorId, int monitorNumber)
    {
        var monitors = EnumerateMonitors();
        if (monitors.Count == 0)
            throw new InvalidOperationException("No external DDC/CI monitors were found. Check that DDC/CI is enabled in the monitor OSD.");
        return SelectBest(monitors, monitorId, monitorNumber);
    }

    private T WithSelectedMonitor<T>(string? monitorId, int monitorNumber, Func<IntPtr, DdcMonitorInfo, T> action)
    {
        var candidates = CollectPhysicalCandidates();
        if (candidates.Count == 0)
            throw new InvalidOperationException("No external DDC/CI monitors were found. Enable DDC/CI in the monitor OSD and check the display cable/adapter.");

        try
        {
            var selectedInfo = SelectBest(candidates.Select(c => c.Info).ToList(), monitorId, monitorNumber);
            var selected = candidates.First(c => string.Equals(c.Info.Key, selectedInfo.Key, StringComparison.OrdinalIgnoreCase));
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

    private static DdcMonitorInfo SelectBest(IReadOnlyList<DdcMonitorInfo> monitors, string? monitorId, int monitorNumber)
    {
        // PowerToys first establishes real DDC/MCCS capability evidence. Mirror-mode VDDs may
        // answer a VCP read but still reject SetVCPFeature. Therefore a caps string is stronger
        // evidence than SupportsPowerRead and drives automatic selection.
        var advertised = monitors.Where(m => m.AdvertisesPowerControl).ToList();
        if (advertised.Count > 0)
        {
            var saved = advertised.FirstOrDefault(m => string.Equals(m.Key, monitorId, StringComparison.OrdinalIgnoreCase));
            return saved ?? advertised[Math.Clamp(monitorNumber, 1, advertised.Count) - 1];
        }

        var mccs = monitors.Where(m => m.HasCapabilities).ToList();
        if (mccs.Count > 0)
        {
            var saved = mccs.FirstOrDefault(m => string.Equals(m.Key, monitorId, StringComparison.OrdinalIgnoreCase));
            return saved ?? mccs[Math.Clamp(monitorNumber, 1, mccs.Count) - 1];
        }

        // If no capabilities string can be obtained, preserve an explicit user selection.
        if (!string.IsNullOrWhiteSpace(monitorId))
        {
            var saved = monitors.FirstOrDefault(m => string.Equals(m.Key, monitorId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null) return saved;
        }

        // Do not prefer a candidate solely because GetVCP(0xD6) succeeded: this is exactly what
        // selected the VDD in the user's clone topology.
        return monitors[Math.Clamp(monitorNumber, 1, monitors.Count) - 1];
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
                result.Add(new Candidate(
                    physicals[i].Handle,
                    BuildMonitorInfo(logicalMonitor.Gdi, logicalMonitor.LogicalIndex, i, physicals[i]),
                    physicals));
                anyAdded = true;
            }

            if (!anyAdded)
                NativeMethods.DestroyPhysicalMonitors(count, physicals);
        }

        return result;
    }

    private static DdcMonitorInfo BuildMonitorInfo(string gdi, int logicalIndex, int physicalIndex, NativeMethods.PhysicalMonitor physical)
    {
        var description = string.IsNullOrWhiteSpace(physical.Description)
            ? $"Physical monitor {physicalIndex + 1}"
            : physical.Description.Trim();

        var caps = ProbeCapabilities(physical.Handle);
        var power = ProbePowerRead(physical.Handle);

        return new DdcMonitorInfo(
            BuildKey(gdi, physicalIndex),
            gdi,
            description,
            logicalIndex,
            physicalIndex,
            caps.HasCapabilities,
            caps.AdvertisesPowerControl,
            caps.Summary,
            caps.Error,
            power.Supported,
            power.State,
            power.Error);
    }

    private static (bool HasCapabilities, bool AdvertisesPowerControl, string? Summary, string? Error) ProbeCapabilities(IntPtr handle)
    {
        Marshal.SetLastPInvokeError(0);
        if (!NativeMethods.GetCapabilitiesStringLength(handle, out var length) || length == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return (false, false, null, error == 0 ? "capabilities length unavailable" : $"error {error}");
        }

        // Protect against bogus virtual-driver lengths.
        if (length > 1024 * 1024)
            return (false, false, null, $"invalid capabilities length {length}");

        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            Marshal.SetLastPInvokeError(0);
            if (!NativeMethods.CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length))
            {
                var error = Marshal.GetLastPInvokeError();
                return (false, false, null, error == 0 ? "capabilities read failed" : $"error {error}");
            }

            var raw = Marshal.PtrToStringAnsi(buffer) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return (false, false, null, "empty capabilities string");

            var advertisesD6 = Regex.IsMatch(raw, @"(?i)(?:^|[\s(])D6(?:[\s()])");
            var compact = Regex.Replace(raw, @"\s+", " ").Trim();
            var summary = compact.Length <= 220 ? compact : compact[..220] + "…";
            return (true, advertisesD6, summary, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, false, null, ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (bool Supported, uint? State, string? Error) ProbePowerRead(IntPtr handle)
    {
        Marshal.SetLastPInvokeError(0);
        if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, VcpPowerMode, IntPtr.Zero, out var current, out _))
            return (true, current, null);

        var error = Marshal.GetLastPInvokeError();
        return error == 0
            ? (false, null, "read failed")
            : (false, null, $"error {error}");
    }

    private static string BuildKey(string gdiDeviceName, int physicalIndex)
        => $"{gdiDeviceName}#{physicalIndex}";

    private static Exception NativeError(string prefix)
    {
        var error = Marshal.GetLastPInvokeError();
        return error == 0
            ? new InvalidOperationException(prefix + ". The selected handle may not accept this DDC/CI command.")
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
        internal static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint capabilitiesStringLengthInCharacters);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, IntPtr asciiCapabilitiesString, uint capabilitiesStringLengthInCharacters);

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
