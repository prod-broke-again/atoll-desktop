using System.Text.Json;
using Microsoft.Win32;

namespace TaskbarSpike;

/// <summary>
/// Accent from the wallpaper theme file, then the Windows accent, then the wallpaper default.
/// File changes are debounced. Nothing here polls or broadcasts a Windows message.
/// </summary>
internal static class AccentWatch
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveWallpaper");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "theme.json");
    private static readonly System.Threading.Timer Debounce = new(OnDebounce, null, Timeout.Infinite, Timeout.Infinite);

    private static FileSystemWatcher? _watcher;
    private static Control? _ui;
    private static int _retries;

    public static void Prime()
    {
        Theme.SetAccent(Resolve(out _));
    }

    public static void Attach(Control ui)
    {
        _ui = ui;
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(DirectoryPath, "theme.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Poke();
            _watcher.Created += (_, _) => Poke();
            _watcher.Deleted += (_, _) => Poke();
            _watcher.Renamed += (_, _) => Poke();
            _watcher.Error += (_, _) =>
            {
                try
                {
                    if (_watcher != null)
                    {
                        _watcher.EnableRaisingEvents = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[spike] theme watch: " + ex.Message);
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] theme watch: " + ex.Message);
        }

        Poke();
    }

    public static void Notify() => Poke();

    public static void Stop()
    {
        _ui = null;
        _watcher?.Dispose();
        _watcher = null;
        Debounce.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private static void Poke()
    {
        Debounce.Change(300, Timeout.Infinite);
    }

    private static void OnDebounce(object? state)
    {
        Color accent;
        bool transient;
        try
        {
            accent = Resolve(out transient);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] theme read: " + ex.Message);
            return;
        }

        if (transient)
        {
            if (_retries++ < 8)
            {
                Poke();
                return;
            }

            accent = TryReadWindows(out var fromWindows) ? fromWindows : Theme.DefaultAccent;
        }

        _retries = 0;
        var ui = _ui;
        if (ui == null || ui.IsDisposed || !ui.IsHandleCreated)
        {
            return;
        }

        try
        {
            ui.BeginInvoke(new Action(() =>
            {
                if (!ui.IsDisposed)
                {
                    Theme.SetAccent(accent);
                }
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Color Resolve(out bool transient)
    {
        transient = false;
        if (TryReadFile(out var fromFile, out transient))
        {
            return fromFile;
        }

        if (transient)
        {
            return Theme.Accent;
        }

        return TryReadWindows(out var fromWindows) ? fromWindows : Theme.DefaultAccent;
    }

    private static bool TryReadFile(out Color accent, out bool transient)
    {
        accent = Theme.DefaultAccent;
        transient = false;
        if (!File.Exists(FilePath))
        {
            return false;
        }

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(FilePath);
        if (age > TimeSpan.FromHours(24) || age < TimeSpan.FromMinutes(-5))
        {
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            transient = true;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("accent", out var property))
            {
                return false;
            }

            return Theme.TryParseHex(property.GetString(), out accent);
        }
        catch (JsonException)
        {
            transient = true;
            return false;
        }
    }

    private static bool TryReadWindows(out Color accent)
    {
        accent = Theme.DefaultAccent;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            var raw = key?.GetValue("AccentColor");
            if (raw is not (int or uint or long))
            {
                return false;
            }

            var packed = raw switch
            {
                int value => unchecked((uint)value),
                uint value => value,
                long value => unchecked((uint)value),
                _ => 0u,
            };
            accent = Color.FromArgb(
                (byte)(packed & 0xFF),
                (byte)((packed >> 8) & 0xFF),
                (byte)((packed >> 16) & 0xFF));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] windows accent: " + ex.Message);
            return false;
        }
    }
}
