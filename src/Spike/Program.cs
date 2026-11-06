using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace LiveWallpaper.Spike;

public static class Program
{
    private static IntPtr _hookId = IntPtr.Zero;
    private static LowLevelMouseProc? _mouseProc;
    private static Form? _mainForm;
    private static Microsoft.Web.WebView2.WinForms.WebView2? _webView;

    [STAThread]
    public static void Main(string[] args)
    {
        var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "livewallpaper_spike_m0.log");
        void Log(string msg)
        {
            Console.WriteLine(msg);
            try { System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n"); } catch { }
        }

        try
        {
            Log("=================================================");
            Log(" LiveWallpaper Spike M0: Windows 11 Discovery");
            Log($" OS: {Environment.OSVersion.Version}");
            Log("=================================================");

            if (NativeMethods.FindWindow("Progman", null) == IntPtr.Zero && !args.Contains("--child-desktop"))
            {
                Log("[Locator] Running in isolated desktop. Spawning process on winsta0\\default...");
                var ok = DesktopLocator.SpawnOnDefaultDesktop();
                Log($"[Locator] SpawnOnDefaultDesktop returned: {ok}. Exiting parent process.");
                return;
            }
            else
            {
                Log("[Locator] On interactive desktop. Running application...");
                RunApplication();
            }
        }
        catch (Exception ex)
        {
            Log($"[FATAL] Unhandled in Main: {ex}");
        }
    }

    private static void RunApplication()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var targetWorkerW = DesktopLocator.FindWallpaperParent();
        if (targetWorkerW == IntPtr.Zero)
        {
            Console.WriteLine("[ERROR] Failed to locate wallpaper parent window behind icons!");
            return;
        }

        Console.WriteLine($"[SUCCESS] Target Wallpaper Parent HWND: 0x{targetWorkerW.ToInt64():X}");

        _mainForm = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080),
            ShowInTaskbar = false
        };

        _webView = new Microsoft.Web.WebView2.WinForms.WebView2
        {
            Dock = DockStyle.Fill
        };
        _mainForm.Controls.Add(_webView);

        _mainForm.Shown += async (s, e) =>
        {
            try
            {
                Console.WriteLine($"[Spike] Form Shown. Form Handle: 0x{_mainForm.Handle.ToInt64():X}");

                var userDataPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LiveWallpaperSpike_UserData");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: userDataPath);
                
                await _webView.EnsureCoreWebView2Async(env);
                Console.WriteLine("[INFO] CoreWebView2 initialized via WinForms control!");
                
                var html = @"
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'/>
<style>
  body, html { margin:0; padding:0; width:100%; height:100%; overflow:hidden; background:#0b0e14; font-family:'Segoe UI', sans-serif; color:white; }
  #canvas { position:absolute; top:0; left:0; width:100%; height:100%; }
  .widget { position:absolute; top:50px; right:70px; background:rgba(20,25,35,0.75); backdrop-filter:blur(20px); padding:28px 36px; border-radius:24px; border:1px solid rgba(255,255,255,0.12); box-shadow:0 24px 48px rgba(0,0,0,0.5); }
  .time { font-size:4.2rem; font-weight:700; line-height:1; letter-spacing:-1px; }
  .date { font-size:1.15rem; color:#8e9aa8; margin-top:10px; }
  .info { position:absolute; bottom:50px; left:70px; background:rgba(20,25,35,0.65); backdrop-filter:blur(14px); padding:18px 28px; border-radius:18px; border:1px solid rgba(255,255,255,0.1); font-size:1rem; }
  .info span { color:#00e5ff; font-weight:600; }
  .pulse { display:inline-block; width:10px; height:10px; border-radius:50%; background:#00e5ff; margin-right:8px; box-shadow:0 0 12px #00e5ff; }
</style>
</head>
<body>
<canvas id='canvas'></canvas>
<div class='widget'>
  <div id='time' class='time'>00:00:00</div>
  <div id='date' class='date'>Connecting...</div>
</div>
<div class='info'>
  <span class='pulse'></span> <b>LiveWallpaper Engine M0 Spike</b> | Status: <span>Live behind desktop icons</span> | Mouse: <span id='mousePos'>0, 0</span>
</div>
<script>
  function updateTime() {
    const now = new Date();
    document.getElementById('time').textContent = now.toLocaleTimeString();
    document.getElementById('date').textContent = now.toLocaleDateString(undefined, { weekday:'long', year:'numeric', month:'long', day:'numeric' });
  }
  setInterval(updateTime, 1000);
  updateTime();

  const canvas = document.getElementById('canvas');
  const ctx = canvas.getContext('2d');
  let w, h;
  function resize() { w = canvas.width = window.innerWidth; h = canvas.height = window.innerHeight; }
  window.addEventListener('resize', resize);
  resize();

  const particles = Array.from({length: 80}, () => ({
    x: Math.random() * w, y: Math.random() * h,
    vx: (Math.random() - 0.5) * 0.9, vy: (Math.random() - 0.5) * 0.9,
    r: Math.random() * 2.5 + 1
  }));

  let mouseX = w/2, mouseY = h/2;
  window.chrome.webview.addEventListener('message', e => {
    try {
      const data = e.data;
      if (data && data.type === 'mouse') {
        mouseX = data.x; mouseY = data.y;
        document.getElementById('mousePos').textContent = `${mouseX}, ${mouseY}`;
      }
    } catch {}
  });

  function draw() {
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = '#0b0e14';
    ctx.fillRect(0, 0, w, h);

    const grad = ctx.createRadialGradient(mouseX, mouseY, 10, mouseX, mouseY, 350);
    grad.addColorStop(0, 'rgba(0, 229, 255, 0.16)');
    grad.addColorStop(1, 'rgba(0, 0, 0, 0)');
    ctx.fillStyle = grad;
    ctx.fillRect(0, 0, w, h);

    ctx.fillStyle = 'rgba(255, 255, 255, 0.7)';
    particles.forEach(p => {
      p.x += p.vx; p.y += p.vy;
      if (p.x < 0) p.x = w; if (p.x > w) p.x = 0;
      if (p.y < 0) p.y = h; if (p.y > h) p.y = 0;
      ctx.beginPath();
      ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
      ctx.fill();
    });
    requestAnimationFrame(draw);
  }
  draw();
</script>
</body>
</html>";
                _webView.CoreWebView2.NavigateToString(html);
                
                // Attach form behind desktop icons AFTER WebView2 controller is created
                NativeMethods.SetParent(_mainForm.Handle, targetWorkerW);
                Console.WriteLine("[INFO] Form attached behind desktop icons via SetParent!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Form Load exception: {ex}");
            }
        };

        // Install WH_MOUSE_LL hook
        _mouseProc = HookCallback;
        _hookId = SetHook(_mouseProc);

        // Auto close after 10 seconds for the spike demo, or press ESC
        var timer = new System.Windows.Forms.Timer { Interval = 10000 };
        timer.Tick += (s, e) =>
        {
            Console.WriteLine("[INFO] Spike 10-second test completed. Exiting cleanly.");
            UnhookWindowsHookEx(_hookId);
            Application.Exit();
        };
        timer.Start();

        _mainForm.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                UnhookWindowsHookEx(_hookId);
                Application.Exit();
            }
        };

        Application.Run(_mainForm);
    }

    private static IntPtr SetHook(LowLevelMouseProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
        {
            MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            try
            {
                _webView?.CoreWebView2?.PostWebMessageAsJson($"{{\"type\":\"mouse\",\"x\":{hookStruct.pt.x},\"y\":{hookStruct.pt.y}}}");
            }
            catch { }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;

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

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}

public static class DesktopLocator
{
    private const uint DESKTOP_ALL_ACCESS = 0x01FF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static bool SpawnOnDefaultDesktop()
    {
        using var curProc = Process.GetCurrentProcess();
        var exePath = curProc.MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath)) return false;

        var cmdLine = $"\"{exePath}\" --child-desktop";
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf<STARTUPINFO>();
        si.lpDesktop = @"winsta0\default";

        var success = CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out var pi);
        if (success)
        {
            Console.WriteLine($"[Locator] Spawned child on winsta0\\default (PID: {pi.dwProcessId})");
            WaitForSingleObject(pi.hProcess, 15000);
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }
        else
        {
            var err = Marshal.GetLastWin32Error();
            Console.WriteLine($"[Locator] CreateProcess failed with win32 error: {err}");
        }
        return success;
    }

    public static IntPtr FindWallpaperParent()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        Console.WriteLine($"[Locator] Found Progman HWND: 0x{progman.ToInt64():X}");

        // Send 0x052C message to spawn WorkerW
        IntPtr result = IntPtr.Zero;
        NativeMethods.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0x0002, 1000, out result);

        // Check Tier 1: Windows 11 24H2/26H1 architecture (WorkerW as direct child of Progman)
        IntPtr workerWUnderProgman = IntPtr.Zero;
        IntPtr child = IntPtr.Zero;
        while (true)
        {
            child = NativeMethods.FindWindowEx(progman, child, null, null);
            if (child == IntPtr.Zero) break;
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(child, sb, sb.Capacity);
            if (sb.ToString() == "WorkerW")
            {
                workerWUnderProgman = child;
                Console.WriteLine($"[Locator] Found WorkerW under Progman: 0x{child.ToInt64():X}");
            }
        }

        if (workerWUnderProgman != IntPtr.Zero)
        {
            Console.WriteLine($"[Locator] Using Tier 1 (Win11 24H2+): WorkerW 0x{workerWUnderProgman.ToInt64():X}");
            return workerWUnderProgman;
        }

        // Tier 2: Classic Windows 10/early 11 architecture (WorkerW as sibling top-level window)
        Console.WriteLine("[Locator] Trying Tier 2 (Classic top-level sibling)...");
        IntPtr siblingWorkerW = IntPtr.Zero;
        NativeMethods.EnumWindows((tophandle, _) =>
        {
            var p = NativeMethods.FindWindowEx(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (p != IntPtr.Zero)
            {
                siblingWorkerW = NativeMethods.FindWindowEx(IntPtr.Zero, tophandle, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);

        if (siblingWorkerW != IntPtr.Zero)
        {
            Console.WriteLine($"[Locator] Using Tier 2 (Classic Sibling): WorkerW 0x{siblingWorkerW.ToInt64():X}");
            return siblingWorkerW;
        }

        // Tier 3: Fallback to Progman itself
        Console.WriteLine($"[Locator] Using Tier 3: Progman directly 0x{progman.ToInt64():X}");
        return progman;
    }
}

public static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
}
