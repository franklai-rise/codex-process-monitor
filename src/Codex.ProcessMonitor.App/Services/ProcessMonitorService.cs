using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Codex.ProcessMonitor.App.Models;
using Codex.ProcessMonitor.Core;
using Codex.ProcessMonitor.Infrastructure;
using AppProcessSample = Codex.ProcessMonitor.App.Models.ProcessSample;
using CoreProcessSample = Codex.ProcessMonitor.Core.ProcessSample;

namespace Codex.ProcessMonitor.App.Services;

/// <summary>
/// App-facing read-only source contract. The WPF layer remains independent from
/// the platform sampler while the adapter maps the shared Core/Infrastructure
/// contracts into the small models used by the UI.
/// </summary>
public interface IMonitorSource
{
    MonitorSnapshot Capture(CancellationToken cancellationToken);
    IReadOnlyList<CapabilitySample> GetCapabilities();
}

/// <summary>Optional raw-counter projection used by the asynchronous history writer.</summary>
public interface IHistoryBatchSource
{
    HistoryBatch CreateHistoryBatch(bool includeProcessDetails, bool includeSystemTotals);
}

/// <summary>
/// Composition root for the runnable shell. It intentionally exposes no process
/// control actions; the app is an observer only.
/// </summary>
public static class MonitorCompositionRoot
{
    public static ProcessMonitorService CreateMonitor()
    {
        var source = new WindowsRuntimeMonitorSource();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        HistoryStore? history = null;
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            history = new HistoryStore(Path.Combine(localAppData, "CodexProcessMonitor", "monitor.sqlite"));
        }

        return new ProcessMonitorService(source, historyStore: history);
    }
}

/// <summary>
/// Owns the background timer and bounded channel. Sampling is performed away
/// from the WPF dispatcher; consumers receive immutable snapshots and marshal
/// only completed data to the UI thread.
/// </summary>
public sealed class ProcessMonitorService : IAsyncDisposable
{
    private readonly IMonitorSource _source;
    private readonly Channel<MonitorSnapshot> _snapshots = Channel.CreateBounded<MonitorSnapshot>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });
    private readonly TimeSpan _sampleInterval;
    private readonly HistoryStore? _historyStore;
    private readonly IHistoryBatchSource? _historySource;
    private readonly Channel<HistoryWorkItem> _historyQueue = Channel.CreateBounded<HistoryWorkItem>(
        new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
    private CancellationTokenSource? _historyLifetime;
    private Task? _historyTask;
    private DateTimeOffset _lastSystemHistory = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProcessHistory = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSummaryHistory = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private CancellationTokenSource? _lifetime;
    private Task? _samplingTask;

    public ProcessMonitorService(IMonitorSource source, TimeSpan? sampleInterval = null, HistoryStore? historyStore = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _historyStore = historyStore;
        _historySource = source as IHistoryBatchSource;
        var interval = sampleInterval.GetValueOrDefault(TimeSpan.FromSeconds(2));
        _sampleInterval = interval < TimeSpan.FromMilliseconds(250)
            ? TimeSpan.FromMilliseconds(250)
            : interval;
    }

    public IReadOnlyList<CapabilitySample> Capabilities => _source.GetCapabilities();

    public bool IsRunning => _samplingTask is { IsCompleted: false };

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _lifetime?.Dispose();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_historyStore is not null && _historyTask is null)
        {
            _historyLifetime = new CancellationTokenSource();
            _historyTask = Task.Run(() => ConsumeHistoryAsync(_historyLifetime.Token), CancellationToken.None);
        }
        _samplingTask = Task.Run(() => ProduceAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<MonitorSnapshot> ReadSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return snapshot;
        }
    }

    public async ValueTask StopAsync()
    {
        var lifetime = _lifetime;
        if (lifetime is null)
        {
            return;
        }

        lifetime.Cancel();
        if (_samplingTask is not null)
        {
            try
            {
                await _samplingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown path.
            }
        }

        _samplingTask = null;
        lifetime.Dispose();
        _lifetime = null;

        _historyQueue.Writer.TryComplete();
        if (_historyTask is not null)
        {
            try
            {
                await _historyTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _historyTask = null;
        }
        _historyLifetime?.Dispose();
        _historyLifetime = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _historyQueue.Writer.TryComplete();
        _historyStore?.Dispose();
        _snapshots.Writer.TryComplete();
    }

    private async Task ProduceAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_sampleInterval);
        Publish(CaptureSafely(cancellationToken));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Publish(CaptureSafely(cancellationToken));
            }
        }
        finally
        {
            _snapshots.Writer.TryComplete();
        }
    }

    private MonitorSnapshot CaptureSafely(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = _source.Capture(cancellationToken);
            QueueHistory(snapshot);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var now = DateTimeOffset.Now;
            return new MonitorSnapshot(
                now,
                0,
                0,
                0,
                0,
                Array.Empty<AppProcessSample>(),
                new[] { new AlertSample(now, "错误", "采样暂不可用", exception.Message) });
        }
    }

    private void Publish(MonitorSnapshot snapshot)
        => _snapshots.Writer.TryWrite(snapshot);

    private void QueueHistory(MonitorSnapshot snapshot)
    {
        if (_historyStore is null || _historySource is null)
            return;

        var includeSystem = IsDue(snapshot.Timestamp, _lastSystemHistory, TimeSpan.FromSeconds(10));
        var includeProcess = IsDue(snapshot.Timestamp, _lastProcessHistory, TimeSpan.FromSeconds(30));
        var includeSummary = IsDue(snapshot.Timestamp, _lastSummaryHistory, TimeSpan.FromMinutes(5));
        var shouldPrune = IsDue(snapshot.Timestamp, _lastPrune, TimeSpan.FromHours(24));
        if (!includeSystem && !includeProcess && !includeSummary && !shouldPrune)
            return;

        var batch = _historySource.CreateHistoryBatch(includeProcess, includeSystem);
        var work = new HistoryWorkItem(
            batch,
            includeSummary
                ? new Summary5mRecord(ToFiveMinuteBucket(snapshot.Timestamp), snapshot.CpuPercent, snapshot.MemoryPercent, snapshot.WorkingSetBytes, snapshot.ProcessCount)
                : null,
            shouldPrune);
        if (!_historyQueue.Writer.TryWrite(work))
            return;

        if (includeSystem) _lastSystemHistory = snapshot.Timestamp;
        if (includeProcess) _lastProcessHistory = snapshot.Timestamp;
        if (includeSummary) _lastSummaryHistory = snapshot.Timestamp;
        if (shouldPrune) _lastPrune = snapshot.Timestamp;
    }

    private async Task ConsumeHistoryAsync(CancellationToken cancellationToken)
    {
        if (_historyStore is null)
            return;

        try
        {
            await foreach (var work in _historyQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (work.Batch.ProcessSamples.Count > 0 || work.Batch.SystemSamples.Count > 0 || work.Batch.MetadataEvents.Count > 0)
                    await Task.Run(() => _historyStore.AppendBatch(work.Batch), cancellationToken).ConfigureAwait(false);
                if (work.Summary is not null)
                    await Task.Run(() => _historyStore.AppendSummary5m(work.Summary), cancellationToken).ConfigureAwait(false);
                if (work.Prune)
                    await Task.Run(() => _historyStore.Prune(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // History is best-effort and must never stop live read-only sampling.
        }
    }

    private static DateTimeOffset ToFiveMinuteBucket(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        var bucketMinute = utc.Minute - utc.Minute % 5;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, bucketMinute, 0, TimeSpan.Zero);
    }

    private static bool IsDue(DateTimeOffset timestamp, DateTimeOffset last, TimeSpan interval)
        => last == DateTimeOffset.MinValue || timestamp >= last + interval;

    private sealed record HistoryWorkItem(HistoryBatch Batch, Summary5mRecord? Summary, bool Prune);
}

/// <summary>
/// Adapter over Infrastructure.WindowsProcessSampler. It uses the Toolhelp
/// process tree and query-limited Win32 counters, then applies Core's stable
/// cumulative-counter differencing before projecting into App.Models.
/// </summary>
public sealed class WindowsRuntimeMonitorSource : IMonitorSource, IHistoryBatchSource
{
    private readonly AlertEngine _alertEngine = new(new[]
    {
        new AlertRule(
            "codex-cpu-sustained",
            AlertMetric.CpuPercent,
            AlertComparison.GreaterThanOrEqual,
            20,
            duration: TimeSpan.FromSeconds(120),
            clearThreshold: 15,
            clearDuration: TimeSpan.FromSeconds(30),
            cooldown: TimeSpan.FromMinutes(5),
            severity: AlertSeverity.Warning,
            targetKey: "codex-tree",
            name: "Codex 树持续高 CPU",
            message: "Codex 桌面树 CPU 已持续超过 20%；请确认是否有前台任务或孤立运行时。"),
        new AlertRule(
            "codex-cpu-critical",
            AlertMetric.CpuPercent,
            AlertComparison.GreaterThanOrEqual,
            50,
            duration: TimeSpan.FromSeconds(120),
            clearThreshold: 40,
            clearDuration: TimeSpan.FromSeconds(30),
            cooldown: TimeSpan.FromMinutes(5),
            severity: AlertSeverity.Critical,
            targetKey: "codex-tree",
            name: "Codex 树持续严重 CPU",
            message: "Codex 桌面树 CPU 已持续超过 50%。"),
        new AlertRule(
            "system-memory-low",
            AlertMetric.WorkingSetBytes,
            AlertComparison.LessThan,
            2d * 1024 * 1024 * 1024,
            duration: TimeSpan.FromMinutes(2),
            clearThreshold: 2.5d * 1024 * 1024 * 1024,
            clearDuration: TimeSpan.FromSeconds(30),
            cooldown: TimeSpan.FromMinutes(5),
            severity: AlertSeverity.Critical,
            targetKey: "system-available-memory",
            name: "系统可用内存偏低",
            message: "系统可用物理内存已持续低于 2 GB。"),
    });
    private readonly WindowsProcessSampler _sampler;
    private readonly Dictionary<string, CoreProcessSample> _previousProcesses = new(StringComparer.Ordinal);
    private readonly int _logicalProcessorCount = Math.Max(1, Environment.ProcessorCount);
    private SystemTimesSnapshot? _previousSystemTimes;
    private ProcessTreeSnapshot _lastRetainedTree = ProcessTreeSnapshot.Empty(DateTimeOffset.UtcNow);
    private GlobalMemorySnapshot _lastGlobalMemory = new(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, 0);

    public WindowsRuntimeMonitorSource(WindowsProcessSampler? sampler = null)
    {
        _sampler = sampler ?? new WindowsProcessSampler(new ProcessSamplerOptions
        {
            IncludeCommandLines = true,
            IncludeImagePaths = true,
            IncludeThreads = true,
            IncludeIoCounters = true,
            IncludeHandleCounts = true,
            IncludeMemoryCounters = true,
            MaxProcesses = 4096
        });
    }

    public MonitorSnapshot Capture(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var systemSample = _sampler.Sample(cancellationToken);
        var allProcesses = systemSample.ProcessTree.Processes;
        var roots = allProcesses.Where(IsCodexDesktopRoot).ToArray();
        var detachedPluginHosts = allProcesses.Where(IsCodexPluginExtensionHost).ToArray();
        var retained = SelectCodexTree(allProcesses, roots, detachedPluginHosts);
        _lastRetainedTree = new ProcessTreeSnapshot(systemSample.CapturedAtUtc, retained, Array.Empty<ThreadInfo>());
        _lastGlobalMemory = systemSample.GlobalMemory;

        var processSamples = new List<AppProcessSample>(retained.Count);
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in retained)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ToCoreSample(process, systemSample.CapturedAtUtc);
            var stableKey = current.Identity.StableKey;
            _previousProcesses.TryGetValue(stableKey, out var previous);
            var derived = previous is null
                ? null
                : ProcessMetricsCalculator.CreateSnapshot(previous, current, _logicalProcessorCount);
            _previousProcesses[stableKey] = current;
            currentKeys.Add(stableKey);

            var cpuPercent = derived?.CpuPercent ?? 0;
            var ioRate = derived is null ? 0 : derived.ReadBytesPerSecond + derived.WriteBytesPerSecond;
            processSamples.Add(new AppProcessSample(
                process.ProcessId,
                process.ParentProcessId,
                Path.GetFileName(process.ImageName) is { Length: > 0 } imageName ? imageName : process.ImageName,
                cpuPercent,
                Math.Max(0, process.WorkingSetBytes),
                BuildStatus(process, ioRate),
                InstanceKey: stableKey));
        }

        foreach (var staleKey in _previousProcesses.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            _previousProcesses.Remove(staleKey);
        }

        var systemCpu = CalculateSystemCpu(systemSample.SystemTimes);
        if (systemCpu <= 0 && processSamples.Count > 0)
        {
            systemCpu = Math.Clamp(processSamples.Sum(static process => process.CpuPercent), 0, 100);
        }

        var memory = systemSample.GlobalMemory;
        var usedBytes = memory.TotalPhysicalBytes > 0
            ? Math.Max(0, memory.TotalPhysicalBytes - memory.AvailablePhysicalBytes)
            : 0;
        var memoryPercent = memory.TotalPhysicalBytes > 0
            ? Math.Clamp(usedBytes / (double)memory.TotalPhysicalBytes * 100, 0, 100)
            : 0;
        var alerts = BuildAlerts(
            systemSample.CapturedAtUtc,
            systemCpu,
            memoryPercent,
            processSamples,
            roots.Length > 0,
            detachedPluginHosts.Length,
            memory.AvailablePhysicalBytes,
            _alertEngine.Evaluate(
                systemSample.CapturedAtUtc,
                new[]
                {
                    new AlertObservation("codex-tree", AlertMetric.CpuPercent, processSamples.Sum(static process => process.CpuPercent)),
                    new AlertObservation("system-available-memory", AlertMetric.WorkingSetBytes, memory.AvailablePhysicalBytes),
                }));

        return new MonitorSnapshot(
            systemSample.CapturedAtUtc,
            systemCpu,
            memoryPercent,
            usedBytes,
            processSamples.Count,
            processSamples.OrderByDescending(static process => process.CpuPercent).ToArray(),
            alerts);
    }

    public IReadOnlyList<CapabilitySample> GetCapabilities()
    {
        var capabilities = new List<CapabilitySample>();
        try
        {
            var inventory = new MetadataInventoryReader().ReadDefault();

            foreach (var plugin in inventory.Plugins)
            {
                var name = plugin.Name ?? plugin.Id ?? Path.GetFileName(Path.GetDirectoryName(plugin.Path) ?? plugin.Path);
                capabilities.Add(new CapabilitySample(
                    "Plugin",
                    name,
                    plugin.Description ?? "从本地 .codex-plugin/plugin.json 发现的插件元数据。",
                    "已发现",
                    plugin.Version ?? "—",
                    "plugin.json"));
            }

            foreach (var config in inventory.Configs)
            {
                var mcpNames = config.Values.Keys
                    .Where(static key => key.StartsWith("mcp_servers.", StringComparison.OrdinalIgnoreCase))
                    .Select(static key => key["mcp_servers.".Length..].Split('.', 2)[0])
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var mcpName in mcpNames)
                {
                    capabilities.Add(new CapabilitySample(
                        "MCP",
                        mcpName,
                        "从本地 config.toml 发现的 MCP 配置；是否有运行时进程由进程树单独判定。",
                        "已配置",
                        "—",
                        "config.toml"));
                }
            }

            foreach (var skill in inventory.Skills)
            {
                var name = skill.Name ?? Path.GetFileName(Path.GetDirectoryName(skill.Path) ?? skill.Path);
                capabilities.Add(new CapabilitySample(
                    "Skill",
                    name,
                    skill.Description ?? "从本地 SKILL.md frontmatter 发现的 Skill 元数据。",
                    "已发现",
                    skill.Version ?? "—",
                    "SKILL.md"));
            }

            foreach (var logPath in GetKnownCodexLogPaths())
            {
                capabilities.Add(new CapabilitySample(
                    "日志",
                    Path.GetFileName(logPath),
                    "仅读取白名单元数据列；不读取或保存 feedback_log_body。",
                    File.Exists(logPath) ? "可读取" : "未发现",
                    "双库",
                    "logs_2.sqlite"));
            }

            foreach (var warning in inventory.Warnings.Take(3))
            {
                capabilities.Add(new CapabilitySample("扫描", "元数据读取受限", warning, "受限", "—", "只读扫描"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            capabilities.Add(new CapabilitySample("扫描", "本地元数据读取受限", exception.Message, "受限", "—", "只读扫描"));
        }

        if (capabilities.Count == 0)
        {
            capabilities.Add(new CapabilitySample(
                "状态",
                "未发现本地 Plugin / MCP / Skill 元数据",
                "仅显示可证明的配置、元数据和运行时进程证据，不根据进程名猜测归属。",
                "待发现",
                "—",
                "只读扫描"));
        }

        return capabilities;
    }

    public HistoryBatch CreateHistoryBatch(bool includeProcessDetails, bool includeSystemTotals)
    {
        var processSamples = includeProcessDetails
            ? _lastRetainedTree.Processes.Select(process => new ProcessSampleRecord(
                _lastRetainedTree.CapturedAtUtc,
                process.ProcessId,
                process.ParentProcessId,
                Path.GetFileName(process.ImageName),
                process.Role,
                process.ThreadCount,
                process.UserProcessorTime100Ns,
                process.KernelProcessorTime100Ns,
                Math.Max(0, process.WorkingSetBytes),
                Math.Max(0, process.PrivateBytes),
                Math.Max(0, process.HandleCount),
                Math.Max(0, process.ReadOperationCount),
                Math.Max(0, process.WriteOperationCount),
                Math.Max(0, process.OtherOperationCount),
                Math.Max(0, process.ReadTransferBytes),
                Math.Max(0, process.WriteTransferBytes),
                Math.Max(0, process.OtherTransferBytes)))
                .ToArray()
            : Array.Empty<ProcessSampleRecord>();

        var systemSamples = includeSystemTotals
            ? new[]
            {
                new SystemSampleRecord(
                    _lastGlobalMemory.CapturedAtUtc,
                    _previousSystemTimes?.IdleTime100Ns ?? 0,
                    _previousSystemTimes?.KernelTime100Ns ?? 0,
                    _previousSystemTimes?.UserTime100Ns ?? 0,
                    Math.Max(0, _lastGlobalMemory.TotalPhysicalBytes),
                    Math.Max(0, _lastGlobalMemory.AvailablePhysicalBytes),
                    Math.Max(0, _lastGlobalMemory.TotalPageFileBytes),
                    Math.Max(0, _lastGlobalMemory.AvailablePageFileBytes),
                    Math.Max(0, _lastGlobalMemory.TotalVirtualBytes),
                    Math.Max(0, _lastGlobalMemory.AvailableVirtualBytes),
                    _lastGlobalMemory.MemoryLoad)
            }
            : Array.Empty<SystemSampleRecord>();

        return new HistoryBatch(processSamples, systemSamples, Array.Empty<MetadataEventRecord>());
    }

    private static IEnumerable<string> GetKnownCodexLogPaths()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            yield break;
        yield return Path.Combine(profile, ".codex", "logs_2.sqlite");
        yield return Path.Combine(profile, ".codex", "sqlite", "logs_2.sqlite");
    }

    private static IReadOnlyList<ProcessInfo> SelectCodexTree(
        IReadOnlyList<ProcessInfo> allProcesses,
        IReadOnlyList<ProcessInfo> roots,
        IReadOnlyList<ProcessInfo> detachedPluginHosts)
    {
        var byParent = allProcesses
            .GroupBy(static process => process.ParentProcessId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var retainedIds = new HashSet<int>(roots.Select(static process => process.ProcessId));
        var queue = new Queue<int>(retainedIds);
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            if (!byParent.TryGetValue(parentId, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (retainedIds.Add(child.ProcessId))
                {
                    queue.Enqueue(child.ProcessId);
                }
            }
        }

        foreach (var pluginHost in detachedPluginHosts)
        {
            retainedIds.Add(pluginHost.ProcessId);
        }

        return allProcesses
            .Where(process => retainedIds.Contains(process.ProcessId))
            .OrderBy(static process => process.ParentProcessId)
            .ThenBy(static process => process.ProcessId)
            .ToArray();
    }

    private static bool IsCodexDesktopRoot(ProcessInfo process)
    {
        var imageName = Path.GetFileName(process.ImageName).Trim().ToLowerInvariant();
        var commandLine = process.CommandLine?.ToLowerInvariant() ?? string.Empty;
        if (imageName is "chatgpt.exe" or "chatgpt")
        {
            return true;
        }

        var isCodexName = imageName is "codex.exe" or "codex";
        var isAppServerName = imageName is "app-server.exe" or "app-server" or "codex-app-server.exe" or "codex-app-server";
        var mentionsCodexAppServer = commandLine.Contains("codex app-server", StringComparison.Ordinal)
            || commandLine.Contains("codex app_server", StringComparison.Ordinal)
            || commandLine.Contains("codex-app-server", StringComparison.Ordinal)
            || commandLine.Contains("codex.exe app-server", StringComparison.Ordinal)
            || commandLine.Contains("codex.exe app_server", StringComparison.Ordinal);
        return mentionsCodexAppServer || (isAppServerName && commandLine.Contains("codex", StringComparison.Ordinal)) ||
               (isCodexName && commandLine.Contains("app-server", StringComparison.Ordinal));
    }

    private static bool IsCodexPluginExtensionHost(ProcessInfo process)
    {
        var imageName = Path.GetFileName(process.ImageName).ToLowerInvariant();
        if (!imageName.Contains("extension-host", StringComparison.Ordinal)
            && !imageName.Contains("extension_host", StringComparison.Ordinal))
        {
            return false;
        }

        var path = (process.ImagePath ?? string.Empty).Replace('/', '\\').ToLowerInvariant();
        var commandLine = (process.CommandLine ?? string.Empty).Replace('/', '\\').ToLowerInvariant();
        return path.Contains("\\.codex\\plugins\\", StringComparison.Ordinal)
            || commandLine.Contains("\\.codex\\plugins\\", StringComparison.Ordinal);
    }

    private CoreProcessSample ToCoreSample(ProcessInfo process, DateTimeOffset capturedAtUtc)
    {
        var metricsAvailable = process.MetricsAvailable;
        var identity = new ProcessIdentity(
            process.ProcessId,
            process.ImageName,
            process.StartTimeUtc,
            process.ImagePath,
            process.CommandLine,
            process.ParentProcessId);
        var totalProcessorTicks = SaturatingAdd(process.UserProcessorTime100Ns, process.KernelProcessorTime100Ns);
        return new CoreProcessSample
        {
            Identity = identity,
            TimestampUtc = capturedAtUtc,
            TotalProcessorTime = TimeSpan.FromTicks(totalProcessorTicks),
            WorkingSetBytes = metricsAvailable ? process.WorkingSetBytes : null,
            PrivateBytes = metricsAvailable ? process.PrivateBytes : null,
            ReadBytesTotal = metricsAvailable ? process.ReadTransferBytes : null,
            WriteBytesTotal = metricsAvailable ? process.WriteTransferBytes : null,
            ThreadCount = process.ThreadCount
        };
    }

    private double CalculateSystemCpu(SystemTimesSnapshot current)
    {
        var previous = _previousSystemTimes;
        _previousSystemTimes = current;
        if (previous is null || !current.Available)
        {
            return 0;
        }

        if (!previous.Available)
        {
            return 0;
        }

        var previousTotal = SaturatingAdd(previous.KernelTime100Ns, previous.UserTime100Ns);
        var currentTotal = SaturatingAdd(current.KernelTime100Ns, current.UserTime100Ns);
        var totalDelta = CounterDifference(previousTotal, currentTotal);
        var idleDelta = CounterDifference(previous.IdleTime100Ns, current.IdleTime100Ns);
        if (totalDelta <= 0)
        {
            return 0;
        }

        return Math.Clamp((totalDelta - Math.Min(totalDelta, idleDelta)) / (double)totalDelta * 100, 0, 100);
    }

    private static IReadOnlyList<AlertSample> BuildAlerts(
        DateTimeOffset timestamp,
        double cpuPercent,
        double memoryPercent,
        IReadOnlyList<AppProcessSample> processes,
        bool hasCodexRoot,
        int detachedPluginHostCount,
        long availableMemoryBytes,
        IReadOnlyList<AlertEvent> transitions)
    {
        var alerts = new List<AlertSample>();
        if (!hasCodexRoot && detachedPluginHostCount == 0)
        {
            alerts.Add(new AlertSample(
                timestamp,
                "信息",
                "未发现 Codex 桌面进程",
                "未找到 ChatGPT.exe 或 Codex app-server；仅保留明确位于 .codex\\plugins 的 extension-host。"));
        }
        else if (timestamp >= DateTimeOffset.Now.AddSeconds(-5))
        {
            alerts.Add(new AlertSample(timestamp, "信息", "监控已启动", "后台采样器正在建立 CPU 与 I/O 基线。"));
        }

        foreach (var transition in transitions)
        {
            var severity = transition.Kind == AlertEventKind.Cleared
                ? "信息"
                : transition.Severity == AlertSeverity.Critical ? "严重" : "警告";
            alerts.Add(new AlertSample(
                timestamp,
                severity,
                transition.Kind == AlertEventKind.Cleared ? $"已清除：{transition.Message}" : transition.Message,
                $"触发值：{transition.Value:0.0}"));
        }

        if (hasCodexRoot && processes.Count > 0 && availableMemoryBytes > 0 && memoryPercent >= 95)
            alerts.Add(new AlertSample(timestamp, "警告", "系统内存使用率很高", $"当前约 {memoryPercent:0.0}%，可用内存 {FormatBytes(availableMemoryBytes)}。"));

        return alerts;
    }

    private static string BuildStatus(ProcessInfo process, double ioBytesPerSecond)
    {
        if (!process.MetricsAvailable)
        {
            return "受限";
        }

        return ioBytesPerSecond > 0
            ? $"运行中 · I/O {FormatRate(ioBytesPerSecond)}"
            : "运行中";
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:0} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024:0.0} KB/s";
        return $"{bytesPerSecond / (1024 * 1024):0.0} MB/s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right) return long.MaxValue;
        if (right < 0 && left < long.MinValue - right) return long.MinValue;
        return left + right;
    }

    private static long CounterDifference(long previous, long current)
        => previous < 0 || current < 0 || current < previous ? 0 : current - previous;
}
