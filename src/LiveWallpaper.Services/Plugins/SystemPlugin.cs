using System;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Services.Telemetry;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Plugins;

/// <summary>
/// Plugin facade over <see cref="ITelemetryService"/> (CPU / RAM / GPU / net gauges).
/// </summary>
public sealed class SystemPlugin : IWidgetPlugin
{
    public const string PluginId = "system";

    private readonly ITelemetryService _telemetry;
    private readonly IConfigManager _configManager;
    private readonly ILogger<SystemPlugin> _logger;

    private IPluginContext? _context;

    public SystemPlugin(
        ITelemetryService telemetry,
        IConfigManager configManager,
        ILogger<SystemPlugin> logger)
    {
        _telemetry = telemetry;
        _configManager = configManager;
        _logger = logger;
    }

    public string Id => PluginId;

    public string DisplayName => "System Metrics";

    public bool IsEnabled => _configManager.CurrentConfig.Widgets.ShowSystemMetrics;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        _telemetry.TelemetryUpdated += OnTelemetryUpdated;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _telemetry.TelemetryUpdated -= OnTelemetryUpdated;
        return Task.CompletedTask;
    }

    public Task<PluginSnapshot?> PollSnapshotAsync(CancellationToken ct)
    {
        var current = _telemetry.CurrentTelemetry;
        if (current == null || !IsEnabled)
        {
            return Task.FromResult<PluginSnapshot?>(null);
        }

        return Task.FromResult<PluginSnapshot?>(new PluginSnapshot { PluginId = Id, Payload = current });
    }

    public Task<bool> HandleActionAsync(string action, System.Text.Json.JsonElement payload) =>
        Task.FromResult(false);

    public PluginSettingsManifest? GetSettingsManifest()
    {
        var enabled = _configManager.CurrentConfig.Widgets.ShowSystemMetrics;
        return new PluginSettingsManifest
        {
            PluginId = Id,
            DisplayName = "Система",
            Description = "CPU, RAM, GPU и сеть на рабочем столе",
            Fields = new[]
            {
                new PluginSettingsField
                {
                    Id = "enabled",
                    Label = "Включить виджет",
                    Kind = "toggle",
                    Value = enabled,
                },
            },
        };
    }

    private void OnTelemetryUpdated(object? sender, TelemetryData data)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            _context?.NotifyUpdated(PluginId, data);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish system telemetry snapshot.");
        }
    }
}
