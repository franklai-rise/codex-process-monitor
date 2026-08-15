using Codex.ProcessMonitor.Core;
using Xunit;

namespace Codex.ProcessMonitor.Core.Tests;

public sealed class MeasurementTests
{
    [Fact]
    public void CounterDiff_returns_delta_and_marks_counter_reset()
    {
        Assert.Equal(new CounterDelta(40), CounterDiff.Difference(100, 140));

        var reset = CounterDiff.Difference(140, 12);
        Assert.Equal(0, reset.Value);
        Assert.True(reset.WasReset);
    }

    [Fact]
    public void Cpu_and_io_rates_are_derived_from_elapsed_interval()
    {
        var cpu = ProcessMetricsCalculator.CalculateCpuPercent(
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            logicalProcessorCount: 2);
        var io = ProcessMetricsCalculator.CalculateIoRates(100, 500, 20, 120, TimeSpan.FromSeconds(2));

        Assert.Equal(25, cpu, precision: 10);
        Assert.Equal(200, io.ReadBytesPerSecond, precision: 10);
        Assert.Equal(50, io.WriteBytesPerSecond, precision: 10);
    }

    [Fact]
    public void Snapshot_rejects_pid_reuse_and_uses_current_counters()
    {
        var first = new ProcessIdentity(7, "worker", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var reused = new ProcessIdentity(7, "worker", DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
        var previous = new ProcessSample(first, DateTimeOffset.UnixEpoch, TimeSpan.Zero, 100, 200);
        var current = new ProcessSample(reused, DateTimeOffset.UnixEpoch.AddSeconds(1), TimeSpan.FromSeconds(1), 300, 500);

        Assert.Null(ProcessMetricsCalculator.CreateSnapshot(previous, current));

        current = new ProcessSample(first, current.TimestampUtc, current.TotalProcessorTime, 300, 500, 1234);
        var snapshot = ProcessMetricsCalculator.CreateSnapshot(previous, current, logicalProcessorCount: 1);
        Assert.NotNull(snapshot);
        Assert.Equal(200, snapshot!.ReadBytesPerSecond, precision: 10);
        Assert.Equal(300, snapshot.WriteBytesPerSecond, precision: 10);
        Assert.Equal(1234, snapshot.WorkingSetBytes);
    }
}

public sealed class ClassificationTests
{
    [Fact]
    public void Integration_descriptor_wins_over_builtin_name_matching()
    {
        var identity = new ProcessIdentity(10, "codex-host", executablePath: @"C:\Tools\codex-host.exe");
        var integrations = new[]
        {
            new IntegrationDescriptor("codex", "Codex", IntegrationRole.HostApplication,
                executableName: "codex-host.exe"),
        };

        var result = ProcessClassifier.Classify(identity, integrations);

        Assert.Equal(ProcessRole.MainApplication, result.Role);
        Assert.Equal(ProcessCategory.Application, result.Category);
        Assert.Equal("codex", result.IntegrationId);
        Assert.True(result.IsRelevant);
    }

    [Fact]
    public void Unknown_process_is_not_relevant()
    {
        var result = ProcessClassifier.Classify(new ProcessIdentity(11, "unrelated-daemon"));

        Assert.Equal(ProcessRole.Unknown, result.Role);
        Assert.False(result.IsRelevant);
    }
}

public sealed class AlertEngineTests
{
    [Fact]
    public void Raise_requires_continuous_duration_and_clear_uses_hysteresis()
    {
        var rule = new AlertRule(
            "cpu-high",
            AlertMetric.CpuPercent,
            AlertComparison.GreaterThanOrEqual,
            threshold: 80,
            duration: TimeSpan.FromSeconds(2),
            clearThreshold: 60,
            clearDuration: TimeSpan.FromSeconds(1));
        var engine = new AlertEngine(new[] { rule });
        var t0 = DateTimeOffset.UnixEpoch;

        Assert.Empty(engine.Evaluate(t0, Observation(81)));
        Assert.Empty(engine.Evaluate(t0.AddSeconds(1), Observation(90)));
        var raised = engine.Evaluate(t0.AddSeconds(2), Observation(85));
        Assert.Single(raised);
        Assert.Equal(AlertEventKind.Raised, raised[0].Kind);

        Assert.Empty(engine.Evaluate(t0.AddSeconds(2.5), Observation(59)));
        var cleared = engine.Evaluate(t0.AddSeconds(3.5), Observation(59));
        Assert.Single(cleared);
        Assert.Equal(AlertEventKind.Cleared, cleared[0].Kind);
    }

    [Fact]
    public void Cooldown_and_deduplication_prevent_duplicate_raise_events()
    {
        var rule = new AlertRule(
            "io-high",
            AlertMetric.TotalIoBytesPerSecond,
            AlertComparison.GreaterThan,
            100,
            cooldown: TimeSpan.FromSeconds(5));
        var engine = new AlertEngine(new[] { rule });
        var t0 = DateTimeOffset.UnixEpoch;

        var raised = engine.Evaluate(t0, new[]
        {
            Observation(101, "process-a", AlertMetric.TotalIoBytesPerSecond),
            Observation(102, "process-a", AlertMetric.TotalIoBytesPerSecond),
        });
        Assert.Single(raised);
        Assert.Empty(engine.Evaluate(t0.AddSeconds(1), Observation(110, "process-a", AlertMetric.TotalIoBytesPerSecond)));

        Assert.Single(engine.Evaluate(t0.AddSeconds(2), Observation(0, "process-a", AlertMetric.TotalIoBytesPerSecond)));
        Assert.Empty(engine.Evaluate(t0.AddSeconds(3), Observation(110, "process-a", AlertMetric.TotalIoBytesPerSecond)));
        Assert.Empty(engine.Evaluate(t0.AddSeconds(6), Observation(110, "process-a", AlertMetric.TotalIoBytesPerSecond)));
        Assert.Single(engine.Evaluate(t0.AddSeconds(7), Observation(110, "process-a", AlertMetric.TotalIoBytesPerSecond)));
    }

    private static AlertObservation Observation(
        double value,
        string target = "process-a",
        AlertMetric metric = AlertMetric.CpuPercent) =>
        new(target, metric, value, processName: target);
}

public sealed class ExportTests
{
    [Fact]
    public void Csv_escapes_quotes_commas_and_newlines()
    {
        Assert.Equal("simple", CsvEscaping.Escape("simple"));
        Assert.Equal("\"a,b\"", Csv.Escape("a,b"));
        Assert.Equal("\"a\"\"b\"", CsvEscaping.Escape("a\"b"));
        Assert.Equal("\"a\r\nb\"", CsvEscaping.Escape("a\r\nb"));
    }

    [Fact]
    public void Report_redaction_removes_host_paths_and_export_stays_csv_safe()
    {
        var identity = new ProcessIdentity(
            42,
            "demo",
            executablePath: @"C:\Users\Alice\demo.exe",
            commandLine: @"C:\Users\Alice\demo.exe --token=secret");
        var report = new DiagnosticReport(
            DateTimeOffset.UnixEpoch,
            processes: new[] { new ProcessSnapshot(identity, DateTimeOffset.UnixEpoch, 1) },
            integrations: new[]
            {
                new IntegrationDescriptor("demo", "Demo", path: @"C:\Users\Alice\plugin"),
            },
            logSignals: new[]
            {
                new LogSignal(DateTimeOffset.UnixEpoch, LogLevel.Error, "demo", @"failed at C:\Users\Alice\demo.log"),
            },
            machineName: "ALICE-PC",
            userName: "Alice");

        var redacted = ReportRedactor.Redact(report);
        var text = new CsvDiagnosticsExporter().Export(report);

        Assert.Equal("[REDACTED]", redacted.MachineName);
        Assert.Equal("[REDACTED]", redacted.UserName);
        Assert.Equal("[REDACTED]", redacted.Processes[0].Identity.ExecutablePath);
        Assert.Equal("[REDACTED]", redacted.Integrations[0].Path);
        Assert.DoesNotContain("Alice", text, StringComparison.Ordinal);
        Assert.Contains("record_type", text, StringComparison.Ordinal);
    }
}
