using System.Collections.ObjectModel;

namespace Codex.ProcessMonitor.App.Models;

/// <summary>One immutable sample produced by the background monitor.</summary>
public sealed record MonitorSnapshot(
    DateTimeOffset Timestamp,
    double CpuPercent,
    double MemoryPercent,
    long WorkingSetBytes,
    int ProcessCount,
    IReadOnlyList<ProcessSample> Processes,
    IReadOnlyList<AlertSample> Alerts);

public sealed record ProcessSample(
    int ProcessId,
    int ParentProcessId,
    string Name,
    double CpuPercent,
    long MemoryBytes,
    string Status,
    bool IsElevated = false);

public sealed record AlertSample(
    DateTimeOffset Timestamp,
    string Severity,
    string Title,
    string Detail);

public sealed record MetricSample(DateTimeOffset Timestamp, double CpuPercent, double MemoryPercent);

public sealed record CapabilitySample(
    string Category,
    string Name,
    string Description,
    string Status,
    string Version,
    string Source);

/// <summary>Observable item used by the process tree.</summary>
public sealed class ProcessNode
{
    public ProcessNode(ProcessSample sample)
    {
        ProcessId = sample.ProcessId;
        ParentProcessId = sample.ParentProcessId;
        Name = sample.Name;
        CpuPercent = sample.CpuPercent;
        MemoryBytes = sample.MemoryBytes;
        Status = sample.Status;
        Children = new ObservableCollection<ProcessNode>();
    }

    public int ProcessId { get; }
    public int ParentProcessId { get; }
    public string Name { get; }
    public double CpuPercent { get; }
    public long MemoryBytes { get; }
    public string Status { get; }
    public ObservableCollection<ProcessNode> Children { get; }

    public string CpuText => $"{CpuPercent:0.0}%";
    public string MemoryText => FormatBytes(MemoryBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
    }
}

public sealed class CapabilityItem
{
    public CapabilityItem(CapabilitySample sample)
    {
        Category = sample.Category;
        Name = sample.Name;
        Description = sample.Description;
        Status = sample.Status;
        Version = sample.Version;
        Source = sample.Source;
    }

    public string Category { get; }
    public string Name { get; }
    public string Description { get; }
    public string Status { get; }
    public string Version { get; }
    public string Source { get; }
}

public sealed class AlertItem
{
    public AlertItem(AlertSample sample)
    {
        Timestamp = sample.Timestamp;
        Severity = sample.Severity;
        Title = sample.Title;
        Detail = sample.Detail;
    }

    public DateTimeOffset Timestamp { get; }
    public string Severity { get; }
    public string Title { get; }
    public string Detail { get; }
    public string TimeText => Timestamp.LocalDateTime.ToString("HH:mm:ss");
}

public sealed class HistoryItem
{
    public HistoryItem(MetricSample sample)
    {
        Timestamp = sample.Timestamp;
        CpuPercent = sample.CpuPercent;
        MemoryPercent = sample.MemoryPercent;
    }

    public DateTimeOffset Timestamp { get; }
    public double CpuPercent { get; }
    public double MemoryPercent { get; }
    public string TimeText => Timestamp.LocalDateTime.ToString("MM-dd HH:mm:ss");
    public string CpuText => $"{CpuPercent:0.0}%";
    public string MemoryText => $"{MemoryPercent:0.0}%";
}
