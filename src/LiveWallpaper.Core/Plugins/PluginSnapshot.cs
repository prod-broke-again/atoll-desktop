using System;

namespace LiveWallpaper.Core.Plugins;

/// <summary>A plugin payload together with its id and capture timestamp.</summary>
public sealed record PluginSnapshot
{
    public required string PluginId { get; init; }

    public required object Payload { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
