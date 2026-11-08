using System;
using System.Runtime.InteropServices;

namespace LiveWallpaper.Native;

public static class DesktopIconManager
{
    private const uint WM_COMMAND = 0x0111;
    private const int TOGGLE_DESKTOP_ICONS_CMD = 0x7402;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private static bool? _originalVisibility;
    private static readonly object _lock = new();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public static IntPtr FindShellDefView()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero) return defView;
        }

        IntPtr foundDefView = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var def = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (def != IntPtr.Zero)
            {
                foundDefView = def;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return foundDefView;
    }

    public static IntPtr FindSysListView32()
    {
        var defView = FindShellDefView();
        if (defView == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    public static bool AreIconsVisible()
    {
        var listView = FindSysListView32();
        if (listView == IntPtr.Zero)
        {
            // If window cannot be located, assume visible
            return true;
        }

        return IsWindowVisible(listView);
    }

    public static void SetIconsVisible(bool visible)
    {
        lock (_lock)
        {
            if (!_originalVisibility.HasValue)
            {
                _originalVisibility = AreIconsVisible();
            }

            var current = AreIconsVisible();
            if (current != visible)
            {
                ToggleDesktopIcons();
            }
        }
    }

    public static void ToggleDesktopIcons()
    {
        var defView = FindShellDefView();
        if (defView != IntPtr.Zero)
        {
            // Timed: a synchronous SendMessage into Explorer can deadlock exit while a
            // low-level mouse hook / WorkerW rebuild is still settling.
            _ = SendMessageTimeout(
                defView,
                WM_COMMAND,
                new IntPtr(TOGGLE_DESKTOP_ICONS_CMD),
                IntPtr.Zero,
                SMTO_ABORTIFHUNG,
                500,
                out _);
        }
    }

    public static void RestoreOriginalState()
    {
        lock (_lock)
        {
            if (_originalVisibility.HasValue)
            {
                var current = AreIconsVisible();
                if (current != _originalVisibility.Value)
                {
                    ToggleDesktopIcons();
                }
                _originalVisibility = null;
            }
        }
    }
}
