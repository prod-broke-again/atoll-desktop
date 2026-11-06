namespace TaskbarSpike;

/// <summary>
/// Hides and restores the shell taskbar with ShowWindow only.
/// Restore is idempotent and, once requested, further hides are refused.
/// </summary>
internal static class TaskbarGuard
{
    private static readonly object Gate = new();
    private static bool _restoreRequested;
    private static bool _restored;
    private static Rectangle _primaryTray = Rectangle.Empty;

    public static Rectangle PrimaryTrayBounds
    {
        get
        {
            lock (Gate)
            {
                return _primaryTray;
            }
        }
    }

    public static void Hide()
    {
        lock (Gate)
        {
            if (_restoreRequested)
            {
                return;
            }

            var trays = FindTrays();
            if (_primaryTray.IsEmpty)
            {
                foreach (var hwnd in trays)
                {
                    if (!GetClass(hwnd).Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (NativeMethods.GetWindowRect(hwnd, out var rect) && rect.Width > 0 && rect.Height > 0)
                    {
                        _primaryTray = rect.ToRectangle();
                        break;
                    }
                }
            }

            foreach (var hwnd in trays)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
            }
        }
    }

    public static void Restore()
    {
        lock (Gate)
        {
            _restoreRequested = true;
            if (_restored)
            {
                return;
            }

            foreach (var hwnd in FindTrays())
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SwShow);
            }

            _restored = true;
        }

        // Hand notify icons back to explorer's tray.
        TrayHost.Stop();
        TrayHost.BroadcastTaskbarCreated();
    }

    private static List<IntPtr> FindTrays()
    {
        var trays = new List<IntPtr>();
        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            var className = GetClass(hwnd);
            if (className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
            {
                // Skip our own TrayHost sink: it must stay hidden.
                if (TrayHost.Handle != hwnd)
                {
                    trays.Add(hwnd);
                }
            }

            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return trays;
    }

    private static string GetClass(IntPtr hwnd)
    {
        try
        {
            return NativeMethods.GetClass(hwnd);
        }
        catch
        {
            return string.Empty;
        }
    }
}
