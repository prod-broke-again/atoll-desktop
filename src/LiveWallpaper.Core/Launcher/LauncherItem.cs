using System.Text.Json.Serialization;

namespace LiveWallpaper.Core.Launcher;

public sealed class LauncherItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;

    [JsonIgnore]
    public string TargetPath { get; init; } = string.Empty;
}
