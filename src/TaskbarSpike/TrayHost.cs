using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSpike;

/// <summary>
/// Hosts our own hidden "Shell_TrayWnd" so applications deliver their notify-icon
/// registrations (WM_COPYDATA / SHELLTRAYDATA) to us. This is the same technique
/// ManagedShell (Cairo) uses on Windows 11, where the classic SysPager toolbar is
/// gone and there is no supported way to read the icon list from explorer.
/// AppBar messages (COPYDATA dwData 0) are forwarded to the real explorer tray so
/// SHAppBarMessage keeps working; handled icon registrations stay with us.
/// </summary>
internal static class TrayHost
{
    private const string TrayClassName = "Shell_TrayWnd";
    private const uint TrayDataSignature = 0x34753423;
    private const int WmCopyData = 0x004A;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmCommand = 0x0111;
    private const int WmActivateApp = 0x001C;
    private const int WmUser = 0x0400;
    private const int SwpShowWindow = 0x0040;
    private const int GwlStyle = -16;
    private const int WsVisible = 0x10000000;

    // NIM codes
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;

    // NIF flags
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifState = 0x00000008;
    private const uint NifGuid = 0x00000020;

    private static readonly object Gate = new();
    private static readonly List<TrayHostedIcon> Icons = new();
    private static WndProcDelegate? _wndProc;
    private static IntPtr _hwnd;
    private static IntPtr _forwardTarget;
    private static long _forwardAt;
    private static System.Threading.Timer? _zOrderTimer;
    private static bool _started;

    public static bool Verbose { get; set; }

    public static IntPtr Handle => _hwnd;

    public static IReadOnlyList<TrayHostedIcon> Snapshot()
    {
        lock (Gate)
        {
            return Icons.ToArray();
        }
    }

    public static TrayHostedIcon? FindIcon(TrayButton button)
    {
        TrayHostedIcon? best = null;
        var bestScore = 0;
        foreach (var icon in Snapshot())
        {
            var score = icon.Score(button);
            if (score > bestScore)
            {
                best = icon;
                bestScore = score;
            }
        }

        return bestScore > 0 ? best : null;
    }

    public static bool Start()
    {
        if (_started)
        {
            return true;
        }

        _wndProc = WndProc;
        var wc = new WndClass
        {
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = Marshal.GetHINSTANCE(typeof(TrayHost).Module),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = IntPtr.Zero,
            lpszClassName = TrayClassName,
        };

        if (RegisterClass(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            Console.WriteLine($"[spike] tray host: RegisterClass failed {Marshal.GetLastWin32Error()}");
            return false;
        }

        // Same geometry as the real taskbar so callers that query the window
        // rect (FindWindow + GetWindowRect) see a sane answer. It stays hidden.
        var rect = TaskbarGuard.PrimaryTrayBounds;

        if (rect.IsEmpty)
        {
            var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            var height = SystemInformation.CaptionHeight > 40 ? 48 : 40;
            rect = new Rectangle(area.Left, area.Bottom - height, area.Width, height);
        }

        _hwnd = CreateWindowEx(
            NativeMethods.WsExToolWindow | NativeMethods.WsExTopmost | 0x08000000, // + WS_EX_NOACTIVATE
            TrayClassName,
            string.Empty,
            unchecked((int)0x80000000), // WS_POPUP, not visible
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            IntPtr.Zero,
            IntPtr.Zero,
            wc.hInstance,
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"[spike] tray host: CreateWindowEx failed {Marshal.GetLastWin32Error()}");
            return false;
        }

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);

        // Keep our Shell_TrayWnd first in z-order so apps' FindWindow picks us.
        _zOrderTimer = new System.Threading.Timer(_ =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0,
                    NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
            }
        }, null, 2000, 2000);

        _started = true;
        Console.WriteLine($"[spike] tray host: Shell_TrayWnd={_hwnd}");
        return true;
    }

    public static void Stop()
    {
        _started = false;
        _zOrderTimer?.Dispose();
        _zOrderTimer = null;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        UnregisterClass(TrayClassName, Marshal.GetHINSTANCE(typeof(TrayHost).Module));
    }

    public static void BroadcastTaskbarCreated()
    {
        var message = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        if (message != 0)
        {
            SendNotifyMessage(new IntPtr(0xffff), message, UIntPtr.Zero, IntPtr.Zero); // HWND_BROADCAST
            // HWND_BROADCAST skips message-only windows (Chromium/Electron status trays,
            // many background helpers). Deliver TaskbarCreated to them directly.
            var parent = new IntPtr(-3); // HWND_MESSAGE
            var child = IntPtr.Zero;
            while ((child = FindWindowEx(parent, child, null, null)) != IntPtr.Zero)
            {
                SendNotifyMessage(child, message, UIntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmCopyData && lParam != IntPtr.Zero)
        {
            var copy = Marshal.PtrToStructure<CopyDataStruct>(lParam);
            if (copy.dwData == 1 && copy.lpData != IntPtr.Zero)
            {
                var handled = HandleTrayData(copy.lpData);
                return handled ? new IntPtr(1) : IntPtr.Zero;
            }

            if (copy.dwData == 3 && copy.lpData != IntPtr.Zero)
            {
                return HandleIconLocationQuery(copy.lpData);
            }

            // dwData 0 = appbar message and anything else: pass to explorer.
            return ForwardToExplorer(hWnd, msg, wParam, lParam);
        }

        if (msg == WmWindowPosChanged && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam);
            if ((pos.flags & SwpShowWindow) != 0)
            {
                // Never let the sink become visible: it is a message-only host.
                SetWindowLong(hWnd, GwlStyle, GetWindowLong(hWnd, GwlStyle) & ~WsVisible);
            }
        }

        if (msg == WmCommand || msg == WmActivateApp || msg >= WmUser)
        {
            return ForwardToExplorer(hWnd, msg, wParam, lParam);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static bool HandleTrayData(IntPtr pData)
    {
        ShellTrayData data;
        try
        {
            data = Marshal.PtrToStructure<ShellTrayData>(pData);
        }
        catch
        {
            return false;
        }

        if (data.dwSignature != TrayDataSignature)
        {
            return false;
        }

        var nid = data.nid;
        var hwnd = new IntPtr((nint)(uint)nid.hWnd);
        if (Verbose)
        {
            Console.WriteLine($"[tray] NIM={data.dwMessage} hwnd={hwnd} uid={nid.uID} flags=0x{nid.uFlags:X} cb=0x{nid.uCallbackMessage:X} ver={nid.uTimeoutOrVersion} guid={(nid.guidItem == Guid.Empty ? "-" : nid.guidItem.ToString())} tip='{nid.szTip}'");
        }

        lock (Gate)
        {
            switch (data.dwMessage)
            {
                case NimAdd:
                case NimModify:
                    {
                        var icon = Icons.FirstOrDefault(i => i.Matches(hwnd, nid.uID, nid.guidItem, (nid.uFlags & NifGuid) != 0));
                        if (icon == null)
                        {
                            if (hwnd == IntPtr.Zero)
                            {
                                return false;
                            }

                            icon = new TrayHostedIcon();
                            Icons.Add(icon);
                        }

                        icon.Apply(hwnd, nid.uID, nid.uFlags, nid.uCallbackMessage, nid.guidItem, nid.szTip, nid.dwState, nid.dwStateMask, nid.uTimeoutOrVersion, isAdd: data.dwMessage == NimAdd);
                        return true;
                    }

                case NimDelete:
                    {
                        var removed = Icons.RemoveAll(i => i.Matches(hwnd, nid.uID, nid.guidItem, (nid.uFlags & NifGuid) != 0)) > 0;
                        return removed;
                    }

                case NimSetVersion:
                    {
                        if (nid.uTimeoutOrVersion > 4)
                        {
                            return false;
                        }

                        var icon = Icons.FirstOrDefault(i => i.Matches(hwnd, nid.uID, nid.guidItem, (nid.uFlags & NifGuid) != 0));
                        if (icon == null)
                        {
                            return false;
                        }

                        icon.Version = nid.uTimeoutOrVersion;
                        return true;
                    }

                default:
                    return false;
            }
        }
    }

    private static IntPtr HandleIconLocationQuery(IntPtr pData)
    {
        // WINNOTIFYICONIDENTIFIER: dwMessage(1/2), hWnd, uID, guidItem.
        // Answer with our host rect so flyout anchoring has something sane.
        uint dwMessage = (uint)Marshal.ReadInt32(pData);
        if (dwMessage != 1 && dwMessage != 2)
        {
            return IntPtr.Zero;
        }

        if (!NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return IntPtr.Zero;
        }

        var lo = (ushort)(dwMessage == 1 ? rect.Left : rect.Right);
        var hi = (ushort)(dwMessage == 1 ? rect.Top : rect.Bottom);
        return new IntPtr((int)((uint)lo | ((uint)hi << 16)));
    }

    private static IntPtr ForwardToExplorer(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var target = ResolveForwardTarget();
        if (target == IntPtr.Zero)
        {
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        return SendMessage(target, msg, wParam, lParam);
    }

    private static IntPtr ResolveForwardTarget()
    {
        if (_forwardTarget != IntPtr.Zero && IsWindow(_forwardTarget) && Environment.TickCount64 - _forwardAt < 3000)
        {
            return _forwardTarget;
        }

        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == _hwnd)
            {
                return true;
            }

            if (!NativeMethods.GetClass(hwnd).Equals(TrayClassName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        _forwardTarget = found;
        _forwardAt = Environment.TickCount64;
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public UIntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    // 32-bit packed cross-bitness wire format used by Shell_NotifyIcon.
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct NotifyIconData32
    {
        public uint cbSize;
        public uint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public uint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public uint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct ShellTrayData
    {
        public uint dwSignature;
        public uint dwMessage;
        public NotifyIconData32 nid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);
}

/// <summary>
/// A live notify icon registered with our tray host. Knows how to replay the
/// exact message sequence explorer.exe sends on a right click.
/// </summary>
internal sealed class TrayHostedIcon
{
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;

    private string? _path;

    public IntPtr HWnd { get; private set; }
    public uint Uid { get; private set; }
    public uint CallbackMessage { get; private set; }
    public uint Version { get; set; }
    public Guid Guid { get; private set; }
    public string Tip { get; private set; } = string.Empty;
    public bool Hidden { get; private set; }

    internal bool Matches(IntPtr hwnd, uint uid, Guid guid, bool useGuid)
    {
        if (useGuid && Guid != Guid.Empty && Guid == guid)
        {
            return true;
        }

        return HWnd == hwnd && Uid == uid;
    }

    internal void Apply(IntPtr hwnd, uint uid, uint flags, uint callbackMessage, Guid guid, string tip,
        uint state, uint stateMask, uint timeoutOrVersion, bool isAdd)
    {
        HWnd = hwnd;
        Uid = uid;
        _path = null;
        if ((flags & 0x1) != 0)
        {
            CallbackMessage = callbackMessage;
        }

        if ((flags & 0x20) != 0)
        {
            Guid = guid;
        }

        if ((flags & 0x4) != 0)
        {
            Tip = tip ?? string.Empty;
        }

        if ((flags & 0x8) != 0 && (stateMask & 1) != 0)
        {
            Hidden = (state & 1) != 0;
        }

        // NIM_ADD always starts at legacy version 0 until NIM_SETVERSION arrives.
        if (isAdd)
        {
            Version = 0;
        }
    }

    public string Path
    {
        get
        {
            if (_path != null)
            {
                return _path;
            }

            _path = ResolvePath();
            return _path;
        }
    }

    public int Score(TrayButton button)
    {
        if (HWnd == IntPtr.Zero)
        {
            return 0;
        }

        var path = Path;
        if (!string.IsNullOrEmpty(path) &&
            !string.IsNullOrWhiteSpace(button.ExecutablePath) &&
            path.Equals(button.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        return ScoreName(button.Name, Tip);
    }

    public bool IsAlive() => HWnd != IntPtr.Zero && IsWindow(HWnd);

    public bool RightClick()
    {
        if (!IsAlive() || CallbackMessage == 0)
        {
            return false;
        }

        var pt = Cursor.Position;
        HandoffForeground();
        Send(WmRButtonDown, pt);
        Send(WmRButtonUp, pt);
        if (Version >= 3)
        {
            Send(WmContextMenu, pt);
        }

        return true;
    }

    public bool LeftClick()
    {
        if (!IsAlive() || CallbackMessage == 0)
        {
            return false;
        }

        var pt = Cursor.Position;
        HandoffForeground();
        Send(WmLButtonDown, pt);
        Send(WmLButtonUp, pt);
        if (Version >= 3)
        {
            Send(NinSelect, pt);
        }

        return true;
    }

    private void Send(uint mouseMessage, Point pt)
    {
        UIntPtr wParam;
        IntPtr lParam;
        if (Version >= 4)
        {
            wParam = (UIntPtr)(uint)((ushort)pt.X | ((uint)(ushort)pt.Y << 16));
            lParam = (IntPtr)(uint)(mouseMessage | (Uid << 16));
        }
        else
        {
            wParam = (UIntPtr)Uid;
            lParam = (IntPtr)(uint)mouseMessage;
        }

        var ok = SendNotifyMessageW(HWnd, CallbackMessage, wParam, lParam);
        if (TrayHost.Verbose)
        {
            Console.WriteLine($"[tray] send 0x{mouseMessage:X} -> {HWnd} ok={ok} err={(ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error())}");
        }
    }

    private void HandoffForeground()
    {
        GetWindowThreadProcessId(HWnd, out var pid);
        if (pid != 0)
        {
            AllowSetForegroundWindow(pid);
        }

        // Our UI thread usually holds the foreground lock (the user just clicked
        // our dock). Attach to the target's input queue so SetForegroundWindow
        // succeeds, exactly like explorer does before forwarding a click.
        var targetThread = GetWindowThreadProcessId(HWnd, out _);
        var ourThread = NativeMethods.GetCurrentThreadId();
        if (targetThread != 0 && targetThread != ourThread)
        {
            AttachThreadInput(ourThread, targetThread, true);
            SetForegroundWindow(HWnd);
            AttachThreadInput(ourThread, targetThread, false);
        }
    }

    private string ResolvePath()
    {
        try
        {
            GetWindowThreadProcessId(HWnd, out var pid);
            if (pid == 0)
            {
                return string.Empty;
            }

            var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == IntPtr.Zero)
            {
                // Elevated owner: we cannot query it. Process name is the best we have.
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName;
            }

            try
            {
                var builder = new StringBuilder(1024);
                uint size = 1024;
                return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString() : string.Empty;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ScoreName(string buttonName, string tip)
    {
        string Fold(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var ours = Fold(buttonName ?? string.Empty);
        var shell = Fold(tip ?? string.Empty);
        if (ours.Length == 0 || shell.Length == 0)
        {
            return 0;
        }

        if (ours == shell)
        {
            return 90;
        }

        if (shell.StartsWith(ours, StringComparison.Ordinal) || ours.StartsWith(shell, StringComparison.Ordinal))
        {
            return 80;
        }

        if (ours.Length >= 4 && shell.Contains(ours, StringComparison.Ordinal))
        {
            return 70;
        }

        return shell.Length >= 4 && ours.Contains(shell, StringComparison.Ordinal) ? 60 : 0;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SendNotifyMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
