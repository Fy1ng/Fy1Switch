using Fy1Switch.Contracts;
using Fy1Switch.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class WolSetupSettingsTests
{
    [TestMethod]
    public void OldInstallWithoutWolFileRemainsUnconfiguredWithoutWritingAnything()
    {
        using var directory = new TestDirectory();
        var store = new WolSetupSettingsStore(directory.Path);
        var settings = store.Load();
        Assert.AreEqual(WolSelectionStatus.NotConfigured, settings.Status([WolAdapterChoicesTests.Adapter()]));
        Assert.IsNull(settings.AdapterId);
        Assert.AreEqual(0, Directory.GetFiles(directory.Path).Length);
        Assert.AreEqual(new WolSetupSettings(), WireJson.Parse<WolSetupSettings>("{}"));
    }

    [TestMethod]
    public void ExplicitSelectionPersistsWithoutTouchingExistingRemoteIdentity()
    {
        using var directory = new TestDirectory();
        var existing = new Dictionary<string, string>
        {
            ["state.db"] = "fixture state identity", ["remote.json"] = "fixture protected remote settings",
            ["clients.json"] = "fixture client hashes", ["identity.pfx.protected"] = "fixture protected certificate"
        };
        foreach (var file in existing) File.WriteAllText(Path.Combine(directory.Path, file.Key), file.Value);
        var adapter = WolAdapterChoicesTests.Adapter(mac: "aa-bb-cc-dd-ee-ff");
        var store = new WolSetupSettingsStore(directory.Path);
        var saved = store.SaveSelection(adapter, [adapter]);
        var loaded = new WolSetupSettingsStore(directory.Path).Load();
        Assert.AreEqual(saved, loaded);
        Assert.AreEqual("AA:BB:CC:DD:EE:FF", loaded.MacAddress);
        Assert.AreEqual(WolSelectionStatus.Confirmed, loaded.Status([adapter]));
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "*.tmp").Length);
        foreach (var file in existing) Assert.AreEqual(file.Value, File.ReadAllText(Path.Combine(directory.Path, file.Key)));
    }

    [TestMethod]
    public void MissingOrChangedAdapterRequiresExplicitConfirmationAndNeverAutoSwitches()
    {
        using var directory = new TestDirectory();
        var original = WolAdapterChoicesTests.Adapter();
        var other = WolAdapterChoicesTests.Adapter("wired-2", "Ethernet 2");
        var changed = original with { MacAddress = "02:11:22:33:44:66" };
        var store = new WolSetupSettingsStore(directory.Path);
        var saved = store.SaveSelection(original, [original]);
        Assert.AreEqual(WolSelectionStatus.AdapterMissing, saved.Status([other]));
        Assert.AreEqual(WolSelectionStatus.MacChanged, saved.Status([changed]));
        Assert.ThrowsException<InvalidOperationException>(() => store.SaveSelection(original, [other]));
        Assert.ThrowsException<InvalidOperationException>(() => store.SaveSelection(original, [changed]));
        Assert.AreEqual(saved, store.Load());
        var reconfirmed = store.SaveSelection(changed, [changed]);
        Assert.AreEqual(changed.MacAddress, reconfirmed.MacAddress);
        Assert.AreEqual(WolSelectionStatus.Confirmed, reconfirmed.Status([changed]));
    }

    [TestMethod]
    public void FormerlyEligibleAdapterCannotRemainConfirmedAfterBecomingVirtual()
    {
        var adapter = WolAdapterChoicesTests.Adapter();
        var saved = new WolSetupSettings(adapter.AdapterId, adapter.Name, adapter.MacAddress);
        Assert.AreEqual(WolSelectionStatus.AdapterMissing, saved.Status([adapter with { Description = "VMware Virtual Adapter" }]));
    }

    [TestMethod]
    public void ClearPersistsUnconfiguredWithoutSelectingAnotherAdapter()
    {
        using var directory = new TestDirectory();
        var adapter = WolAdapterChoicesTests.Adapter();
        var store = new WolSetupSettingsStore(directory.Path);
        store.SaveSelection(adapter, [adapter]);
        store.Clear();
        Assert.AreEqual(new WolSetupSettings(), store.Load());
        Assert.AreEqual(WolSelectionStatus.NotConfigured, store.Load().Status([adapter]));
    }

    [DataTestMethod]
    [DataRow("{\"version\":2}")]
    [DataRow("{\"adapterId\":\"wired\"}")]
    [DataRow("{\"adapterId\":\"wired\",\"adapterName\":\"Ethernet\",\"macAddress\":\"FF:FF:FF:FF:FF:FF\"}")]
    public void InvalidAndFutureConfigurationFailsClosed(string json)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "wol-setup.json");
        File.WriteAllText(path, json);
        var store = new WolSetupSettingsStore(directory.Path);
        Assert.ThrowsException<InvalidDataException>(() => store.Load());
        var adapter = WolAdapterChoicesTests.Adapter();
        Assert.ThrowsException<InvalidDataException>(() => store.SaveSelection(adapter, [adapter]));
        Assert.ThrowsException<InvalidDataException>(() => store.Clear());
        Assert.AreEqual(json, File.ReadAllText(path));
    }
}
