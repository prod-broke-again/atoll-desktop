using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiveWallpaper.Native;

public sealed class MouseHookService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const uint WM_QUIT = 0x0012;

    private readonly object _lock = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelMouseProc? _mouseProc;
    private long _lastReportedTicks;
    private int _lastX = -1;
    private int _lastY = -1;
    private bool _disposed;
    /// <summary>
    /// When true, button/wheel events are delivered to subscribers but not passed to Explorer —
    /// used while the pointer is over a custom wallpaper widget so Windows' desktop menu does not open.
    /// </summary>
    private volatile bool _consumeInput;

    /// <summary>
    /// Optional gate: return false to still deliver the event to Explorer (e.g. click landed on a TopMost prompt).
    /// Invoked on the hook thread — must be fast and lock-free.
    /// </summary>
    public Func<int, int, bool>? ShouldConsumeAt { get; set; }

    public event EventHandler<MouseCoordinates>? MouseMoved;
    public event EventHandler<MouseButtonCoordinates>? MouseDown;
    public event EventHandler<MouseButtonCoordinates>? MouseUp;
    public event EventHandler<MouseWheelCoordinates>? MouseWheel;

    public void SetConsumeInput(bool consume) => _consumeInput = consume;

    private bool OnDesktop(int x, int y)
    {
        var gate = ShouldConsumeAt;
        return gate == null || gate(x, y);
    }

    /// <summary>Left/wheel: only while web requested capture over our chrome.</summary>
    private bool ShouldBlock(int x, int y) => _consumeInput && OnDesktop(x, y);

    /// <summary>
    /// Right button: always swallow on the desktop wallpaper zone so Explorer never opens
    /// its context menu there — the web side shows our custom menu instead.
    /// </summary>
    private bool ShouldBlockRight(int x, int y) => OnDesktop(x, y);

    public void Start()
    {
        lock (_lock)
        {
            if (_hookThread != null) return;

            var readyEvent = new ManualResetEventSlim(false);

            _hookThread = new Thread(() =>
            {
                _hookThreadId = GetCurrentThreadId();
                _mouseProc = HookProcedure;

                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule!;
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(curModule.ModuleName), 0);

                readyEvent.Set();

                // Run dedicated Win32 message loop
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                UnhookIfNeeded();
            })
            {
                IsBackground = true,
                Name = "LiveWallpaper_MouseHookThread"
            };

            _hookThread.Start();
            readyEvent.Wait(2000);
        }
    }

    private IntPtr HookProcedure(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_disposed) return CallNextHookEx(_hookId, nCode, wParam, lParam);

            if (nCode >= 0)
            {
                var msg = wParam.ToInt32();
                if (msg == WM_MOUSEMOVE)
                {
                    var now = Stopwatch.GetTimestamp();
                    // Throttle to max 60Hz (~16ms)
                    var elapsedMs = (now - _lastReportedTicks) * 1000 / Stopwatch.Frequency;
                    if (elapsedMs >= 16)
                    {
                        var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        if (hookStruct.pt.x != _lastX || hookStruct.pt.y != _lastY)
                        {
                            _lastX = hookStruct.pt.x;
                            _lastY = hookStruct.pt.y;
                            _lastReportedTicks = now;
                            MouseMoved?.Invoke(this, new MouseCoordinates(_lastX, _lastY));
                        }
                    }
                }
                else if (msg == WM_LBUTTONDOWN)
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    MouseDown?.Invoke(this, new MouseButtonCoordinates(hookStruct.pt.x, hookStruct.pt.y, "left"));
                    if (ShouldBlock(hookStruct.pt.x, hookStruct.pt.y)) return (IntPtr)1;
                }
                else if (msg == WM_LBUTTONUP)
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    MouseUp?.Invoke(this, new MouseButtonCoordinates(hookStruct.pt.x, hookStruct.pt.y, "left"));
                    if (ShouldBlock(hookStruct.pt.x, hookStruct.pt.y)) return (IntPtr)1;
                }
                else if (msg == WM_RBUTTONDOWN)
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    MouseDown?.Invoke(this, new MouseButtonCoordinates(hookStruct.pt.x, hookStruct.pt.y, "right"));
                    if (ShouldBlockRight(hookStruct.pt.x, hookStruct.pt.y)) return (IntPtr)1;
                }
                else if (msg == WM_RBUTTONUP)
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    MouseUp?.Invoke(this, new MouseButtonCoordinates(hookStruct.pt.x, hookStruct.pt.y, "right"));
                    if (ShouldBlockRight(hookStruct.pt.x, hookStruct.pt.y)) return (IntPtr)1;
                }
                else if (msg == WM_MOUSEWHEEL)
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    // High word of mouseData is the signed wheel delta (multiples of 120).
                    var delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                    MouseWheel?.Invoke(this, new MouseWheelCoordinates(hookStruct.pt.x, hookStruct.pt.y, delta));
                    if (ShouldBlock(hookStruct.pt.x, hookStruct.pt.y)) return (IntPtr)1;
                }
            }
        }
        catch { }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        // Drop callbacks first so a late hook invocation cannot re-enter UI during exit.
        ShouldConsumeAt = null;
        MouseMoved = null;
        MouseDown = null;
        MouseUp = null;
        MouseWheel = null;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            // Unhook before joining. If Join times out, the global LL hook must not stay installed —
            // that freezes mouse input for the whole desktop.
            UnhookIfNeeded();

            if (_hookThreadId != 0)
            {
                PostThreadMessage(_hookThreadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            }

            if (_hookThread != null && _hookThread.IsAlive)
            {
                _hookThread.Join(500);
            }

            _mouseProc = null;
            _hookThread = null;
            _hookThreadId = 0;
        }
    }

    private void UnhookIfNeeded()
    {
        if (_hookId == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(_hookId); } catch { }
        _hookId = IntPtr.Zero;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG lpmsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, UIntPtr wParam, IntPtr lParam);
}

public readonly record struct MouseCoordinates(int X, int Y);
public readonly record struct MouseButtonCoordinates(int X, int Y, string Button);
public readonly record struct MouseWheelCoordinates(int X, int Y, int Delta);
