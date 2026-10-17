using System;

namespace RemoteDisplaySwitch.Host.UI;

internal static class PairingRenewalPolicy
{
    // Natural expiry may renew a visible session. Failed authentication, exhausted
    // admission budgets and in-flight handshakes always require separate handling.
    internal static bool ShouldRenew(string status, DateTimeOffset? bundleExpiry, DateTimeOffset now) =>
        status == "expired" || (status is "stopped" or "listening" or "discovery_unavailable") && bundleExpiry <= now;
}
