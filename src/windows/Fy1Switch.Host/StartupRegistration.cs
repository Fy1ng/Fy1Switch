using System;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Fy1Switch.Runtime;

namespace Fy1Switch.Host;

internal sealed class StartupRegistration : IStartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Fy1Switch";
    private readonly string _executable;
    private readonly bool _simulation;
    private readonly string _keyPath;
    internal string Command => $"\"{Path.GetFullPath(_executable)}\" --background";
    private bool _simulated;

    internal StartupRegistration(string executable, bool simulation) : this(executable, simulation, KeyPath) { }

    // Tests use a unique, isolated registry key, never the actual Run key.
    internal StartupRegistration(string executable, bool simulation, string keyPath)
    {
        _executable = executable;
        _simulation = simulation;
        _keyPath = keyPath;
    }
    public bool Enabled
    {
        get
        {
            if (_simulation) return _simulated;
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }
    public void SetEnabled(bool enabled)
    {
        if (_simulation) { _simulated = enabled; return; }
        RequireExecutable();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
            var previous = key.GetValue(ValueName);
            if (previous is not null && (previous is not string command || !command.Equals(Command, StringComparison.OrdinalIgnoreCase)))
                throw new ControlException("startup_owned_by_another_install");
            if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new ControlException("startup_registration_failed");
        }
    }

    internal string? ConflictingCommand
    {
        get
        {
            if (_simulation) return null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
                return key?.GetValue(ValueName) is string command && !command.Equals(Command, StringComparison.OrdinalIgnoreCase) ? command : null;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
            {
                throw new ControlException("startup_registration_failed");
            }
        }
    }

    internal void ReplaceAfterConfirmation(string expectedCommand)
    {
        if (string.IsNullOrWhiteSpace(expectedCommand) || _simulation) throw new ControlException("startup_registration_changed");
        RequireExecutable();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            // Do not overwrite a different value written while the confirmation was open.
            if (key?.GetValue(ValueName) is not string current || !current.Equals(expectedCommand, StringComparison.Ordinal))
                throw new ControlException("startup_registration_changed");
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new ControlException("startup_registration_failed");
        }
    }

    private void RequireExecutable()
    {
        if (Path.GetFileNameWithoutExtension(_executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new ControlException("startup_requires_executable");
    }
}
