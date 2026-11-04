using System.Text;
using Fy1Switch.Contracts;

namespace Fy1Switch.Remote;

public enum WolSelectionStatus { NotConfigured, Confirmed, AdapterMissing, MacChanged }

public sealed record WolSetupSettings(string? AdapterId = null, string? AdapterName = null, string? MacAddress = null, int Version = 1)
{
    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("unsupported_wol_setup_version");
        if (AdapterId is null && AdapterName is null && MacAddress is null) return;
        if (string.IsNullOrWhiteSpace(AdapterId) || AdapterId.Length > 256
            || string.IsNullOrWhiteSpace(AdapterName) || AdapterName.Length > 512
            || !WolAdapterChoices.TryNormalizeMac(MacAddress, out _))
            throw new InvalidDataException("invalid_wol_setup");
    }

    public WolSelectionStatus Status(IReadOnlyList<WolAdapterChoice> current)
    {
        Validate();
        if (AdapterId is null) return WolSelectionStatus.NotConfigured;
        var adapter = WolAdapterChoices.Eligible(current).FirstOrDefault(item => item.AdapterId == AdapterId);
        if (adapter is null) return WolSelectionStatus.AdapterMissing;
        return adapter.MacAddress == WolAdapterChoices.NormalizeMac(MacAddress!)
            ? WolSelectionStatus.Confirmed : WolSelectionStatus.MacChanged;
    }
}

/// <summary>Local setup hints only. This file grants no remote permission and contains no access credentials.</summary>
public sealed class WolSetupSettingsStore(string directory)
{
    private string SettingsPath => Path.Combine(directory, "wol-setup.json");

    public WolSetupSettings Load()
    {
        var settings = File.Exists(SettingsPath) ? WireJson.Parse<WolSetupSettings>(File.ReadAllText(SettingsPath)) : new();
        settings.Validate();
        return settings;
    }

    public WolSetupSettings SaveSelection(WolAdapterChoice selected, IReadOnlyList<WolAdapterChoice> current)
    {
        var available = WolAdapterChoices.Eligible(current).FirstOrDefault(adapter => adapter.AdapterId == selected.AdapterId);
        if (available is null) throw new InvalidOperationException("wol_adapter_missing");
        if (!WolAdapterChoices.TryNormalizeMac(selected.MacAddress, out var mac) || available.MacAddress != mac)
            throw new InvalidOperationException("wol_mac_changed");
        var settings = new WolSetupSettings(available.AdapterId, available.Name, available.MacAddress);
        Save(settings);
        return settings;
    }

    public void Clear() => Save(new());

    private void Save(WolSetupSettings settings)
    {
        settings.Validate();
        // Do not let an older build overwrite a future schema or corrupt user configuration.
        if (File.Exists(SettingsPath)) Load();
        Directory.CreateDirectory(directory);
        AtomicFile.Write(SettingsPath, Encoding.UTF8.GetBytes(WireJson.Serialize(settings)));
    }
}
