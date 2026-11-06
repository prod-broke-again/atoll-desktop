using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TaskbarSpike;

internal sealed record TrayButton(string Id, string Name, string? IconDataUrl, bool Overflow, string? ExecutablePath);

internal static class TrayCatalog
{
    public static readonly string OverflowId = Hash("overflow");

    private static readonly Dictionary<string, Environment.SpecialFolder> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.SpecialFolder.ProgramFiles,
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.SpecialFolder.ProgramFilesX86,
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.SpecialFolder.Windows,
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.SpecialFolder.System,
    };

    private static readonly Dictionary<string, string?> IconCache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SkipHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "dllhost",
        "svchost",
        "sihost",
        "RuntimeBroker",
        "backgroundTaskHost",
        "ApplicationFrameHost",
    };

    private static IReadOnlyList<TrayButton> _last = Array.Empty<TrayButton>();
    private static HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private static long _runningAt;
    private static bool _logged;

    public static int CachedCount => _last.Count;

    public static void Warm()
    {
        try
        {
            _ = Snapshot();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] tray warm: {ex.Message}");
        }
    }

    public static IReadOnlyList<TrayButton> Snapshot()
    {
        var buttons = new List<TrayButton> { new(OverflowId, "Скрытые значки", null, true, null) };
        foreach (var entry in Dedupe(Entries().Where(entry => entry.Promoted && IsRunning(entry.Path) && HasLiveIcon(entry.Path))))
        {
            buttons.Add(ToButton(entry, overflow: false));
        }

        _last = buttons;
        if (!_logged)
        {
            _logged = true;
            Console.WriteLine($"[spike] tray icons: {buttons.Count - 1}");
        }

        return _last;
    }

    public static IReadOnlyList<TrayButton> Hidden()
    {
        return Dedupe(Entries().Where(entry => !entry.Promoted && IsRunning(entry.Path) && HasLiveIcon(entry.Path)))
            .Select(entry => ToButton(entry, overflow: false))
            .ToList();
    }

    public static bool TryGet(string id, out TrayButton? button)
    {
        button = Snapshot().FirstOrDefault(item => item.Id == id) ?? Hidden().FirstOrDefault(item => item.Id == id);
        return button != null;
    }

    public static void Open(string id)
    {
        if (!TryGet(id, out var button) || button?.ExecutablePath == null)
        {
            return;
        }

        var hwnd = FindMainWindow(button.ExecutablePath);
        if (hwnd != IntPtr.Zero)
        {
            WindowCatalog.Activate(hwnd);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(button.ExecutablePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] tray open: {ex.Message}");
        }
    }

    private static IEnumerable<Entry> Dedupe(IEnumerable<Entry> entries)
    {
        foreach (var group in entries.GroupBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            yield return group.FirstOrDefault(entry => _paths.Contains(entry.Path)) ?? group.First();
        }
    }

    private static TrayButton ToButton(Entry entry, bool overflow)
    {
        return new TrayButton(entry.Id, entry.Name, IconFor(entry), overflow, entry.Path);
    }

    private static List<Entry> Entries()
    {
        RefreshRunning();
        var entries = new List<Entry>();
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (root == null)
        {
            return entries;
        }

        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key == null)
            {
                continue;
            }

            var path = ExpandPath(key.GetValue("ExecutablePath") as string);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var file = Path.GetFileNameWithoutExtension(path);
            var tip = (key.GetValue("InitialTooltip") as string ?? string.Empty).Trim();
            if (tip.Length == 0 && SkipHosts.Contains(file))
            {
                continue;
            }

            var promoted = key.GetValue("IsPromoted") is int flag && flag == 1;
            var uid = key.GetValue("UID")?.ToString() ?? string.Empty;
            var snapshot = key.GetValue("IconSnapshot") as byte[];
            entries.Add(new Entry(Hash(path + "#" + uid), DisplayName(tip, path), path, promoted, snapshot));
        }

        entries.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }

    private static string DisplayName(string tip, string path)
    {
        if (tip.Length > 0)
        {
            return tip;
        }

        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Trim();
            }
        }
        catch
        {
            // The file may be missing or locked. The file name is enough.
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private static void RefreshRunning()
    {
        if (_runningAt != 0 && Environment.TickCount64 - _runningAt < 1000)
        {
            return;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }

                if (!string.IsNullOrWhiteSpace(process.ProcessName))
                {
                    names.Add(process.ProcessName);
                }
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(process.ProcessName))
                {
                    names.Add(process.ProcessName);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        _paths = paths;
        _names = names;
        _runningAt = Environment.TickCount64;
    }

    /// <summary>
    /// The registry remembers every app that ever had a tray icon. Only icons that are registered
    /// with our tray host right now (and can receive clicks) are worth showing.
    /// </summary>
    private static bool HasLiveIcon(string path)
    {
        var file = Path.GetFileName(path);
        foreach (var icon in TrayHost.Snapshot())
        {
            if (icon.CallbackMessage == 0 || icon.Hidden || !icon.IsAlive())
            {
                continue;
            }

            var iconPath = icon.Path;
            if (string.IsNullOrEmpty(iconPath))
            {
                continue;
            }

            if (iconPath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(iconPath).Equals(file, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRunning(string path)
    {
        if (_paths.Contains(path))
        {
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length >= 3 && _names.Contains(name);
    }

    private static IntPtr FindMainWindow(string path)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        IntPtr visible = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindowTextLength(hwnd) == 0)
            {
                return true;
            }

            var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
            if ((style & NativeMethods.WsExToolWindow) != 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || !ProcessMatches((int)pid, path, file))
            {
                return true;
            }

            visible = hwnd;
            return false;
        }, IntPtr.Zero);

        return visible;
    }

    private static bool ProcessMatches(int pid, string path, string file)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            try
            {
                var module = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(module))
                {
                    return string.Equals(module, path, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Elevated processes refuse MainModule. The process name still matches.
            }

            return file.Length >= 3 && string.Equals(process.ProcessName, file, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? IconFor(Entry entry)
    {
        if (IconCache.TryGetValue(entry.Id, out var cached))
        {
            return cached;
        }

        var url = entry.Snapshot is { Length: > 8 } ? PngUrl(KnockOutBlack(entry.Snapshot)) : null;
        if (url == null && File.Exists(entry.Path))
        {
            url = ExtractExeIcon(entry.Path);
        }

        IconCache[entry.Id] = url;
        return url;
    }

    private static Bitmap? KnockOutBlack(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            using var source = new Bitmap(stream);
            var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            var knock = CornersAreBlack(source);
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var color = source.GetPixel(x, y);
                    if (knock && color.R < 28 && color.G < 28 && color.B < 28)
                    {
                        color = Color.Transparent;
                    }

                    bitmap.SetPixel(x, y, color);
                }
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static bool CornersAreBlack(Bitmap bitmap)
    {
        if (bitmap.Width < 2 || bitmap.Height < 2)
        {
            return false;
        }

        return IsBlack(bitmap.GetPixel(0, 0))
            && IsBlack(bitmap.GetPixel(bitmap.Width - 1, 0))
            && IsBlack(bitmap.GetPixel(0, bitmap.Height - 1))
            && IsBlack(bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1));
    }

    private static bool IsBlack(Color color)
    {
        return color.A > 0 && color.R < 28 && color.G < 28 && color.B < 28;
    }

    private static string? ExtractExeIcon(string path)
    {
        var handles = new IntPtr[1];
        var ids = new uint[1];
        try
        {
            var count = NativeMethods.PrivateExtractIcons(path, 0, 32, 32, handles, ids, 1, 0);
            if (count == 0 || handles[0] == IntPtr.Zero)
            {
                return null;
            }

            using var icon = Icon.FromHandle(handles[0]);
            return PngUrl(icon.ToBitmap());
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handles[0] != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(handles[0]);
            }
        }
    }

    private static string? PngUrl(Image? bitmap)
    {
        if (bitmap == null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return null;
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        var end = trimmed.IndexOf('}');
        if (end <= 0)
        {
            return trimmed;
        }

        var guid = trimmed[..(end + 1)];
        if (!KnownFolders.TryGetValue(guid, out var folder))
        {
            return trimmed;
        }

        var root = Environment.GetFolderPath(folder);
        var rest = trimmed[(end + 1)..].TrimStart('\\', '/');
        return string.IsNullOrEmpty(rest) ? root : Path.Combine(root, rest);
    }

    private static string Hash(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..16];
    }

    private sealed record Entry(string Id, string Name, string Path, bool Promoted, byte[]? Snapshot);
}
