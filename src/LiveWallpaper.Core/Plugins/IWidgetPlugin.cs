using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiveWallpaper.Core.Plugins;

/// <summary>
/// Contract for a modular wallpaper widget backed by its own data source.
/// The plugin host discovers registered instances, initializes them with an
/// <see cref="IPluginContext"/>, polls them for snapshots and routes actions
/// coming from web messages to them.
/// </summary>
public interface IWidgetPlugin
{
    /// <summary>Stable machine id; also used as the web message type, e.g. <c>"timeweb"</c>.</summary>
    string Id { get; }

    /// <summary>Human readable name for logs and diagnostics.</summary>
    string DisplayName { get; }

    /// <summary>Config gate: whether the plugin may produce snapshots right now.</summary>
    bool IsEnabled { get; }

    Task InitializeAsync(IPluginContext context, CancellationToken ct);

    Task ShutdownAsync(CancellationToken ct);

    /// <summary>Returns the current snapshot, or <c>null</c> when the plugin has nothing to publish.</summary>
    Task<PluginSnapshot?> PollSnapshotAsync(CancellationToken ct);

    /// <summary>Handles an action routed from the web frontend. Returns <c>true</c> when the action was recognized.</summary>
    Task<bool> HandleActionAsync(string action, JsonElement payload);

    /// <summary>
    /// Optional settings panel for the plugin. Return <c>null</c> when the plugin has no dedicated UI
    /// (core widgets stay in the main settings categories).
    /// </summary>
    PluginSettingsManifest? GetSettingsManifest();
}
