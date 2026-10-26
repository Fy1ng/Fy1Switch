using Microsoft.Data.Sqlite;
using Fy1Switch.Contracts;

namespace Fy1Switch.Runtime;

public sealed class StateStore : IDisposable
{
    private readonly SqliteConnection _connection;
    public StateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "state.db") }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS binding (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS operations (id TEXT PRIMARY KEY, payload TEXT NOT NULL);");
        Execute("INSERT OR IGNORE INTO metadata(key,value) VALUES('device_id',$value)", ("$value", Guid.NewGuid().ToString()));
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM operations";
        using var reader = command.ExecuteReader();
        var interrupted = new List<OperationRecord>();
        while (reader.Read())
        {
            var record = WireJson.Parse<OperationRecord>(reader.GetString(0));
            if (record.Outcome == OperationOutcome.Pending) interrupted.Add(record with { Outcome = OperationOutcome.Unknown, ErrorCode = "host_restarted", UpdatedAt = DateTimeOffset.UtcNow });
        }
        reader.Close();
        foreach (var record in interrupted) SaveOperation(record);
    }

    public Guid DeviceId => Guid.Parse(ReadScalar("SELECT value FROM metadata WHERE key='device_id'")!);
    public MonitorPreferences LoadMonitorPreferences(string targetKey) =>
        ReadScalar("SELECT value FROM metadata WHERE key=$key", ("$key", "monitor_preferences:" + targetKey)) is { } json
            ? WireJson.Parse<MonitorPreferences>(json) : new();
    public void SaveMonitorPreferences(string targetKey, MonitorPreferences preferences) =>
        Execute("INSERT INTO metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            ("$key", "monitor_preferences:" + targetKey), ("$value", WireJson.Serialize(preferences)));
    public MonitorBinding? LoadBinding() => ReadScalar("SELECT payload FROM binding WHERE id=1") is { } json ? WireJson.Parse<MonitorBinding>(json) : null;
    public void SaveBinding(MonitorBinding? binding)
    {
        if (binding is null) Execute("DELETE FROM binding WHERE id=1");
        else Execute("INSERT INTO binding(id,payload) VALUES(1,$value) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload", ("$value", WireJson.Serialize(binding)));
    }
    public OperationRecord? FindOperation(Guid id) => ReadScalar("SELECT payload FROM operations WHERE id=$id", ("$id", id.ToString())) is { } json ? WireJson.Parse<OperationRecord>(json) : null;
    public void SaveOperation(OperationRecord record) => Execute("INSERT INTO operations(id,payload) VALUES($id,$value) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload", ("$id", record.OperationId.ToString()), ("$value", WireJson.Serialize(record)));
    private string? ReadScalar(string sql, params (string Name, string Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command.ExecuteScalar() as string;
    }
    private void Execute(string sql, params (string Name, string Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }
    public void Dispose() => _connection.Dispose();
}
