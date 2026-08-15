using System.Globalization;
using System.Text;
using System.Text.Json;
using Codex.ProcessMonitor.App.Models;

namespace Codex.ProcessMonitor.App.Services;

public static class DiagnosticReportService
{
    public static string BuildReport(
        DateTimeOffset generatedAt,
        double cpuPercent,
        double memoryPercent,
        int processCount,
        IEnumerable<CapabilityItem> capabilities,
        IEnumerable<AlertItem> alerts)
    {
        var report = new
        {
            generatedAt,
            application = "Codex 进程监视器",
            mode = "只读观察模式",
            system = new
            {
                cpuPercent,
                memoryPercent,
                processCount
            },
            capabilities = capabilities.Select(static item => new
            {
                item.Category,
                item.Name,
                item.Status,
                item.Version,
                item.Source
            }),
            alerts = alerts.Select(static item => new
            {
                item.Timestamp,
                item.Severity,
                item.Title,
                item.Detail
            })
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public static string BuildCsv(IEnumerable<HistoryItem> history)
    {
        var builder = new StringBuilder();
        builder.AppendLine("时间,CPU 使用率 (%),内存使用率 (%)");
        foreach (var item in history)
        {
            builder.Append(item.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(item.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.AppendLine(item.MemoryPercent.ToString("0.0", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
