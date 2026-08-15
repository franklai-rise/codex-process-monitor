using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Codex.ProcessMonitor.Infrastructure;

/// <summary>
/// Enumerates only native top-level windows that already belong to a caller
/// supplied PID set. This is a single user32 enumeration and never sends input,
/// uses UI Automation, or reads window captions/content.
/// </summary>
public sealed class WindowsTopLevelWindowSampler : IWindowsWindowSampler
{
    public IReadOnlyList<DesktopWindowInfo> Sample(
        IReadOnlySet<int> processIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        if (processIds.Count == 0)
        {
            return Array.Empty<DesktopWindowInfo>();
        }

        var windows = new List<DesktopWindowInfo>();
        nint foreground = nint.Zero;
        try
        {
            foreground = NativeMethods.GetForegroundWindow();
        }
        catch (Exception exception) when (exception is Win32Exception or SEHException)
        {
            // A missing foreground marker does not make the mapping unusable.
        }

        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                var threadId = NativeMethods.GetWindowThreadProcessId(windowHandle, out var rawProcessId);
                if (rawProcessId == 0 || rawProcessId > int.MaxValue || !processIds.Contains((int)rawProcessId))
                {
                    return true;
                }

                var isForeground = windowHandle == foreground;
                var isVisible = NativeMethods.IsWindowVisible(windowHandle);
                var isMinimized = NativeMethods.IsIconic(windowHandle);
                // Hidden implementation windows do not correspond to a user
                // conversation window. Keep a foreground window even if it is
                // in a transient state.
                if (!isVisible && !isForeground)
                {
                    return true;
                }

                var rectangle = new NativeMethods.WindowRect();
                NativeMethods.GetWindowRect(windowHandle, out rectangle);
                windows.Add(new DesktopWindowInfo(
                    windowHandle,
                    (int)rawProcessId,
                    threadId > int.MaxValue ? 0 : (int)threadId,
                    isVisible,
                    isForeground,
                    isMinimized,
                    Math.Max(0, rectangle.Right - rectangle.Left),
                    Math.Max(0, rectangle.Bottom - rectangle.Top),
                    ReadWindowClass(windowHandle)));
            }
            catch (Exception exception) when (exception is Win32Exception or SEHException or ArgumentException)
            {
                // Individual windows can disappear while EnumWindows is
                // running. Treat those as an absent association.
            }

            return true;
        }, nint.Zero);

        cancellationToken.ThrowIfCancellationRequested();
        return windows
            .OrderByDescending(static window => window.IsForeground)
            .ThenByDescending(static window => window.IsVisible)
            .ThenBy(static window => window.ProcessId)
            .ThenBy(static window => window.Handle.ToInt64())
            .ToArray();
    }

    private static string ReadWindowClass(nint windowHandle)
    {
        try
        {
            var buffer = new char[256];
            var length = NativeMethods.GetClassNameW(windowHandle, buffer, buffer.Length);
            return length > 0 ? new string(buffer, 0, length) : string.Empty;
        }
        catch (Exception exception) when (exception is Win32Exception or SEHException)
        {
            return string.Empty;
        }
    }
}
