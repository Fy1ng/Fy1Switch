using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using Fy1Switch.Host;
using Fy1Switch.Host.UI;
using Fy1Switch.Runtime;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class StartupRegistrationTests
{
    private const string Name = "Fy1Switch";
    private const string OldCommand = "\"C:\\Fy1Switch Test\\old\\Fy1Switch.Host.exe\" --background";
    private static string Executable => Path.Combine(Path.GetTempPath(), "Fy1Switch Test", "current", "Fy1Switch.Host.exe");

    [TestMethod]
    public void EnableDisableAndReadbackPreserveOtherValues()
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        key.SetValue("Unrelated", "preserve");
        var startup = new StartupRegistration(Executable, false, fixture.Path);
        Assert.IsFalse(startup.Enabled);
        startup.SetEnabled(true);
        Assert.IsTrue(startup.Enabled);
        Assert.AreEqual($"\"{Path.GetFullPath(Executable)}\" --background", key.GetValue(Name));
        Assert.AreEqual(RegistryValueKind.String, key.GetValueKind(Name));
        startup.SetEnabled(false);
        Assert.IsFalse(startup.Enabled);
        Assert.IsNull(key.GetValue(Name));
        Assert.AreEqual("preserve", key.GetValue("Unrelated"));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void StartupModeCanBeChangedReloadedAndDisabledWithoutLosingOtherValues(bool silent)
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        key.SetValue("Unrelated", "preserve");
        var startup = new StartupRegistration(Executable, false, fixture.Path);
        Assert.IsTrue(startup.Silent);
        startup.SetSilent(silent);
        Assert.IsFalse(startup.Enabled);
        Assert.IsNull(key.GetValue(Name));
        startup.SetEnabled(true);
        Assert.AreEqual(StartupCommand(silent), key.GetValue(Name));

        var reopened = new StartupRegistration(Executable, false, fixture.Path);
        Assert.IsTrue(reopened.Enabled);
        Assert.AreEqual(silent, reopened.Silent);
        Assert.AreEqual(StartupCommand(silent), reopened.Command);
        Assert.IsNull(reopened.ConflictingCommand);
        reopened.SetEnabled(true);
        Assert.AreEqual(StartupCommand(silent), key.GetValue(Name));

        reopened.SetSilent(!silent);
        Assert.AreEqual(StartupCommand(!silent), key.GetValue(Name));
        Assert.AreEqual(!silent, new StartupRegistration(Executable, false, fixture.Path).Silent);
        reopened.SetEnabled(false);
        Assert.IsFalse(reopened.Enabled);
        Assert.IsNull(key.GetValue(Name));
        reopened.SetEnabled(true);
        Assert.AreEqual(StartupCommand(!silent), key.GetValue(Name));
        Assert.AreEqual("preserve", key.GetValue("Unrelated"));
    }

    [TestMethod]
    public void DisabledModePreferenceDoesNotCreateARegistryKeyOrEnableStartup()
    {
        using var fixture = new RegistryFixture();
        var startup = new StartupRegistration(Executable, false, fixture.Path);
        startup.SetSilent(false);
        Assert.IsFalse(startup.Enabled);
        Assert.IsFalse(startup.Silent);
        using var key = Registry.CurrentUser.OpenSubKey(fixture.Path);
        Assert.IsNull(key);
    }

    [TestMethod]
    public void SilentModeChangeCannotReplaceAnotherInstall()
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        var startup = new StartupRegistration(Executable, false, fixture.Path);
        startup.SetEnabled(true);
        key.SetValue(Name, OldCommand);
        var error = Assert.ThrowsException<ControlException>(() => startup.SetSilent(false));
        Assert.AreEqual("startup_owned_by_another_install", error.Code);
        Assert.AreEqual(OldCommand, key.GetValue(Name));
    }

    [DataTestMethod]
    [DataRow(" --background --unexpected")]
    [DataRow(" --unknown")]
    public void AdditionalArgumentsAreNotConsideredOwnedStartupCommands(string arguments)
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        var command = StartupCommand(false) + arguments;
        key.SetValue(Name, command);
        var startup = new StartupRegistration(Executable, false, fixture.Path);
        Assert.IsFalse(startup.Enabled);
        Assert.AreEqual(command, startup.ConflictingCommand);
        Assert.AreEqual("startup_owned_by_another_install", Assert.ThrowsException<ControlException>(() => startup.SetSilent(true)).Code);
        Assert.AreEqual(command, key.GetValue(Name));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OrdinaryToggleNeverReplacesOrRemovesAnotherInstall(bool enabled)
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        key.SetValue(Name, OldCommand);
        var startup = new StartupRegistration(Executable, false, fixture.Path);
        Assert.IsFalse(startup.Enabled);
        Assert.AreEqual(OldCommand, startup.ConflictingCommand);
        var error = Assert.ThrowsException<ControlException>(() => startup.SetEnabled(enabled));
        Assert.AreEqual("startup_owned_by_another_install", error.Code);
        Assert.AreEqual(OldCommand, key.GetValue(Name));
    }

    [TestMethod]
    public void ExplicitReplacementUpdatesOnlyTheConfirmedStartupEntry()
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        key.SetValue(Name, OldCommand);
        key.SetValue("Unrelated", "preserve");
        var startup = new StartupRegistration(Executable, false, fixture.Path);
        var expected = startup.ConflictingCommand;
        Assert.AreEqual(OldCommand, key.GetValue(Name)); // Reading a conflict does not approve it.
        startup.ReplaceAfterConfirmation(expected!);
        Assert.IsTrue(startup.Enabled);
        Assert.IsNull(startup.ConflictingCommand);
        startup.SetEnabled(true); // The controller's subsequent save remains valid.
        Assert.AreEqual("preserve", key.GetValue("Unrelated"));
    }

    [TestMethod]
    public void ConfirmationCannotOverwriteAChangedEntry()
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        key.SetValue(Name, OldCommand);
        var startup = new StartupRegistration(Executable, false, fixture.Path);
        var expected = startup.ConflictingCommand;
        const string changed = "\"C:\\Another\\Fy1Switch.Host.exe\" --background";
        key.SetValue(Name, changed);
        var error = Assert.ThrowsException<ControlException>(() => startup.ReplaceAfterConfirmation(expected!));
        Assert.AreEqual("startup_registration_changed", error.Code);
        Assert.AreEqual(changed, key.GetValue(Name));
    }

    [TestMethod]
    public void ConfirmationCannotRecreateARemovedEntry()
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        key.SetValue(Name, OldCommand);
        var startup = new StartupRegistration(Executable, false, fixture.Path);
        var expected = startup.ConflictingCommand;
        key.DeleteValue(Name);
        var error = Assert.ThrowsException<ControlException>(() => startup.ReplaceAfterConfirmation(expected!));
        Assert.AreEqual("startup_registration_changed", error.Code);
        Assert.IsNull(key.GetValue(Name));
    }

    [TestMethod]
    public void NonStringEntryIsNeverSilentlyOverwritten()
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        key.SetValue(Name, 42, RegistryValueKind.DWord);
        var startup = new StartupRegistration(Executable, false, fixture.Path);
        Assert.IsNull(startup.ConflictingCommand);
        Assert.AreEqual("startup_owned_by_another_install", Assert.ThrowsException<ControlException>(() => startup.SetEnabled(true)).Code);
        Assert.AreEqual("startup_owned_by_another_install", Assert.ThrowsException<ControlException>(() => startup.SetSilent(false)).Code);
        Assert.AreEqual(42, key.GetValue(Name));
    }

    [TestMethod]
    public void DotnetLaunchCannotRegisterOrReplaceAnEntry()
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        key.SetValue(Name, OldCommand);
        var startup = new StartupRegistration(Path.Combine(Path.GetTempPath(), "dotnet.exe"), false, fixture.Path);
        Assert.AreEqual("startup_requires_executable", Assert.ThrowsException<ControlException>(() => startup.SetEnabled(true)).Code);
        Assert.AreEqual("startup_requires_executable", Assert.ThrowsException<ControlException>(() => startup.SetSilent(false)).Code);
        Assert.AreEqual("startup_requires_executable", Assert.ThrowsException<ControlException>(() => startup.ReplaceAfterConfirmation(OldCommand)).Code);
        Assert.AreEqual(OldCommand, key.GetValue(Name));
    }

    [TestMethod]
    public void SimulationNeverChangesARegistryEntry()
    {
        using var fixture = new RegistryFixture();
        using var key = Registry.CurrentUser.CreateSubKey(fixture.Path);
        key.SetValue(Name, OldCommand);
        var startup = new StartupRegistration(Executable, true, fixture.Path);
        startup.SetEnabled(true);
        Assert.IsTrue(startup.Enabled);
        startup.SetSilent(false);
        Assert.IsFalse(startup.Silent);
        Assert.AreEqual(StartupCommand(false), startup.Command);
        startup.SetEnabled(false);
        Assert.IsFalse(startup.Enabled);
        Assert.IsNull(startup.ConflictingCommand);
        Assert.ThrowsException<ControlException>(() => startup.ReplaceAfterConfirmation(OldCommand));
        Assert.AreEqual(OldCommand, key.GetValue(Name));
    }

    [DataTestMethod]
    [DataRow("startup_owned_by_another_install")]
    [DataRow("startup_requires_executable")]
    [DataRow("startup_registration_changed")]
    [DataRow("startup_registration_failed")]
    public void StartupErrorsHaveSpecificUserFacingMessages(string code)
    {
        Assert.AreNotEqual(UiText.BriefError("unexpected_error"), UiText.BriefError(code));
        Assert.AreNotEqual(UiText.Error("unexpected_error"), UiText.Error(code));
    }

    private static string StartupCommand(bool silent) => $"\"{Path.GetFullPath(Executable)}\"" + (silent ? " --background" : string.Empty);

    private sealed class RegistryFixture : IDisposable
    {
        private const string Prefix = @"Software\Fy1Switch.Tests\Startup-";
        public string Path { get; } = Prefix + Guid.NewGuid().ToString("N");
        public void Dispose()
        {
            var suffix = Path[Prefix.Length..];
            if (!Path.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(suffix, "N", out _))
                throw new InvalidOperationException("Refusing to delete a registry key outside the owned test path.");
            Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
        }
    }
}
