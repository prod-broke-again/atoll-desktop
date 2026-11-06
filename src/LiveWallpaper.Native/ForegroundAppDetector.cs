using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveWallpaper.Native;

public static class ForegroundAppDetector
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    public static string GetForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || hwnd == GetDesktopWindow() || hwnd == GetShellWindow())
            {
                return string.Empty;
            }

            var buffer = new char[64];
            var len = GetClassName(hwnd, buffer, buffer.Length);
            var className = len > 0 ? new string(buffer, 0, len) : string.Empty;

            // Shell / desktop classes
            if (className.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId)
            {
                return string.Empty;
            }

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static List<string> GetRunningProcessNames()
    {
        var names = new List<string>();
        try
        {
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    names.Add(p.ProcessName);
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        return names;
    }
}
