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
    internal string Command => CommandFor(Silent);
    private bool _simulated;
    private bool _silent = true;

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
            if (!TryOwnedCommand(key?.GetValue(ValueName), out var silent)) return false;
            _silent = silent;
            return true;
        }
    }
    internal bool Silent
    {
        get
        {
            if (_simulation) return _silent;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
                if (TryOwnedCommand(key?.GetValue(ValueName), out var silent)) _silent = silent;
                return _silent;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
            {
                throw new ControlException("startup_registration_failed");
            }
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
            if (previous is not null)
            {
                if (!TryOwnedCommand(previous, out var silent)) throw new ControlException("startup_owned_by_another_install");
                _silent = silent;
            }
            if (enabled) key.SetValue(ValueName, CommandFor(_silent), RegistryValueKind.String);
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new ControlException("startup_registration_failed");
        }
    }

    internal void SetSilent(bool silent)
    {
        if (_simulation) { _silent = silent; return; }
        RequireExecutable();
        try
        {
            string expected;
            using (var original = Registry.CurrentUser.OpenSubKey(_keyPath))
            {
                var previous = original?.GetValue(ValueName);
                // Changing a disabled preference must never register automatic startup.
                if (previous is null) { _silent = silent; return; }
                if (!TryOwnedCommand(previous, out _)) throw new ControlException("startup_owned_by_another_install");
                expected = (string)previous;
            }
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            if (key?.GetValue(ValueName) is not string current || !current.Equals(expected, StringComparison.Ordinal))
                throw new ControlException("startup_registration_changed");
            key.SetValue(ValueName, CommandFor(silent), RegistryValueKind.String);
            _silent = silent;
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
                return key?.GetValue(ValueName) is string command && !TryOwnedCommand(command, out _) ? command : null;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
            {
                throw new ControlException("startup_registration_failed");
            }
        }
    }

    private string CommandFor(bool silent) => $"\"{Path.GetFullPath(_executable)}\"" + (silent ? " --background" : string.Empty);

    private bool TryOwnedCommand(object? value, out bool silent)
    {
        silent = true;
        if (value is not string command) return false;
        if (command.Equals(CommandFor(true), StringComparison.OrdinalIgnoreCase)) return true;
        silent = false;
        return command.Equals(CommandFor(false), StringComparison.OrdinalIgnoreCase);
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
