using System;
using System.IO;
using Microsoft.Win32;
using RemoteDisplaySwitch.Runtime;

namespace RemoteDisplaySwitch.Host;

internal sealed class StartupRegistration(string executable, bool simulation) : IStartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RemoteDisplaySwitch";
    private string Command => $"\"{Path.GetFullPath(executable)}\" --background";
    private bool _simulated;
    public bool Enabled
    {
        get
        {
            if (simulation) return _simulated;
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }
    public void SetEnabled(bool enabled)
    {
        if (simulation) { _simulated = enabled; return; }
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new ControlException("startup_requires_executable");
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        var previous = key.GetValue(ValueName) as string;
        if (previous is not null && !previous.Equals(Command, StringComparison.OrdinalIgnoreCase)) throw new ControlException("startup_owned_by_another_install");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }
}
