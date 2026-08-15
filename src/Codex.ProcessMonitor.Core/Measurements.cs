namespace Codex.ProcessMonitor.Core;

/// <summary>The safe difference between two cumulative counters.</summary>
public readonly record struct CounterDelta(long Value, bool WasReset = false)
{
    public long Delta => Value;

    public static implicit operator long(CounterDelta value) => value.Value;
}

public readonly record struct IoRates(double ReadBytesPerSecond, double WriteBytesPerSecond)
{
    public double TotalBytesPerSecond => ReadBytesPerSecond + WriteBytesPerSecond;
    public double ReadRate => ReadBytesPerSecond;
    public double WriteRate => WriteBytesPerSecond;
}

/// <summary>Helpers for cumulative CPU and I/O counters. A counter reset never produces a spike.</summary>
public static class CounterDiff
{
    public static CounterDelta Difference(long previous, long current)
    {
        if (previous < 0 || current < 0 || current < previous)
        {
            return new CounterDelta(0, WasReset: true);
        }

        return new CounterDelta(current - previous);
    }

    public static CounterDelta Difference(long? previous, long? current)
    {
        if (!previous.HasValue || !current.HasValue)
        {
            return new CounterDelta(0, WasReset: true);
        }

        return Difference(previous.Value, current.Value);
    }

    public static double Rate(long previous, long current, TimeSpan elapsed)
    {
        return Rate(Difference(previous, current), elapsed);
    }

    public static double Rate(CounterDelta delta, TimeSpan elapsed)
    {
        if (delta.WasReset || elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        return delta.Value / elapsed.TotalSeconds;
    }
}

/// <summary>Computes a process CPU percentage and read/write rates from two samples.</summary>
public static class ProcessMetricsCalculator
{
    public static double CalculateCpuPercent(
        TimeSpan previousProcessorTime,
        TimeSpan currentProcessorTime,
        TimeSpan elapsed,
        int logicalProcessorCount = 1)
    {
        if (elapsed <= TimeSpan.Zero || logicalProcessorCount <= 0)
        {
            return 0;
        }

        var processorDelta = currentProcessorTime - previousProcessorTime;
        if (processorDelta <= TimeSpan.Zero)
        {
            return 0;
        }

        var percent = processorDelta.TotalSeconds / elapsed.TotalSeconds * 100 / logicalProcessorCount;
        return double.IsFinite(percent) && percent >= 0 ? Math.Clamp(percent, 0, 100) : 0;
    }

    public static double CalculateCpuPercent(
        ProcessSample previous,
        ProcessSample current,
        int logicalProcessorCount = 1)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var elapsed = current.TimestampUtc - previous.TimestampUtc;
        if (!SameProcess(previous.Identity, current.Identity))
        {
            return 0;
        }

        return CalculateCpuPercent(
            previous.TotalProcessorTime,
            current.TotalProcessorTime,
            elapsed,
            logicalProcessorCount);
    }

    public static IoRates CalculateIoRates(
        long? previousReadBytes,
        long? currentReadBytes,
        long? previousWriteBytes,
        long? currentWriteBytes,
        TimeSpan elapsed)
    {
        var read = CounterDiff.Rate(CounterDiff.Difference(previousReadBytes, currentReadBytes), elapsed);
        var write = CounterDiff.Rate(CounterDiff.Difference(previousWriteBytes, currentWriteBytes), elapsed);
        return new IoRates(read, write);
    }

    public static IoRates CalculateIoRates(ProcessSample previous, ProcessSample current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (!SameProcess(previous.Identity, current.Identity))
        {
            return default;
        }

        return CalculateIoRates(
            previous.ReadBytesTotal,
            current.ReadBytesTotal,
            previous.WriteBytesTotal,
            current.WriteBytesTotal,
            current.TimestampUtc - previous.TimestampUtc);
    }

    /// <summary>Builds a derived snapshot, or null when the samples cannot be safely differenced.</summary>
    public static ProcessSnapshot? CreateSnapshot(
        ProcessSample previous,
        ProcessSample current,
        int logicalProcessorCount = 1,
        ProcessClassificationResult? classification = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (!SameProcess(previous.Identity, current.Identity) || current.TimestampUtc <= previous.TimestampUtc)
        {
            return null;
        }

        var rates = CalculateIoRates(previous, current);
        var result = classification ?? new ProcessClassificationResult(
            ProcessRole.Unknown,
            ProcessCategory.Unknown,
            confidence: 0,
            isRelevant: true);
        return new ProcessSnapshot(
            current.Identity,
            current.TimestampUtc,
            CalculateCpuPercent(previous, current, logicalProcessorCount),
            rates.ReadBytesPerSecond,
            rates.WriteBytesPerSecond,
            current.WorkingSetBytes,
            current.PrivateBytes,
            current.ThreadCount,
            result.Role,
            result.Category,
            result.IntegrationId);
    }

    public static bool TryCreateSnapshot(
        ProcessSample previous,
        ProcessSample current,
        out ProcessSnapshot snapshot,
        int logicalProcessorCount = 1,
        ProcessClassificationResult? classification = null)
    {
        snapshot = CreateSnapshot(previous, current, logicalProcessorCount, classification)!;
        return snapshot is not null;
    }

    private static bool SameProcess(ProcessIdentity previous, ProcessIdentity current)
    {
        return previous.StableKey.Equals(current.StableKey, StringComparison.Ordinal);
    }
}

/// <summary>Short aliases for consumers that call these operations CPU/I/O diffs.</summary>
public static class CpuIoDiff
{
    public static double CpuPercent(
        TimeSpan previousProcessorTime,
        TimeSpan currentProcessorTime,
        TimeSpan elapsed,
        int logicalProcessorCount = 1) =>
        ProcessMetricsCalculator.CalculateCpuPercent(
            previousProcessorTime,
            currentProcessorTime,
            elapsed,
            logicalProcessorCount);

    public static IoRates IoRates(
        long? previousReadBytes,
        long? currentReadBytes,
        long? previousWriteBytes,
        long? currentWriteBytes,
        TimeSpan elapsed) =>
        ProcessMetricsCalculator.CalculateIoRates(
            previousReadBytes,
            currentReadBytes,
            previousWriteBytes,
            currentWriteBytes,
            elapsed);
}
