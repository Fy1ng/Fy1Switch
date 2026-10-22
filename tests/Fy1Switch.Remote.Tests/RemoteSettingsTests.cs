using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Remote;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class RemoteSettingsTests
{
    [TestMethod]
    public void LegacyOptionalPathsRemainEnabledAndNewSwitchesCanDisableWithoutErasingConfiguration()
    {
        var legacy = Fy1Switch.Contracts.WireJson.Parse<RemoteSettings>("""{"relayUrl":"https://relay.example","tailscaleEndpoint":"https://100.70.1.2:48152","tailscaleListenAddress":"100.70.1.2"}""");
        Assert.IsTrue(legacy.UseTailscale);
        Assert.IsTrue(legacy.UseRelay);
        Assert.IsNull(legacy.AutomaticNetwork);
        legacy.Validate();
        using var directory = new TestDirectory();
        var store = new RemoteSettingsStore(directory.Path, new TestOnlyEncodingProtector(directory));
        var disabled = legacy with
        {
            AutomaticNetwork = true, TailscaleEnabled = false, RelayEnabled = false,
            ProtectedRelayToken = store.ProtectToken("fixture-retained-relay-token-" + Guid.NewGuid().ToString("N"))
        };
        store.Save(disabled);
        var loaded = store.Load();
        Assert.AreEqual(disabled, loaded);
        Assert.IsFalse(loaded.UseTailscale);
        Assert.IsFalse(loaded.UseRelay);
        Assert.AreEqual(legacy.TailscaleEndpoint, loaded.TailscaleEndpoint);
        Assert.AreEqual(legacy.RelayUrl, loaded.RelayUrl);
        Assert.IsNotNull(store.RelayToken(loaded));
        (loaded with { TailscaleEnabled = true, RelayEnabled = true }).Validate();
    }

    [TestMethod]
    public void DisabledDraftsAreNotValidatedAsActivePathsButReenablingRequiresValidConfiguration()
    {
        var disabled = new RemoteSettings(TailscaleListenAddress: "not-an-address", TailscaleEndpoint: "unfinished", RelayUrl: "unfinished", TailscaleEnabled: false, RelayEnabled: false);
        disabled.Validate();
        Assert.ThrowsException<ArgumentException>(() => (disabled with { TailscaleEnabled = true }).Validate());
        Assert.ThrowsException<ArgumentException>(() => (disabled with { RelayEnabled = true }).Validate());
        Assert.ThrowsException<ArgumentException>(() => new RemoteSettings(TailscaleEnabled: true).Validate());
        Assert.ThrowsException<ArgumentException>(() => new RemoteSettings(RelayEnabled: true).Validate());
    }

    [DataTestMethod]
    [DataRow("http://127.0.0.1:48152")]
    [DataRow("https://user:password@localhost:48152")]
    [DataRow("https://localhost:48152/path")]
    [DataRow("https://localhost:48152/?query=value")]
    [DataRow("https://localhost:48152/#fragment")]
    [DataRow("not-a-url")]
    public void UnsafeEndpointsAreRejectedForPublicAndRelayUrls(string endpoint)
    {
        Assert.ThrowsException<ArgumentException>(() => new RemoteSettings(PublicEndpoint: endpoint).Validate());
        Assert.ThrowsException<ArgumentException>(() => new RemoteSettings(RelayUrl: endpoint).Validate());
    }

    [DataTestMethod]
    [DataRow("localhost", 48152)]
    [DataRow("invalid-address", 48152)]
    [DataRow("127.0.0.1", 1023)]
    [DataRow("127.0.0.1", 65536)]
    public void InvalidListenersAreRejected(string address, int port)
    {
        Assert.ThrowsException<ArgumentException>(() => new RemoteSettings(ListenAddress: address, Port: port).Validate());
    }

    [TestMethod]
    public void TemporarySettingsRoundTripThroughTestOnlyProtector()
    {
        using var directory = new TestDirectory();
        var protector = new TestOnlyEncodingProtector(directory);
        var store = new RemoteSettingsStore(directory.Path, protector);
        Assert.AreEqual(new RemoteSettings(), store.Load());
        Assert.IsNull(store.RelayToken(store.Load()));
        var testToken = "test-only-relay-token-" + Guid.NewGuid().ToString("N");
        var settings = new RemoteSettings(RelayUrl: "https://localhost:48153", ProtectedRelayToken: store.ProtectToken(testToken));
        store.Save(settings);
        var restarted = new RemoteSettingsStore(directory.Path, protector);
        Assert.AreEqual(settings, restarted.Load());
        Assert.IsTrue(testToken == restarted.RelayToken(restarted.Load()), "Test-only token did not round-trip.");
        Assert.IsFalse(File.ReadAllText(Path.Combine(directory.Path, "remote.json")).Contains(testToken, StringComparison.Ordinal));
        Assert.AreEqual(1, protector.ProtectCalls);
        Assert.AreEqual(1, protector.UnprotectCalls);
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "*.tmp").Length);
        Assert.ThrowsException<ArgumentException>(() => store.Save(settings with { PublicEndpoint = "http://localhost" }));
        Assert.AreEqual(settings, restarted.Load());
    }

    [TestMethod]
    public async Task DisabledManagerUsesOnlyTemporarySettingsAndNeverInvokesController()
    {
        using var directory = new TestDirectory();
        var protector = new TestOnlyEncodingProtector(directory);
        var deviceId = Guid.NewGuid();
        var controller = new FakeDesktopController(deviceId, directory, new TestClock());
        await using var manager = new RemoteManager(controller, deviceId, directory.Path, protector);
        Assert.IsFalse(manager.Running);
        Assert.AreEqual("disabled", manager.RelayStatus);
        Assert.ThrowsException<InvalidOperationException>(() => manager.BeginPairing());
        Assert.IsNull(manager.ProtectedRelayToken("  "));
        var settings = new RemoteSettings(PublicEndpoint: "https://localhost:49152", Port: 49152);
        await manager.ApplyAsync(settings, persist: false);
        Assert.IsFalse(File.Exists(Path.Combine(directory.Path, "remote.json")));
        await manager.ApplyAsync(settings);
        Assert.AreEqual(settings, new RemoteSettingsStore(directory.Path, protector).Load());
        Assert.IsFalse(manager.Running);
        Assert.IsNull(manager.LastError);
        Assert.AreEqual(0, controller.Calls.Length);
    }
}
