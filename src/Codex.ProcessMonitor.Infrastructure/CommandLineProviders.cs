using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Codex.ProcessMonitor.Infrastructure;

/// <summary>Read-only process command-line provider. Implementations must treat access failures as normal.</summary>
public interface IProcessCommandLineProvider
{
    CommandLineQueryResult TryGetCommandLine(int processId);
}

/// <summary>A provider that never opens a process. Useful when command-line access is intentionally disabled.</summary>
public sealed class NullCommandLineProvider : IProcessCommandLineProvider
{
    public CommandLineQueryResult TryGetCommandLine(int processId) =>
        new(processId, null, false, null, "Command-line collection disabled.");
}

/// <summary>
/// Isolated provider using the documented query-limited process right and the native command-line query.
/// It never throws for an inaccessible or exited process.
/// </summary>
public sealed class WindowsCommandLineProvider : IProcessCommandLineProvider
{
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    public CommandLineQueryResult TryGetCommandLine(int processId)
    {
        if (processId <= 0)
            return new(processId, null, false, 87, "Invalid process id.");

        SafeProcessHandle? process = null;
        try
        {
            process = NativeMethods.OpenProcessSafe(ProcessQueryLimitedInformation, false, processId);
            if (process is null || process.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                return new(processId, null, false, error, new Win32Exception(error).Message);
            }

            var size = 4096;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NativeMethods.NtQueryInformationProcess(
                        process, ProcessCommandLineInformation, buffer, size, out var returned);
                    if (status == 0)
                    {
                        var unicode = Marshal.PtrToStructure<NativeMethods.UnicodeString>(buffer);
                        if (unicode.Length == 0 || unicode.Buffer == nint.Zero)
                            return new(processId, string.Empty, true);

                        var textBuffer = Marshal.AllocHGlobal(unicode.Length + 2);
                        try
                        {
                            if (!NativeMethods.ReadProcessMemory(process, unicode.Buffer, textBuffer, unicode.Length, out var read) || read.ToInt64() < unicode.Length)
                            {
                                var error = Marshal.GetLastWin32Error();
                                return new(processId, null, false, error, new Win32Exception(error).Message);
                            }

                            var commandLine = Marshal.PtrToStringUni(textBuffer, unicode.Length / 2) ?? string.Empty;
                            return new(processId, commandLine, true);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(textBuffer);
                        }
                    }

                    // STATUS_INFO_LENGTH_MISMATCH. The exact value is stable for NT and is only used to retry.
                    if (status == unchecked((int)0xC0000004) && returned > size && returned < 1024 * 1024)
                    {
                        size = Math.Max(size * 2, returned);
                        continue;
                    }

                    return new(processId, null, false, status, $"NtQueryInformationProcess returned NTSTATUS 0x{status:X8}.");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return new(processId, null, false, unchecked((int)0xC0000004), "Command-line buffer was not stable.");
        }
        catch (Exception ex) when (ex is Win32Exception or SEHException or InvalidOperationException or ArgumentException)
        {
            return new(processId, null, false, Marshal.GetLastWin32Error(), ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }
}

/// <summary>
/// Prevents a command-line failure from aborting an entire sample. The fallback is called only when the primary fails.
/// </summary>
public sealed class ResilientCommandLineProvider : IProcessCommandLineProvider
{
    private readonly IProcessCommandLineProvider _primary;
    private readonly IProcessCommandLineProvider _fallback;

    public ResilientCommandLineProvider(
        IProcessCommandLineProvider? primary = null,
        IProcessCommandLineProvider? fallback = null)
    {
        _primary = primary ?? new WindowsCommandLineProvider();
        _fallback = fallback ?? new NullCommandLineProvider();
    }

    public CommandLineQueryResult TryGetCommandLine(int processId)
    {
        try
        {
            var result = _primary.TryGetCommandLine(processId);
            if (result.Succeeded)
                return result;
            try
            {
                var fallback = _fallback.TryGetCommandLine(processId);
                return fallback with
                {
                    Error = fallback.Error is null
                        ? result.Error
                        : $"Primary command-line provider failed: {result.Error}; fallback failed: {fallback.Error}"
                };
            }
            catch (Exception fallbackError) when (fallbackError is Win32Exception or SEHException or InvalidOperationException or ArgumentException)
            {
                return result with { Error = $"{result.Error}; fallback failed: {fallbackError.Message}" };
            }
        }
        catch (Exception ex) when (ex is Win32Exception or SEHException or InvalidOperationException or ArgumentException)
        {
            try
            {
                return _fallback.TryGetCommandLine(processId);
            }
            catch (Exception fallbackError) when (fallbackError is Win32Exception or SEHException or InvalidOperationException or ArgumentException)
            {
                return new(processId, null, false, Marshal.GetLastWin32Error(), $"Command-line providers failed: {ex.Message}; {fallbackError.Message}");
            }
        }
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcessSafe(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        nint processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        nint buffer,
        int size,
        out nint numberOfBytesRead);
}
