using System.Text;
using System.Text.Json;
using Fy1Switch.Contracts;
using Fy1Switch.Relay;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Relay.Tests;

[TestClass]
public sealed class RegistryTests
{
    [TestMethod]
    public void DurableCredentialsAreEncryptedAndCodesAreEphemeralAndRevocationSurvivesRestart()
    {
        using var fixture = new RegistryFixture();
        var id = Guid.NewGuid();
        var phone = Guid.NewGuid();
        RelayRegistrationReply registration;
        RelayControllerReply grant;
        using (var store = fixture.Open())
        {
            var code = store.GenerateRegistrationCode();
            registration = store.Register(new(id, code.Code, "fixture"))!;
            grant = store.ControllerFor(id, phone)!;
            Assert.IsTrue(store.SetPhoneCode(id, new("000007", fixture.Now.AddMinutes(4))));
        }
        var contents = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(fixture.Directory, "registry.db")));
        Assert.IsFalse(contents.Contains(registration.WindowsToken, StringComparison.Ordinal));
        Assert.IsFalse(contents.Contains(grant.ControllerToken, StringComparison.Ordinal));
        using (var reopened = fixture.Open())
        {
            Assert.IsTrue(reopened.Authenticate(id, registration.WindowsToken, true));
            Assert.AreEqual(grant.ControllerToken, reopened.ControllerFor(id, phone)!.ControllerToken);
            Assert.IsNull(reopened.TakePhoneCode("000007"));
            Assert.IsTrue(reopened.Revoke(id));
        }
        using var revoked = fixture.Open();
        Assert.IsTrue(revoked.Contains(id));
        Assert.IsFalse(revoked.Active(id));
        Assert.IsFalse(revoked.Authenticate(id, registration.WindowsToken, true));
        Assert.IsFalse(revoked.Authenticate(id, grant.ControllerToken, false));
    }

    [TestMethod]
    public void ExpiryLeadingZeroCollisionCancellationAndAttemptBudgetAreEnforced()
    {
        using var fixture = new RegistryFixture();
        using var store = fixture.Open();
        var id = Guid.NewGuid();
        var code = store.GenerateRegistrationCode();
        fixture.Now = fixture.Now.AddMinutes(6);
        Assert.IsNull(store.Register(new(id, code.Code, "fixture")));
        code = store.GenerateRegistrationCode();
        Assert.IsNotNull(store.Register(new(id, code.Code, "fixture")));
        Assert.IsTrue(store.SetPhoneCode(id, new("000000", fixture.Now.AddMinutes(5))));
        Assert.IsFalse(store.SetPhoneCode(id, new("000000", fixture.Now.AddMinutes(5))));
        Assert.AreEqual(id, store.TakePhoneCode("000000"));
        Assert.IsNull(store.TakePhoneCode("000000"));
        Assert.IsTrue(store.SetPhoneCode(id, new("000001", fixture.Now.AddMinutes(1))));
        fixture.Now = fixture.Now.AddMinutes(2);
        Assert.IsNull(store.TakePhoneCode("000001"));
        Assert.IsFalse(store.SetPhoneCode(id, new("１２３４５６", fixture.Now.AddMinutes(1))));
        Assert.IsFalse(store.SetPhoneCode(id, new("123456", fixture.Now.AddMinutes(6))));
        Assert.IsTrue(store.SetPhoneCode(id, new("000002", fixture.Now.AddMinutes(1))));
        store.CancelPhoneCode(id);
        Assert.IsNull(store.TakePhoneCode("000002"));
        for (var i = 0; i < 10; i++) Assert.IsTrue(store.AdmitCodeAttempt());
        Assert.IsFalse(store.AdmitCodeAttempt());
        store.GenerateRegistrationCode();
        Assert.IsFalse(store.AdmitCodeAttempt());
        fixture.Now = fixture.Now.AddMinutes(5);
        Assert.IsTrue(store.AdmitCodeAttempt());
    }

    [TestMethod]
    public void SynchronizingWindowsClientsPrunesRevokedGrantsAndFreesCapacity()
    {
        using var fixture = new RegistryFixture();
        using var store = fixture.Open();
        var id = Guid.NewGuid();
        Assert.IsNotNull(store.Register(new(id, store.GenerateRegistrationCode().Code, "fixture")));
        var clients = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        var first = store.ControllerFor(id, clients[0])!;
        foreach (var client in clients.Skip(1)) Assert.IsNotNull(store.ControllerFor(id, client));
        Assert.IsNull(store.ControllerFor(id, Guid.NewGuid()));
        Assert.IsFalse(store.SynchronizeControllers(id, [Guid.Empty]));
        Assert.IsFalse(store.SynchronizeControllers(id, [clients[0], clients[0]]));
        Assert.IsTrue(store.SynchronizeControllers(id, clients.Skip(1).ToArray()));
        Assert.IsFalse(store.Authenticate(id, first.ControllerToken, false));
        Assert.IsNotNull(store.ControllerFor(id, Guid.NewGuid()));
    }

    [TestMethod]
    public void LegacyImportIsExplicitAndCannotReactivateARevokedRegistration()
    {
        using var fixture = new RegistryFixture();
        var id = Guid.NewGuid();
        var windowsToken = RelayFixture.RandomToken();
        var controllerToken = RelayFixture.RandomToken();
        var legacyPath = Path.Combine(fixture.Directory, "legacy.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { devices = new[] { new { deviceId = id, windowsToken, controllerToken } } }));
        var legacy = RelaySettings.Load(legacyPath);
        using (var store = fixture.Open())
        {
            Assert.IsFalse(store.Contains(id));
            Assert.AreEqual(1, store.ImportLegacy(legacy));
            Assert.IsTrue(store.Authenticate(id, windowsToken, true));
            Assert.IsTrue(store.Authenticate(id, controllerToken, false));
            Assert.AreEqual(0, store.ImportLegacy(legacy));
            Assert.IsTrue(store.Revoke(id));
        }
        using var reopened = fixture.Open();
        Assert.AreEqual(0, reopened.ImportLegacy(legacy));
        Assert.IsFalse(reopened.Authenticate(id, windowsToken, true));
        Assert.IsFalse(reopened.Authenticate(id, controllerToken, false));
    }

    private sealed class RegistryFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "TestResults", Guid.NewGuid().ToString("N"));
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
        private readonly RelaySettings settings;
        public RegistryFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var config = Path.Combine(Directory, "config.json");
            File.WriteAllText(config, JsonSerializer.Serialize(new { management = new { publicUrl = "https://relay.example.test", dataDirectory = Directory } }));
            settings = RelaySettings.Load(config);
        }
        public RelayRegistry Open() => new(settings, protection, () => Now);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
