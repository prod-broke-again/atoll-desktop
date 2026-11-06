using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSpike;

internal sealed record SearchHit(string Kind, string Title, string Detail, string? Launch, string? AppId, string? IconPath);

/// <summary>
/// Local search for the dock. The Windows search window (SearchHost) is never opened.
/// Apps are the Start menu shortcuts, settings are the built-in ms-settings pages,
/// and files are the desktop, documents, and downloads folders.
/// </summary>
internal static class SearchCatalog
{
    private const int AppLimit = 8;
    private const int SettingLimit = 4;
    private const int FileLimit = 4;
    private const int ScanLimit = 700;

    private static readonly (string Name, string Uri, string[] Extra)[] Settings =
    {
        ("Параметры", "ms-settings:", new[] { "settings" }),
        ("Система", "ms-settings:system", Array.Empty<string>()),
        ("Дисплей", "ms-settings:display", new[] { "экран", "монитор" }),
        ("Звук", "ms-settings:sound", new[] { "громкость", "динамики", "наушники", "sound" }),
        ("Микшер громкости", "ms-settings:apps-volume", new[] { "громкость приложений" }),
        ("Уведомления", "ms-settings:notifications", Array.Empty<string>()),
        ("Питание и батарея", "ms-settings:powersleep", new[] { "сон", "батарея" }),
        ("Сеть и интернет", "ms-settings:network", new[] { "интернет", "network" }),
        ("Wi-Fi", "ms-settings:network-wifi", new[] { "вайфай", "wifi", "беспроводн" }),
        ("Ethernet", "ms-settings:network-ethernet", new[] { "проводн" }),
        ("Bluetooth", "ms-settings:bluetooth", new[] { "блютуз" }),
        ("Режим «в самолете»", "ms-settings:network-airplanemode", new[] { "самолет", "полёт", "полет" }),
        ("Ночной свет", "ms-settings:nightlight", Array.Empty<string>()),
        ("Персонализация", "ms-settings:personalization", Array.Empty<string>()),
        ("Фон", "ms-settings:personalization-background", new[] { "обои" }),
        ("Цвета", "ms-settings:colors", Array.Empty<string>()),
        ("Темы", "ms-settings:themes", Array.Empty<string>()),
        ("Экран блокировки", "ms-settings:lockscreen", Array.Empty<string>()),
        ("Пуск", "ms-settings:personalization-start", new[] { "меню пуск" }),
        ("Панель задач", "ms-settings:taskbar", Array.Empty<string>()),
        ("Приложения", "ms-settings:appsfeatures", new[] { "установка", "удаление программ" }),
        ("Учётные записи", "ms-settings:yourinfo", new[] { "аккаунт" }),
        ("Время и язык", "ms-settings:dateandtime", new[] { "дата", "часовой" }),
        ("Язык и регион", "ms-settings:regionlanguage", new[] { "раскладка", "язык" }),
        ("Конфиденциальность", "ms-settings:privacy", Array.Empty<string>()),
        ("Центр обновления Windows", "ms-settings:windowsupdate", new[] { "обновлен", "update" }),
        ("Память устройства", "ms-settings:storagesense", new[] { "хранилище", "диск" }),
        ("Принтеры и сканеры", "ms-settings:printers", new[] { "принтер" }),
        ("Камера", "ms-settings:camera", Array.Empty<string>()),
        ("Специальные возможности", "ms-settings:easeofaccess", Array.Empty<string>()),
    };

    private static IReadOnlyList<AppEntry>? _apps;
    private static DateTime _appsAt = DateTime.MinValue;
    private static bool _logged;
    private static readonly Dictionary<string, string?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Image> _shellImages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Image> _iconImages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<Image?>> _iconLoads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _iconGate = new();

    public static string Describe()
    {
        var apps = Apps();
        var sample = string.Join(", ", apps.Take(6).Select(app => app.Name));
        var report = new StringBuilder();
        report.Append("search apps=").Append(apps.Count);
        if (sample.Length > 0)
        {
            report.Append(" sample=").Append(sample);
        }

        foreach (var query in new[] { "steam", "яндекс", "звук" })
        {
            var hits = Find(query)
                .Where(hit => hit.Kind is "app" or "setting")
                .Take(4)
                .Select(hit => hit.Kind + ":" + hit.Title);
            report.AppendLine();
            report.Append("search \"").Append(query).Append("\" -> ").Append(string.Join("; ", hits));
        }

        return report.ToString();
    }

    public static IReadOnlyList<SearchHit> Home()
    {
        var hits = new List<SearchHit>();
        foreach (var pin in ShellProperties.Pins())
        {
            if (hits.Count >= AppLimit || string.IsNullOrWhiteSpace(pin.Name))
            {
                continue;
            }

            if (hits.Any(hit => string.Equals(hit.Title, pin.Name, StringComparison.CurrentCultureIgnoreCase)))
            {
                continue;
            }

            hits.Add(AppHit(pin.Name, pin.LinkPath, pin.IconPath));
        }

        return hits;
    }

    public static IReadOnlyList<SearchHit> Applications()
    {
        return Apps()
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(app => AppHit(app.Name, app.LinkPath, app.LinkPath))
            .ToList();
    }

    public static IReadOnlyList<SearchHit> Find(string query)
    {
        var text = query.Trim();
        if (text.Length == 0)
        {
            return Home();
        }

        var hits = new List<SearchHit>();
        var apps = Apps()
            .Select(app => (app, rank: Rank(app.Name, text)))
            .Where(item => item.rank < 9)
            .OrderBy(item => item.rank)
            .ThenBy(item => OpenAppId(item.app.Name) == null ? 1 : 0)
            .ThenBy(item => item.app.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(AppLimit);
        foreach (var item in apps)
        {
            hits.Add(AppHit(item.app.Name, item.app.LinkPath, item.app.LinkPath));
        }

        if (text.Length >= 2)
        {
            foreach (var setting in Settings)
            {
                if (hits.Count(hit => hit.Kind == "setting") >= SettingLimit)
                {
                    break;
                }

                var names = new[] { setting.Name }.Concat(setting.Extra);
                if (names.Any(name => Rank(name, text) < 9))
                {
                    hits.Add(new SearchHit("setting", setting.Name, "Параметры", setting.Uri, null, null));
                }
            }

            foreach (var file in MatchingFiles(text))
            {
                hits.Add(new SearchHit("file", Path.GetFileName(file), PlaceName(file), file, null, file));
            }
        }

        return hits;
    }

    public static SearchHit Web(string query)
    {
        var text = query.Trim();
        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(text);
        return new SearchHit("web", text, "Bing", url, null, null);
    }

    public static void Launch(SearchHit hit)
    {
        if (!string.IsNullOrWhiteSpace(hit.AppId) &&
            WindowCatalog.TryGetApp(hit.AppId, out var app) &&
            app != null &&
            app.Hwnds.Count > 0)
        {
            var hwnd = app.ActiveHwnd != 0 ? app.ActiveHwnd : app.Hwnds[0];
            WindowCatalog.Activate(new IntPtr(hwnd));
            return;
        }

        if (string.IsNullOrWhiteSpace(hit.Launch))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(hit.Launch) { UseShellExecute = true });
    }

    public static string? ShellName(string parsingName)
    {
        if (!TryPidl(parsingName, out var pidl))
        {
            return null;
        }

        try
        {
            var info = new ShFileInfo();
            var result = SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiPidl | ShgfiDisplayName);
            if (result == IntPtr.Zero || string.IsNullOrWhiteSpace(info.DisplayName))
            {
                return null;
            }

            return info.DisplayName;
        }
        finally
        {
            CoTaskMemFree(pidl);
        }
    }

    public static Image? ShellImage(string parsingName)
    {
        if (_shellImages.TryGetValue(parsingName, out var cached))
        {
            return new Bitmap(cached);
        }

        if (!TryPidl(parsingName, out var pidl))
        {
            return null;
        }

        try
        {
            var info = new ShFileInfo();
            var result = SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiPidl | ShgfiIcon | ShgfiLargeIcon);
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                using var icon = Icon.FromHandle(info.Icon);
                using var bitmap = icon.ToBitmap();
                var copy = new Bitmap(bitmap);
                _shellImages[parsingName] = new Bitmap(copy);
                return copy;
            }
            finally
            {
                NativeMethods.DestroyIcon(info.Icon);
            }
        }
        finally
        {
            CoTaskMemFree(pidl);
        }
    }

    /// <summary>PNG data URL for a shortcut, file, folder, or shell:AppsFolder item. Cached.</summary>
    public static string? IconData(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return ShellIconUrl(path);
        }

        return IconUrl(path);
    }

    public static Image? LoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        lock (_iconGate)
        {
            if (_iconImages.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        var image = DecodeIcon(path);
        if (image != null)
        {
            lock (_iconGate)
            {
                _iconImages[path] = image;
            }
        }

        return image;
    }

    /// <summary>Shared cached instance — callers must not dispose it.</summary>
    public static Image? IconCached(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        lock (_iconGate)
        {
            return _iconImages.TryGetValue(path, out var cached) ? cached : null;
        }
    }

    /// <summary>
    /// Loads an icon off the UI thread. The returned task completes with the
    /// shared cached instance; concurrent requests for one path share one load.
    /// </summary>
    public static Task<Image?> IconAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult<Image?>(null);
        }

        lock (_iconGate)
        {
            if (_iconImages.TryGetValue(path, out var cached))
            {
                return Task.FromResult<Image?>(cached);
            }

            if (_iconLoads.TryGetValue(path, out var pending))
            {
                return pending;
            }

            var load = Task.Run(() => LoadIcon(path));
            _iconLoads[path] = load;
            return load;
        }
    }

    private static Image? DecodeIcon(string path)
    {
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return ShellImage(path);
        }

        var url = IconUrl(path);
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(url["data:image/png;base64,".Length..]);
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }

    private static SearchHit AppHit(string name, string linkPath, string? iconPath)
    {
        var appId = OpenAppId(name);
        var detail = appId == null ? "Приложение" : "Открыто";
        return new SearchHit("app", name, detail, linkPath, appId, string.IsNullOrWhiteSpace(iconPath) ? linkPath : iconPath);
    }

    private static string? OpenAppId(string title)
    {
        foreach (var app in WindowCatalog.Current)
        {
            if (app.Running && string.Equals(app.Name, title, StringComparison.CurrentCultureIgnoreCase))
            {
                return app.Id;
            }
        }

        return null;
    }

    private static readonly object _appsGate = new();

    private static IReadOnlyList<AppEntry> Apps()
    {
        lock (_appsGate)
        {
            return AppsCore();
        }
    }

    private static IReadOnlyList<AppEntry> AppsCore()
    {
        if (_apps != null && DateTime.UtcNow - _appsAt < TimeSpan.FromSeconds(30))
        {
            return _apps;
        }

        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), list, seen);
        AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), list, seen);
        foreach (var pin in ShellProperties.Pins())
        {
            if (string.IsNullOrWhiteSpace(pin.Name) || string.IsNullOrWhiteSpace(pin.LinkPath) || !seen.Add(pin.Name))
            {
                continue;
            }

            list.Add(new AppEntry(pin.Name, pin.LinkPath));
        }

        _apps = list;
        _appsAt = DateTime.UtcNow;
        if (!_logged)
        {
            _logged = true;
            Console.WriteLine("[spike] search indexed " + list.Count + " apps");
        }

        return list;
    }

    private static void AddFolder(string root, List<AppEntry> list, HashSet<string> seen)
    {
        var programs = Path.Combine(root, "Programs");
        if (!Directory.Exists(programs))
        {
            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(programs, "*.lnk", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            if (file.Contains("\\Startup\\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("\\Автозагрузка\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(name) || IsNoise(name) || !seen.Add(name))
            {
                continue;
            }

            list.Add(new AppEntry(name, file));
        }
    }

    private static bool IsNoise(string name)
    {
        var text = Norm(name);
        return text.Contains("uninstall", StringComparison.Ordinal) ||
               text.Contains("uninstaller", StringComparison.Ordinal) ||
               text.Contains("удалить", StringComparison.Ordinal) ||
               text.Contains("удаление", StringComparison.Ordinal) ||
               text.Contains("деинстал", StringComparison.Ordinal);
    }

    private static int Rank(string name, string query)
    {
        var left = Norm(name);
        var right = Norm(query);
        if (left.Length == 0 || right.Length == 0)
        {
            return 9;
        }

        if (left.Equals(right, StringComparison.Ordinal))
        {
            return 0;
        }

        if (left.StartsWith(right, StringComparison.Ordinal))
        {
            return 1;
        }

        var words = left.Split(new[] { ' ', '-', '_', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(word => word.StartsWith(right, StringComparison.Ordinal)))
        {
            return 2;
        }

        var tokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && tokens.All(token => left.Contains(token, StringComparison.Ordinal)))
        {
            return 3;
        }

        return 9;
    }

    private static IEnumerable<string> MatchingFiles(string query)
    {
        var needle = Norm(query);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        var found = 0;
        foreach (var root in roots)
        {
            if (found >= FileLimit || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Scan(root))
            {
                var name = Norm(Path.GetFileNameWithoutExtension(file));
                if (!name.Contains(needle, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
                if (++found >= FileLimit)
                {
                    yield break;
                }
            }
        }
    }

    private static IEnumerable<string> Scan(string root)
    {
        var seen = 0;
        foreach (var file in SafeFiles(root))
        {
            yield return file;
            if (++seen >= ScanLimit)
            {
                yield break;
            }
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in SafeFiles(dir))
            {
                yield return file;
                if (++seen >= ScanLimit)
                {
                    yield break;
                }
            }
        }
    }

    private static IEnumerable<string> SafeFiles(string directory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.'))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string PlaceName(string file)
    {
        var dir = Path.GetDirectoryName(file) ?? string.Empty;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (dir.Equals(desktop, StringComparison.OrdinalIgnoreCase) || IsChild(desktop, dir))
        {
            return "Рабочий стол";
        }

        if (dir.Equals(documents, StringComparison.OrdinalIgnoreCase) || IsChild(documents, dir))
        {
            return "Документы";
        }

        if (dir.Equals(downloads, StringComparison.OrdinalIgnoreCase) || IsChild(downloads, dir))
        {
            return "Загрузки";
        }

        return string.IsNullOrWhiteSpace(Path.GetFileName(dir)) ? "Файл" : Path.GetFileName(dir);
    }

    private static bool IsChild(string root, string dir)
    {
        return dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string value)
    {
        return value.Trim().ToLower(CultureInfo.CurrentCulture).Replace('ё', 'е');
    }

    private static string? IconUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return null;
        }

        lock (_iconGate)
        {
            if (IconCache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        var url = ExtractIcon(path);
        lock (_iconGate)
        {
            IconCache[path] = url;
        }

        return url;
    }

    private static string? ShellIconUrl(string parsingName)
    {
        lock (_iconGate)
        {
            if (IconCache.TryGetValue(parsingName, out var cached))
            {
                return cached;
            }
        }

        string? url = null;
        try
        {
            using var image = ShellImage(parsingName);
            if (image != null)
            {
                using var stream = new MemoryStream();
                image.Save(stream, ImageFormat.Png);
                url = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
            }
        }
        catch
        {
            url = null;
        }

        lock (_iconGate)
        {
            IconCache[parsingName] = url;
        }

        return url;
    }

    private static string? ExtractIcon(string path)
    {
        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var icon = Icon.FromHandle(info.Icon);
            using var bitmap = icon.ToBitmap();
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
            NativeMethods.DestroyIcon(info.Icon);
        }
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiDisplayName = 0x200;
    private const uint ShgfiPidl = 0x8;
    private const uint ShgfiLargeIcon = 0x0;

    private static bool TryPidl(string parsingName, out IntPtr pidl)
    {
        var hr = SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _);
        if (hr != 0)
        {
            pidl = IntPtr.Zero;
            return false;
        }

        return pidl != IntPtr.Zero;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint infoSize, uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint attributes, ref ShFileInfo info, uint infoSize, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    private sealed record AppEntry(string Name, string LinkPath);
}
