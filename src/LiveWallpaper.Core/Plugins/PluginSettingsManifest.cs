using System.Collections.Generic;

namespace LiveWallpaper.Core.Plugins;

/// <summary>Describes a settings panel contributed by a widget plugin.</summary>
public sealed class PluginSettingsManifest
{
    public string PluginId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<PluginSettingsField> Fields { get; init; } = System.Array.Empty<PluginSettingsField>();
}

public sealed class PluginSettingsField
{
    /// <summary>Stable field id used in settings save payloads under plugins[pluginId][id].</summary>
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Hint { get; init; }
    /// <summary>toggle | text | password | number | url</summary>
    public string Kind { get; init; } = "toggle";
    public object? Value { get; init; }
    public bool SecretExists { get; init; }
}
