using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LiveWallpaper.Native;

public static class FullscreenDetector
{
    public enum QueryUserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7
    }

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern int SHQueryUserNotificationState(out QueryUserNotificationState pquns);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;

    /// <summary>Human-readable reason of the last positive detection (for logs).</summary>
    public static string LastReason { get; private set; } = string.Empty;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static bool IsAnyApplicationFullscreen()
    {
        LastReason = string.Empty;
        var fgHwnd = GetForegroundWindow();
        var fgInfo = Describe(fgHwnd);

        // 1. Exclusive full-screen (DirectX/Vulkan) and presentation mode are always trusted.
        if (SHQueryUserNotificationState(out var state) == 0)
        {
            if (state == QueryUserNotificationState.RunningD3dFullScreen ||
                state == QueryUserNotificationState.PresentationMode)
            {
                LastReason = $"{state}; foreground: {fgInfo}";
                return true;
            }
        }

        if (fgHwnd == IntPtr.Zero || fgHwnd == GetDesktopWindow() || fgHwnd == GetShellWindow())
        {
            return false;
        }

        // Never pause because of our own windows (settings dialog, wallpaper host).
        _ = GetWindowThreadProcessId(fgHwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        var buffer = new char[64];
        var len = GetClassName(fgHwnd, buffer, buffer.Length);
        var className = len > 0 ? new string(buffer, 0, len) : string.Empty;

        if (className.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 2. Borderless full-screen: a window WITHOUT a caption that covers its whole monitor.
        //    Maximized regular windows (they overhang the screen by the frame width) have a caption
        //    and must not pause the wallpaper. SHQUNS "Busy" alone is not trusted for the same reason.
        var style = GetWindowLongPtr(fgHwnd, GWL_STYLE).ToInt64();
        if ((style & WS_CAPTION) == WS_CAPTION)
        {
            return false;
        }

        if (GetWindowRect(fgHwnd, out var rect))
        {
            var bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            var screen = Screen.FromHandle(fgHwnd);

            if (bounds.Left <= screen.Bounds.Left &&
                bounds.Top <= screen.Bounds.Top &&
                bounds.Right >= screen.Bounds.Right &&
                bounds.Bottom >= screen.Bounds.Bottom)
            {
                LastReason = $"borderless fullscreen; foreground: {fgInfo}";
                return true;
            }
        }

        return false;
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        try
        {
            var buf = new char[64];
            var n = GetClassName(hwnd, buf, buf.Length);
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            string proc;
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); proc = p.ProcessName; }
            catch { proc = pid.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            return $"{proc} [{(n > 0 ? new string(buf, 0, n) : "?")}]";
        }
        catch
        {
            return "?";
        }
    }
}
