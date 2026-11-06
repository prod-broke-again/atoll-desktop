using System;

namespace LiveWallpaper.Services.Plugins;

/// <summary>Tuning knobs for <see cref="PluginHostService"/>.</summary>
public sealed class PluginHostOptions
{
    /// <summary>How often the host asks every enabled plugin for a fresh snapshot.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
}
