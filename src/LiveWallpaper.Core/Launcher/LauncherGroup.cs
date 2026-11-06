using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LiveWallpaper.Core.Launcher;

public sealed class LauncherGroup
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    [JsonIgnore]
    public string FolderPath { get; init; } = string.Empty;

    public List<LauncherItem> Items { get; init; } = new();
}
