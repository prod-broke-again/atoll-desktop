namespace LiveWallpaper.Core.Plugins;

/// <summary>
/// What the host gives a plugin: secure credential resolution and a channel
/// to push snapshot updates that get forwarded to WebView2.
/// </summary>
public interface IPluginContext
{
    /// <summary>Reads a secret for the given credential target (e.g. <c>"LiveWallpaper/Timeweb"</c>), or <c>null</c> when it is not stored.</summary>
    string? GetCredential(string targetKey);

    /// <summary>
    /// Pushes a snapshot payload to the host. The host wraps it into a
    /// <see cref="PluginSnapshot"/> and raises its <c>SnapshotReceived</c> event,
    /// so the app layer can post <c>{ type = pluginId, data = data }</c> to WebView2.
    /// </summary>
    void NotifyUpdated(string pluginId, object data);
}
