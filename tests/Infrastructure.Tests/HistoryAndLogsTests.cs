using Codex.ProcessMonitor.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Tests;

public sealed class HistoryAndLogsTests
{
    [Fact]
    public async Task HistoryStoreMigratesWritesQueriesAndExports()
    {
        using var fixture = new TemporaryDirectory();
        var dbPath = System.IO.Path.Combine(fixture.Path, "history.sqlite");
        using var store = new HistoryStore(dbPath);
        var time = DateTimeOffset.UtcNow;
        store.AppendBatch(new HistoryBatch(
            new[] { new ProcessSampleRecord(time, 7, 1, "fixture.exe", Codex.ProcessMonitor.Core.ProcessRole.MainApplication, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13) },
            new[] { new SystemSampleRecord(time, 1, 2, 3, 100, 50, 200, 100, 300, 150, 42) },
            new[] { new MetadataEventRecord(time, "fixture", "loaded", "id", "fixture") }));

        Assert.Single(store.QueryProcessSamples(processId: 7));
        Assert.Single(store.QuerySystemSamples());
        Assert.Single(store.QueryMetadataEvents());
        var csv = store.ExportProcessCsv();
        Assert.Contains("fixture.exe", csv, StringComparison.Ordinal);
        var reportPath = System.IO.Path.Combine(fixture.Path, "report.md");
        await store.ExportReportAsync(reportPath);
        Assert.True(File.Exists(reportPath));
    }

    [Fact]
    public void HistoryStoreUsesVersionedSummaryAndRetentionTables()
    {
        using var fixture = new TemporaryDirectory();
        var dbPath = System.IO.Path.Combine(fixture.Path, "history.sqlite");
        using var store = new HistoryStore(dbPath);
        var now = DateTimeOffset.UtcNow;
        store.AppendSummary5m(new Summary5mRecord(now, 12, 34, 56, 2));
        store.Prune(now);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ConnectionString);
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, Convert.ToInt64(version.ExecuteScalar()));
        using var summary = connection.CreateCommand();
        summary.CommandText = "SELECT COUNT(*) FROM summary_5m";
        Assert.Equal(1L, Convert.ToInt64(summary.ExecuteScalar()));
    }

    [Fact]
    public void LogsReaderSkipsBodyTableAndReadsIncrementally()
    {
        using var fixture = new TemporaryDirectory();
        var dbPath = System.IO.Path.Combine(fixture.Path, "logs_2.sqlite");
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate };
        using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE feedback_log(id INTEGER PRIMARY KEY, event_type TEXT, created_at TEXT); CREATE TABLE feedback_log_body(id INTEGER PRIMARY KEY, body TEXT); INSERT INTO feedback_log VALUES(1, 'opened', '2026-01-01T00:00:00Z'); INSERT INTO feedback_log_body VALUES(1, 'secret');";
            command.ExecuteNonQuery();
        }

        var reader = new Logs2MetadataReader();
        var first = reader.ReadIncremental(new[] { dbPath });
        Assert.Single(first.Rows);
        Assert.Equal("feedback_log", first.Rows[0].TableName);
        Assert.Contains("id", first.Rows[0].Fields.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("created_at", first.Rows[0].Fields.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("event_type", first.Rows[0].Fields.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(first.Rows[0].Fields.Keys, key => key.Contains("body", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(reader.ReadIncremental(new[] { dbPath }).Rows);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-infra-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
