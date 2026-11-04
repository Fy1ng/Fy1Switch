using Fy1Switch.Contracts;
using Microsoft.Data.Sqlite;

namespace Fy1Switch.Remote;

internal sealed class RemoteDisplayControlJournal
{
    private readonly string _connectionString;
    private readonly int _capacity;
    private bool _initialized;

    internal RemoteDisplayControlJournal(string directory, int capacity)
    {
        if (capacity is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetFullPath(directory), "remote-display-controls.db"),
            Pooling = false, DefaultTimeout = 2
        }.ToString();
    }

    internal SqliteConnection Open(DateTimeOffset now)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        try
        {
            // One Host owns this journal. The remote service serializes access; no pooled file handles survive it.
            Execute(connection, "PRAGMA synchronous=FULL; PRAGMA max_page_count=32768;");
            if (!_initialized)
            {
                using var version = connection.CreateCommand();
                version.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(version.ExecuteScalar()) is not (0 or 1))
                    throw new InvalidDataException("unsupported_control_journal_version");
                Execute(connection, "CREATE TABLE IF NOT EXISTS control_operations (id TEXT PRIMARY KEY, payload TEXT NOT NULL); PRAGMA user_version=1;");
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT payload FROM control_operations";
                using var reader = command.ExecuteReader();
                var interrupted = new List<DisplayControlOperation>();
                while (reader.Read())
                {
                    var operation = WireJson.Parse<DisplayControlOperation>(reader.GetString(0));
                    if (operation.Outcome == OperationOutcome.Pending)
                        interrupted.Add(operation with { Outcome = OperationOutcome.Unknown, ErrorCode = "host_restarted", Result = null, UpdatedAt = now });
                }
                reader.Close();
                foreach (var operation in interrupted) Save(connection, operation);
                _initialized = true;
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal DisplayControlOperation? Find(SqliteConnection connection, Guid operationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM control_operations WHERE id=$id";
        command.Parameters.AddWithValue("$id", operationId.ToString());
        return command.ExecuteScalar() is string payload ? WireJson.Parse<DisplayControlOperation>(payload) : null;
    }

    internal bool HasCapacity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM control_operations";
        return Convert.ToInt64(command.ExecuteScalar()) < _capacity;
    }

    internal void Save(SqliteConnection connection, DisplayControlOperation operation)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO control_operations(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$id", operation.OperationId.ToString());
        command.Parameters.AddWithValue("$payload", WireJson.Serialize(operation));
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
