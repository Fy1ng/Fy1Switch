using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fy1Switch.WolGateway;

// Every mutation is a SQLite transaction. The CLI and service share no credential cache,
// so a revocation is effective without restarting the service.
public sealed class GatewayStore
{
    private readonly GatewaySettings settings;
    private readonly TimeProvider time;
    private readonly string connectionString;
    public GatewayStore(GatewaySettings settings, TimeProvider time)
    {
        this.settings = settings;
        this.time = time;
        Directory.CreateDirectory(settings.DataDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(settings.DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(settings.DataDirectory, "wol.db"), Pooling = false }.ToString();
        using var db = Open();
        Execute(db, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; " +
            "CREATE TABLE IF NOT EXISTS clients(id TEXT PRIMARY KEY,name TEXT NOT NULL,token_hash BLOB NOT NULL UNIQUE,created INTEGER NOT NULL,revoked INTEGER NOT NULL DEFAULT 0);" +
            "CREATE TABLE IF NOT EXISTS grants(client_id TEXT NOT NULL REFERENCES clients(id),device_id TEXT NOT NULL,PRIMARY KEY(client_id,device_id));" +
            "CREATE TABLE IF NOT EXISTS codes(id TEXT PRIMARY KEY,hash BLOB NOT NULL,expires INTEGER NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,used INTEGER NOT NULL DEFAULT 0);" +
            "CREATE TABLE IF NOT EXISTS code_grants(code_id TEXT NOT NULL REFERENCES codes(id) ON DELETE CASCADE,device_id TEXT NOT NULL,PRIMARY KEY(code_id,device_id));" +
            "CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY,client_id TEXT NOT NULL,device_id TEXT NOT NULL,fingerprint BLOB NOT NULL,status TEXT NOT NULL,error TEXT,updated INTEGER NOT NULL,retain_until INTEGER NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS requests(client_id TEXT NOT NULL,id TEXT NOT NULL,fingerprint BLOB NOT NULL,expires INTEGER NOT NULL,PRIMARY KEY(client_id,id));" +
            "CREATE TABLE IF NOT EXISTS cooldowns(device_id TEXT PRIMARY KEY,until_at INTEGER NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS budgets(id TEXT PRIMARY KEY,window_start INTEGER NOT NULL,count INTEGER NOT NULL);");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(settings.DataDirectory, "wol.db"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public RegistrationCode GenerateCode(IReadOnlyList<Guid> deviceIds)
    {
        if (deviceIds.Count == 0 || deviceIds.Count > 64 || deviceIds.Distinct().Count() != deviceIds.Count || deviceIds.Any(id => settings.Devices.All(d => d.DeviceId != id)))
            throw new GatewayException(400, "invalid_devices");
        var id = Guid.NewGuid().ToString("N");
        var secret = WolValidation.RandomSecret(24);
        var code = id + "." + secret;
        var expires = time.GetUtcNow().AddMinutes(5);
        using var db = Open();
        using var tx = db.BeginTransaction();
        Cleanup(db, tx);
        if (Count(db, tx, "SELECT COUNT(*) FROM codes WHERE used=0") >= 32) throw new GatewayException(429, "registration_capacity");
        Execute(db, tx, "INSERT INTO codes(id,hash,expires) VALUES($id,$hash,$expires)", ("$id", id), ("$hash", WolValidation.Hash(secret)), ("$expires", Millis(expires)));
        foreach (var deviceId in deviceIds) Execute(db, tx, "INSERT INTO code_grants(code_id,device_id) VALUES($id,$device)", ("$id", id), ("$device", deviceId.ToString()));
        tx.Commit();
        return new(code, expires);
    }

    public RegistrationReply Register(RegistrationRequest request)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (!Budget(db, tx, "registration", 10, TimeSpan.FromMinutes(5))) { tx.Commit(); throw new GatewayException(429, "rate_limited"); }
        var parts = request.RegistrationCode?.Split('.') ?? [];
        var valid = request.Version == 1 && WolValidation.ValidName(request.ClientName) && parts.Length == 2 &&
            parts[0].Length == 32 && Guid.TryParseExact(parts[0], "N", out _) && parts[1].Length == 32 && parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        if (!valid) { tx.Commit(); throw new GatewayException(401, "registration_rejected"); }
        byte[]? expected = null;
        var expires = 0L;
        var attempts = 0;
        var used = true;
        using (var command = Command(db, tx, "SELECT hash,expires,attempts,used FROM codes WHERE id=$id", ("$id", parts[0])))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) { expected = (byte[])reader[0]; expires = reader.GetInt64(1); attempts = reader.GetInt32(2); used = reader.GetBoolean(3); }
        if (expected is not null) Execute(db, tx, "UPDATE codes SET attempts=attempts+1 WHERE id=$id", ("$id", parts[0]));
        if (expected is null || used || attempts >= 5 || expires <= Millis(time.GetUtcNow()) || !CryptographicOperations.FixedTimeEquals(expected, WolValidation.Hash(parts[1])))
        { tx.Commit(); throw new GatewayException(401, "registration_rejected"); }
        var devices = new List<DeviceDescription>();
        using (var command = Command(db, tx, "SELECT device_id FROM code_grants WHERE code_id=$id", ("$id", parts[0])))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (settings.Devices.FirstOrDefault(d => d.DeviceId == Guid.Parse(reader.GetString(0))) is { } device) devices.Add(new(device.DeviceId, device.Name));
        if (devices.Count == 0) { tx.Commit(); throw new GatewayException(401, "registration_rejected"); }
        if (Count(db, tx, "SELECT COUNT(*) FROM clients") >= 512) { tx.Commit(); throw new GatewayException(429, "client_capacity"); }
        var id = Guid.NewGuid();
        var token = WolValidation.RandomSecret();
        Execute(db, tx, "INSERT INTO clients(id,name,token_hash,created) VALUES($id,$name,$hash,$created)",
            ("$id", id.ToString()), ("$name", request.ClientName), ("$hash", WolValidation.Hash(token)), ("$created", Millis(time.GetUtcNow())));
        foreach (var device in devices) Execute(db, tx, "INSERT INTO grants(client_id,device_id) VALUES($client,$device)", ("$client", id.ToString()), ("$device", device.DeviceId.ToString()));
        Execute(db, tx, "UPDATE codes SET used=1 WHERE id=$id", ("$id", parts[0]));
        tx.Commit();
        return new(1, id, token, devices);
    }

    public Guid Authenticate(string? token)
    {
        if (!WolValidation.ValidToken(token)) throw new GatewayException(401, "unauthorized");
        using var db = Open();
        var hash = WolValidation.Hash(token!);
        using var command = Command(db, null, "SELECT id,token_hash FROM clients WHERE token_hash=$hash AND revoked=0", ("$hash", hash));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !CryptographicOperations.FixedTimeEquals(hash, (byte[])reader[1])) throw new GatewayException(401, "unauthorized");
        return Guid.Parse(reader.GetString(0));
    }

    public DeviceList Devices(Guid clientId)
    {
        using var db = Open();
        EnsureActive(db, null, clientId);
        var devices = new List<DeviceDescription>();
        using var command = Command(db, null, "SELECT device_id FROM grants WHERE client_id=$id", ("$id", clientId.ToString()));
        using var reader = command.ExecuteReader();
        while (reader.Read()) if (settings.Devices.FirstOrDefault(d => d.DeviceId == Guid.Parse(reader.GetString(0))) is { } device) devices.Add(new(device.DeviceId, device.Name));
        return new(1, clientId, devices);
    }

    public void Authorize(Guid clientId, Guid deviceId)
    {
        using var db = Open();
        EnsureAuthorized(db, null, clientId, deviceId);
    }

    public void AdmitClientRequest(Guid clientId)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        EnsureActive(db, tx, clientId);
        var allowed = Budget(db, tx, "client:" + clientId, settings.ClientRequestsPerMinute, TimeSpan.FromMinutes(1));
        tx.Commit();
        if (!allowed) throw new GatewayException(429, "rate_limited");
    }

    public void Revoke(Guid clientId)
    {
        using var db = Open();
        if (Execute(db, null, "UPDATE clients SET revoked=1 WHERE id=$id", ("$id", clientId.ToString())) == 0)
            throw new GatewayException(404, "client_not_found");
    }

    public IReadOnlyList<RegisteredClient> Clients()
    {
        using var db = Open();
        var clients = new List<RegisteredClient>();
        using (var command = Command(db, null, "SELECT id,name,created,revoked FROM clients ORDER BY created"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) clients.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), reader.GetBoolean(3), []));
        for (var i = 0; i < clients.Count; i++)
        {
            var ids = new List<Guid>();
            using var command = Command(db, null, "SELECT device_id FROM grants WHERE client_id=$id", ("$id", clients[i].ClientId.ToString()));
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
            clients[i] = clients[i] with { DeviceIds = ids };
        }
        return clients;
    }

    public (WakeOperation Operation, bool Execute) AdmitWake(Guid clientId, WakeRequest request)
    {
        if (request.OperationId == Guid.Empty) throw new GatewayException(400, "invalid_request");
        using var db = Open();
        using var tx = db.BeginTransaction();
        EnsureAuthorized(db, tx, clientId, request.DeviceId);
        Cleanup(db, tx);
        var fingerprint = WolValidation.Hash(JsonSerializer.Serialize(new { request.Version, request.DeviceId, request.OperationId, request.IssuedAt, request.ExpiresAt }, WolJson.Options));
        RecordRequest(db, tx, clientId, request.RequestId, fingerprint, request.ExpiresAt, allowIdentical: true);
        using (var command = Command(db, tx, "SELECT client_id,device_id,fingerprint,status,error,updated FROM operations WHERE id=$id", ("$id", request.OperationId.ToString())))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != clientId.ToString() || reader.GetString(1) != request.DeviceId.ToString() || !CryptographicOperations.FixedTimeEquals((byte[])reader[2], fingerprint))
                    throw new GatewayException(409, "operation_conflict");
                var operation = new WakeOperation(1, request.DeviceId, request.OperationId, reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)));
                reader.Close();
                tx.Commit();
                return (operation, false);
            }
        }
        if (Count(db, tx, "SELECT COUNT(*) FROM operations") >= 8192) throw new GatewayException(429, "operation_capacity");
        if (Count(db, tx, "SELECT COUNT(*) FROM operations WHERE device_id=$id AND status='pending'", ("$id", request.DeviceId.ToString())) != 0)
            throw new GatewayException(409, "device_busy");
        if (Count(db, tx, "SELECT COUNT(*) FROM cooldowns WHERE device_id=$id AND until_at>$now", ("$id", request.DeviceId.ToString()), ("$now", Millis(time.GetUtcNow()))) != 0)
            throw new GatewayException(429, "device_cooldown");
        var now = DateTimeOffset.FromUnixTimeMilliseconds(Millis(time.GetUtcNow()));
        Execute(db, tx, "INSERT INTO operations(id,client_id,device_id,fingerprint,status,updated,retain_until) VALUES($id,$client,$device,$fingerprint,'pending',$now,$retain)",
            ("$id", request.OperationId.ToString()), ("$client", clientId.ToString()), ("$device", request.DeviceId.ToString()), ("$fingerprint", fingerprint), ("$now", Millis(now)), ("$retain", Millis(now.AddHours(24))));
        Execute(db, tx, "INSERT INTO cooldowns(device_id,until_at) VALUES($id,$until) ON CONFLICT(device_id) DO UPDATE SET until_at=$until",
            ("$id", request.DeviceId.ToString()), ("$until", Millis(now.AddSeconds(settings.DeviceCooldownSeconds))));
        tx.Commit();
        return (new(1, request.DeviceId, request.OperationId, "pending", null, now), true);
    }

    public void AdmitCheck(Guid clientId, CheckRequest request)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        EnsureAuthorized(db, tx, clientId, request.DeviceId);
        Cleanup(db, tx);
        RecordRequest(db, tx, clientId, request.RequestId, WolValidation.Hash(JsonSerializer.Serialize(request, WolJson.Options)), request.ExpiresAt, false);
        if (!Budget(db, tx, "check:" + request.DeviceId, 6, TimeSpan.FromMinutes(1))) { tx.Commit(); throw new GatewayException(429, "rate_limited"); }
        tx.Commit();
    }

    public WakeOperation Finish(Guid clientId, Guid deviceId, Guid operationId, SshExecution result)
    {
        using var db = Open();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(Millis(time.GetUtcNow()));
        Execute(db, null, "UPDATE operations SET status=$status,error=$error,updated=$now WHERE id=$id AND client_id=$client AND device_id=$device AND status='pending'",
            ("$status", result.Status), ("$error", (object?)result.ErrorCode ?? DBNull.Value), ("$now", Millis(now)), ("$id", operationId.ToString()), ("$client", clientId.ToString()), ("$device", deviceId.ToString()));
        return new(1, deviceId, operationId, result.Status, result.ErrorCode, now);
    }

    public WakeOperation Operation(Guid clientId, Guid deviceId, Guid operationId)
    {
        using var db = Open();
        EnsureAuthorized(db, null, clientId, deviceId);
        using var command = Command(db, null, "SELECT status,error,updated FROM operations WHERE id=$id AND client_id=$client AND device_id=$device AND retain_until>$now",
            ("$id", operationId.ToString()), ("$client", clientId.ToString()), ("$device", deviceId.ToString()), ("$now", Millis(time.GetUtcNow())));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new GatewayException(404, "operation_not_found");
        return new(1, deviceId, operationId, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
    }

    public void RecoverInterruptedOperations()
    {
        using var db = Open();
        Execute(db, null, "UPDATE operations SET status='unknown',error='gateway_restarted',updated=$now WHERE status='pending'", ("$now", Millis(time.GetUtcNow())));
    }

    public void CheckStorage()
    {
        using var db = Open();
        _ = Count(db, null, "SELECT COUNT(*) FROM clients");
    }

    private void RecordRequest(SqliteConnection db, SqliteTransaction tx, Guid clientId, Guid id, byte[] hash, DateTimeOffset expires, bool allowIdentical)
    {
        using (var command = Command(db, tx, "SELECT fingerprint FROM requests WHERE client_id=$client AND id=$id", ("$client", clientId.ToString()), ("$id", id.ToString())))
        {
            if (command.ExecuteScalar() is byte[] previous)
            {
                if (!allowIdentical || !CryptographicOperations.FixedTimeEquals(previous, hash)) throw new GatewayException(409, "request_replayed");
                return;
            }
        }
        if (Count(db, tx, "SELECT COUNT(*) FROM requests") >= 16384) throw new GatewayException(429, "replay_capacity");
        Execute(db, tx, "INSERT INTO requests(client_id,id,fingerprint,expires) VALUES($client,$id,$hash,$expires)",
            ("$client", clientId.ToString()), ("$id", id.ToString()), ("$hash", hash), ("$expires", Millis(expires)));
    }

    private bool Budget(SqliteConnection db, SqliteTransaction tx, string key, int limit, TimeSpan window)
    {
        var now = Millis(time.GetUtcNow());
        Execute(db, tx, "INSERT INTO budgets(id,window_start,count) VALUES($id,$now,0) ON CONFLICT(id) DO UPDATE SET count=CASE WHEN window_start<=$cutoff THEN 0 ELSE count END,window_start=CASE WHEN window_start<=$cutoff THEN $now ELSE window_start END",
            ("$id", key), ("$now", now), ("$cutoff", now - (long)window.TotalMilliseconds));
        return Execute(db, tx, "UPDATE budgets SET count=count+1 WHERE id=$id AND count<$limit", ("$id", key), ("$limit", limit)) == 1;
    }
    private void Cleanup(SqliteConnection db, SqliteTransaction tx)
    {
        var now = Millis(time.GetUtcNow());
        Execute(db, tx, "DELETE FROM codes WHERE expires<=$now; DELETE FROM requests WHERE expires<=$now; DELETE FROM operations WHERE retain_until<=$now AND status!='pending'; DELETE FROM cooldowns WHERE until_at<=$now; DELETE FROM budgets WHERE window_start<$old",
            ("$now", now), ("$old", now - 24 * 60 * 60 * 1000));
    }
    private void EnsureAuthorized(SqliteConnection db, SqliteTransaction? tx, Guid clientId, Guid deviceId)
    {
        EnsureActive(db, tx, clientId);
        if (settings.Devices.All(x => x.DeviceId != deviceId) || Count(db, tx, "SELECT COUNT(*) FROM grants WHERE client_id=$client AND device_id=$device", ("$client", clientId.ToString()), ("$device", deviceId.ToString())) == 0)
            throw new GatewayException(403, "device_forbidden");
    }
    private static void EnsureActive(SqliteConnection db, SqliteTransaction? tx, Guid clientId)
    {
        if (Count(db, tx, "SELECT COUNT(*) FROM clients WHERE id=$id AND revoked=0", ("$id", clientId.ToString())) != 1) throw new GatewayException(401, "unauthorized");
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        Execute(db, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return db;
    }
    private static long Millis(DateTimeOffset time) => time.ToUnixTimeMilliseconds();
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    { using var command = Command(db, tx, sql, parameters); return command.ExecuteNonQuery(); }
    private static long Count(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    { using var command = Command(db, tx, sql, parameters); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
}
