namespace Codex.ProcessMonitor.Infrastructure;

/// <summary>Adapters expose the platform records through the contracts in Core without leaking Win32 types.</summary>
public sealed partial class WindowsProcessSampler : Codex.ProcessMonitor.Core.IProcessSampler
{
    public ValueTask<IReadOnlyList<Codex.ProcessMonitor.Core.ProcessSample>> SampleAsync(CancellationToken cancellationToken = default)
    {
        var sample = Sample(cancellationToken);
        var mapped = sample.ProcessTree.Processes
            .Select(process => new Codex.ProcessMonitor.Core.ProcessSample(
                new Codex.ProcessMonitor.Core.ProcessIdentity(
                    process.ProcessId,
                    process.ImageName,
                    process.StartTimeUtc,
                    process.ImagePath,
                    process.CommandLine,
                    process.ParentProcessId),
                sample.CapturedAtUtc,
                TimeSpan.FromTicks(SaturatingTicks(process.UserProcessorTime100Ns, process.KernelProcessorTime100Ns)),
                process.ReadTransferBytes,
                process.WriteTransferBytes,
                process.WorkingSetBytes,
                process.PrivateBytes,
                process.ThreadCount))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<Codex.ProcessMonitor.Core.ProcessSample>>(mapped);
    }

    private static long SaturatingTicks(long user, long kernel)
    {
        if (user <= 0 && kernel <= 0)
            return 0;
        if (user > long.MaxValue - kernel)
            return long.MaxValue;
        return Math.Max(0, user + kernel);
    }
}
public sealed partial class HistoryStore : Codex.ProcessMonitor.Core.IHistoryStore
{
    public ValueTask AppendAsync(Codex.ProcessMonitor.Core.ProcessSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = snapshot.Identity;
        Append(new ProcessSampleRecord(
            snapshot.TimestampUtc,
            identity.ProcessId,
            identity.ParentProcessId ?? 0,
            identity.Name,
            snapshot.Role,
            snapshot.ThreadCount ?? 0,
            0,
            0,
            snapshot.WorkingSetBytes ?? 0,
            snapshot.PrivateBytes ?? 0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            identity.CommandLine,
            identity.ExecutablePath));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<Codex.ProcessMonitor.Core.ProcessSnapshot>> QueryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset? untilUtc = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = QueryProcessSamples(fromUtc, untilUtc)
            .Select(sample => new Codex.ProcessMonitor.Core.ProcessSnapshot(
                new Codex.ProcessMonitor.Core.ProcessIdentity(
                    sample.ProcessId,
                    sample.ImageName,
                    executablePath: sample.ImagePath,
                    commandLine: sample.CommandLine,
                    parentProcessId: sample.ParentProcessId),
                sample.CapturedAtUtc,
                workingSetBytes: sample.WorkingSetBytes,
                privateBytes: sample.PrivateBytes,
                threadCount: sample.ThreadCount,
                role: sample.Role))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<Codex.ProcessMonitor.Core.ProcessSnapshot>>(snapshots);
    }
}

public sealed partial class MetadataInventoryReader : Codex.ProcessMonitor.Core.IIntegrationCatalog
{
    public ValueTask<IReadOnlyList<Codex.ProcessMonitor.Core.IntegrationDescriptor>> GetIntegrationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inventory = ReadDefault(cancellationToken);
        var descriptors = inventory.Plugins.Select(plugin => new Codex.ProcessMonitor.Core.IntegrationDescriptor(
                plugin.Id ?? plugin.Name ?? Path.GetFileName(Path.GetDirectoryName(plugin.Path) ?? plugin.Path),
                plugin.Name ?? plugin.Id ?? Path.GetFileName(plugin.Path),
                Codex.ProcessMonitor.Core.IntegrationRole.Plugin,
                true,
                plugin.Version,
                plugin.Path,
                null,
                plugin.Values.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase)))
            .Concat(inventory.Skills.Select(skill => new Codex.ProcessMonitor.Core.IntegrationDescriptor(
                $"skill:{Path.GetFileName(Path.GetDirectoryName(skill.Path) ?? skill.Path)}",
                skill.Name ?? Path.GetFileName(Path.GetDirectoryName(skill.Path) ?? skill.Path),
                Codex.ProcessMonitor.Core.IntegrationRole.Extension,
                true,
                skill.Version,
                skill.Path,
                null,
                skill.Values.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase))))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<Codex.ProcessMonitor.Core.IntegrationDescriptor>>(descriptors);
    }
}
