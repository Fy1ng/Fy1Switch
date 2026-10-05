using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PowerDisplay.Common.Drivers;
using PowerDisplay.Common.Models;
using Windows.Win32.Graphics.Gdi;
using static PowerDisplay.Common.Drivers.NativeConstants;
using NativeDisplay = PowerDisplay.Common.Drivers.PInvoke;
using NativeWindows = Windows.Win32.PInvoke;

namespace RemoteDisplaySwitch.PowerDisplay;

public sealed record TopologyTarget(int PathIndex, string TargetKey, string Name, string EdidModel, string Source, uint OutputTechnology, uint ConnectorInstance);
public sealed record GdiMonitorDevice(string Source, uint Index, string TargetKey, string Name, string Description, uint StateFlags);
public sealed record TopologyDiagnosticReport(IReadOnlyList<TopologyTarget> Targets, IReadOnlyList<GdiMonitorDevice> GdiDevices);

public static class TopologyDiagnostics
{
    public static unsafe TopologyDiagnosticReport Capture()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = NativeDisplay.GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
            if (result != 0)
            {
                throw new Win32Exception(result);
            }

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            fixed (DisplayConfigPathInfo* pathBuffer = paths)
            fixed (DisplayConfigModeInfo* modeBuffer = modes)
            {
                result = NativeDisplay.QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero);
            }

            if (result == 122)
            {
                continue;
            }

            if (result != 0)
            {
                throw new Win32Exception(result);
            }

            var targets = new List<TopologyTarget>();
            for (var index = 0; index < pathCount; index++)
            {
                var displayPath = paths[index];
                var source = new DisplayConfigSourceDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = DisplayconfigDeviceInfoGetSourceName,
                        Size = (uint)sizeof(DisplayConfigSourceDeviceName),
                        AdapterId = displayPath.SourceInfo.AdapterId,
                        Id = displayPath.SourceInfo.Id,
                    },
                };
                var target = new DisplayConfigTargetDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = DisplayconfigDeviceInfoGetTargetName,
                        Size = (uint)sizeof(DisplayConfigTargetDeviceName),
                        AdapterId = displayPath.TargetInfo.AdapterId,
                        Id = displayPath.TargetInfo.Id,
                    },
                };
                result = NativeDisplay.DisplayConfigGetDeviceInfo(&source);
                if (result != 0)
                {
                    throw new Win32Exception(result);
                }

                result = NativeDisplay.DisplayConfigGetDeviceInfo(&target);
                if (result != 0)
                {
                    throw new Win32Exception(result);
                }

                var devicePath = target.GetMonitorDevicePath();
                targets.Add(new(index, Key(devicePath), target.GetMonitorFriendlyDeviceName(),
                    MonitorIdentity.EdidIdFromMonitorId(devicePath), source.GetViewGdiDeviceName(),
                    displayPath.TargetInfo.OutputTechnology, target.ConnectorInstance));
            }

            var devices = new List<GdiMonitorDevice>();
            foreach (var source in targets.Select(target => target.Source).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                for (uint index = 0; index < 64; index++)
                {
                    var device = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
                    if (!NativeWindows.EnumDisplayDevices(source, index, ref device, 1))
                    {
                        break;
                    }

                    devices.Add(new(source, index, Key(device.DeviceID.ToString()), device.DeviceName.ToString(), device.DeviceString.ToString(), (uint)device.StateFlags));
                }
            }

            return new(targets, devices);
        }

        throw new Win32Exception(122);
    }

    internal static string Key(string devicePath) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(MonitorIdentity.FromDevicePath(devicePath).ToUpperInvariant()))).ToLowerInvariant();
}
