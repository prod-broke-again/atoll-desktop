using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LiveWallpaper.Core.Launcher;

public sealed class LauncherSecurityManager
{
    private static readonly TimeSpan LaunchDebounceInterval = TimeSpan.FromMilliseconds(1500);

    private readonly ConcurrentDictionary<string, string> _itemPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _groupPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastLaunchTimes = new(StringComparer.OrdinalIgnoreCase);

    public void UpdateMappings(IReadOnlyDictionary<string, string> itemPaths, IReadOnlyDictionary<string, string> groupPaths)
    {
        _itemPaths.Clear();
        foreach (var (k, v) in itemPaths)
        {
            _itemPaths[k] = v;
        }

        _groupPaths.Clear();
        foreach (var (k, v) in groupPaths)
        {
            _groupPaths[k] = v;
        }
    }

    public bool TryGetItemPath(string id, out string path)
    {
        return _itemPaths.TryGetValue(id, out path!);
    }

    public bool TryGetGroupPath(string id, out string path)
    {
        return _groupPaths.TryGetValue(id, out path!);
    }

    public bool ValidateLaunch(string id, out string? targetPath, out string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(id) || !_itemPaths.TryGetValue(id, out targetPath))
        {
            targetPath = null;
            failureReason = "Unknown or unauthorized item id.";
            return false;
        }

        var now = DateTime.UtcNow;
        if (_lastLaunchTimes.TryGetValue(id, out var lastTime) && (now - lastTime) < LaunchDebounceInterval)
        {
            failureReason = $"Launch request rate-limited (cooldown 1.5s). Elapsed: {(now - lastTime).TotalMilliseconds:F0}ms";
            return false;
        }

        _lastLaunchTimes[id] = now;
        failureReason = null;
        return true;
    }
}
