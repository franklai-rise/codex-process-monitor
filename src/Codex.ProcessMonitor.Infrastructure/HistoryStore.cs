using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Codex.ProcessMonitor.Infrastructure;

/// <summary>
/// Application-owned history database. Migrations and writes are confined to the supplied path.
/// It never opens logs_2.sqlite and does not expose destructive process operations.
/// </summary>
public sealed partial class HistoryStore : IDisposable, IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly HistoryStoreOptions _options;
    private bool _disposed;

    public HistoryStore(string databasePath, HistoryStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A history database path is required.", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
        _options = options ?? new HistoryStoreOptions();
    }

    public string DatabasePath => _databasePath;

    public void Migrate()
    {
        ThrowIfDisposed();
        if (_options.CreateDirectory)
        {
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(transaction, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL)");
        var version = ReadUserVersion(transaction);
        if (version < 1)
        {
            Execute(transaction, """
            CREATE TABLE IF NOT EXISTS process_samples (
                sample_id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at_utc TEXT NOT NULL,
                process_id INTEGER NOT NULL,
                parent_process_id INTEGER NOT NULL,
                image_name TEXT NOT NULL,
                role INTEGER NOT NULL,
                thread_count INTEGER NOT NULL,
                user_processor_time_100ns INTEGER NOT NULL,
                kernel_processor_time_100ns INTEGER NOT NULL,
                working_set_bytes INTEGER NOT NULL,
                private_bytes INTEGER NOT NULL,
                handle_count INTEGER NOT NULL,
                read_operation_count INTEGER NOT NULL,
                write_operation_count INTEGER NOT NULL,
                other_operation_count INTEGER NOT NULL,
                read_transfer_bytes INTEGER NOT NULL,
                write_transfer_bytes INTEGER NOT NULL,
                other_transfer_bytes INTEGER NOT NULL,
                command_line TEXT NULL,
                image_path TEXT NULL,
                UNIQUE(captured_at_utc, process_id)
            )
            """);
            Execute(transaction, """
            CREATE TABLE IF NOT EXISTS system_samples (
                sample_id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at_utc TEXT NOT NULL,
                idle_time_100ns INTEGER NOT NULL,
                kernel_time_100ns INTEGER NOT NULL,
                user_time_100ns INTEGER NOT NULL,
                total_physical_bytes INTEGER NOT NULL,
                available_physical_bytes INTEGER NOT NULL,
                total_page_file_bytes INTEGER NOT NULL,
                available_page_file_bytes INTEGER NOT NULL,
                total_virtual_bytes INTEGER NOT NULL,
                available_virtual_bytes INTEGER NOT NULL,
                memory_load INTEGER NOT NULL,
                UNIQUE(captured_at_utc)
            )
            """);
            Execute(transaction, """
            CREATE TABLE IF NOT EXISTS metadata_events (
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at_utc TEXT NOT NULL,
                source TEXT NOT NULL,
                kind TEXT NOT NULL,
                event_key TEXT NULL,
                event_value TEXT NULL,
                origin TEXT NULL
            )
            """);
            Execute(transaction, "CREATE INDEX IF NOT EXISTS ix_process_samples_time ON process_samples(captured_at_utc)");
            Execute(transaction, "CREATE INDEX IF NOT EXISTS ix_process_samples_pid_time ON process_samples(process_id, captured_at_utc)");
            Execute(transaction, "CREATE INDEX IF NOT EXISTS ix_system_samples_time ON system_samples(captured_at_utc)");
            Execute(transaction, "CREATE INDEX IF NOT EXISTS ix_metadata_events_time ON metadata_events(captured_at_utc)");
            Execute(transaction, "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES (1, $now)", ("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            Execute(transaction, "PRAGMA user_version = 1");
            version = 1;
        }

        if (version < 2)
        {
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS summary_5m (
                    summary_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bucket_start_utc TEXT NOT NULL UNIQUE,
                    cpu_percent REAL NOT NULL,
                    memory_percent REAL NOT NULL,
                    working_set_bytes INTEGER NOT NULL,
                    process_count INTEGER NOT NULL
                )
                """);
            Execute(transaction, "CREATE INDEX IF NOT EXISTS ix_summary_5m_bucket ON summary_5m(bucket_start_utc)");
            Execute(transaction, "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES (2, $now)", ("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            Execute(transaction, "PRAGMA user_version = 2");
        }
        transaction.Commit();
    }

    public Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(Migrate, cancellationToken);
    }

    public void AppendBatch(HistoryBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ThrowIfDisposed();
        Migrate();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var processCommand = connection.CreateCommand();
        processCommand.Transaction = transaction;
        processCommand.CommandText = """
            INSERT OR IGNORE INTO process_samples(
                captured_at_utc, process_id, parent_process_id, image_name, role, thread_count,
                user_processor_time_100ns, kernel_processor_time_100ns, working_set_bytes, private_bytes,
                handle_count, read_operation_count, write_operation_count, other_operation_count,
                read_transfer_bytes, write_transfer_bytes, other_transfer_bytes, command_line, image_path)
            VALUES($captured, $pid, $parent, $image, $role, $threads, $user, $kernel, $working, $private,
                $handles, $readOps, $writeOps, $otherOps, $readBytes, $writeBytes, $otherBytes, $command, $path)
            """;
        AddParameters(processCommand, "$captured", "$pid", "$parent", "$image", "$role", "$threads", "$user", "$kernel", "$working", "$private", "$handles", "$readOps", "$writeOps", "$otherOps", "$readBytes", "$writeBytes", "$otherBytes", "$command", "$path");
        foreach (var sample in batch.ProcessSamples)
        {
            processCommand.Parameters["$captured"].Value = FormatTime(sample.CapturedAtUtc);
            processCommand.Parameters["$pid"].Value = sample.ProcessId;
            processCommand.Parameters["$parent"].Value = sample.ParentProcessId;
            processCommand.Parameters["$image"].Value = sample.ImageName;
            processCommand.Parameters["$role"].Value = (int)sample.Role;
            processCommand.Parameters["$threads"].Value = sample.ThreadCount;
            processCommand.Parameters["$user"].Value = sample.UserProcessorTime100Ns;
            processCommand.Parameters["$kernel"].Value = sample.KernelProcessorTime100Ns;
            processCommand.Parameters["$working"].Value = sample.WorkingSetBytes;
            processCommand.Parameters["$private"].Value = sample.PrivateBytes;
            processCommand.Parameters["$handles"].Value = sample.HandleCount;
            processCommand.Parameters["$readOps"].Value = sample.ReadOperationCount;
            processCommand.Parameters["$writeOps"].Value = sample.WriteOperationCount;
            processCommand.Parameters["$otherOps"].Value = sample.OtherOperationCount;
            processCommand.Parameters["$readBytes"].Value = sample.ReadTransferBytes;
            processCommand.Parameters["$writeBytes"].Value = sample.WriteTransferBytes;
            processCommand.Parameters["$otherBytes"].Value = sample.OtherTransferBytes;
            // Process identity details are useful during the live sample but are
            // deliberately not persisted in the application history database.
            // Keep the columns for forward-compatible migrations, but always
            // write NULL so command lines and full paths cannot leak into local
            // history or an exported report.
            processCommand.Parameters["$command"].Value = DBNull.Value;
            processCommand.Parameters["$path"].Value = DBNull.Value;
            processCommand.ExecuteNonQuery();
        }

        using var systemCommand = connection.CreateCommand();
        systemCommand.Transaction = transaction;
        systemCommand.CommandText = """
            INSERT OR IGNORE INTO system_samples(
                captured_at_utc, idle_time_100ns, kernel_time_100ns, user_time_100ns,
                total_physical_bytes, available_physical_bytes, total_page_file_bytes, available_page_file_bytes,
                total_virtual_bytes, available_virtual_bytes, memory_load)
            VALUES($captured, $idle, $kernel, $user, $totalPhysical, $availablePhysical,
                $totalPage, $availablePage, $totalVirtual, $availableVirtual, $load)
            """;
        AddParameters(systemCommand, "$captured", "$idle", "$kernel", "$user", "$totalPhysical", "$availablePhysical", "$totalPage", "$availablePage", "$totalVirtual", "$availableVirtual", "$load");
        foreach (var sample in batch.SystemSamples)
        {
            systemCommand.Parameters["$captured"].Value = FormatTime(sample.CapturedAtUtc);
            systemCommand.Parameters["$idle"].Value = sample.IdleTime100Ns;
            systemCommand.Parameters["$kernel"].Value = sample.KernelTime100Ns;
            systemCommand.Parameters["$user"].Value = sample.UserTime100Ns;
            systemCommand.Parameters["$totalPhysical"].Value = sample.TotalPhysicalBytes;
            systemCommand.Parameters["$availablePhysical"].Value = sample.AvailablePhysicalBytes;
            systemCommand.Parameters["$totalPage"].Value = sample.TotalPageFileBytes;
            systemCommand.Parameters["$availablePage"].Value = sample.AvailablePageFileBytes;
            systemCommand.Parameters["$totalVirtual"].Value = sample.TotalVirtualBytes;
            systemCommand.Parameters["$availableVirtual"].Value = sample.AvailableVirtualBytes;
            systemCommand.Parameters["$load"].Value = sample.MemoryLoad;
            systemCommand.ExecuteNonQuery();
        }

        using var eventCommand = connection.CreateCommand();
        eventCommand.Transaction = transaction;
        eventCommand.CommandText = """
            INSERT INTO metadata_events(captured_at_utc, source, kind, event_key, event_value, origin)
            VALUES($captured, $source, $kind, $key, $value, $origin)
            """;
        AddParameters(eventCommand, "$captured", "$source", "$kind", "$key", "$value", "$origin");
        foreach (var item in batch.MetadataEvents)
        {
            eventCommand.Parameters["$captured"].Value = FormatTime(item.CapturedAtUtc);
            eventCommand.Parameters["$source"].Value = item.Source;
            eventCommand.Parameters["$kind"].Value = item.Kind;
            eventCommand.Parameters["$key"].Value = (object?)item.Key ?? DBNull.Value;
            eventCommand.Parameters["$value"].Value = (object?)item.Value ?? DBNull.Value;
            eventCommand.Parameters["$origin"].Value = (object?)item.Origin ?? DBNull.Value;
            eventCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Task AppendBatchAsync(HistoryBatch batch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => AppendBatch(batch), cancellationToken);
    }

    public void Append(ProcessSampleRecord sample) => AppendBatch(new HistoryBatch(new[] { sample }, Array.Empty<SystemSampleRecord>(), Array.Empty<MetadataEventRecord>()));

    public void Append(SystemSampleRecord sample) => AppendBatch(new HistoryBatch(Array.Empty<ProcessSampleRecord>(), new[] { sample }, Array.Empty<MetadataEventRecord>()));

    public void AppendSummary5m(Summary5mRecord summary)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(summary);
        Migrate();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO summary_5m(bucket_start_utc, cpu_percent, memory_percent, working_set_bytes, process_count)
            VALUES($bucket, $cpu, $memory, $working, $count)
            ON CONFLICT(bucket_start_utc) DO UPDATE SET
                cpu_percent = excluded.cpu_percent,
                memory_percent = excluded.memory_percent,
                working_set_bytes = excluded.working_set_bytes,
                process_count = excluded.process_count
            """;
        command.Parameters.AddWithValue("$bucket", FormatTime(summary.BucketStartUtc));
        command.Parameters.AddWithValue("$cpu", summary.CpuPercent);
        command.Parameters.AddWithValue("$memory", summary.MemoryPercent);
        command.Parameters.AddWithValue("$working", summary.WorkingSetBytes);
        command.Parameters.AddWithValue("$count", summary.ProcessCount);
        command.ExecuteNonQuery();
    }

    public Task AppendSummary5mAsync(Summary5mRecord summary, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => AppendSummary5m(summary), cancellationToken);
    }

    /// <summary>Applies the bounded local retention policy without touching Codex data.</summary>
    public void Prune(DateTimeOffset? nowUtc = null)
    {
        ThrowIfDisposed();
        Migrate();
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        DeleteOlderThan(transaction, "process_samples", "captured_at_utc", now - _options.ProcessRetention);
        DeleteOlderThan(transaction, "system_samples", "captured_at_utc", now - _options.SummaryRetention);
        DeleteOlderThan(transaction, "metadata_events", "captured_at_utc", now - _options.MetadataRetention);
        DeleteOlderThan(transaction, "summary_5m", "bucket_start_utc", now - _options.SummaryRetention);
        transaction.Commit();
    }

    public Task PruneAsync(DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Prune(nowUtc), cancellationToken);
    }

    public IReadOnlyList<ProcessSampleRecord> QueryProcessSamples(
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int? processId = null,
        int limit = 10000)
    {
        ThrowIfDisposed();
        Migrate();
        limit = Math.Clamp(limit, 1, 1_000_000);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var clauses = new List<string>();
        if (fromUtc is not null) { clauses.Add("captured_at_utc >= $from"); command.Parameters.AddWithValue("$from", FormatTime(fromUtc.Value)); }
        if (toUtc is not null) { clauses.Add("captured_at_utc <= $to"); command.Parameters.AddWithValue("$to", FormatTime(toUtc.Value)); }
        if (processId is not null) { clauses.Add("process_id = $pid"); command.Parameters.AddWithValue("$pid", processId.Value); }
        command.CommandText = $"SELECT captured_at_utc, process_id, parent_process_id, image_name, role, thread_count, user_processor_time_100ns, kernel_processor_time_100ns, working_set_bytes, private_bytes, handle_count, read_operation_count, write_operation_count, other_operation_count, read_transfer_bytes, write_transfer_bytes, other_transfer_bytes, NULL AS command_line, NULL AS image_path FROM process_samples {(clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses))} ORDER BY captured_at_utc, process_id LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var results = new List<ProcessSampleRecord>();
        while (reader.Read())
            results.Add(ReadProcessSample(reader));
        return results;
    }

    public Task<IReadOnlyList<ProcessSampleRecord>> QueryProcessSamplesAsync(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int? processId = null, int limit = 10000, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => (IReadOnlyList<ProcessSampleRecord>)QueryProcessSamples(fromUtc, toUtc, processId, limit), cancellationToken);
    }

    public IReadOnlyList<SystemSampleRecord> QuerySystemSamples(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 10000)
    {
        ThrowIfDisposed();
        Migrate();
        limit = Math.Clamp(limit, 1, 1_000_000);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var clauses = new List<string>();
        if (fromUtc is not null) { clauses.Add("captured_at_utc >= $from"); command.Parameters.AddWithValue("$from", FormatTime(fromUtc.Value)); }
        if (toUtc is not null) { clauses.Add("captured_at_utc <= $to"); command.Parameters.AddWithValue("$to", FormatTime(toUtc.Value)); }
        command.CommandText = $"SELECT captured_at_utc, idle_time_100ns, kernel_time_100ns, user_time_100ns, total_physical_bytes, available_physical_bytes, total_page_file_bytes, available_page_file_bytes, total_virtual_bytes, available_virtual_bytes, memory_load FROM system_samples {(clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses))} ORDER BY captured_at_utc LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var results = new List<SystemSampleRecord>();
        while (reader.Read())
            results.Add(ReadSystemSample(reader));
        return results;
    }

    public IReadOnlyList<MetadataEventRecord> QueryMetadataEvents(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 10000)
    {
        ThrowIfDisposed();
        Migrate();
        limit = Math.Clamp(limit, 1, 1_000_000);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var clauses = new List<string>();
        if (fromUtc is not null) { clauses.Add("captured_at_utc >= $from"); command.Parameters.AddWithValue("$from", FormatTime(fromUtc.Value)); }
        if (toUtc is not null) { clauses.Add("captured_at_utc <= $to"); command.Parameters.AddWithValue("$to", FormatTime(toUtc.Value)); }
        command.CommandText = $"SELECT captured_at_utc, source, kind, event_key, event_value, origin FROM metadata_events {(clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses))} ORDER BY captured_at_utc, event_id LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var results = new List<MetadataEventRecord>();
        while (reader.Read())
            results.Add(new MetadataEventRecord(ParseTime(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return results;
    }

    public Task<IReadOnlyList<SystemSampleRecord>> QuerySystemSamplesAsync(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 10000, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => (IReadOnlyList<SystemSampleRecord>)QuerySystemSamples(fromUtc, toUtc, limit), cancellationToken);
    }

    public Task<IReadOnlyList<MetadataEventRecord>> QueryMetadataEventsAsync(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 10000, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => (IReadOnlyList<MetadataEventRecord>)QueryMetadataEvents(fromUtc, toUtc, limit), cancellationToken);
    }

    public string ExportProcessCsv(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int? processId = null, int limit = 10000) =>
        HistoryCsvExporter.ToCsv(QueryProcessSamples(fromUtc, toUtc, processId, limit));

    public Task ExportProcessCsvAsync(string outputPath, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int? processId = null, int limit = 10000, CancellationToken cancellationToken = default) =>
        HistoryCsvExporter.ExportProcessSamplesAsync(this, outputPath, fromUtc, toUtc, processId, limit, cancellationToken);

    public Task ExportReportAsync(string outputPath, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default) =>
        HistoryReportExporter.ExportMarkdownAsync(this, outputPath, fromUtc, toUtc, cancellationToken);

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = Math.Max(1, _options.BusyTimeoutMilliseconds / 1000)
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = $"PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds}";
        busy.ExecuteNonQuery();
        return connection;
    }

    private static void AddParameters(SqliteCommand command, params string[] names)
    {
        foreach (var name in names)
            command.Parameters.Add(name, SqliteType.Text);
    }

    private static void Execute(SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static int ReadUserVersion(SqliteTransaction transaction)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static void DeleteOlderThan(SqliteTransaction transaction, string table, string column, DateTimeOffset cutoffUtc)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE {column} < $cutoff";
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static ProcessSampleRecord ReadProcessSample(SqliteDataReader reader) => new(
        ParseTime(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), (ProcessRole)reader.GetInt32(4), reader.GetInt32(5),
        reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13),
        reader.GetInt64(14), reader.GetInt64(15), reader.GetInt64(16), reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetString(18));

    private static SystemSampleRecord ReadSystemSample(SqliteDataReader reader) => new(
        ParseTime(reader.GetString(0)), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), Convert.ToUInt32(reader.GetValue(10), CultureInfo.InvariantCulture));

    private static string FormatTime(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HistoryStore));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SqliteConnection.ClearAllPools();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public static class HistoryCsvExporter
{
    public static string ToCsv(IEnumerable<ProcessSampleRecord> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var builder = new StringBuilder();
        builder.AppendLine("captured_at_utc,process_id,parent_process_id,image_name,role,thread_count,user_processor_time_100ns,kernel_processor_time_100ns,working_set_bytes,private_bytes,handle_count,read_operation_count,write_operation_count,other_operation_count,read_transfer_bytes,write_transfer_bytes,other_transfer_bytes");
        foreach (var sample in samples)
        {
            var fields = new object?[] { Format(sample.CapturedAtUtc), sample.ProcessId, sample.ParentProcessId, sample.ImageName, sample.Role, sample.ThreadCount, sample.UserProcessorTime100Ns, sample.KernelProcessorTime100Ns, sample.WorkingSetBytes, sample.PrivateBytes, sample.HandleCount, sample.ReadOperationCount, sample.WriteOperationCount, sample.OtherOperationCount, sample.ReadTransferBytes, sample.WriteTransferBytes, sample.OtherTransferBytes };
            builder.AppendLine(string.Join(',', fields.Select(value => Escape(value?.ToString()))));
        }
        return builder.ToString();
    }

    public static Task ExportProcessSamplesAsync(HistoryStore store, string outputPath, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int? processId = null, int limit = 10000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var csv = ToCsv(store.QueryProcessSamples(fromUtc, toUtc, processId, limit));
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Task.CompletedTask;
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',', StringComparison.Ordinal) || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

public static class HistoryReportExporter
{
    public static string ToMarkdown(IEnumerable<ProcessSampleRecord> processSamples, IEnumerable<SystemSampleRecord> systemSamples, IEnumerable<MetadataEventRecord>? metadataEvents = null)
    {
        var process = processSamples.ToArray();
        var system = systemSamples.ToArray();
        var events = (metadataEvents ?? Array.Empty<MetadataEventRecord>()).ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("# Codex process monitor report");
        builder.AppendLine();
        builder.AppendLine($"- Process samples: {process.Length}");
        builder.AppendLine($"- System samples: {system.Length}");
        builder.AppendLine($"- Metadata events: {events.Length}");
        builder.AppendLine();
        if (process.Length > 0)
        {
            builder.AppendLine("## Latest process samples");
            builder.AppendLine();
            builder.AppendLine("| Time (UTC) | PID | Image | Role | Working set | Handles |");
            builder.AppendLine("| --- | ---: | --- | --- | ---: | ---: |");
            foreach (var sample in process.OrderByDescending(item => item.CapturedAtUtc).Take(20))
                builder.AppendLine($"| {sample.CapturedAtUtc:O} | {sample.ProcessId} | {Escape(sample.ImageName)} | {sample.Role} | {sample.WorkingSetBytes:N0} | {sample.HandleCount:N0} |");
        }
        return builder.ToString();
    }

    public static Task ExportMarkdownAsync(HistoryStore store, string outputPath, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var markdown = ToMarkdown(store.QueryProcessSamples(fromUtc, toUtc), store.QuerySystemSamples(fromUtc, toUtc), store.QueryMetadataEvents(fromUtc, toUtc));
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Task.CompletedTask;
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
