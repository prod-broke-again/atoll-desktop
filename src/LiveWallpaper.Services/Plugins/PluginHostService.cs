using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Native;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Plugins;

/// <summary>
/// Host runtime for <see cref="IWidgetPlugin"/> instances. Starts and stops
/// the plugins with the application lifetime, hands them an
/// <see cref="IPluginContext"/> (this very service), polls enabled plugins for
/// snapshots and routes actions from web messages to the target plugin.
/// Every snapshot flows through <see cref="SnapshotReceived"/>, so the app
/// layer can post it to WebView2.
/// </summary>
public sealed class PluginHostService : IHostedService, IPluginContext, IDisposable
{
    private readonly IReadOnlyList<IWidgetPlugin> _plugins;
    private readonly PluginHostOptions _options;
    private readonly ILogger<PluginHostService> _logger;
    private readonly ConcurrentDictionary<string, PluginSnapshot> _latestSnapshots = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopCts = new();

    private Task? _pollLoop;

    public PluginHostService(
        IEnumerable<IWidgetPlugin> plugins,
        PluginHostOptions options,
        ILogger<PluginHostService> logger)
    {
        _plugins = plugins.ToList();
        _options = options;
        _logger = logger;
    }

    /// <summary>Raised whenever a plugin publishes a new snapshot.</summary>
    public event Action<PluginSnapshot>? SnapshotReceived;

    /// <summary>Latest snapshot per plugin id; used to re-hydrate the frontend after a reload.</summary>
    public IReadOnlyDictionary<string, PluginSnapshot> CurrentSnapshots => _latestSnapshots;

    public PluginSnapshot? GetCurrentSnapshot(string pluginId) =>
        _latestSnapshots.TryGetValue(pluginId, out var snapshot) ? snapshot : null;

    public IWidgetPlugin? FindPlugin(string pluginId) =>
        _plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.Ordinal));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Plugin host starting with {Count} plugin(s): {Plugins}",
            _plugins.Count,
            string.Join(", ", _plugins.Select(p => $"{p.Id} ({p.DisplayName})")));

        foreach (var plugin in _plugins)
        {
            try
            {
                await plugin.InitializeAsync(this, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin '{PluginId}' failed to initialize.", plugin.Id);
            }
        }

        _pollLoop = Task.Run(() => PollLoopAsync(_stopCts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts.Cancel();

        foreach (var plugin in _plugins)
        {
            try
            {
                await plugin.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin '{PluginId}' failed to shut down cleanly.", plugin.Id);
            }
        }

        if (_pollLoop != null)
        {
            try
            {
                await _pollLoop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Plugin poll loop did not stop within the grace period.");
            }
        }
    }

    /// <summary>Routes an action coming from a web message to the target plugin.</summary>
    public async Task<bool> DispatchActionAsync(string pluginId, string action, JsonElement payload)
    {
        var plugin = FindPlugin(pluginId);
        if (plugin == null)
        {
            _logger.LogDebug("Ignoring action '{Action}' for unknown plugin '{PluginId}'.", action, pluginId);
            return false;
        }

        try
        {
            return await plugin.HandleActionAsync(action, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin '{PluginId}' failed to handle action '{Action}'.", pluginId, action);
            return false;
        }
    }

    // ---- IPluginContext ----

    public string? GetCredential(string targetKey) => CredentialManager.ReadSecret(targetKey);

    public void NotifyUpdated(string pluginId, object data)
    {
        Publish(new PluginSnapshot { PluginId = pluginId, Payload = data });
    }

    public void Dispose()
    {
        _stopCts.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var plugin in _plugins)
            {
                if (!plugin.IsEnabled)
                {
                    continue;
                }

                PluginSnapshot? snapshot;
                try
                {
                    snapshot = await plugin.PollSnapshotAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Plugin '{PluginId}' poll failed.", plugin.Id);
                    continue;
                }

                if (snapshot != null)
                {
                    Publish(snapshot);
                }
            }

            try
            {
                await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void Publish(PluginSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.PluginId))
        {
            return;
        }

        if (_latestSnapshots.TryGetValue(snapshot.PluginId, out var previous) &&
            ReferenceEquals(previous.Payload, snapshot.Payload))
        {
            // Same payload instance already published (e.g. the poll right after an event push).
            return;
        }

        _latestSnapshots[snapshot.PluginId] = snapshot;
        SnapshotReceived?.Invoke(snapshot);
    }
}
