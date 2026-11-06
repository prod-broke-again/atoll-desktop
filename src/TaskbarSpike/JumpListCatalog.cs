using System.Globalization;
using System.Text;
using ExtensionBlocks;
using JumpList.Automatic;
using JumpList.Custom;
using Lnk;
using Lnk.ExtraData;

namespace TaskbarSpike;

internal enum JumpListKind
{
    Recent,
    Frequent,
    Pinned,
    Custom,
    Task,
}

internal sealed record JumpListItem(
    string Label,
    string? Path,
    string? Arguments,
    string? WorkingDirectory,
    JumpListKind Kind,
    string? IconPath = null,
    int IconIndex = 0);

internal sealed record JumpListSection(string Title, IReadOnlyList<JumpListItem> Items);

internal sealed record JumpListModel(IReadOnlyList<JumpListSection> Sections)
{
    public static JumpListModel Empty { get; } = new(Array.Empty<JumpListSection>());

    public bool IsEmpty => Sections.Count == 0 || Sections.All(section => section.Items.Count == 0);
}

/// <summary>
/// Reads shell Jump List destinations for an app (by AUMID / exe path AppId).
/// Builds a menu model — Explorer owns the native flyout; we rebuild from the same files.
/// </summary>
internal static class JumpListCatalog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime At, JumpListModel Model)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static FileSystemWatcher? _autoWatch;
    private static FileSystemWatcher? _customWatch;
    private static bool _watching;

    public static JumpListModel ForApp(string? aumid, string? targetPath)
    {
        EnsureWatchers();
        var keys = AppIds(aumid, targetPath);
        if (keys.Count == 0)
        {
            return JumpListModel.Empty;
        }

        lock (Gate)
        {
            foreach (var key in keys)
            {
                if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(4))
                {
                    return hit.Model;
                }
            }
        }

        JumpListModel? best = null;
        string? bestKey = null;
        foreach (var key in keys)
        {
            var model = Load(key);
            if (best == null || (!model.IsEmpty && (best.IsEmpty || model.Sections.Sum(s => s.Items.Count) > best.Sections.Sum(s => s.Items.Count))))
            {
                best = model;
                bestKey = key;
            }
        }

        best ??= JumpListModel.Empty;
        if (bestKey != null)
        {
            lock (Gate)
            {
                Cache[bestKey] = (DateTime.UtcNow, best);
            }
        }

        return best;
    }

    public static void Activate(JumpListItem item)
    {
        if (item == null)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                var info = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = item.Path,
                    UseShellExecute = true,
                };
                if (!string.IsNullOrWhiteSpace(item.Arguments))
                {
                    info.Arguments = item.Arguments;
                }

                if (!string.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                {
                    info.WorkingDirectory = item.WorkingDirectory;
                }

                System.Diagnostics.Process.Start(info);
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] jump list launch: " + ex.Message);
        }
    }

    private static JumpListModel Load(string appId)
    {
        var sections = new List<JumpListSection>();
        try
        {
            AddCustom(sections, appId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] jump custom {appId}: {ex.Message}");
        }

        try
        {
            AddAutomatic(sections, appId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] jump auto {appId}: {ex.Message}");
        }

        return new JumpListModel(sections.Where(section => section.Items.Count > 0).ToArray());
    }

    private static void AddCustom(List<JumpListSection> sections, string appId)
    {
        var path = Path.Combine(CustomDir(), appId + ".customDestinations-ms");
        if (!File.Exists(path))
        {
            return;
        }

        CustomDestination custom;
        try
        {
            custom = JumpList.JumpList.LoadCustomJumplist(path);
        }
        catch
        {
            return;
        }

        foreach (var entry in custom.Entries ?? Enumerable.Empty<Entry>())
        {
            var items = ItemsFrom(entry);
            if (items.Count == 0)
            {
                continue;
            }

            sections.Add(new JumpListSection(SectionTitle(entry), items));
        }
    }

    private static void AddAutomatic(List<JumpListSection> sections, string appId)
    {
        var filePath = Path.Combine(AutoDir(), appId + ".automaticDestinations-ms");
        if (!File.Exists(filePath))
        {
            return;
        }

        AutomaticDestination auto;
        try
        {
            auto = JumpList.JumpList.LoadAutoJumplist(filePath);
        }
        catch
        {
            return;
        }

        var pinned = new List<JumpListItem>();
        var recent = new List<JumpListItem>();
        var entries = auto.DestListEntries ?? new List<AutoDestList>();
        foreach (var entry in entries)
        {
            // DestList Path is UTF-16 and usually correct; LNK LocalPath is often CP1251
            // bytes misread as Latin-1 (e.g. Cyrillic folder names). Prefer DestList.
            var target = ResolveAutomaticTarget(entry.Path, entry.Lnk);
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            var args = entry.Lnk != null && !string.IsNullOrWhiteSpace(entry.Lnk.Arguments) ? entry.Lnk.Arguments : null;
            var work = entry.Lnk != null && !string.IsNullOrWhiteSpace(entry.Lnk.WorkingDirectory)
                ? RepairAnsiPath(entry.Lnk.WorkingDirectory)
                : null;
            var label = AutomaticLabel(entry.Lnk, target);
            var (iconPath, iconIndex) = IconRef(entry.Lnk, target);
            var item = new JumpListItem(label, target, args, work,
                entry.Pinned ? JumpListKind.Pinned : JumpListKind.Recent,
                iconPath,
                iconIndex);
            if (entry.Pinned)
            {
                pinned.Add(item);
            }
            else
            {
                recent.Add(item);
            }
        }

        if (pinned.Count > 0)
        {
            sections.Add(new JumpListSection("Закреплённые", pinned));
        }

        // Avoid duplicating a Recent section already provided by custom destinations.
        if (recent.Count > 0 && sections.All(section =>
                !section.Title.Equals("Недавние", StringComparison.OrdinalIgnoreCase) &&
                !section.Title.Equals("Recent", StringComparison.OrdinalIgnoreCase)))
        {
            sections.Add(new JumpListSection("Недавние", recent.Take(12).ToArray()));
        }
    }

    private static List<JumpListItem> ItemsFrom(Entry entry)
    {
        var kind = entry.CategoryType switch
        {
            2 => JumpListKind.Task,
            1 when entry.KnownCategoryId == 1 => JumpListKind.Frequent,
            1 => JumpListKind.Recent,
            _ => JumpListKind.Custom,
        };
        var items = new List<JumpListItem>();
        foreach (var lnk in entry.LnkFiles ?? Enumerable.Empty<LnkFile>())
        {
            var path = FirstPath(lnk);
            var label = FirstLabel(lnk, path);
            if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var resolved = string.IsNullOrWhiteSpace(label) ? ShortLabel(path!) : label!;
            LogLabel(kind, lnk, path, resolved);
            var (iconPath, iconIndex) = IconRef(lnk, path);
            items.Add(new JumpListItem(
                resolved,
                path,
                string.IsNullOrWhiteSpace(lnk.Arguments) ? null : lnk.Arguments,
                string.IsNullOrWhiteSpace(lnk.WorkingDirectory) ? null : RepairAnsiPath(lnk.WorkingDirectory),
                kind,
                iconPath,
                iconIndex));
        }

        return items;
    }

    private static (string? Path, int Index) IconRef(LnkFile? lnk, string? targetPath)
    {
        if (lnk != null && !string.IsNullOrWhiteSpace(lnk.IconLocation))
        {
            var raw = lnk.IconLocation.Trim().Trim('"');
            var comma = raw.LastIndexOf(',');
            if (comma > 1 && int.TryParse(raw[(comma + 1)..].Trim(), out var index))
            {
                return (RepairAnsiPath(raw[..comma].Trim().Trim('"')), index);
            }

            return (RepairAnsiPath(raw), 0);
        }

        return (targetPath, 0);
    }

    private static string SectionTitle(Entry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Name))
        {
            return entry.Name;
        }

        return entry.CategoryType switch
        {
            2 => "Задачи",
            1 when entry.KnownCategoryId == 1 => "Частые",
            1 => "Недавние",
            _ => "Ещё",
        };
    }

    private static string? FirstPath(LnkFile lnk) =>
        RepairAnsiPath(
            !string.IsNullOrWhiteSpace(lnk.LocalPath) ? lnk.LocalPath
            : !string.IsNullOrWhiteSpace(lnk.CommonPath) ? lnk.CommonPath
            : null);

    /// <summary>
    /// DestList UTF-16 path wins over LNK LocalPath (Lnk often decodes ANSI paths as Latin-1).
    /// </summary>
    private static string? ResolveAutomaticTarget(string? destListPath, LnkFile? lnk)
    {
        var dest = DestListPath(destListPath);
        var local = lnk != null ? FirstPath(lnk) : null;

        if (!string.IsNullOrWhiteSpace(dest) && !IsRawKnownFolder(dest))
        {
            if (string.IsNullOrWhiteSpace(local) ||
                LooksLikeCp1251Mojibake(local) ||
                HasCyrillic(dest) ||
                string.Equals(dest, local, StringComparison.OrdinalIgnoreCase))
            {
                return dest;
            }

            // DestList sometimes stores a friendly form; LocalPath is the real file — but only if readable.
            if (!LooksLikeCp1251Mojibake(local!))
            {
                return local;
            }

            return dest;
        }

        return local ?? dest;
    }

    private static string AutomaticLabel(LnkFile? lnk, string target)
    {
        if (lnk != null)
        {
            // Host exe + args (Steam/Chrome tasks): Title from property store.
            if (IsHostExecutable(target) || !string.IsNullOrWhiteSpace(lnk.Arguments))
            {
                var fromLnk = FirstLabel(lnk, target);
                if (!string.IsNullOrWhiteSpace(fromLnk) && !LooksLikeCp1251Mojibake(fromLnk))
                {
                    return fromLnk!;
                }
            }
        }

        return ShortLabel(target);
    }

    private static bool IsHostExecutable(string path)
    {
        try
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRawKnownFolder(string path) =>
        path.StartsWith("knownfolder:", StringComparison.OrdinalIgnoreCase) &&
        !path.Contains(" ==> ", StringComparison.Ordinal);

    private static string? FirstLabel(LnkFile lnk, string? path)
    {
        // Shell NameString (IShellLink::GetDescription) — often empty for custom tasks.
        if (!string.IsNullOrWhiteSpace(lnk.Name) && !LooksLikeCp1251Mojibake(lnk.Name))
        {
            return lnk.Name.Trim();
        }

        // Explorer shows System.Title / ItemNameDisplay from the LNK property store
        // (Steam/Chrome: LocalPath is always the host exe; human names live here).
        var title = PropertyStoreTitle(lnk);
        if (!string.IsNullOrWhiteSpace(title) && !LooksLikeCp1251Mojibake(title))
        {
            return title;
        }

        var displayPath = FirstPath(lnk) ?? path;
        return displayPath == null ? null : ShortLabel(displayPath);
    }

    /// <summary>
    /// Lnk maps CP1251 path bytes onto U+00C0–U+00FF (ÅÖÄ…); re-decode as Windows-1251.
    /// </summary>
    private static string? RepairAnsiPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || !LooksLikeCp1251Mojibake(value))
        {
            return value;
        }

        try
        {
            var bytes = new byte[value.Length];
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (ch > 255)
                {
                    return value;
                }

                bytes[i] = (byte)ch;
            }

            return Encoding.GetEncoding(1251).GetString(bytes);
        }
        catch
        {
            return value;
        }
    }

    private static bool LooksLikeCp1251Mojibake(string value)
    {
        var latin1 = false;
        var cyrillic = false;
        foreach (var ch in value)
        {
            if (ch is >= '\u0400' and <= '\u04FF')
            {
                cyrillic = true;
            }
            else if (ch is >= '\u00C0' and <= '\u00FF')
            {
                latin1 = true;
            }
        }

        return latin1 && !cyrillic;
    }

    private static bool HasCyrillic(string value)
    {
        foreach (var ch in value)
        {
            if (ch is >= '\u0400' and <= '\u04FF')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// PKEY_Title (FMTID_SummaryInformation, pid 2) then PKEY_ItemNameDisplay.
    /// </summary>
    private static string? PropertyStoreTitle(LnkFile lnk)
    {
        foreach (var block in lnk.ExtraBlocks?.OfType<PropertyStoreDataBlock>() ?? Enumerable.Empty<PropertyStoreDataBlock>())
        {
            var sheets = block.PropertyStore?.Sheets;
            if (sheets == null || sheets.Count == 0)
            {
                continue;
            }

            foreach (var sheet in sheets)
            {
                if (!IsGuid(sheet.GUID, SummaryInformationFmtId))
                {
                    continue;
                }

                if (TryProp(sheet, "2", out var title))
                {
                    return title;
                }
            }

            foreach (var sheet in sheets)
            {
                if (!IsGuid(sheet.GUID, StorageItemFmtId))
                {
                    continue;
                }

                if (TryProp(sheet, "10", out var name))
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static bool TryProp(PropertySheet sheet, string key, out string value)
    {
        value = string.Empty;
        if (sheet.PropertyNames == null ||
            !sheet.PropertyNames.TryGetValue(key, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw.Trim();
        return value.Length > 0;
    }

    private static bool IsGuid(string? value, string expected) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string ShortLabel(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>DestList Path may be <c>knownfolder:{guid} ==&gt; Display</c> — prefer the display tail.</summary>
    private static string? DestListPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var marker = " ==> ";
        var at = path.IndexOf(marker, StringComparison.Ordinal);
        if (at >= 0 && at + marker.Length < path.Length)
        {
            return path[(at + marker.Length)..].Trim();
        }

        return path;
    }

    private static List<string> AppIds(string? aumid, string? targetPath)
    {
        var keys = new List<string>();
        void AddRaw(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (!keys.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(id);
            }
        }

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            AddRaw(AppIdOf(raw));
        }

        Add(aumid);
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            Add(NormalizePath(targetPath));
            Add(targetPath);
        }

        // Classic apps (Steam) often set an explicit AUMID that SHGetPropertyStoreForWindow
        // does not expose on every hwnd; path CRC then misses the real destinations file.
        if (!string.IsNullOrWhiteSpace(targetPath) && !keys.Any(HasDestinationFiles))
        {
            var found = FindAppIdByTarget(targetPath);
            if (!string.IsNullOrWhiteSpace(found))
            {
                Console.WriteLine($"[spike] jump appId by target: {Path.GetFileName(targetPath)} -> {found}");
                AddRaw(found);
            }
        }

        return keys;
    }

    private static bool HasDestinationFiles(string appId) =>
        File.Exists(Path.Combine(CustomDir(), appId + ".customDestinations-ms")) ||
        File.Exists(Path.Combine(AutoDir(), appId + ".automaticDestinations-ms"));

    private static readonly Dictionary<string, string?> TargetAppIdCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Scan CustomDestinations for a jump list whose LNKs point at this exe (e.g. Steam → d356105fac5527ef).
    /// </summary>
    private static string? FindAppIdByTarget(string targetPath)
    {
        string full;
        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(targetPath));
        }
        catch
        {
            full = targetPath;
        }

        lock (Gate)
        {
            if (TargetAppIdCache.TryGetValue(full, out var cached))
            {
                return cached;
            }
        }

        string? found = null;
        try
        {
            var dir = CustomDir();
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.customDestinations-ms"))
                {
                    if (!CustomReferencesExe(file, full))
                    {
                        continue;
                    }

                    // "{appId}.customDestinations-ms"
                    var name = Path.GetFileName(file);
                    const string suffix = ".customDestinations-ms";
                    found = name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        ? name[..^suffix.Length]
                        : Path.GetFileNameWithoutExtension(name);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] jump appId scan: " + ex.Message);
        }

        lock (Gate)
        {
            TargetAppIdCache[full] = found;
        }

        return found;
    }

    private static bool CustomReferencesExe(string file, string exePath)
    {
        try
        {
            var custom = JumpList.JumpList.LoadCustomJumplist(file);
            foreach (var entry in custom.Entries ?? Enumerable.Empty<Entry>())
            {
                foreach (var lnk in entry.LnkFiles ?? Enumerable.Empty<LnkFile>())
                {
                    var local = FirstPath(lnk);
                    if (!string.IsNullOrWhiteSpace(local) &&
                        string.Equals(local, exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static int _labelLogs;

    private static void LogLabel(JumpListKind kind, LnkFile lnk, string? path, string label)
    {
        // One-shot samples so Steam/Chrome regressions are obvious in the console.
        if (_labelLogs >= 4)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(lnk.Name) &&
            string.Equals(lnk.Name.Trim(), label, StringComparison.Ordinal))
        {
            return;
        }

        var title = PropertyStoreTitle(lnk);
        if (string.IsNullOrWhiteSpace(title) && string.Equals(label, ShortLabel(path ?? string.Empty), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _labelLogs++;
        var args = string.IsNullOrWhiteSpace(lnk.Arguments) ? "" : lnk.Arguments;
        if (args.Length > 48)
        {
            args = args[..48] + "…";
        }

        Console.WriteLine(
            $"[spike] jump label {kind}: Name=[{lnk.Name}] Title=[{title}] File=[{ShortLabel(path ?? "")}] Args=[{args}] -> [{label}]");
    }

    private static readonly string SummaryInformationFmtId = "f29f85e0-4ff9-1068-ab91-08002b27b3d9";
    private static readonly string StorageItemFmtId = "b725f130-47ef-101a-a5f1-02608c9eebac";

    /// <summary>CRC-64 AppId used for Jump List file names (Hexacorn / JumpListReader).</summary>
    public static string AppIdOf(string appUserModelId)
    {
        var normalized = string.Join("\0", appUserModelId.ToUpperInvariant().ToCharArray()) + "\0";
        var crc = 0xFFFFFFFFFFFFFFFFUL;
        var table = Crc64Table;
        foreach (var ch in normalized)
        {
            crc = (crc >> 8) ^ table[(crc ^ ch) & 0xFF];
        }

        return string.Create(CultureInfo.InvariantCulture, $"{crc:x16}");
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return path;
        }
    }

    private static string AutoDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Windows\Recent\AutomaticDestinations");

    private static string CustomDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Windows\Recent\CustomDestinations");

    private static void EnsureWatchers()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        _autoWatch = Watch(AutoDir());
        _customWatch = Watch(CustomDir());
    }

    private static FileSystemWatcher? Watch(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            void Bust(object _, FileSystemEventArgs __) => BustCache();
            watcher.Changed += Bust;
            watcher.Created += Bust;
            watcher.Deleted += Bust;
            watcher.Renamed += Bust;
            return watcher;
        }
        catch
        {
            return null;
        }
    }

    private static void BustCache()
    {
        lock (Gate)
        {
            Cache.Clear();
            TargetAppIdCache.Clear();
        }
    }

    private static readonly ulong[] Crc64Table = BuildCrc64Table();

    private static ulong[] BuildCrc64Table()
    {
        const ulong poly = 0x92C64265D32139A4UL;
        var table = new ulong[256];
        for (ulong i = 0; i < 256; i++)
        {
            var lv = i;
            for (var j = 0; j < 8; j++)
            {
                var fl = lv & 1;
                lv >>= 1;
                if (fl == 1)
                {
                    lv ^= poly;
                }
            }

            table[i] = lv;
        }

        return table;
    }
}
