using System;
using LiveWallpaper.Core.Launcher;

namespace LiveWallpaper.Services.Launcher;

public interface ILauncherService
{
    LauncherSnapshot CurrentSnapshot { get; }
    event EventHandler<LauncherSnapshot>? LauncherUpdated;
    void Refresh();
    bool Launch(string id);
    bool OpenGroup(string groupId);
    bool TryGetItemPath(string id, out string? path);
    bool TryGetGroupPath(string id, out string? path);
    string? CreateFolder(string name);
    bool RenameFolder(string groupId, string newName);
    bool MoveItem(string itemId, string? targetGroupId);
    bool RenameItem(string itemId, string newName);
    bool DeleteItem(string itemId);
}
