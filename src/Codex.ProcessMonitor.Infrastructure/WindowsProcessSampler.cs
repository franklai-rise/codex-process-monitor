using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Codex.ProcessMonitor.Infrastructure;

public interface IWindowsProcessSampler
{
    WindowsSystemSample Sample(CancellationToken cancellationToken = default);
}

/// <summary>
/// Low-overhead, read-only Windows sampler. It takes one Toolhelp snapshot for the process/thread tree
/// and performs best-effort query-limited metric reads per process.
/// </summary>
public sealed partial class WindowsProcessSampler : IWindowsProcessSampler
{
    private readonly ProcessSamplerOptions _options;
    private readonly IProcessCommandLineProvider _commandLineProvider;
    private readonly IProcessRoleClassifier _roleClassifier;
    private readonly object _commandLineGate = new();
    private readonly Dictionary<int, CachedCommandLine> _commandLineCache = new();

    public WindowsProcessSampler(
        ProcessSamplerOptions? options = null,
        IProcessCommandLineProvider? commandLineProvider = null,
        IProcessRoleClassifier? roleClassifier = null)
    {
        _options = options ?? new ProcessSamplerOptions();
        _commandLineProvider = commandLineProvider ?? new ResilientCommandLineProvider();
        _roleClassifier = roleClassifier ?? new ProcessRoleClassifier();
    }

    public WindowsSystemSample Sample(CancellationToken cancellationToken = default)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        var processEntries = ReadProcessEntries(cancellationToken);
        IReadOnlyList<ThreadInfo> threads = _options.IncludeThreads
            ? ReadThreadEntries(cancellationToken)
            : Array.Empty<ThreadInfo>();
        var threadCounts = threads.GroupBy(t => t.OwnerProcessId).ToDictionary(g => g.Key, g => g.Count());
        var processes = new List<ProcessInfo>(processEntries.Count);

        foreach (var entry in processEntries.Take(Math.Max(1, _options.MaxProcesses)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processes.Add(ReadProcess(entry, threadCounts.GetValueOrDefault(entry.ProcessId)));
        }

        var processTree = new ProcessTreeSnapshot(capturedAt, processes, threads);
        return new WindowsSystemSample(capturedAt, processTree, ReadSystemTimes(capturedAt), ReadGlobalMemory(capturedAt));
    }

    public ProcessTreeSnapshot SampleProcessTree(CancellationToken cancellationToken = default) => Sample(cancellationToken).ProcessTree;

    public SystemTimesSnapshot SampleSystemTimes() => ReadSystemTimes(DateTimeOffset.UtcNow);

    public GlobalMemorySnapshot SampleGlobalMemory() => ReadGlobalMemory(DateTimeOffset.UtcNow);

    public WindowsSystemSample Collect(CancellationToken cancellationToken = default) => Sample(cancellationToken);

    private List<RawProcessEntry> ReadProcessEntries(CancellationToken cancellationToken)
    {
        var entries = new List<RawProcessEntry>();
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
            return entries;

        var entry = new NativeMethods.ProcessEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>() };
        if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            return entries;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ProcessId <= int.MaxValue && entry.ProcessId > 0)
                entries.Add(new RawProcessEntry((int)entry.ProcessId, (int)Math.Min(entry.ParentProcessId, int.MaxValue), entry.ExeFile ?? string.Empty));
            entry = new NativeMethods.ProcessEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>() };
        }
        while (NativeMethods.Process32NextW(snapshot, ref entry));

        return entries;
    }

    private static List<ThreadInfo> ReadThreadEntries(CancellationToken cancellationToken)
    {
        var entries = new List<ThreadInfo>();
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapThread, 0);
        if (snapshot.IsInvalid)
            return entries;

        var entry = new NativeMethods.ThreadEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ThreadEntry32>() };
        if (!NativeMethods.Thread32First(snapshot, ref entry))
            return entries;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ThreadId <= int.MaxValue && entry.OwnerProcessId <= int.MaxValue)
            {
                // Toolhelp exposes state/wait reason only through additional thread APIs. Keep the
                // tree cheap and report the fields as unknown (zero) rather than opening every thread.
                entries.Add(new ThreadInfo(
                    (int)entry.ThreadId,
                    (int)entry.OwnerProcessId,
                    entry.BasePriority,
                    entry.DeltaPriority,
                    0,
                    0));
            }
            entry = new NativeMethods.ThreadEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ThreadEntry32>() };
        }
        while (NativeMethods.Thread32Next(snapshot, ref entry));
        return entries;
    }

    private ProcessInfo ReadProcess(RawProcessEntry entry, int threadCount)
    {
        string? imagePath = null;
        string? commandLine = null;
        var userTime = 0L;
        var kernelTime = 0L;
        var workingSet = 0L;
        var privateBytes = 0L;
        var handleCount = 0L;
        var readOperations = 0L;
        var writeOperations = 0L;
        var otherOperations = 0L;
        var readBytes = 0L;
        var writeBytes = 0L;
        var otherBytes = 0L;
        var available = false;
        var errorCode = (int?)null;
        DateTimeOffset? startTime = null;

        var access = NativeMethods.ProcessQueryLimitedInformation;
        using var process = NativeMethods.OpenProcessSafe(access, false, entry.ProcessId);
        if (process is not null && !process.IsInvalid)
        {
            available = true;
            if (_options.IncludeImagePaths)
                imagePath = TryGetImagePath(process);

            if (NativeMethods.GetProcessTimes(process, out var creation, out _, out var kernel, out var user))
            {
                userTime = user.ToLong();
                kernelTime = kernel.ToLong();
                try
                {
                    startTime = DateTimeOffset.FromFileTime(creation.ToLong());
                }
                catch (ArgumentOutOfRangeException)
                {
                    startTime = null;
                }
            }
            else
            {
                errorCode = Marshal.GetLastWin32Error();
            }

            if (_options.IncludeMemoryCounters && NativeMethods.GetProcessMemoryInfo(process, out var memory, (uint)Marshal.SizeOf<NativeMethods.ProcessMemoryCounters>()))
            {
                workingSet = SaturatingInt64(memory.WorkingSetSize);
                privateBytes = SaturatingInt64(memory.PrivateUsage);
            }
            else if (_options.IncludeMemoryCounters)
            {
                errorCode ??= Marshal.GetLastWin32Error();
            }

            if (_options.IncludeHandleCounts && NativeMethods.GetProcessHandleCount(process, out var handles))
                handleCount = handles;
            else if (_options.IncludeHandleCounts)
                errorCode ??= Marshal.GetLastWin32Error();

            if (_options.IncludeIoCounters && NativeMethods.GetProcessIoCounters(process, out var io))
            {
                readOperations = SaturatingInt64(io.ReadOperationCount);
                writeOperations = SaturatingInt64(io.WriteOperationCount);
                otherOperations = SaturatingInt64(io.OtherOperationCount);
                readBytes = SaturatingInt64(io.ReadTransferCount);
                writeBytes = SaturatingInt64(io.WriteTransferCount);
                otherBytes = SaturatingInt64(io.OtherTransferCount);
            }
            else if (_options.IncludeIoCounters)
            {
                errorCode ??= Marshal.GetLastWin32Error();
            }
        }
        else
        {
            errorCode = Marshal.GetLastWin32Error();
        }

        if (_options.IncludeCommandLines && ShouldInspectCommandLine(entry.ImageName, imagePath))
        {
            var shouldQuery = ShouldQueryCommandLine(entry.ProcessId, startTime, out commandLine);
            if (shouldQuery)
            {
                try
                {
                    var commandResult = _commandLineProvider.TryGetCommandLine(entry.ProcessId);
                    commandLine = commandResult.Succeeded ? commandResult.CommandLine : null;
                    RememberCommandLine(entry.ProcessId, startTime, commandLine, commandResult.Succeeded);
                    if (!commandResult.Succeeded)
                        errorCode ??= commandResult.ErrorCode;
                }
                catch (Exception ex) when (ex is Win32Exception or SEHException or InvalidOperationException or ArgumentException)
                {
                    RememberCommandLine(entry.ProcessId, startTime, null, succeeded: false);
                    errorCode ??= Marshal.GetLastWin32Error();
                }
            }
        }

        var role = _roleClassifier.Classify(new ProcessRoleContext(
            entry.ProcessId, entry.ParentProcessId, entry.ImageName, imagePath, commandLine));
        return new ProcessInfo(
            entry.ProcessId,
            entry.ParentProcessId,
            entry.ImageName,
            imagePath,
            role,
            commandLine,
            threadCount,
            startTime,
            userTime,
            kernelTime,
            workingSet,
            privateBytes,
            handleCount,
            readOperations,
            writeOperations,
            otherOperations,
            readBytes,
            writeBytes,
            otherBytes,
            available,
            errorCode);
    }

    private static string? TryGetImagePath(SafeProcessHandle process)
    {
        try
        {
            var buffer = new char[32768];
            uint length = (uint)buffer.Length;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref length)
                ? new string(buffer, 0, (int)length)
                : null;
        }
        catch (Exception ex) when (ex is Win32Exception or SEHException)
        {
            return null;
        }
    }

    private static long SaturatingInt64(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static bool ShouldInspectCommandLine(string imageName, string? imagePath)
    {
        var image = Path.GetFileNameWithoutExtension(imageName ?? string.Empty);
        if (image.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
            image.Contains("chatgpt", StringComparison.OrdinalIgnoreCase) ||
            image.Contains("extension-host", StringComparison.OrdinalIgnoreCase) ||
            image.Contains("extension_host", StringComparison.OrdinalIgnoreCase) ||
            image.Equals("node_repl", StringComparison.OrdinalIgnoreCase) ||
            image.Equals("node", StringComparison.OrdinalIgnoreCase) ||
            image.Equals("python", StringComparison.OrdinalIgnoreCase) ||
            image.Equals("pythonw", StringComparison.OrdinalIgnoreCase) ||
            image.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
            image.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
            return true;

        var path = imagePath ?? string.Empty;
        return path.Contains("\\.codex\\", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\OpenAI.Codex", StringComparison.OrdinalIgnoreCase);
    }

    private static long SaturatingInt64(nuint value)
    {
        if (IntPtr.Size == 4)
            return (long)value;
        var maximum = unchecked((nuint)long.MaxValue);
        return value > maximum ? long.MaxValue : (long)value;
    }

    private static SystemTimesSnapshot ReadSystemTimes(DateTimeOffset capturedAt)
    {
        try
        {
            if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
                return new(capturedAt, idle.ToLong(), kernel.ToLong(), user.ToLong());
        }
        catch (Exception ex) when (ex is Win32Exception or SEHException)
        {
        }
        return new(capturedAt, 0, 0, 0, false);
    }

    private static GlobalMemorySnapshot ReadGlobalMemory(DateTimeOffset capturedAt)
    {
        try
        {
            var status = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
            if (NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                return new(
                    capturedAt,
                    SaturatingInt64(status.TotalPhysicalMemory),
                    SaturatingInt64(status.AvailablePhysicalMemory),
                    SaturatingInt64(status.TotalPageFile),
                    SaturatingInt64(status.AvailablePageFile),
                    SaturatingInt64(status.TotalVirtual),
                    SaturatingInt64(status.AvailableVirtual),
                    status.MemoryLoad);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or SEHException)
        {
        }
        return new(capturedAt, 0, 0, 0, 0, 0, 0, 0, false);
    }

    private readonly record struct RawProcessEntry(int ProcessId, int ParentProcessId, string ImageName);

    private bool ShouldQueryCommandLine(int processId, DateTimeOffset? startTimeUtc, out string? cachedCommandLine)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_commandLineGate)
        {
            if (_commandLineCache.TryGetValue(processId, out var cached) &&
                cached.StartTimeUtc == startTimeUtc &&
                now - cached.LastAttemptUtc < TimeSpan.FromSeconds(30))
            {
                cachedCommandLine = cached.CommandLine;
                return false;
            }
        }

        cachedCommandLine = null;
        return true;
    }

    private void RememberCommandLine(int processId, DateTimeOffset? startTimeUtc, string? commandLine, bool succeeded)
    {
        lock (_commandLineGate)
        {
            _commandLineCache[processId] = new CachedCommandLine(
                startTimeUtc,
                commandLine,
                DateTimeOffset.UtcNow,
                succeeded);
        }
    }

    private readonly record struct CachedCommandLine(
        DateTimeOffset? StartTimeUtc,
        string? CommandLine,
        DateTimeOffset LastAttemptUtc,
        bool Succeeded);
}
