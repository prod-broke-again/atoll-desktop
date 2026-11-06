using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Launcher;
using LiveWallpaper.Native;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Launcher;

public sealed class LauncherService : BackgroundService, ILauncherService
{
    private readonly IConfigManager _configManager;
    private readonly ILogger<LauncherService> _logger;
    private readonly IconExtractor _iconExtractor;
    private readonly LauncherSecurityManager _security = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _lock = new();

    private System.Threading.Timer? _debounceTimer;
    private LauncherSnapshot _currentSnapshot = new();

    public LauncherSnapshot CurrentSnapshot
    {
        get
        {
            lock (_lock)
            {
                return _currentSnapshot;
            }
        }
        private set
        {
            lock (_lock)
            {
                _currentSnapshot = value;
            }
        }
    }

    public event EventHandler<LauncherSnapshot>? LauncherUpdated;

    public LauncherService(
        IConfigManager configManager,
        ILogger<LauncherService> logger,
        ILogger<IconExtractor> iconLogger)
    {
        _configManager = configManager;
        _logger = logger;

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveWallpaper",
            "IconCache");

        _iconExtractor = new IconExtractor(cacheDir, iconLogger);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LauncherService started. Initial desktop scan...");
        Refresh();
        SetupWatchers();

        _configManager.ConfigChanged += OnConfigChanged;
        return Task.CompletedTask;
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        Refresh();
    }

    public void Refresh()
    {
        Task.Run(() =>
        {
            try
            {
                var cfg = _configManager.CurrentConfig.Launcher;
                if (!cfg.Enabled)
                {
                    _logger.LogInformation("Launcher is disabled in config. Clearing snapshot.");
                    var empty = new LauncherSnapshot();
                    CurrentSnapshot = empty;
                    _security.UpdateMappings(new Dictionary<string, string>(), new Dictionary<string, string>());
                    LauncherUpdated?.Invoke(this, empty);
                    return;
                }

                var userDesk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var commonDesk = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

                _logger.LogInformation("Scanning desktop directories: '{UserDesk}', '{CommonDesk}'", userDesk, commonDesk);

                var scanner = new LauncherScanner(
                    excludedFolderNames: cfg.ExcludedFolders,
                    iconUrlResolver: path => _iconExtractor.GetOrExtractIconUrl(path));

                var result = scanner.Scan(userDesk, commonDesk);

                CurrentSnapshot = result.Snapshot;
                _security.UpdateMappings(result.ItemPaths, result.GroupPaths);

                _logger.LogInformation(
                    "Desktop scan completed. Pinned items: {PinnedCount}, Groups: {GroupCount}",
                    result.Snapshot.Pinned.Count,
                    result.Snapshot.Groups.Count);

                LauncherUpdated?.Invoke(this, result.Snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while executing desktop launcher scan.");
            }
        });
    }

    public bool Launch(string id)
    {
        if (!_security.ValidateLaunch(id, out var targetPath, out var reason))
        {
            _logger.LogWarning("Launch rejected for id '{Id}': {Reason}", id, reason);
            return false;
        }

        try
        {
            _logger.LogInformation("Launching application for id '{Id}' -> '{Path}'", id, targetPath);

            var workingDir = string.Empty;
            try
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    workingDir = dir;
                }
            }
            catch { }

            var psi = new ProcessStartInfo
            {
                FileName = targetPath!,
                UseShellExecute = true,
                WorkingDirectory = workingDir
            };

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start process for target '{Path}' (id: '{Id}')", targetPath, id);
            return false;
        }
    }

    public bool OpenGroup(string groupId)
    {
        if (!_security.TryGetGroupPath(groupId, out var folderPath))
        {
            _logger.LogWarning("OpenGroup rejected for unknown group id '{GroupId}'", groupId);
            return false;
        }

        try
        {
            if (!Directory.Exists(folderPath))
            {
                _logger.LogWarning("OpenGroup folder '{Path}' does not exist", folderPath);
                return false;
            }

            _logger.LogInformation("Opening explorer for group '{GroupId}' -> '{Path}'", groupId, folderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open folder '{Path}' in Explorer", folderPath);
            return false;
        }
    }

    public bool TryGetItemPath(string id, out string? path) => _security.TryGetItemPath(id, out path);

    public bool TryGetGroupPath(string id, out string? path) => _security.TryGetGroupPath(id, out path);

    public string? CreateFolder(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        var invalid = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalid) >= 0)
        {
            _logger.LogWarning("CreateFolder rejected invalid name '{Name}'", name);
            return null;
        }

        var userDesk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(userDesk, name);
        if (Directory.Exists(path))
        {
            _logger.LogWarning("CreateFolder folder already exists at '{Path}'", path);
            return null;
        }

        try
        {
            Directory.CreateDirectory(path);
            _logger.LogInformation("Created desktop folder '{Path}'", path);
            Refresh();
            return LauncherScanner.GenerateStableId("group", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create desktop folder '{Name}'", name);
            return null;
        }
    }

    public bool RenameFolder(string groupId, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return false;

        if (!_security.TryGetGroupPath(groupId, out var folderPath) || string.IsNullOrEmpty(folderPath))
        {
            _logger.LogWarning("RenameFolder unknown group '{GroupId}'", groupId);
            return false;
        }

        if (!ShellFileActions.Rename(folderPath, newName))
        {
            _logger.LogWarning("RenameFolder failed for '{Path}' -> '{NewName}'", folderPath, newName);
            return false;
        }

        _logger.LogInformation("Renamed folder '{OldPath}' to '{NewName}'", folderPath, newName);
        Refresh();
        return true;
    }

    public bool MoveItem(string itemId, string? targetGroupId)
    {
        if (!_security.TryGetItemPath(itemId, out var sourcePath) || string.IsNullOrEmpty(sourcePath))
        {
            _logger.LogWarning("MoveItem unknown item '{ItemId}'", itemId);
            return false;
        }

        string destDir;
        if (string.IsNullOrWhiteSpace(targetGroupId))
        {
            destDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
        else if (!_security.TryGetGroupPath(targetGroupId, out var groupPath) || string.IsNullOrEmpty(groupPath))
        {
            _logger.LogWarning("MoveItem unknown target group '{GroupId}'", targetGroupId);
            return false;
        }
        else
        {
            destDir = groupPath;
        }

        try
        {
            var fileName = Path.GetFileName(sourcePath);
            var dest = Path.Combine(destDir, fileName);
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                return true; // already there
            }

            if (File.Exists(dest))
            {
                dest = NextAvailablePath(destDir, fileName);
            }

            File.Move(sourcePath, dest);
            _logger.LogInformation("Moved '{Source}' -> '{Dest}'", sourcePath, dest);
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveItem failed for '{Source}'", sourcePath);
            return false;
        }
    }

    private static string NextAvailablePath(string destDir, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(destDir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(destDir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    public bool RenameItem(string itemId, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return false;

        if (!_security.TryGetItemPath(itemId, out var path) || string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("RenameItem unknown item '{ItemId}'", itemId);
            return false;
        }

        if (!ShellFileActions.Rename(path, newName))
        {
            _logger.LogWarning("RenameItem failed for '{Path}' -> '{NewName}'", path, newName);
            return false;
        }

        _logger.LogInformation("Renamed item '{Path}' to '{NewName}'", path, newName);
        Refresh();
        return true;
    }

    public bool DeleteItem(string itemId)
    {
        if (!_security.TryGetItemPath(itemId, out var path) || string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("DeleteItem unknown item '{ItemId}'", itemId);
            return false;
        }

        if (!ShellFileActions.Delete(path))
        {
            _logger.LogWarning("DeleteItem failed for '{Path}'", path);
            return false;
        }

        _logger.LogInformation("Deleted item '{Path}'", path);
        Refresh();
        return true;
    }

    private void SetupWatchers()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers)
            {
                w.Dispose();
            }
            _watchers.Clear();

            var paths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;

                try
                {
                    var watcher = new FileSystemWatcher(path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                        EnableRaisingEvents = true
                    };

                    watcher.Created += OnFileSystemChanged;
                    watcher.Deleted += OnFileSystemChanged;
                    watcher.Renamed += OnFileSystemChanged;
                    watcher.Changed += OnFileSystemChanged;

                    _watchers.Add(watcher);
                    _logger.LogInformation("FileSystemWatcher attached to desktop path: '{Path}'", path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to attach FileSystemWatcher to '{Path}'", path);
                }
            }
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        // 500ms debounce
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Threading.Timer(_ =>
        {
            _logger.LogInformation("Desktop file change detected ({ChangeType}: '{Name}'). Refreshing launcher...", e.ChangeType, e.Name);
            Refresh();
        }, null, 500, Timeout.Infinite);
    }

    public override void Dispose()
    {
        _configManager.ConfigChanged -= OnConfigChanged;
        lock (_watchers)
        {
            foreach (var w in _watchers)
            {
                w.Dispose();
            }
            _watchers.Clear();
        }
        _debounceTimer?.Dispose();
        base.Dispose();
    }
}
