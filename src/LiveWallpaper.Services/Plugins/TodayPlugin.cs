using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Services.Today;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Plugins;

/// <summary>
/// Plugin facade over <see cref="ITodayService"/>. The service keeps owning the
/// calendar/WEEEK polling loop; this class translates its updates into plugin
/// snapshots (so they reach WebView2 through the plugin host) and handles the
/// <c>open_task</c> action from the web frontend.
/// </summary>
public sealed class TodayPlugin : IWidgetPlugin
{
    public const string PluginId = "today";
    public const string OpenTaskAction = "open_task";

    private readonly ITodayService _todayService;
    private readonly IConfigManager _configManager;
    private readonly ILogger<TodayPlugin> _logger;

    private IPluginContext? _context;

    public TodayPlugin(
        ITodayService todayService,
        IConfigManager configManager,
        ILogger<TodayPlugin> logger)
    {
        _todayService = todayService;
        _configManager = configManager;
        _logger = logger;
    }

    public string Id => PluginId;

    public string DisplayName => "Today Agenda & WEEEK";

    public bool IsEnabled => _configManager.CurrentConfig.Today.Enabled;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        _todayService.TodayUpdated += OnTodayUpdated;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _todayService.TodayUpdated -= OnTodayUpdated;
        return Task.CompletedTask;
    }

    public Task<PluginSnapshot?> PollSnapshotAsync(CancellationToken ct)
    {
        var current = _todayService.CurrentSnapshot;
        if (current == null)
        {
            return Task.FromResult<PluginSnapshot?>(null);
        }
        return Task.FromResult<PluginSnapshot?>(new PluginSnapshot { PluginId = Id, Payload = current });
    }

    /// <summary>
    /// The web side sends only an opaque task id; the WEEEK URL is resolved here
    /// against the latest snapshot (unknown ids and double clicks are rejected).
    /// </summary>
    public Task<bool> HandleActionAsync(string action, JsonElement payload)
    {
        if (!string.Equals(action, OpenTaskAction, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        if (!payload.TryGetProperty("id", out var idProp))
        {
            return Task.FromResult(false);
        }

        var taskId = idProp.GetString();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(false);
        }

        var uri = _todayService.ResolveTaskUri(taskId, out var reason);
        if (uri == null)
        {
            _logger.LogDebug("open_task ignored: {Reason}", reason);
            return Task.FromResult(false);
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open WEEEK task {Url}", uri);
            return Task.FromResult(false);
        }
    }

    public PluginSettingsManifest? GetSettingsManifest()
    {
        var cfg = _configManager.CurrentConfig.Today;
        var icsExists = !string.IsNullOrWhiteSpace(cfg.GoogleCalendarIcsUrl) ||
                        !string.IsNullOrWhiteSpace(_context?.GetCredential("LiveWallpaper/CalendarIcs"));
        return new PluginSettingsManifest
        {
            PluginId = Id,
            DisplayName = "Сегодня / WEEEK",
            Description = "Повестка дня и задачи WEEEK",
            Fields = new[]
            {
                new PluginSettingsField { Id = "enabled", Label = "Включить виджет", Kind = "toggle", Value = cfg.Enabled },
                new PluginSettingsField { Id = "googleCalendarIcsUrl", Label = "Google Calendar ICS URL", Kind = "url", Value = cfg.GoogleCalendarIcsUrl },
                new PluginSettingsField { Id = "weeekToken", Label = "Токен WEEEK", Kind = "password", SecretExists = false, Hint = "Сохраняется в Credential Manager" },
            }
        };
    }

    private void OnTodayUpdated(object? sender, Core.Today.TodaySnapshot snapshot)
    {
        _context?.NotifyUpdated(Id, snapshot);
    }
}
