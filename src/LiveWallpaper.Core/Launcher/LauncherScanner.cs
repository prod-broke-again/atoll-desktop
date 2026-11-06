using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LiveWallpaper.Core.Launcher;

public sealed class LauncherScanner
{
    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini",
        "thumbs.db",
        "thumbs.db:encryptable",
        ".ds_store",
    };

    private readonly IReadOnlyCollection<string> _excludedFolderNames;
    private readonly Func<string, string>? _iconUrlResolver;

    public LauncherScanner(
        IEnumerable<string>? excludedFolderNames = null,
        Func<string, string>? iconUrlResolver = null)
    {
        _excludedFolderNames = (excludedFolderNames ?? Array.Empty<string>())
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _iconUrlResolver = iconUrlResolver;
    }

    public LauncherScanResult Scan(params string[] desktopDirectories)
    {
        return Scan((IEnumerable<string>)desktopDirectories);
    }

    public LauncherScanResult Scan(IEnumerable<string> desktopDirectories)
    {
        var existingDirs = desktopDirectories
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pinnedItems = new List<LauncherItem>();
        var pinnedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Scan root files for the dock (shortcuts, docs, media — anything openable).
        foreach (var dir in existingDirs)
        {
            foreach (var file in SafeGetFiles(dir))
            {
                if (!IsScannableFile(file)) continue;

                var name = DisplayName(file);
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!pinnedNames.Add(name))
                {
                    continue;
                }

                var id = GenerateStableId("pin", name);
                var iconUrl = _iconUrlResolver?.Invoke(file) ?? string.Empty;

                pinnedItems.Add(new LauncherItem
                {
                    Id = id,
                    Name = name,
                    IconUrl = iconUrl,
                    TargetPath = file
                });
                itemPaths[id] = file;
            }
        }

        // 2. Scan subdirectories for folder tiles (groups).
        var groups = new List<LauncherGroup>();
        var seenGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in existingDirs)
        {
            foreach (var subDir in SafeGetDirectories(dir))
            {
                var folderName = Path.GetFileName(subDir);
                if (string.IsNullOrWhiteSpace(folderName)) continue;

                if (_excludedFolderNames.Contains(folderName))
                {
                    continue;
                }

                try
                {
                    var di = new DirectoryInfo(subDir);
                    if ((di.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }
                }
                catch { }

                if (!seenGroupNames.Add(folderName))
                {
                    continue;
                }

                var items = new List<LauncherItem>();
                foreach (var file in SafeGetFiles(subDir))
                {
                    if (!IsScannableFile(file)) continue;

                    var name = DisplayName(file);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var id = GenerateStableId($"grp_{folderName}", name);
                    var iconUrl = _iconUrlResolver?.Invoke(file) ?? string.Empty;

                    items.Add(new LauncherItem
                    {
                        Id = id,
                        Name = name,
                        IconUrl = iconUrl,
                        TargetPath = file
                    });
                    itemPaths[id] = file;
                }

                var groupId = GenerateStableId("group", folderName);
                groupPaths[groupId] = subDir;

                groups.Add(new LauncherGroup
                {
                    Id = groupId,
                    Name = folderName,
                    FolderPath = subDir,
                    Items = items
                });
            }
        }

        return new LauncherScanResult(
            new LauncherSnapshot { Pinned = pinnedItems, Groups = groups },
            itemPaths,
            groupPaths);
    }

    public static string GenerateStableId(string prefix, string value)
    {
        var input = $"{prefix}:{value.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{prefix}_{hex[..12]}";
    }

    /// <summary>Any non-hidden desktop file except shell junk (desktop.ini, …).</summary>
    public static bool IsScannableFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

        var name = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) return false;
        if (IgnoredFileNames.Contains(name)) return false;

        try
        {
            var attrs = File.GetAttributes(filePath);
            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
            if ((attrs & FileAttributes.Directory) != 0) return false;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static string DisplayName(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        // Shortcuts keep the friendly name without extension; documents keep the base name too
        // (Explorer usually hides known extensions).
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(filePath);
        }

        return Path.GetFileNameWithoutExtension(filePath);
    }

    private static string[] SafeGetFiles(string dir)
    {
        try
        {
            return Directory.GetFiles(dir);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string[] SafeGetDirectories(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

public sealed class LauncherScanResult
{
    public LauncherSnapshot Snapshot { get; }
    public IReadOnlyDictionary<string, string> ItemPaths { get; }
    public IReadOnlyDictionary<string, string> GroupPaths { get; }

    public LauncherScanResult(
        LauncherSnapshot snapshot,
        IReadOnlyDictionary<string, string> itemPaths,
        IReadOnlyDictionary<string, string> groupPaths)
    {
        Snapshot = snapshot;
        ItemPaths = itemPaths;
        GroupPaths = groupPaths;
    }
}
