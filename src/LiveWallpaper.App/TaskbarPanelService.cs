using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveWallpaper.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.App;

/// <summary>
/// Starts and stops TaskbarSpike from the wallpaper setting.
/// Exit is a posted hotkey so the Windows taskbar is restored. The process is not killed.
/// </summary>
public sealed class TaskbarPanelService : IHostedService
{
    private readonly IConfigManager _config;
    private readonly ILogger<TaskbarPanelService> _logger;
    private readonly object _gate = new();
    private int _ticket;
    private bool _stopping;
    private bool _missingLogged;

    public TaskbarPanelService(IConfigManager config, ILogger<TaskbarPanelService> logger)
    {
        _config = config;
        _logger = logger;
        _config.ConfigChanged += (_, cfg) => Apply(cfg.Taskbar?.Enabled == true);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        WallpaperUpdate.EnsureShortcut();
        Apply(_config.CurrentConfig.Taskbar?.Enabled == true);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        lock (_gate)
        {
            TaskbarSpikeProcess.RequestExit();
        }

        return Task.CompletedTask;
    }

    private void Apply(bool enabled)
    {
        var ticket = Interlocked.Increment(ref _ticket);
        Task.Run(() =>
        {
            lock (_gate)
            {
                if (_stopping || ticket != Volatile.Read(ref _ticket))
                {
                    return;
                }

                if (enabled)
                {
                    Start();
                }
                else
                {
                    TaskbarSpikeProcess.RequestExit();
                }
            }
        });
    }

    private void Start()
    {
        if (TaskbarSpikeProcess.IsRunning())
        {
            return;
        }

        var exe = TaskbarSpikeProcess.LocateExe();
        if (exe == null)
        {
            if (!_missingLogged)
            {
                _missingLogged = true;
                _logger.LogWarning("Taskbar panel is enabled, but TaskbarSpike.exe was not found next to the wallpaper.");
            }

            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--appbar", // flat 44 DIP bar like Windows, not the fat floating dock
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            _logger.LogInformation("Taskbar panel started.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Taskbar panel failed to start.");
        }
    }
}

internal static class TaskbarSpikeProcess
{
    public static string? LocateExe()
    {
        var process = Environment.ProcessPath;
        if (string.IsNullOrEmpty(process))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(process);
        if (dir == null)
        {
            return null;
        }

        var bundled = Path.Combine(dir, "TaskbarSpike", "TaskbarSpike.exe");
        return File.Exists(bundled) ? bundled : null;
    }

    public static bool IsRunning() => FindWindow(null, "TaskbarSpike") != IntPtr.Zero;

    public static void RequestExit()
    {
        var deadline = Environment.TickCount64 + 4000;
        var posted = false;
        while (Environment.TickCount64 < deadline)
        {
            var hwnd = FindWindow(null, "TaskbarSpike");
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            if (!posted)
            {
                PostMessage(hwnd, 0x0312, (IntPtr)1, IntPtr.Zero);
                posted = true;
            }

            Thread.Sleep(150);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

internal static class WallpaperUpdate
{
    public static bool TryStart(out string error)
    {
        EnsureShortcut();
        var bat = FindUpdateBat();
        if (bat == null)
        {
            error = "Рядом с программой нет update.bat. Обновление запускается из папки проекта.";
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            WorkingDirectory = Path.GetDirectoryName(bat)!,
            UseShellExecute = true,
        });
        error = "";
        return true;
    }

    public static void EnsureShortcut()
    {
        try
        {
            var bat = FindUpdateBat();
            if (bat == null)
            {
                return;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return;
            }

            var shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return;
            }

            var linkPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "Обновить обои.lnk");
            var shortcut = Invoke(shellType, "CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, shell, linkPath);
            if (shortcut == null)
            {
                return;
            }

            var shortcutType = shortcut.GetType();
            Invoke(shortcutType, "TargetPath", System.Reflection.BindingFlags.SetProperty, shortcut, bat);
            Invoke(shortcutType, "WorkingDirectory", System.Reflection.BindingFlags.SetProperty, shortcut, Path.GetDirectoryName(bat)!);
            Invoke(shortcutType, "Description", System.Reflection.BindingFlags.SetProperty, shortcut, "Собрать и перезапустить обои");
            Invoke(shortcutType, "WindowStyle", System.Reflection.BindingFlags.SetProperty, shortcut, 1);
            Invoke(shortcutType, "Save", System.Reflection.BindingFlags.InvokeMethod, shortcut);
        }
        catch
        {
            // A missing shortcut only means the Start menu entry was not refreshed.
        }
    }

    private static object? Invoke(Type type, string name, System.Reflection.BindingFlags flags, object target, params object[] args)
    {
        return type.InvokeMember(name, flags, null, target, args, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? FindUpdateBat()
    {
        var process = Environment.ProcessPath;
        var dir = string.IsNullOrEmpty(process) ? null : Path.GetDirectoryName(process);
        var parent = string.IsNullOrEmpty(dir) ? null : Path.GetDirectoryName(dir);
        if (parent == null)
        {
            return null;
        }

        var bat = Path.Combine(parent, "update.bat");
        return File.Exists(bat) ? bat : null;
    }
}
