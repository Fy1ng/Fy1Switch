using Fy1Switch.Host.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class RelayRegistrationPresentationTests
{
    private static RemoteSettings Saved => new(true, RelayUrl: "https://relay.example.test", ProtectedRelayToken: "test-protected-value", RelayEnabled: true);

    [TestMethod]
    public void CredentialPresenceIsNotAnOnlineOrCurrentRegistrationGuarantee()
    {
        var waiting = RelayRegistrationPresentation.Create(Saved, Saved.RelayUrl!, true, true, "connecting");
        Assert.IsTrue(waiting.HasCredential);
        Assert.IsFalse(waiting.Connected);
        Assert.AreEqual(RelaySetupState.Connecting, waiting.State);
        Assert.IsFalse(waiting.Title.Contains("已注册", StringComparison.Ordinal));
        var accepted = RelayRegistrationPresentation.Create(Saved, Saved.RelayUrl!, true, true, "connecting", justRegistered: true);
        StringAssert.Contains(accepted.Title, "已注册");
        Assert.IsFalse(accepted.Connected);
        Assert.IsTrue(RelayRegistrationPresentation.Create(Saved, Saved.RelayUrl!, true, true, "connected").Connected);
    }

    [DataTestMethod]
    [DataRow("disconnected", "Disconnected")]
    [DataRow("unavailable", "Unavailable")]
    [DataRow("unauthorized", "Unauthorized")]
    [DataRow("conflict", "Conflict")]
    public void ConnectionLossPreservesCredentialButDoesNotRemainOnline(string connection, string expected)
    {
        var state = RelayRegistrationPresentation.Create(Saved, Saved.RelayUrl!, true, true, connection, justRegistered: true);
        Assert.AreEqual(expected, state.State.ToString());
        Assert.IsTrue(state.HasCredential);
        Assert.IsFalse(state.Connected);
    }

    [DataTestMethod]
    [DataRow("https://RELAY.example.test:443/", true)]
    [DataRow(" https://relay.example.test/ ", true)]
    [DataRow("https://another.example.test", false)]
    [DataRow("https://relay.example.test:444", false)]
    [DataRow("https://relay.example.test/path", false)]
    [DataRow("https://relay.example.test?token=example", false)]
    [DataRow("not a URL", false)]
    public void DraftAddressOnlyUsesCredentialsOfTheSameHttpsOrigin(string address, bool same)
    {
        var state = RelayRegistrationPresentation.Create(Saved, address, true, true, "connected");
        Assert.AreEqual(same, state.HasCredential);
        Assert.AreEqual(same, state.Connected);
        Assert.AreEqual(same ? RelaySetupState.Connected : RelaySetupState.AddressChanged, state.State);
    }

    [TestMethod]
    public void DisabledAndStoppedServicesCannotEnablePostRegistrationActions()
    {
        Assert.AreEqual(RelaySetupState.Disabled, RelayRegistrationPresentation.Create(Saved, Saved.RelayUrl!, false, true, "connected").State);
        Assert.AreEqual(RelaySetupState.Saved, RelayRegistrationPresentation.Create(Saved, Saved.RelayUrl!, true, false, "connected").State);
        Assert.AreEqual(RelaySetupState.Saved, RelayRegistrationPresentation.Create(Saved with { RelayEnabled = false }, Saved.RelayUrl!, true, true, "connected").State);
        Assert.AreEqual(RelaySetupState.NotRegistered, RelayRegistrationPresentation.Create(Saved with { ProtectedRelayToken = null }, Saved.RelayUrl!, true, true, "connected").State);
    }

    [TestMethod]
    public void RegistrationProgressAndFailureDoNotPretendToBeConnected()
    {
        var progress = RelayRegistrationPresentation.Create(Saved, Saved.RelayUrl!, true, true, "connected", registering: true);
        Assert.AreEqual(RelaySetupState.Registering, progress.State);
        Assert.IsFalse(progress.Connected);
        var failed = RelayRegistrationPresentation.Create(Saved, Saved.RelayUrl!, true, true, "connected", registrationIncomplete: true);
        Assert.AreEqual(RelaySetupState.RegistrationIncomplete, failed.State);
        Assert.IsTrue(failed.HasCredential);
        Assert.IsFalse(failed.Connected);
    }
}
