namespace Codex.ProcessMonitor.Core;

/// <summary>Obtains cumulative process counters without exposing a platform-specific process type.</summary>
public interface IProcessSampler
{
    ValueTask<IReadOnlyList<ProcessSample>> SampleAsync(CancellationToken cancellationToken = default);
}

/// <summary>Lists installed integrations and their logical roles.</summary>
public interface IIntegrationCatalog
{
    ValueTask<IReadOnlyList<IntegrationDescriptor>> GetIntegrationsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Reads normalized log signals from one or more platform-specific sources.</summary>
public interface ILogSignalReader
{
    ValueTask<IReadOnlyList<LogSignal>> ReadAsync(
        DateTimeOffset sinceUtc,
        DateTimeOffset? untilUtc = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Stores and retrieves derived process snapshots for a bounded history view.</summary>
public interface IHistoryStore
{
    ValueTask AppendAsync(ProcessSnapshot snapshot, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ProcessSnapshot>> QueryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset? untilUtc = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Evaluates observations against immutable rules and emits state transitions.</summary>
public interface IAlertEngine
{
    IReadOnlyList<AlertEvent> Evaluate(
        DateTimeOffset timestampUtc,
        IEnumerable<AlertObservation> observations);

    void Reset();
}

/// <summary>Exports a report into a portable text representation.</summary>
public interface IDiagnosticsExporter
{
    string Export(DiagnosticReport report, DiagnosticsExportOptions? options = null);
}

public enum DiagnosticsExportFormat
{
    Csv = 0,
    Json,
}

public sealed record DiagnosticsExportOptions
{
    public DiagnosticsExportFormat Format { get; init; } = DiagnosticsExportFormat.Csv;
    public bool Redact { get; init; } = true;
    public ReportRedactionOptions? RedactionOptions { get; init; }
    public bool IncludeHeader { get; init; } = true;
}

/// <summary>Convenience adapters keep callers independent of the exact async method names.</summary>
public static class CoreInterfaceExtensions
{
    public static ValueTask<IReadOnlyList<IntegrationDescriptor>> GetAsync(
        this IIntegrationCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.GetIntegrationsAsync(cancellationToken);
    }

    public static async ValueTask<IReadOnlyList<ProcessSample>> Sample(
        this IProcessSampler sampler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        return await sampler.SampleAsync(cancellationToken).ConfigureAwait(false);
    }

    public static ValueTask<IReadOnlyList<LogSignal>> ReadSignalsAsync(
        this ILogSignalReader reader,
        DateTimeOffset sinceUtc,
        DateTimeOffset? untilUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.ReadAsync(sinceUtc, untilUtc, cancellationToken);
    }

    public static ValueTask SaveAsync(
        this IHistoryStore store,
        ProcessSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.AppendAsync(snapshot, cancellationToken);
    }

    public static ValueTask<IReadOnlyList<ProcessSnapshot>> GetAsync(
        this IHistoryStore store,
        DateTimeOffset fromUtc,
        DateTimeOffset? untilUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.QueryAsync(fromUtc, untilUtc, cancellationToken);
    }

    public static ValueTask<string> ExportAsync(
        this IDiagnosticsExporter exporter,
        DiagnosticReport report,
        DiagnosticsExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(exporter.Export(report, options));
    }
}
