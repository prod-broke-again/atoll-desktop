using System.Collections.Generic;

namespace LiveWallpaper.Core.Launcher;

public sealed class LauncherSnapshot
{
    public List<LauncherItem> Pinned { get; init; } = new();
    public List<LauncherGroup> Groups { get; init; } = new();
}
