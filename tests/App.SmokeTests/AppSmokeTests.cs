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
        Assert.All(snapshot.Processes, process => Assert.False(string.IsNullOrWhiteSpace(process.WindowAssociation)));
        Assert.All(snapshot.Windows ?? Array.Empty<WindowSample>(), window =>
            Assert.Contains(snapshot.Processes, process => process.ProcessId == window.OwnerProcessId));
        Assert.DoesNotContain(snapshot.Processes, process =>
            process.Name.Equals("services.exe", StringComparison.OrdinalIgnoreCase) ||
            process.Name.Equals("lsass.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProcessNodeKeepsExpansionStateAcrossStableRefreshIdentity()
    {
        var first = new ProcessNode(new ProcessSample(
            42,
            7,
            "ChatGPT.exe",
            1,
            1024,
            "运行中",
            InstanceKey: "42:638000000000000000"));
        first.IsExpanded = true;

        var refreshed = new ProcessNode(new ProcessSample(
            42,
            7,
            "ChatGPT.exe",
            2,
            2048,
            "运行中",
            InstanceKey: "42:638000000000000000"));
        refreshed.IsExpanded = first.IsExpanded;

        Assert.Equal(first.InstanceKey, refreshed.InstanceKey);
        Assert.True(refreshed.IsExpanded);
    }

    [Fact]
    public void ProcessNodeKeepsPrivacyPreservingWindowAssociation()
    {
        var node = new ProcessNode(new ProcessSample(
            42,
            7,
            "ChatGPT.exe",
            1,
            1024,
            "运行中",
            InstanceKey: "42:638000000000000000",
            WindowAssociation: "共享渲染器 · 窗口 1 前台（无法按对话确认）",
            WindowAssociationKind: "shared-renderer"));

        Assert.Contains("共享渲染器", node.WindowAssociation, StringComparison.Ordinal);
        Assert.Equal("shared-renderer", node.WindowAssociationKind);
    }
}
