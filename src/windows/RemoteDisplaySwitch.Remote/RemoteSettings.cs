using System.Net;
using System.Security.Cryptography;
using System.Text;
using RemoteDisplaySwitch.Contracts;

namespace RemoteDisplaySwitch.Remote;

public sealed record RemoteSettings(bool Enabled = false, string ListenAddress = "127.0.0.1", int Port = 48152, string PublicEndpoint = "https://127.0.0.1:48152", string? RelayUrl = null, string? ProtectedRelayToken = null)
{
    public void Validate()
    {
        if (!IPAddress.TryParse(ListenAddress, out _) || Port is < 1024 or > 65535) throw new ArgumentException("invalid_listener");
        RequireUrl(PublicEndpoint, "https");
        if (!string.IsNullOrEmpty(RelayUrl)) RequireUrl(RelayUrl, "https");
    }
    public static Uri RequireUrl(string text, string scheme)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != scheme || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/") throw new ArgumentException("invalid_endpoint");
        return uri;
    }
}

public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public sealed class UserSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}

public sealed class RemoteSettingsStore(string directory, ISecretProtector protector)
{
    public RemoteSettings Load() => File.Exists(SettingsPath) ? WireJson.Parse<RemoteSettings>(File.ReadAllText(SettingsPath)) : new();
    private string SettingsPath => Path.Combine(directory, "remote.json");
    public string ProtectToken(string token) => Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(token)));
    public string? RelayToken(RemoteSettings settings) => settings.ProtectedRelayToken is { } secret ? Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(secret))) : null;
    public void Save(RemoteSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(directory);
        AtomicFile.Write(SettingsPath, Encoding.UTF8.GetBytes(WireJson.Serialize(settings)));
    }
}

internal static class AtomicFile
{
    public static void Write(string target, byte[] bytes)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
