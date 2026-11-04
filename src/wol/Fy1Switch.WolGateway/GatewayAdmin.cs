using System.Text;
using System.Text.Json;

namespace Fy1Switch.WolGateway;

public static class GatewayAdmin
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 2) throw new ArgumentException();
            var settings = GatewaySettings.Load(args[0]);
            var store = new GatewayStore(settings, TimeProvider.System);
            switch (args[1])
            {
                case "code" when args.Length > 2:
                    var code = store.GenerateCode(args.Skip(2).Select(Guid.Parse).ToArray());
                    Console.WriteLine(JsonSerializer.Serialize(new { registrationCode = code.Code, expiresAt = code.ExpiresAt }, WolJson.Options));
                    break;
                case "clients" when args.Length == 2:
                    Console.WriteLine(JsonSerializer.Serialize(store.Clients(), WolJson.Options));
                    break;
                case "revoke" when args.Length == 3:
                    store.Revoke(Guid.Parse(args[2]));
                    Console.WriteLine("Client revoked. Already dispatched packets cannot be withdrawn.");
                    break;
                case "export-openwrt" when args.Length == 3:
                    if (!Path.IsPathFullyQualified(args[2])) throw new ArgumentException();
                    using (var stream = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.NewLine = "\n";
                        foreach (var device in settings.Devices)
                            writer.WriteLine($"{device.DeviceId:D} {device.MacAddress.ToUpperInvariant()} {device.Interface} {OpenSshExecutor.DeviceDigest(device)}");
                    }
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(args[2], UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    Console.WriteLine("Created OpenWrt allowlist. Install it using a trusted administrative channel; no router connection was made.");
                    break;
                default: throw new ArgumentException();
            }
            return 0;
        }
        catch (GatewayException error) { Console.Error.WriteLine(error.Code); return 1; }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            Console.Error.WriteLine("Usage: Fy1Switch.WolGateway admin <absolute-config> code <device-uuid> [...] | clients | revoke <client-uuid> | export-openwrt <new-absolute-file>");
            return 2;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { Console.Error.WriteLine("Administration failed. Check configuration, permissions and data storage. No private values were logged."); return 1; }
    }
}
