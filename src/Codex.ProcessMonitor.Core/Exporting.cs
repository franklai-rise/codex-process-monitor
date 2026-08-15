using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codex.ProcessMonitor.Core;

/// <summary>RFC 4180-compatible escaping for one CSV field.</summary>
public static class CsvEscaping
{
    public static string Escape(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (!value.Contains(',', StringComparison.Ordinal) &&
            !value.Contains('"', StringComparison.Ordinal) &&
            !value.Contains('\r', StringComparison.Ordinal) &&
            !value.Contains('\n', StringComparison.Ordinal))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    public static string EscapeField(string? value) => Escape(value);
}

public static class Csv
{
    public static string Escape(string? value) => CsvEscaping.Escape(value);
}

public static class CsvWriter
{
    public static string WriteRow(IEnumerable<string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return string.Join(',', fields.Select(CsvEscaping.Escape));
    }

    public static string WriteRows(IEnumerable<IEnumerable<string?>> rows, bool trailingNewline = true)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = string.Join("\r\n", rows.Select(WriteRow));
        return trailingNewline && text.Length > 0 ? text + "\r\n" : text;
    }
}

public sealed record ReportRedactionOptions
{
    public bool RedactMachineName { get; init; } = true;
    public bool RedactUserName { get; init; } = true;
    public bool RedactPaths { get; init; } = true;
    public bool RedactCommandLines { get; init; } = true;
    public string Replacement { get; init; } = "[REDACTED]";
}

/// <summary>Copies a report while removing host-specific paths and account information.</summary>
public static class ReportRedactor
{
    private static readonly Regex WindowsPath = new(
        @"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\t,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnixPath = new(
        @"(?<![A-Za-z0-9])/(?:[^\r\n\t,; ]+/)+[^\r\n\t,; ]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DiagnosticReport Redact(
        DiagnosticReport report,
        ReportRedactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        options ??= new ReportRedactionOptions();
        var replacement = string.IsNullOrEmpty(options.Replacement) ? "[REDACTED]" : options.Replacement;

        var processes = report.Processes.Select(process =>
        {
            var identity = process.Identity with
            {
                ExecutablePath = options.RedactPaths
                    ? RedactValue(process.Identity.ExecutablePath, replacement, options.RedactPaths)
                    : process.Identity.ExecutablePath,
                CommandLine = options.RedactCommandLines
                    ? RedactValue(process.Identity.CommandLine, replacement, redact: true)
                    : process.Identity.CommandLine,
            };
            return process with { Identity = identity };
        });

        var integrations = report.Integrations.Select(integration => integration with
        {
            Path = options.RedactPaths
                ? RedactValue(integration.Path, replacement, redact: true)
                : integration.Path,
            Metadata = RedactMetadata(integration.Metadata, options, replacement),
        });

        var signals = report.LogSignals.Select(signal => signal with
        {
            Message = RedactText(signal.Message, options, replacement),
            Metadata = RedactMetadata(signal.Metadata, options, replacement),
        });

        return report with
        {
            MachineName = options.RedactMachineName ? replacement : report.MachineName,
            UserName = options.RedactUserName ? replacement : report.UserName,
            Processes = processes.ToArray(),
            Integrations = integrations.ToArray(),
            LogSignals = signals.ToArray(),
            Metadata = RedactMetadata(report.Metadata, options, replacement),
        };
    }

    public static DiagnosticReport Sanitize(
        DiagnosticReport report,
        ReportRedactionOptions? options = null) => Redact(report, options);

    private static IReadOnlyDictionary<string, string> RedactMetadata(
        IReadOnlyDictionary<string, string> metadata,
        ReportRedactionOptions options,
        string replacement)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in metadata)
        {
            var key = pair.Key;
            var keySuggestsPath = key.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                                  key.Contains("file", StringComparison.OrdinalIgnoreCase) ||
                                  key.Contains("directory", StringComparison.OrdinalIgnoreCase);
            var keySuggestsUser = key.Contains("user", StringComparison.OrdinalIgnoreCase) ||
                                  key.Contains("account", StringComparison.OrdinalIgnoreCase) ||
                                  key.Contains("machine", StringComparison.OrdinalIgnoreCase);
            var redact = (options.RedactPaths && keySuggestsPath) ||
                         (options.RedactUserName && keySuggestsUser);
            result[key] = redact ? replacement : RedactText(pair.Value, options, replacement);
        }

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(result);
    }

    private static string RedactText(string value, ReportRedactionOptions options, string replacement)
    {
        var redacted = value;
        if (options.RedactPaths)
        {
            redacted = WindowsPath.Replace(redacted, replacement);
            redacted = UnixPath.Replace(redacted, replacement);
        }

        return redacted;
    }

    private static string? RedactValue(string? value, string replacement, bool redact)
    {
        if (!redact || value is null)
        {
            return value;
        }

        return replacement;
    }
}

public sealed class CsvDiagnosticsExporter : IDiagnosticsExporter
{
    public string Export(DiagnosticReport report, DiagnosticsExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        options ??= new DiagnosticsExportOptions();
        var source = options.Redact
            ? ReportRedactor.Redact(report, options.RedactionOptions)
            : report;

        var rows = new List<IEnumerable<string?>>();
        if (options.IncludeHeader)
        {
            rows.Add(new[]
            {
                "record_type", "timestamp_utc", "process_id", "process_name", "role", "category",
                "cpu_percent", "working_set_bytes", "private_bytes", "read_bytes_per_second",
                "write_bytes_per_second", "integration_id", "alert_kind", "rule_id", "severity", "message",
            });
        }

        foreach (var process in source.Processes)
        {
            rows.Add(new[]
            {
                "process",
                process.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                process.Identity.ProcessId.ToString(CultureInfo.InvariantCulture),
                process.Identity.Name,
                process.Role.ToString(),
                process.Category.ToString(),
                process.CpuPercent.ToString("G17", CultureInfo.InvariantCulture),
                process.WorkingSetBytes?.ToString(CultureInfo.InvariantCulture),
                process.PrivateBytes?.ToString(CultureInfo.InvariantCulture),
                process.ReadBytesPerSecond.ToString("G17", CultureInfo.InvariantCulture),
                process.WriteBytesPerSecond.ToString("G17", CultureInfo.InvariantCulture),
                process.IntegrationId,
                null, null, null, null,
            });
        }

        foreach (var alert in source.Alerts)
        {
            rows.Add(new[]
            {
                "alert",
                alert.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                null, null, null, null, null, null, null, null, null, null,
                alert.Kind.ToString(),
                alert.RuleId,
                alert.Severity.ToString(),
                alert.Message,
            });
        }

        return CsvWriter.WriteRows(rows);
    }
}

public sealed class JsonDiagnosticsExporter : IDiagnosticsExporter
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        WriteIndented = true,
    };

    public string Export(DiagnosticReport report, DiagnosticsExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        options ??= new DiagnosticsExportOptions { Format = DiagnosticsExportFormat.Json };
        var source = options.Redact
            ? ReportRedactor.Redact(report, options.RedactionOptions)
            : report;
        return JsonSerializer.Serialize(source, DefaultOptions);
    }
}

/// <summary>Format-selecting exporter suitable as the default composition-root dependency.</summary>
public sealed class DiagnosticsExporter : IDiagnosticsExporter
{
    private readonly CsvDiagnosticsExporter _csv = new();
    private readonly JsonDiagnosticsExporter _json = new();

    public string Export(DiagnosticReport report, DiagnosticsExportOptions? options = null)
    {
        options ??= new DiagnosticsExportOptions();
        return options.Format == DiagnosticsExportFormat.Json
            ? _json.Export(report, options)
            : _csv.Export(report, options);
    }
}
