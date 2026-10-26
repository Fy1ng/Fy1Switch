using System;
using System.Collections.Generic;
using Fy1Switch.Remote;

namespace Fy1Switch.Host;

internal static class RemoteStartupSettings
{
    internal static RemoteSettings Prepare(RemoteSettings saved, IReadOnlyList<LocalNetworkChoice> networks)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(networks);
        var settings = saved with { Enabled = true };
        var automatic = saved.AutomaticNetwork ??
            (saved.ListenAddress == "127.0.0.1" && saved.PublicEndpoint == Endpoint(saved.ListenAddress, saved.Port));
        if (!automatic) return settings;

        var choice = LocalNetworkChoices.SelectLan(networks, saved.ListenAddress);
        var address = choice?.Address ?? "127.0.0.1";
        return settings with
        {
            ListenAddress = address,
            PublicEndpoint = Endpoint(address, saved.Port),
            AutomaticNetwork = true
        };
    }

    private static string Endpoint(string address, int port) =>
        new UriBuilder("https", address, port).Uri.GetLeftPart(UriPartial.Authority);
}
