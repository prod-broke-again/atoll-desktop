using System.Diagnostics;

namespace TaskbarSpike;

internal static class Program
{
    private const string MutexName = @"Local\TaskbarSpike";
    private static int _exitRequested;

    [STAThread]
    public static void Main(string[] args)
    {
        if (IsWatchdogRequest(args))
        {
            if (!TryReadWatchdogPid(args, out var parentPid))
            {
                Console.WriteLine("Watchdog mode requires a parent process id.");
                return;
            }

            RunWatchdog(parentPid);
            return;
        }

        if (args.Any(arg => string.Equals(arg, "--status", StringComparison.OrdinalIgnoreCase)))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine(NetworkStatus.Describe());
            Console.WriteLine(AudioDevices.Describe());
            Console.WriteLine(SearchCatalog.Describe());
            Console.WriteLine(StartMenu.Describe());
            Console.WriteLine(VirtualDesktops.Describe());
            return;
        }

        if (TryGetSmokeTarget(args, out var smokeTarget))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunTraySmoke(smokeTarget);
            return;
        }

        using var mutex = new Mutex(false, MutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                Console.WriteLine("TaskbarSpike is already running. The Windows taskbar was not touched.");
                return;
            }

            var useAppBar = args.Any(arg => string.Equals(arg, "--appbar", StringComparison.OrdinalIgnoreCase));
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("TaskbarSpike hides the Windows 11 taskbar with ShowWindow only.");
            Console.WriteLine("Exit: dock button, close this console, or Win+Alt+Backspace.");
            Console.WriteLine("If the spike hangs, run restore_taskbar.bat next to the exe.");
            if (useAppBar)
            {
                Console.WriteLine("AppBar reservation is ON (--appbar). Expect a double gap while Explorer's own bar stays registered.");
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => TaskbarGuard.Restore();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => TaskbarGuard.Restore();
            Application.ApplicationExit += (_, _) => TaskbarGuard.Restore();
            Application.ThreadException += (_, eventArgs) =>
            {
                Console.WriteLine(eventArgs.Exception);
                RequestExit();
            };
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                RequestExit();
            };

            StartWatchdog();

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            try
            {
                TrayCatalog.Warm();
                NotificationCounts.Warm();
                TaskbarGuard.Hide();
                if (TrayHost.Start())
                {
                    TrayHost.BroadcastTaskbarCreated();
                }

                InputLayout.Observe();
                Application.Run(new DockForm(useAppBar));
            }
            finally
            {
                TaskbarGuard.Restore();
            }
        }
        finally
        {
            if (ownsMutex)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Already released during process teardown.
                }
            }
        }
    }

    private static bool TryGetSmokeTarget(string[] args, out string target)
    {
        target = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--tray-smoke", StringComparison.OrdinalIgnoreCase))
            {
                target = i + 1 < args.Length ? args[i + 1] : string.Empty;
                return true;
            }
        }

        return false;
    }

    /// <summary>Hosts the tray without touching the taskbar, lists icons, right-clicks the match.</summary>
    private static void RunTraySmoke(string target)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        TrayHost.Verbose = true;
        if (!TrayHost.Start())
        {
            return;
        }

        try
        {
            TrayHost.BroadcastTaskbarCreated();
            TrayHostedIcon? match = null;
            var deadline = Environment.TickCount64 + 15000;
            while (Environment.TickCount64 < deadline)
            {
                Application.DoEvents();
                match = TrayHost.Snapshot().FirstOrDefault(i =>
                    i.Tip.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                    i.Path.Contains(target, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    break;
                }

                Thread.Sleep(50);
            }

            foreach (var icon in TrayHost.Snapshot())
            {
                Console.WriteLine($"icon hwnd={icon.HWnd} uid={icon.Uid} cb=0x{icon.CallbackMessage:X} v={icon.Version} tip='{icon.Tip}' path={icon.Path}");
            }

            if (match == null)
            {
                Console.WriteLine($"No icon matching '{target}'.");
                return;
            }

            var before = new HashSet<IntPtr>();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (NativeMethods.IsWindowVisible(hwnd))
                {
                    before.Add(hwnd);
                }

                return true;
            }, IntPtr.Zero);
            Console.WriteLine(Environment.GetEnvironmentVariable("TRAY_LEFT") == "1" ? $"LeftClick -> {match.LeftClick()}" : $"RightClick -> {match.RightClick()}");
            var end = Environment.TickCount64 + 3000;
            var appeared = new List<string>();
            while (Environment.TickCount64 < end && appeared.Count == 0)
            {
                Application.DoEvents();
                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    if (!before.Contains(hwnd) && NativeMethods.IsWindowVisible(hwnd) && hwnd != TrayHost.Handle)
                    {
                        appeared.Add($"{hwnd} class={NativeMethods.GetClass(hwnd)}");
                    }

                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(50);
            }

            Console.WriteLine(appeared.Count > 0 ? "NEW WINDOWS: " + string.Join("; ", appeared) : "NO NEW WINDOW");
            Thread.Sleep(1500);
        }
        finally
        {
            TrayHost.Stop();
            TrayHost.BroadcastTaskbarCreated();
        }
    }

    private static void RequestExit()
    {
        TaskbarGuard.Restore();
        if (Interlocked.Exchange(ref _exitRequested, 1) == 1)
        {
            return;
        }

        try
        {
            Application.Exit();
        }
        catch
        {
            // The message loop may already be gone.
        }
    }

    private static bool IsWatchdogRequest(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--watchdog", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadWatchdogPid(string[] args, out int pid)
    {
        pid = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--watchdog", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out pid) &&
                pid > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void RunWatchdog(int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            while (!parent.HasExited)
            {
                parent.WaitForExit(1000);
            }
        }
        catch (ArgumentException)
        {
            // Parent already exited.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[watchdog] {ex.Message}");
        }

        if (!AnotherSpikeOwnsMutex())
        {
            TaskbarGuard.Restore();
        }
    }

    private static bool AnotherSpikeOwnsMutex()
    {
        using var mutex = new Mutex(false, MutexName);
        try
        {
            if (!mutex.WaitOne(0))
            {
                return true;
            }

            mutex.ReleaseMutex();
            return false;
        }
        catch (AbandonedMutexException)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The abandoned mutex was already released.
            }

            return false;
        }
    }

    private static void StartWatchdog()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Console.WriteLine("[spike] Watchdog was not started: process path is unknown.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--watchdog {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] Watchdog failed to start: {ex.Message}");
        }
    }
}
