using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Native;
using LiveWallpaper.Services.Server;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Plugins;

/// <summary>
/// Plugin wrapper around <see cref="ITimewebClient"/>. Publishes Timeweb Cloud
/// telemetry (balance, days left, S3 bucket usage, backup freshness) as plugin
/// snapshots and opens the cloud console for the <c>open_console</c> action.
/// The client itself stays untouched: it remains the single source of truth for
/// the server widget snapshot.
/// </summary>
public sealed class TimewebPlugin : IWidgetPlugin
{
    public const string PluginId = "timeweb";
    public const string OpenConsoleAction = "open_console";

    private const string ConsoleUrl = "https://timeweb.cloud";

    private readonly ITimewebClient _timewebClient;
    private readonly IConfigManager _configManager;
    private readonly ILogger<TimewebPlugin> _logger;

    private IPluginContext? _context;

    public TimewebPlugin(
        ITimewebClient timewebClient,
        IConfigManager configManager,
        ILogger<TimewebPlugin> logger)
    {
        _timewebClient = timewebClient;
        _configManager = configManager;
        _logger = logger;
    }

    public string Id => PluginId;

    public string DisplayName => "Timeweb Cloud";

    // Same gate the server widget uses, so the plugin never runs ahead of the widget's own polling.
    public bool IsEnabled
    {
        get
        {
            var server = _configManager.CurrentConfig.Server;
            return server.Enabled && server.TimewebEnabled;
        }
    }

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        if (string.IsNullOrWhiteSpace(context.GetCredential(CredentialManager.DefaultTimewebTarget)))
        {
            _logger.LogDebug(
                "Timeweb token is not stored yet (credential target {Target}). The plugin stays silent until it appears.",
                CredentialManager.DefaultTimewebTarget);
        }
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<PluginSnapshot?> PollSnapshotAsync(CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return null;
        }

        // Credentials come exclusively through the context; the plugin never touches the vault itself.
        var token = _context?.GetCredential(CredentialManager.DefaultTimewebTarget);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var snapshot = await _timewebClient.PollAsync(ct).ConfigureAwait(false);
            return new PluginSnapshot { PluginId = Id, Payload = snapshot };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timeweb plugin poll failed.");
            return null;
        }
    }

    public Task<bool> HandleActionAsync(string action, JsonElement payload)
    {
        if (!string.Equals(action, OpenConsoleAction, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        try
        {
            Process.Start(new ProcessStartInfo(ConsoleUrl) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Timeweb console");
            return Task.FromResult(false);
        }
    }

    public PluginSettingsManifest? GetSettingsManifest()
    {
        var server = _configManager.CurrentConfig.Server;
        var tokenExists = !string.IsNullOrWhiteSpace(
            _context?.GetCredential(CredentialManager.DefaultTimewebTarget));
        return new PluginSettingsManifest
        {
            PluginId = Id,
            DisplayName = "Timeweb Cloud",
            Description = "Баланс, S3 и бэкапы Timeweb",
            Fields = new[]
            {
                new PluginSettingsField { Id = "timewebEnabled", Label = "Включить Timeweb", Kind = "toggle", Value = server.TimewebEnabled },
                new PluginSettingsField
                {
                    Id = "token",
                    Label = "API-токен",
                    Kind = "password",
                    SecretExists = tokenExists,
                    Hint = "Хранится в Windows Credential Manager"
                },
            }
        };
    }
}
