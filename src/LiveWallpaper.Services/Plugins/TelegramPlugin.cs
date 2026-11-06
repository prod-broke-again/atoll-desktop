using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Core.Telegram;
using LiveWallpaper.Services.Telegram;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Plugins;

/// <summary>
/// Plugin facade over <see cref="ITelegramUnreadService"/>. The service keeps
/// owning the polling loop; this class translates its updates into plugin
/// snapshots (so they reach WebView2 through the plugin host) and handles the
/// <c>open_telegram_chat</c> action from the web frontend.
/// </summary>
public sealed class TelegramPlugin : IWidgetPlugin
{
    public const string PluginId = "telegram";
    public const string OpenChatAction = "open_telegram_chat";

    private readonly ITelegramUnreadService _unreadService;
    private readonly IConfigManager _configManager;
    private readonly ILogger<TelegramPlugin> _logger;

    private IPluginContext? _context;

    public TelegramPlugin(
        ITelegramUnreadService unreadService,
        IConfigManager configManager,
        ILogger<TelegramPlugin> logger)
    {
        _unreadService = unreadService;
        _configManager = configManager;
        _logger = logger;
    }

    public string Id => PluginId;

    public string DisplayName => "Telegram Messenger";

    public bool IsEnabled => _configManager.CurrentConfig.Telegram.Enabled;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        _unreadService.Updated += OnUnreadUpdated;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _unreadService.Updated -= OnUnreadUpdated;
        return Task.CompletedTask;
    }

    public Task<PluginSnapshot?> PollSnapshotAsync(CancellationToken ct)
    {
        var current = _unreadService.Current;
        if (current == null)
        {
            return Task.FromResult<PluginSnapshot?>(null);
        }
        return Task.FromResult<PluginSnapshot?>(new PluginSnapshot { PluginId = Id, Payload = current });
    }

    /// <summary>
    /// The web side sends only an id from the last snapshot; the link is resolved here.
    /// </summary>
    public Task<bool> HandleActionAsync(string action, JsonElement payload)
    {
        if (!string.Equals(action, OpenChatAction, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        if (!payload.TryGetProperty("id", out var idProp) || !idProp.TryGetInt64(out var chatId))
        {
            return Task.FromResult(false);
        }

        var uri = _unreadService.ResolveChatUri(chatId, out var reason);
        if (uri == null)
        {
            _logger.LogDebug("open_telegram_chat ignored: {Reason}", reason);
            return Task.FromResult(false);
        }

        try
        {
            Process.Start(new ProcessStartInfo(TelegramLinks.ToShellTarget(uri)) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Telegram chat {Url}", uri);
            return Task.FromResult(false);
        }
    }

    public PluginSettingsManifest? GetSettingsManifest()
    {
        var cfg = _configManager.CurrentConfig.Telegram;
        return new PluginSettingsManifest
        {
            PluginId = Id,
            DisplayName = "Telegram",
            Description = "Непрочитанные чаты на рабочем столе",
            Fields = new[]
            {
                new PluginSettingsField { Id = "enabled", Label = "Включить виджет", Kind = "toggle", Value = cfg.Enabled },
                new PluginSettingsField { Id = "baseUrl", Label = "Base URL API", Kind = "url", Value = cfg.BaseUrl, Hint = "Локальный bridge / HTTP API" },
            }
        };
    }

    private void OnUnreadUpdated(object? sender, TelegramSnapshot snapshot)
    {
        _context?.NotifyUpdated(Id, snapshot);
    }
}
