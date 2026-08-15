using Codex.ProcessMonitor.App.Models;
using Codex.ProcessMonitor.App.Services;
using Xunit;

namespace Codex.ProcessMonitor.App.SmokeTests;

public sealed class AppSmokeTests
{
    [Fact]
    public void DiagnosticReportIsPortableAndDoesNotIncludeRawIdentityData()
    {
        var report = DiagnosticReportService.BuildReport(
            DateTimeOffset.UtcNow,
            12.3,
            45.6,
            2,
            new[]
            {
                new CapabilityItem(new CapabilitySample(
                    "Plugin",
                    "Fixture plugin",
                    "Fixture",
                    "已发现",
                    "1.0",
                    "plugin.json"))
            },
            Array.Empty<AlertItem>());

        Assert.Contains("只读观察模式", report, StringComparison.Ordinal);
        Assert.DoesNotContain("feedback_log_body", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command_line", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users\\", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeSourceReturnsOnlyTheEvidenceBackedTree()
    {
        var snapshot = new WindowsRuntimeMonitorSource().Capture(CancellationToken.None);

        Assert.Equal(snapshot.ProcessCount, snapshot.Processes.Count);
        Assert.All(snapshot.Processes, process => Assert.True(process.ProcessId > 0));
        Assert.DoesNotContain(snapshot.Processes, process =>
            process.Name.Equals("services.exe", StringComparison.OrdinalIgnoreCase) ||
            process.Name.Equals("lsass.exe", StringComparison.OrdinalIgnoreCase));
    }
}
