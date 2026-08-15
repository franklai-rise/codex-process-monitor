global using ProcessRole = Codex.ProcessMonitor.Core.ProcessRole;
using System.Collections.ObjectModel;

namespace Codex.ProcessMonitor.Infrastructure;

public sealed record ProcessInfo(
    int ProcessId,
    int ParentProcessId,
    string ImageName,
    string? ImagePath,
    ProcessRole Role,
    string? CommandLine,
    int ThreadCount,
    DateTimeOffset? StartTimeUtc,
    long UserProcessorTime100Ns,
    long KernelProcessorTime100Ns,
    long WorkingSetBytes,
    long PrivateBytes,
    long HandleCount,
    long ReadOperationCount,
    long WriteOperationCount,
    long OtherOperationCount,
    long ReadTransferBytes,
    long WriteTransferBytes,
    long OtherTransferBytes,
    bool MetricsAvailable = true,
    int? ErrorCode = null);

public sealed record ThreadInfo(
    int ThreadId,
    int OwnerProcessId,
    int BasePriority,
    int DeltaPriority,
    int State,
    int WaitReason);

public sealed record ProcessTreeSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ProcessInfo> Processes,
    IReadOnlyList<ThreadInfo> Threads)
{
    public static ProcessTreeSnapshot Empty(DateTimeOffset capturedAtUtc) =>
        new(capturedAtUtc, Array.Empty<ProcessInfo>(), Array.Empty<ThreadInfo>());

    public IReadOnlyDictionary<int, IReadOnlyList<ProcessInfo>> ChildrenByParent =>
        Processes
            .GroupBy(p => p.ParentProcessId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProcessInfo>)g.OrderBy(p => p.ProcessId).ToArray());
}

public sealed record SystemTimesSnapshot(
    DateTimeOffset CapturedAtUtc,
    long IdleTime100Ns,
    long KernelTime100Ns,
    long UserTime100Ns,
    bool Available = true);

public sealed record GlobalMemorySnapshot(
    DateTimeOffset CapturedAtUtc,
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long TotalPageFileBytes,
    long AvailablePageFileBytes,
    long TotalVirtualBytes,
    long AvailableVirtualBytes,
    uint MemoryLoad,
    bool Available = true);

public sealed record WindowsSystemSample(
    DateTimeOffset CapturedAtUtc,
    ProcessTreeSnapshot ProcessTree,
    SystemTimesSnapshot SystemTimes,
    GlobalMemorySnapshot GlobalMemory);

/// <summary>
/// A privacy-preserving view of a native top-level window.  The monitor keeps
/// only ownership and state information; it intentionally does not read a
/// window title, document title, accessibility tree, or conversation content.
/// </summary>
public sealed record DesktopWindowInfo(
    nint Handle,
    int ProcessId,
    int ThreadId,
    bool IsVisible,
    bool IsForeground,
    bool IsMinimized,
    int Width,
    int Height,
    string WindowClass);

/// <summary>Read-only top-level window sampler used to connect native windows
/// to PIDs already proven to be in the Codex process tree.</summary>
public interface IWindowsWindowSampler
{
    IReadOnlyList<DesktopWindowInfo> Sample(
        IReadOnlySet<int> processIds,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessSamplerOptions
{
    public bool IncludeCommandLines { get; init; } = true;
    public bool IncludeImagePaths { get; init; } = false;
    public bool IncludeThreads { get; init; } = true;
    public bool IncludeIoCounters { get; init; } = true;
    public bool IncludeHandleCounts { get; init; } = true;
    public bool IncludeMemoryCounters { get; init; } = true;
    public TimeSpan? CommandLineTimeout { get; init; }
    public int MaxProcesses { get; init; } = 4096;
}

public sealed record CommandLineQueryResult(
    int ProcessId,
    string? CommandLine,
    bool Succeeded,
    int? ErrorCode = null,
    string? Error = null);

public sealed record ProcessRoleContext(
    int ProcessId,
    int ParentProcessId,
    string ImageName,
    string? ImagePath,
    string? CommandLine);

public sealed record MetadataField(string Name, string? Value);

public sealed record LogsMetadataRow(
    string SourcePath,
    string TableName,
    long? RowId,
    DateTimeOffset? TimestampUtc,
    IReadOnlyDictionary<string, string?> Fields);

public sealed record LogsMetadataReadResult(
    IReadOnlyList<LogsMetadataRow> Rows,
    IReadOnlyDictionary<string, long> Checkpoints,
    IReadOnlyList<string> Warnings)
{
    public static LogsMetadataReadResult Empty =>
        new(Array.Empty<LogsMetadataRow>(), new ReadOnlyDictionary<string, long>(new Dictionary<string, long>()), Array.Empty<string>());
}

public sealed record PluginMetadata(
    string Path,
    string? Id,
    string? Name,
    string? Version,
    string? Description,
    IReadOnlyDictionary<string, string?> Values);

public sealed record ConfigMetadata(
    string Path,
    IReadOnlyDictionary<string, string?> Values);

public sealed record SkillMetadata(
    string Path,
    string? Name,
    string? Description,
    string? Version,
    IReadOnlyDictionary<string, string?> Values);

public sealed record DirectoryInventory(
    IReadOnlyList<PluginMetadata> Plugins,
    IReadOnlyList<ConfigMetadata> Configs,
    IReadOnlyList<SkillMetadata> Skills,
    IReadOnlyList<string> Warnings)
{
    public static DirectoryInventory Empty =>
        new(Array.Empty<PluginMetadata>(), Array.Empty<ConfigMetadata>(), Array.Empty<SkillMetadata>(), Array.Empty<string>());
}

public sealed record ProcessSampleRecord(
    DateTimeOffset CapturedAtUtc,
    int ProcessId,
    int ParentProcessId,
    string ImageName,
    ProcessRole Role,
    int ThreadCount,
    long UserProcessorTime100Ns,
    long KernelProcessorTime100Ns,
    long WorkingSetBytes,
    long PrivateBytes,
    long HandleCount,
    long ReadOperationCount,
    long WriteOperationCount,
    long OtherOperationCount,
    long ReadTransferBytes,
    long WriteTransferBytes,
    long OtherTransferBytes,
    string? CommandLine = null,
    string? ImagePath = null);

public sealed record SystemSampleRecord(
    DateTimeOffset CapturedAtUtc,
    long IdleTime100Ns,
    long KernelTime100Ns,
    long UserTime100Ns,
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long TotalPageFileBytes,
    long AvailablePageFileBytes,
    long TotalVirtualBytes,
    long AvailableVirtualBytes,
    uint MemoryLoad);

public sealed record MetadataEventRecord(
    DateTimeOffset CapturedAtUtc,
    string Source,
    string Kind,
    string? Key,
    string? Value,
    string? Origin = null);

public sealed record HistoryBatch(
    IReadOnlyCollection<ProcessSampleRecord> ProcessSamples,
    IReadOnlyCollection<SystemSampleRecord> SystemSamples,
    IReadOnlyCollection<MetadataEventRecord> MetadataEvents)
{
    public static HistoryBatch Empty => new(
        Array.Empty<ProcessSampleRecord>(),
        Array.Empty<SystemSampleRecord>(),
        Array.Empty<MetadataEventRecord>());
}

/// <summary>A five-minute roll-up owned by the monitor, never written to Codex databases.</summary>
public sealed record Summary5mRecord(
    DateTimeOffset BucketStartUtc,
    double CpuPercent,
    double MemoryPercent,
    long WorkingSetBytes,
    int ProcessCount);

public sealed record HistoryStoreOptions
{
    public bool CreateDirectory { get; init; } = true;
    public int BusyTimeoutMilliseconds { get; init; } = 5000;
    public TimeSpan ProcessRetention { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan SummaryRetention { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan MetadataRetention { get; init; } = TimeSpan.FromDays(30);
}
