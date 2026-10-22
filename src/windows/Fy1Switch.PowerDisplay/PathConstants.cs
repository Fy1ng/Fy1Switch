using System;
using System.IO;

namespace PowerDisplay.Common;

public static class PathConstants
{
    public static string PowerDisplayFolderPath { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Fy1Switch", "Probe");

    public static string DiscoveryLockPath => Path.Combine(PowerDisplayFolderPath, "discovery.lock");

    public static void Configure(string directory)
    {
        PowerDisplayFolderPath = Path.GetFullPath(directory);
    }

    public static string EnsurePowerDisplayFolderExists()
    {
        Directory.CreateDirectory(PowerDisplayFolderPath);
        return PowerDisplayFolderPath;
    }
}
