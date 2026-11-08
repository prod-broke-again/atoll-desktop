using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveWallpaper.Native;

public sealed class DesktopManager
{
    private const uint WM_SPAWN_WORKER = 0x052C;
    private const uint SMTO_NORMAL = 0x0000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOCOPYBITS = 0x0100;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    public IntPtr TargetWorkerW { get; private set; } = IntPtr.Zero;

    public IntPtr LocateWallpaperParent()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // Send 0x052C message to Progman to spawn WorkerW
        _ = SendMessageTimeout(progman, WM_SPAWN_WORKER, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);
        return FindExistingParent();
    }

    /// <summary>
    /// The WorkerW Explorer is using right now, without asking Progman to create another one.
    /// A color change rebuilds that window; the previous parent stays alive but is no longer shown.
    /// </summary>
    public IntPtr FindExistingParent()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // Tier 1: Windows 11 24H2 / 26H1 - WorkerW created as direct child of Progman
        IntPtr childWorkerW = IntPtr.Zero;
        IntPtr child = IntPtr.Zero;
        var buffer = new char[256];
        while (true)
        {
            child = FindWindowEx(progman, child, null, null);
            if (child == IntPtr.Zero) break;
            
            var len = GetClassName(child, buffer, buffer.Length);
            if (len > 0)
            {
                var className = new string(buffer, 0, len);
            if (className == "WorkerW")
            {
                if (IsWindowVisible(child))
                {
                    childWorkerW = child;
                    break;
                }

                if (childWorkerW == IntPtr.Zero)
                {
                    childWorkerW = child;
                }
            }
            }
        }

        if (childWorkerW != IntPtr.Zero)
        {
            TargetWorkerW = childWorkerW;
            return TargetWorkerW;
        }

        // Tier 2: Windows 10 & early Windows 11 - WorkerW as sibling top-level window
        IntPtr siblingWorkerW = IntPtr.Zero;
        EnumWindows((tophandle, _) =>
        {
            var defView = FindWindowEx(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                siblingWorkerW = FindWindowEx(IntPtr.Zero, tophandle, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);

        if (siblingWorkerW != IntPtr.Zero)
        {
            TargetWorkerW = siblingWorkerW;
            return TargetWorkerW;
        }

        // Tier 3: Fallback directly to Progman
        TargetWorkerW = progman;
        return TargetWorkerW;
    }

    public bool AttachWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        // Prefer an already-existing WorkerW; only spawn when none is present.
        if (FindExistingParent() == IntPtr.Zero)
        {
            LocateWallpaperParent();
        }

        if (TargetWorkerW == IntPtr.Zero || !IsWindow(TargetWorkerW)) return false;

        SetParent(hWnd, TargetWorkerW);
        // SWP_NOCOPYBITS: after SetParent the old surface bits would otherwise paint as an opaque white fill.
        SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOCOPYBITS);
        return true;
    }

    public static void DetachWindow(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero)
        {
            SetParent(hWnd, IntPtr.Zero);
        }
    }

    public static void RefreshDesktopWallpaper()
    {
        const uint SPI_SETDESKWALLPAPER = 0x0014;
        const uint SPIF_UPDATEINIFILE = 0x01;

        try
        {
            // No SPIF_SENDCHANGE: that broadcast is synchronous and deadlocks exit
            // while a color notification is still walking the other windows.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _ = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, null, SPIF_UPDATEINIFILE);
                }
                catch
                {
                    // The static wallpaper reload is best-effort on the way out.
                }
            });
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uAction, uint uParam, string? lpvParam, uint fuWinIni);
}
