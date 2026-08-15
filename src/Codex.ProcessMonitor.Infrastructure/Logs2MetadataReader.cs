using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Codex.ProcessMonitor.Infrastructure;

/// <summary>
/// Incrementally reads metadata from one or more logs_2.sqlite files in read-only mode.
/// Content/body tables and body-like columns are never selected.
/// </summary>
public sealed class Logs2MetadataReader
{
    // The Codex logs database can contain task text and other user-provided
    // payloads. Keep this allow-list intentionally narrow: even an unfamiliar
    // column name is not metadata we are permitted to copy.
    private static readonly HashSet<string> AllowedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "ts",
        "ts_nanos",
        "level",
        "target",
        "module_path",
        "file",
        "line",
        "thread_id",
        "process_uuid",
        "estimated_bytes",
    };

    private readonly Dictionary<string, long> _checkpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public Logs2MetadataReader(IReadOnlyDictionary<string, long>? checkpoints = null)
    {
        if (checkpoints is null)
            return;
        foreach (var checkpoint in checkpoints)
            _checkpoints[checkpoint.Key] = checkpoint.Value;
    }

    public IReadOnlyDictionary<string, long> Checkpoints
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, long>(_checkpoints, StringComparer.OrdinalIgnoreCase);
        }
    }

    public LogsMetadataReadResult ReadIncremental(
        IEnumerable<string> databasePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(databasePaths);
        var rows = new List<LogsMetadataRow>();
        var warnings = new List<string>();

        foreach (var inputPath in databasePaths.Where(static p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(inputPath);
            if (!File.Exists(path))
            {
                warnings.Add($"SQLite source does not exist: {path}");
                continue;
            }

            try
            {
                ReadDatabase(path, rows, warnings, cancellationToken);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not read SQLite metadata from {path}: {ex.Message}");
            }
        }

        IReadOnlyDictionary<string, long> checkpoints;
        lock (_gate)
            checkpoints = new Dictionary<string, long>(_checkpoints, StringComparer.OrdinalIgnoreCase);
        SqliteConnection.ClearAllPools();
        return new LogsMetadataReadResult(rows, checkpoints, warnings);
    }

    public Task<LogsMetadataReadResult> ReadIncrementalAsync(
        IEnumerable<string> databasePaths,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadIncremental(databasePaths, cancellationToken), cancellationToken);

    public LogsMetadataReadResult Read(IEnumerable<string> databasePaths, CancellationToken cancellationToken = default) =>
        ReadIncremental(databasePaths, cancellationToken);

    public void SetCheckpoint(string sourcePath, string tableName, long rowId)
    {
        var key = CheckpointKey(sourcePath, tableName);
        lock (_gate)
            _checkpoints[key] = rowId;
    }

    public void Reset() {
        lock (_gate)
            _checkpoints.Clear();
    }

    private void ReadDatabase(
        string path,
        ICollection<LogsMetadataRow> rows,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        using var tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var tableReader = tablesCommand.ExecuteReader();
        var tableNames = new List<string>();
        while (tableReader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = tableReader.GetString(0);
            if (!IsReadableTable(name))
                continue;
            tableNames.Add(name);
        }
        tableReader.Dispose();

        foreach (var tableName in tableNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReadTable(connection, path, tableName, rows, cancellationToken);
            }
            catch (SqliteException ex)
            {
                // A table may be dropped between sqlite_master and PRAGMA. Keep the other source usable.
                warnings.Add($"Could not read metadata table {tableName} from {path}: {ex.Message}");
            }
        }
    }

    private void ReadTable(
        SqliteConnection connection,
        string path,
        string tableName,
        ICollection<LogsMetadataRow> rows,
        CancellationToken cancellationToken)
    {
        var columns = ReadColumns(connection, tableName);
        if (columns.Count == 0)
            return;

        var selected = columns
            .Where(static c => AllowedColumns.Contains(c.Name))
            .ToArray();
        if (selected.Length == 0)
            return;

        var checkpointKey = CheckpointKey(path, tableName);
        long checkpoint;
        lock (_gate)
            checkpoint = _checkpoints.GetValueOrDefault(checkpointKey);

        var hasRowId = columns.Any(static c => c.Name.Equals("rowid", StringComparison.OrdinalIgnoreCase));
        // SQLite tables normally have an implicit rowid even though PRAGMA does not list one.
        var rowIdExpression = "rowid";
        var selectList = string.Join(", ", selected.Select(c => QuoteIdentifier(c.Name)));
        var sql = $"SELECT {rowIdExpression}, {selectList} FROM {QuoteIdentifier(tableName)} WHERE rowid > $checkpoint ORDER BY rowid";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$checkpoint", checkpoint);
        using var reader = command.ExecuteReader();

        long maxRowId = checkpoint;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long? rowId = reader.IsDBNull(0) ? null : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (rowId is null)
                continue;
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            DateTimeOffset? timestamp = null;
            for (var index = 0; index < selected.Length; index++)
            {
                var value = ToMetadataString(reader.GetValue(index + 1));
                values[selected[index].Name] = value;
                if (timestamp is null && IsTimestampColumn(selected[index].Name))
                    timestamp = ParseTimestamp(value);
            }

            rows.Add(new LogsMetadataRow(path, tableName, rowId, timestamp, values));
            maxRowId = Math.Max(maxRowId, rowId.Value);
        }

        lock (_gate)
            _checkpoints[checkpointKey] = maxRowId;
    }

    private static List<ColumnInfo> ReadColumns(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        using var reader = command.ExecuteReader();
        var columns = new List<ColumnInfo>();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            columns.Add(new ColumnInfo(name));
        }
        return columns;
    }

    private static bool IsReadableTable(string tableName)
    {
        // Keep the deny-list intentionally broad: metadata may be retained, but body/content must not be touched.
        var normalized = tableName.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return !normalized.Contains("body", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("content", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("attachment", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("transcript", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTimestampColumn(string name) =>
        name.Contains("time", StringComparison.OrdinalIgnoreCase)
        || name.Contains("date", StringComparison.OrdinalIgnoreCase)
        || name.Contains("created", StringComparison.OrdinalIgnoreCase)
        || name.Contains("updated", StringComparison.OrdinalIgnoreCase);

    private static string? ToMetadataString(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            try
            {
                // Accept both Unix seconds and Unix milliseconds without guessing for small ids.
                return number > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                    : DateTimeOffset.FromUnixTimeSeconds(number);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string CheckpointKey(string sourcePath, string tableName) =>
        $"{Path.GetFullPath(sourcePath)}\u001f{tableName}";

    private readonly record struct ColumnInfo(string Name);
}
