using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Services.Server;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Plugins;

/// <summary>
/// Plugin facade over <see cref="IServerWidgetService"/>. The service keeps
/// owning the SSH metrics stream, site health checks and alert evaluation; this
/// class forwards its state/metric updates as plugin snapshots (so they reach
/// WebView2 through the plugin host) and handles the <c>server_open</c> action
/// (SSH terminal, site URLs) plus the privacy toggle coming from the web frontend.
/// </summary>
public sealed class ServerPlugin : IWidgetPlugin
{
    public const string PluginId = "server";
    public const string ServerOpenAction = "server_open";

    /// <summary>High-frequency metric points go out under this id, not <see cref="PluginId"/>.</summary>
    public const string MetricsPluginId = "serverMetrics";

    public const string TogglePrivacyAction = "toggle_privacy";

    /// <summary>Alias accepted for the privacy toggle (kept for older web builds).</summary>
    public const string SetPrivacyModeAction = "SetPrivacyMode";

    private readonly IServerWidgetService _serverService;
    private readonly IConfigManager _configManager;
    private readonly ILogger<ServerPlugin> _logger;

    private IPluginContext? _context;

    public ServerPlugin(
        IServerWidgetService serverService,
        IConfigManager configManager,
        ILogger<ServerPlugin> logger)
    {
        _serverService = serverService;
        _configManager = configManager;
        _logger = logger;
    }

    public string Id => PluginId;

    public string DisplayName => "DevOps Server Monitor";

    public bool IsEnabled => _configManager.CurrentConfig.Server.Enabled;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        _serverService.StateSnapshotUpdated += OnStateSnapshotUpdated;
        _serverService.MetricPointReceived += OnMetricPointReceived;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _serverService.StateSnapshotUpdated -= OnStateSnapshotUpdated;
        _serverService.MetricPointReceived -= OnMetricPointReceived;
        return Task.CompletedTask;
    }

    public Task<PluginSnapshot?> PollSnapshotAsync(CancellationToken ct)
    {
        var current = _serverService.CurrentSnapshot;
        if (current == null)
        {
            return Task.FromResult<PluginSnapshot?>(null);
        }
        return Task.FromResult<PluginSnapshot?>(new PluginSnapshot { PluginId = Id, Payload = current });
    }

    public Task<bool> HandleActionAsync(string action, JsonElement payload)
    {
        if (string.Equals(action, TogglePrivacyAction, StringComparison.Ordinal) ||
            string.Equals(action, SetPrivacyModeAction, StringComparison.Ordinal))
        {
            TogglePrivacyMode();
            return Task.FromResult(true);
        }

        if (!string.Equals(action, ServerOpenAction, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        if (!payload.TryGetProperty("target", out var targetProp))
        {
            return Task.FromResult(false);
        }

        var target = targetProp.GetString();
        if (string.Equals(target, "ssh", StringComparison.Ordinal))
        {
            OpenSshTerminal();
            return Task.FromResult(true);
        }

        if (string.Equals(target, "site", StringComparison.Ordinal))
        {
            OpenSite(payload);
            return Task.FromResult(true);
        }

        // e.g. "timeweb": owned by the Timeweb plugin, not by us.
        return Task.FromResult(false);
    }

    private void TogglePrivacyMode()
    {
        var cfg = _configManager.CurrentConfig;
        var enabled = !cfg.Server.PrivacyMode;
        cfg.Server.PrivacyMode = enabled;
        _configManager.Save(cfg);
        _serverService.SetPrivacyMode(enabled);
    }

    private void OpenSshTerminal()
    {
        string alias = _configManager.CurrentConfig.Server.SshAlias;
        if (string.IsNullOrWhiteSpace(alias)) alias = "myserver";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"ssh {alias}",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k ssh {alias}",
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open SSH terminal for alias {Alias}", alias);
            }
        }
    }

    private void OpenSite(JsonElement payload)
    {
        if (!payload.TryGetProperty("name", out var nameProp))
        {
            return;
        }

        var name = nameProp.GetString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var matchingSite = _configManager.CurrentConfig.Server.Sites
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (matchingSite == null || !Uri.TryCreate(matchingSite.Url, UriKind.Absolute, out var siteUri))
        {
            _logger.LogDebug("server_open site ignored: '{Name}' is not a configured site.", name);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(siteUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open site {Site}", matchingSite.Name);
        }
    }

    private void OnStateSnapshotUpdated(Core.Server.ServerStateSnapshot snapshot)
    {
        if (!IsEnabled) return;
        _context?.NotifyUpdated(PluginId, snapshot);
    }

    public PluginSettingsManifest? GetSettingsManifest()
    {
        var cfg = _configManager.CurrentConfig.Server;
        return new PluginSettingsManifest
        {
            PluginId = Id,
            DisplayName = "Сервер",
            Description = "SSH-метрики и статус сайтов",
            Fields = new[]
            {
                new PluginSettingsField { Id = "enabled", Label = "Включить виджет", Kind = "toggle", Value = cfg.Enabled },
                new PluginSettingsField { Id = "sshAlias", Label = "SSH alias", Kind = "text", Value = cfg.SshAlias },
                new PluginSettingsField { Id = "privacyMode", Label = "Режим приватности", Kind = "toggle", Value = cfg.PrivacyMode },
            }
        };
    }

    private void OnMetricPointReceived(Core.Server.MetricPoint point)
    {
        if (!IsEnabled) return;
        _context?.NotifyUpdated(MetricsPluginId, point);
    }
}
