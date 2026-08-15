namespace Codex.ProcessMonitor.Core;

/// <summary>Logical roles that a process or integration can play in a monitored installation.</summary>
public enum ProcessRole
{
    Unknown = 0,
    MainApplication,
    Renderer,
    Browser,
    Editor,
    Terminal,
    SourceControl,
    ExtensionHost,
    Integration,
    Worker,
    Service,
}

/// <summary>Coarse process categories useful for grouping results in a report.</summary>
public enum ProcessCategory
{
    Unknown = 0,
    Application,
    Browser,
    DevelopmentTool,
    Shell,
    Integration,
    BackgroundService,
}

/// <summary>Roles advertised by an integration descriptor.</summary>
public enum IntegrationRole
{
    Unknown = 0,
    HostApplication,
    Browser,
    Editor,
    Terminal,
    SourceControl,
    Tooling,
    Extension,
    Plugin,
    Runtime,
    Service,
}

public enum AlertMetric
{
    CpuPercent = 0,
    WorkingSetBytes,
    PrivateBytes,
    ReadBytesPerSecond,
    WriteBytesPerSecond,
    TotalIoBytesPerSecond,
}

public enum AlertComparison
{
    GreaterThan = 0,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Equal,
    NotEqual,
}

public enum AlertSeverity
{
    Information = 0,
    Warning,
    Critical,
}

public enum AlertEventKind
{
    Raised = 0,
    Cleared,
}

public enum AlertLifecycleState
{
    Inactive = 0,
    Pending,
    Active,
    CoolingDown,
}

public enum LogLevel
{
    Trace = 0,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>Stable identity for a process. StartTimeUtc prevents PID reuse from joining two processes.</summary>
public sealed record ProcessIdentity
{
    public int ProcessId { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset? StartTimeUtc { get; init; }
    public string? ExecutablePath { get; init; }
    public string? CommandLine { get; init; }
    public int? ParentProcessId { get; init; }
    public int? SessionId { get; init; }

    public ProcessIdentity()
    {
    }

    public ProcessIdentity(
        int processId,
        string name,
        DateTimeOffset? startTimeUtc = null,
        string? executablePath = null,
        string? commandLine = null,
        int? parentProcessId = null,
        int? sessionId = null)
    {
        ProcessId = processId;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        StartTimeUtc = startTimeUtc;
        ExecutablePath = executablePath;
        CommandLine = commandLine;
        ParentProcessId = parentProcessId;
        SessionId = sessionId;
    }

    /// <summary>A PID plus its start time, or a conservative PID/name fallback when start time is unavailable.</summary>
    public string StableKey => StartTimeUtc is { } start
        ? $"{ProcessId}:{start.UtcDateTime.Ticks}"
        : $"{ProcessId}:{Name}";
}

/// <summary>A point-in-time process sample containing cumulative counters.</summary>
public sealed record ProcessSample
{
    public ProcessIdentity Identity { get; init; } = new();
    public DateTimeOffset TimestampUtc { get; init; }
    public TimeSpan TotalProcessorTime { get; init; }
    public long? WorkingSetBytes { get; init; }
    public long? PrivateBytes { get; init; }
    public long? ReadBytesTotal { get; init; }
    public long? WriteBytesTotal { get; init; }
    public int? ThreadCount { get; init; }
    public double? CpuPercent { get; init; }

    public ProcessSample()
    {
    }

    public ProcessSample(
        ProcessIdentity identity,
        DateTimeOffset timestampUtc,
        TimeSpan totalProcessorTime,
        long? readBytesTotal = null,
        long? writeBytesTotal = null,
        long? workingSetBytes = null,
        long? privateBytes = null,
        int? threadCount = null,
        double? cpuPercent = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        TimestampUtc = timestampUtc;
        TotalProcessorTime = totalProcessorTime;
        ReadBytesTotal = readBytesTotal;
        WriteBytesTotal = writeBytesTotal;
        WorkingSetBytes = workingSetBytes;
        PrivateBytes = privateBytes;
        ThreadCount = threadCount;
        CpuPercent = cpuPercent;
    }

    public DateTimeOffset SampledAtUtc => TimestampUtc;
    public long? ReadBytes => ReadBytesTotal;
    public long? WriteBytes => WriteBytesTotal;
}

/// <summary>Machine-level metrics observed alongside process samples.</summary>
public sealed record SystemMetrics
{
    public DateTimeOffset TimestampUtc { get; init; }
    public double CpuPercent { get; init; }
    public long? TotalPhysicalMemoryBytes { get; init; }
    public long? AvailablePhysicalMemoryBytes { get; init; }
    public double? ReadBytesPerSecond { get; init; }
    public double? WriteBytesPerSecond { get; init; }

    public SystemMetrics()
    {
    }

    public SystemMetrics(
        DateTimeOffset timestampUtc,
        double cpuPercent,
        long? totalPhysicalMemoryBytes = null,
        long? availablePhysicalMemoryBytes = null,
        double? readBytesPerSecond = null,
        double? writeBytesPerSecond = null)
    {
        TimestampUtc = timestampUtc;
        CpuPercent = cpuPercent;
        TotalPhysicalMemoryBytes = totalPhysicalMemoryBytes;
        AvailablePhysicalMemoryBytes = availablePhysicalMemoryBytes;
        ReadBytesPerSecond = readBytesPerSecond;
        WriteBytesPerSecond = writeBytesPerSecond;
    }

    public DateTimeOffset SampledAtUtc => TimestampUtc;
    public long? FreePhysicalMemoryBytes => AvailablePhysicalMemoryBytes;
}

/// <summary>Derived process metrics for one observation interval.</summary>
public sealed record ProcessSnapshot
{
    public ProcessIdentity Identity { get; init; } = new();
    public DateTimeOffset TimestampUtc { get; init; }
    public double CpuPercent { get; init; }
    public double ReadBytesPerSecond { get; init; }
    public double WriteBytesPerSecond { get; init; }
    public long? WorkingSetBytes { get; init; }
    public long? PrivateBytes { get; init; }
    public int? ThreadCount { get; init; }
    public ProcessRole Role { get; init; }
    public ProcessCategory Category { get; init; }
    public string? IntegrationId { get; init; }

    public ProcessSnapshot()
    {
    }

    public ProcessSnapshot(
        ProcessIdentity identity,
        DateTimeOffset timestampUtc,
        double cpuPercent = 0,
        double readBytesPerSecond = 0,
        double writeBytesPerSecond = 0,
        long? workingSetBytes = null,
        long? privateBytes = null,
        int? threadCount = null,
        ProcessRole role = ProcessRole.Unknown,
        ProcessCategory category = ProcessCategory.Unknown,
        string? integrationId = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        TimestampUtc = timestampUtc;
        CpuPercent = cpuPercent;
        ReadBytesPerSecond = readBytesPerSecond;
        WriteBytesPerSecond = writeBytesPerSecond;
        WorkingSetBytes = workingSetBytes;
        PrivateBytes = privateBytes;
        ThreadCount = threadCount;
        Role = role;
        Category = category;
        IntegrationId = integrationId;
    }

    public int ProcessId => Identity.ProcessId;
    public string ProcessName => Identity.Name;
    public double TotalIoBytesPerSecond => ReadBytesPerSecond + WriteBytesPerSecond;
    public DateTimeOffset SampledAtUtc => TimestampUtc;
}

/// <summary>Metadata describing an optional host, plugin, or external integration.</summary>
public sealed record IntegrationDescriptor
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public IntegrationRole Role { get; init; }
    public bool IsInstalled { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? Version { get; init; }
    public string? Path { get; init; }
    public string? ExecutableName { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IntegrationDescriptor()
    {
    }

    public IntegrationDescriptor(
        string id,
        string displayName,
        IntegrationRole role = IntegrationRole.Unknown,
        bool isInstalled = true,
        string? version = null,
        string? path = null,
        string? executableName = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        bool isEnabled = true)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        Role = role;
        IsInstalled = isInstalled;
        IsEnabled = isEnabled;
        Version = version;
        Path = path;
        ExecutableName = executableName;
        Metadata = CollectionCopy.ReadOnlyDictionary(metadata);
    }
}

/// <summary>A signal read from an application or system log.</summary>
public sealed record LogSignal
{
    public DateTimeOffset TimestampUtc { get; init; }
    public LogLevel Level { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public LogSignal()
    {
    }

    public LogSignal(
        DateTimeOffset timestampUtc,
        LogLevel level,
        string source,
        string message,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        TimestampUtc = timestampUtc;
        Level = level;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Metadata = CollectionCopy.ReadOnlyDictionary(metadata);
    }

    public LogSignal(string source, LogLevel level, string message, DateTimeOffset timestampUtc)
        : this(timestampUtc, level, source, message)
    {
    }
}

/// <summary>An immutable rule evaluated by <see cref="AlertEngine"/>.</summary>
public sealed record AlertRule
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public AlertMetric Metric { get; init; }
    public AlertComparison Comparison { get; init; } = AlertComparison.GreaterThanOrEqual;
    public double Threshold { get; init; }
    public TimeSpan Duration { get; init; }
    public double? ClearThreshold { get; init; }
    public TimeSpan ClearDuration { get; init; }
    public TimeSpan Cooldown { get; init; }
    public AlertSeverity Severity { get; init; } = AlertSeverity.Warning;
    public bool Enabled { get; init; } = true;
    public string? TargetKey { get; init; }
    public int? TargetProcessId { get; init; }
    public string? TargetProcessName { get; init; }
    public ProcessRole? TargetRole { get; init; }
    public string? DeduplicationKey { get; init; }
    public string? Message { get; init; }

    public AlertRule()
    {
    }

    public AlertRule(
        string id,
        AlertMetric metric,
        AlertComparison comparison,
        double threshold,
        TimeSpan? duration = null,
        double? clearThreshold = null,
        TimeSpan? clearDuration = null,
        TimeSpan? cooldown = null,
        AlertSeverity severity = AlertSeverity.Warning,
        string? targetKey = null,
        int? targetProcessId = null,
        string? targetProcessName = null,
        ProcessRole? targetRole = null,
        string? deduplicationKey = null,
        string? name = null,
        string? message = null,
        bool enabled = true)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        Metric = metric;
        Comparison = comparison;
        Threshold = threshold;
        Duration = NonNegative(duration ?? TimeSpan.Zero, nameof(duration));
        ClearThreshold = clearThreshold;
        ClearDuration = NonNegative(clearDuration ?? TimeSpan.Zero, nameof(clearDuration));
        Cooldown = NonNegative(cooldown ?? TimeSpan.Zero, nameof(cooldown));
        Severity = severity;
        TargetKey = targetKey;
        TargetProcessId = targetProcessId;
        TargetProcessName = targetProcessName;
        TargetRole = targetRole;
        DeduplicationKey = deduplicationKey;
        Message = message;
        Enabled = enabled;
    }

    public AlertRule(
        string id,
        AlertMetric metric,
        double threshold,
        TimeSpan? duration = null,
        TimeSpan? cooldown = null,
        AlertSeverity severity = AlertSeverity.Warning)
        : this(id, metric, AlertComparison.GreaterThanOrEqual, threshold, duration, null, null, cooldown, severity)
    {
    }

    public TimeSpan TriggerDuration => Duration;
    public TimeSpan CooldownDuration => Cooldown;
    public TimeSpan RecoveryDuration => ClearDuration;
    public AlertComparison Operator => Comparison;

    internal double EffectiveClearThreshold => ClearThreshold ?? Threshold;

    private static TimeSpan NonNegative(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A duration cannot be negative.");
        }

        return value;
    }
}

/// <summary>One metric observation fed into the alert state machine.</summary>
public sealed record AlertObservation
{
    public string TargetKey { get; init; } = string.Empty;
    public AlertMetric Metric { get; init; }
    public double Value { get; init; }
    public string? DisplayName { get; init; }
    public int? ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public ProcessRole? Role { get; init; }
    public string? DeduplicationKey { get; init; }

    public AlertObservation()
    {
    }

    public AlertObservation(
        string targetKey,
        AlertMetric metric,
        double value,
        string? displayName = null,
        int? processId = null,
        string? processName = null,
        ProcessRole? role = null,
        string? deduplicationKey = null)
    {
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        Metric = metric;
        Value = value;
        DisplayName = displayName;
        ProcessId = processId;
        ProcessName = processName;
        Role = role;
        DeduplicationKey = deduplicationKey;
    }

    public static AlertObservation FromSnapshot(ProcessSnapshot snapshot, AlertMetric metric)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var value = metric switch
        {
            AlertMetric.CpuPercent => snapshot.CpuPercent,
            AlertMetric.WorkingSetBytes => snapshot.WorkingSetBytes ?? 0,
            AlertMetric.PrivateBytes => snapshot.PrivateBytes ?? 0,
            AlertMetric.ReadBytesPerSecond => snapshot.ReadBytesPerSecond,
            AlertMetric.WriteBytesPerSecond => snapshot.WriteBytesPerSecond,
            AlertMetric.TotalIoBytesPerSecond => snapshot.TotalIoBytesPerSecond,
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown alert metric.")
        };

        return new AlertObservation(
            snapshot.Identity.StableKey,
            metric,
            value,
            snapshot.ProcessName,
            snapshot.ProcessId,
            snapshot.ProcessName,
            snapshot.Role,
            snapshot.IntegrationId);
    }
}

/// <summary>An immutable alert transition (raised or cleared).</summary>
public sealed record AlertEvent
{
    public string RuleId { get; init; } = string.Empty;
    public string TargetKey { get; init; } = string.Empty;
    public AlertEventKind Kind { get; init; }
    public AlertSeverity Severity { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }
    public double Value { get; init; }
    public string Message { get; init; } = string.Empty;
    public string DeduplicationKey { get; init; } = string.Empty;

    public AlertEvent()
    {
    }

    public AlertEvent(
        string ruleId,
        string targetKey,
        AlertEventKind kind,
        AlertSeverity severity,
        DateTimeOffset occurredAtUtc,
        double value,
        string message,
        string? deduplicationKey = null)
    {
        RuleId = ruleId ?? throw new ArgumentNullException(nameof(ruleId));
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        Kind = kind;
        Severity = severity;
        OccurredAtUtc = occurredAtUtc;
        Value = value;
        Message = message ?? string.Empty;
        DeduplicationKey = deduplicationKey ?? $"{ruleId}:{targetKey}";
    }

    public AlertEventKind Type => Kind;
    public DateTimeOffset TimestampUtc => OccurredAtUtc;
    public bool IsRaised => Kind == AlertEventKind.Raised;
}

/// <summary>A classification result for a process.</summary>
public sealed record ProcessClassificationResult
{
    public ProcessRole Role { get; init; }
    public ProcessCategory Category { get; init; }
    public string? IntegrationId { get; init; }
    public double Confidence { get; init; }
    public bool IsRelevant { get; init; }
    public string Reason { get; init; } = string.Empty;

    public ProcessClassificationResult()
    {
    }

    public ProcessClassificationResult(
        ProcessRole role,
        ProcessCategory category,
        string? integrationId = null,
        double confidence = 1,
        bool isRelevant = true,
        string? reason = null)
    {
        Role = role;
        Category = category;
        IntegrationId = integrationId;
        Confidence = Math.Clamp(confidence, 0, 1);
        IsRelevant = isRelevant;
        Reason = reason ?? string.Empty;
    }

    public ProcessRole ProcessRole => Role;
    public ProcessCategory ProcessCategory => Category;
}

/// <summary>A report-ready, immutable set of observations.</summary>
public sealed record DiagnosticReport
{
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public string? MachineName { get; init; }
    public string? UserName { get; init; }
    public string? ApplicationVersion { get; init; }
    public SystemMetrics? SystemMetrics { get; init; }
    public IReadOnlyList<ProcessSnapshot> Processes { get; init; } = Array.Empty<ProcessSnapshot>();
    public IReadOnlyList<IntegrationDescriptor> Integrations { get; init; } = Array.Empty<IntegrationDescriptor>();
    public IReadOnlyList<LogSignal> LogSignals { get; init; } = Array.Empty<LogSignal>();
    public IReadOnlyList<AlertEvent> Alerts { get; init; } = Array.Empty<AlertEvent>();
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public DiagnosticReport()
    {
    }

    public DiagnosticReport(
        DateTimeOffset generatedAtUtc,
        IEnumerable<ProcessSnapshot>? processes = null,
        SystemMetrics? systemMetrics = null,
        IEnumerable<IntegrationDescriptor>? integrations = null,
        IEnumerable<LogSignal>? logSignals = null,
        IEnumerable<AlertEvent>? alerts = null,
        string? machineName = null,
        string? userName = null,
        string? applicationVersion = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        GeneratedAtUtc = generatedAtUtc;
        Processes = CollectionCopy.ReadOnlyList(processes);
        SystemMetrics = systemMetrics;
        Integrations = CollectionCopy.ReadOnlyList(integrations);
        LogSignals = CollectionCopy.ReadOnlyList(logSignals);
        Alerts = CollectionCopy.ReadOnlyList(alerts);
        MachineName = machineName;
        UserName = userName;
        ApplicationVersion = applicationVersion;
        Metadata = CollectionCopy.ReadOnlyDictionary(metadata);
    }

    public IReadOnlyList<ProcessSnapshot> ProcessSnapshots => Processes;
    public DateTimeOffset TimestampUtc => GeneratedAtUtc;
}

internal static class CollectionCopy
{
    public static IReadOnlyList<T> ReadOnlyList<T>(IEnumerable<T>? items)
    {
        if (items is null)
        {
            return Array.Empty<T>();
        }

        return new System.Collections.ObjectModel.ReadOnlyCollection<T>(items.ToArray());
    }

    public static IReadOnlyDictionary<string, string> ReadOnlyDictionary(
        IReadOnlyDictionary<string, string>? items)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (items is not null)
        {
            foreach (var pair in items)
            {
                copy[pair.Key] = pair.Value;
            }
        }

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(copy);
    }
}
