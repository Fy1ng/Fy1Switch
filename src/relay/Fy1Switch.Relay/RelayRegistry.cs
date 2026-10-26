using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fy1Switch.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Fy1Switch.Relay;

public sealed record RegisteredRelayDevice(Guid DeviceId, string DeviceName, DateTimeOffset CreatedAt);

// Durable credentials, ephemeral short codes and connection routing have separate lifetimes.
public sealed class RelayRegistry : IDisposable
{
    private const int DeviceLimit = 256;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Device> devices = new();
    private readonly Dictionary<(Guid Device, Guid Client), Controller> controllers = new();
    private readonly Dictionary<string, PhoneCode> phoneCodes = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> clock;
    private readonly IDataProtector protector;
    private readonly SqliteConnection? database;
    private (byte[] Hash, DateTimeOffset Expires)? registrationCode;
    private DateTimeOffset attemptWindow;
    private int codeAttempts;

    public RelayRegistry(RelaySettings settings, IDataProtectionProvider dataProtection) : this(settings, dataProtection, () => DateTimeOffset.UtcNow) { }

    public RelayRegistry(RelaySettings settings, IDataProtectionProvider dataProtection, Func<DateTimeOffset> clock)
    {
        this.clock = clock;
        protector = dataProtection.CreateProtector("Fy1Switch.Relay.Controller.v1");
        if (settings.Management is not { } options) return;
        Directory.CreateDirectory(options.DataDirectory);
        database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.DataDirectory, "registry.db"), Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        database.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Execute("CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, name TEXT NOT NULL, windows_hash BLOB NOT NULL, created TEXT NOT NULL, revoked INTEGER NOT NULL DEFAULT 0);" +
            "CREATE TABLE IF NOT EXISTS controllers (device_id TEXT NOT NULL REFERENCES devices(id), client_id TEXT NOT NULL, token_hash BLOB NOT NULL, protected_token TEXT NOT NULL, PRIMARY KEY(device_id,client_id));");
        using (var command = database.CreateCommand())
        {
            command.CommandText = "SELECT id,name,windows_hash,created,revoked FROM devices";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (devices.Count >= DeviceLimit) throw new InvalidOperationException("Relay device capacity exceeded.");
                devices.Add(Guid.Parse(reader.GetString(0)), new(reader.GetString(1), (byte[])reader[2],
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetInt64(4) != 0));
            }
        }
        using (var command = database.CreateCommand())
        {
            command.CommandText = "SELECT device_id,client_id,token_hash,protected_token FROM controllers";
            using var reader = command.ExecuteReader();
            while (reader.Read()) controllers.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))), new((byte[])reader[2], reader.GetString(3)));
        }
    }

    public bool Contains(Guid id) { lock (gate) return devices.ContainsKey(id); }
    public bool Active(Guid id) { lock (gate) return devices.TryGetValue(id, out var value) && !value.Revoked; }
    public bool Authenticate(Guid id, string token, bool windows)
    {
        var supplied = Hash(token);
        lock (gate)
        {
            if (!devices.TryGetValue(id, out var device) || device.Revoked) return false;
            if (windows) return CryptographicOperations.FixedTimeEquals(device.WindowsHash, supplied);
            // Do not stop at the first match, or reveal which phone owns the credential.
            var valid = false;
            foreach (var entry in controllers)
                if (entry.Key.Device == id) valid |= CryptographicOperations.FixedTimeEquals(entry.Value.Hash, supplied);
            return valid;
        }
    }

    public IReadOnlyList<RegisteredRelayDevice> Devices
    {
        get { lock (gate) return devices.Where(x => !x.Value.Revoked).Select(x => new RegisteredRelayDevice(x.Key, x.Value.Name, x.Value.CreatedAt)).ToArray(); }
    }

    public RelayCode GenerateRegistrationCode()
    {
        lock (gate)
        {
            EnsureManaged();
            var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            var expiry = clock().AddMinutes(5);
            registrationCode = (Hash(code), expiry);
            return new(code, expiry);
        }
    }

    public RelayRegistrationReply? Register(RelayRegistrationRequest request)
    {
        lock (gate)
        {
            EnsureManaged();
            if (request.DeviceId == Guid.Empty || !ValidName(request.DeviceName) || !ValidCode(request.Code) ||
                registrationCode is not { } code || code.Expires <= clock() ||
                !CryptographicOperations.FixedTimeEquals(code.Hash, Hash(request.Code))) return null;
            if (devices.TryGetValue(request.DeviceId, out var old) && !old.Revoked) return null;
            if (!devices.ContainsKey(request.DeviceId) && devices.Count >= DeviceLimit) return null;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var device = new Device(request.DeviceName.Trim(), Hash(token), clock(), false);
            using var transaction = database!.BeginTransaction();
            Execute("DELETE FROM controllers WHERE device_id=$id", transaction, ("$id", request.DeviceId.ToString()));
            Execute("INSERT INTO devices(id,name,windows_hash,created,revoked) VALUES($id,$name,$hash,$created,0) " +
                "ON CONFLICT(id) DO UPDATE SET name=$name,windows_hash=$hash,created=$created,revoked=0", transaction,
                ("$id", request.DeviceId.ToString()), ("$name", device.Name), ("$hash", device.WindowsHash), ("$created", device.CreatedAt.ToString("O")));
            transaction.Commit();
            devices[request.DeviceId] = device;
            foreach (var key in controllers.Keys.Where(k => k.Device == request.DeviceId).ToArray()) controllers.Remove(key);
            registrationCode = null;
            return new(request.DeviceId, token);
        }
    }

    public bool Revoke(Guid id)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(id, out var value) || value.Revoked) return false;
            using var transaction = database!.BeginTransaction();
            Execute("UPDATE devices SET revoked=1 WHERE id=$id", transaction, ("$id", id.ToString()));
            Execute("DELETE FROM controllers WHERE device_id=$id", transaction, ("$id", id.ToString()));
            transaction.Commit();
            devices[id] = value with { Revoked = true };
            foreach (var key in controllers.Keys.Where(k => k.Device == id).ToArray()) controllers.Remove(key);
            CancelPhoneCode(id);
            return true;
        }
    }

    public int ImportLegacy(RelaySettings settings)
    {
        lock (gate)
        {
            EnsureManaged();
            var import = settings.LegacyDevices.Where(x => !devices.ContainsKey(x.DeviceId)).ToArray();
            if (devices.Count + import.Length > DeviceLimit) throw new InvalidOperationException("Relay device capacity exceeded.");
            using var transaction = database!.BeginTransaction();
            var created = clock();
            foreach (var entry in import)
            {
                Execute("INSERT INTO devices(id,name,windows_hash,created,revoked) VALUES($id,$name,$hash,$created,0)", transaction,
                    ("$id", entry.DeviceId.ToString()), ("$name", "Imported Windows"), ("$hash", entry.WindowsHash), ("$created", created.ToString("O")));
                Execute("INSERT INTO controllers(device_id,client_id,token_hash,protected_token) VALUES($id,$client,$hash,'')", transaction,
                    ("$id", entry.DeviceId.ToString()), ("$client", Guid.Empty.ToString()), ("$hash", entry.ControllerHash));
            }
            transaction.Commit();
            foreach (var entry in import)
            {
                devices.Add(entry.DeviceId, new("Imported Windows", entry.WindowsHash, created, false));
                controllers.Add((entry.DeviceId, Guid.Empty), new(entry.ControllerHash, ""));
            }
            return import.Length;
        }
    }

    public RelayControllerReply? ControllerFor(Guid deviceId, Guid clientId)
    {
        lock (gate)
        {
            if (clientId == Guid.Empty || !Active(deviceId)) return null;
            var key = (deviceId, clientId);
            if (controllers.TryGetValue(key, out var existing))
                return new(deviceId, clientId, protector.Unprotect(existing.ProtectedToken));
            if (controllers.Keys.Count(k => k.Device == deviceId) >= 32) return null;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var controller = new Controller(Hash(token), protector.Protect(token));
            Execute("INSERT INTO controllers(device_id,client_id,token_hash,protected_token) VALUES($id,$client,$hash,$protected)", null,
                ("$id", deviceId.ToString()), ("$client", clientId.ToString()), ("$hash", controller.Hash), ("$protected", controller.ProtectedToken));
            controllers.Add(key, controller);
            return new(deviceId, clientId, token);
        }
    }

    public bool SynchronizeControllers(Guid deviceId, IReadOnlyList<Guid>? clientIds)
    {
        lock (gate)
        {
            if (!Active(deviceId) || clientIds is null || clientIds.Count > 32 || clientIds.Contains(Guid.Empty) ||
                clientIds.Distinct().Count() != clientIds.Count) return false;
            var removed = controllers.Keys.Where(k => k.Device == deviceId && !clientIds.Contains(k.Client)).ToArray();
            using var transaction = database!.BeginTransaction();
            foreach (var key in removed)
                Execute("DELETE FROM controllers WHERE device_id=$id AND client_id=$client", transaction,
                    ("$id", deviceId.ToString()), ("$client", key.Client.ToString()));
            transaction.Commit();
            foreach (var key in removed) controllers.Remove(key);
            return true;
        }
    }

    public bool SetPhoneCode(Guid deviceId, RelayCode code)
    {
        lock (gate)
        {
            PruneCodes();
            if (!Active(deviceId) || !ValidCode(code.Code) || code.ExpiresAt <= clock() || code.ExpiresAt > clock().AddMinutes(5)) return false;
            var hash = Convert.ToHexString(Hash(code.Code));
            if (phoneCodes.ContainsKey(hash)) return false;
            // Eight simultaneous sessions bound the guessing surface of a six-digit namespace.
            if (phoneCodes.Count >= 8 && !phoneCodes.Values.Any(x => x.DeviceId == deviceId)) return false;
            CancelPhoneCode(deviceId);
            phoneCodes.Add(hash, new(deviceId, code.ExpiresAt));
            return true;
        }
    }

    public void CancelPhoneCode(Guid deviceId)
    {
        lock (gate) foreach (var key in phoneCodes.Where(x => x.Value.DeviceId == deviceId).Select(x => x.Key).ToArray()) phoneCodes.Remove(key);
    }

    public Guid? TakePhoneCode(string code)
    {
        lock (gate)
        {
            PruneCodes();
            return ValidCode(code) && phoneCodes.Remove(Convert.ToHexString(Hash(code)), out var session) && Active(session.DeviceId) ? session.DeviceId : null;
        }
    }

    public bool AdmitCodeAttempt()
    {
        lock (gate)
        {
            var now = clock();
            if (attemptWindow <= now) { attemptWindow = now.AddMinutes(5); codeAttempts = 0; }
            return ++codeAttempts <= 10;
        }
    }

    public static bool ValidCode(string? code) => code is { Length: 6 } && code.All(c => c is >= '0' and <= '9');
    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 80 && !name.Any(char.IsControl);
    private void PruneCodes()
    {
        foreach (var key in phoneCodes.Where(x => x.Value.ExpiresAt <= clock()).Select(x => x.Key).ToArray()) phoneCodes.Remove(key);
    }
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
    private void EnsureManaged() { if (database is null) throw new InvalidOperationException("Relay management is disabled."); }
    private void Execute(string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        using var command = database!.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }
    public void Dispose() => database?.Dispose();
    private sealed record Device(string Name, byte[] WindowsHash, DateTimeOffset CreatedAt, bool Revoked);
    private sealed record Controller(byte[] Hash, string ProtectedToken);
    private sealed record PhoneCode(Guid DeviceId, DateTimeOffset ExpiresAt);
}
