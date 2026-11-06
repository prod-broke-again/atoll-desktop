using System.Text.Json;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Core.Telegram;
using LiveWallpaper.Services.Plugins;
using LiveWallpaper.Services.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveWallpaper.Core.Tests.Plugins;

public sealed class TelegramPluginTests
{
    [Fact]
    public void Id_And_DisplayName_AreStable()
    {
        var plugin = CreatePlugin();

        Assert.Equal("telegram", plugin.Id);
        Assert.Equal("Telegram Messenger", plugin.DisplayName);
    }

    [Fact]
    public void IsEnabled_MirrorsTelegramConfig()
    {
        var config = AppConfig.CreateDefault();
        var plugin = CreatePlugin(config);
        Assert.True(plugin.IsEnabled);

        config.Telegram.Enabled = false;
        Assert.False(plugin.IsEnabled);
    }

    [Fact]
    public async Task ServiceUpdate_ForwardedThroughContextNotify()
    {
        var service = new StubTelegramService();
        var plugin = CreatePlugin(service: service);
        var context = new RecordingContext();
        await plugin.InitializeAsync(context, CancellationToken.None);
        var snapshot = new TelegramSnapshot { Enabled = true, Available = true };

        service.RaiseUpdated(snapshot);

        var call = Assert.Single(context.Notifications);
        Assert.Equal("telegram", call.PluginId);
        Assert.Same(snapshot, call.Data);
    }

    [Fact]
    public async Task Shutdown_DetachesFromServiceUpdates()
    {
        var service = new StubTelegramService();
        var plugin = CreatePlugin(service: service);
        var context = new RecordingContext();
        await plugin.InitializeAsync(context, CancellationToken.None);

        await plugin.ShutdownAsync(CancellationToken.None);
        service.RaiseUpdated(new TelegramSnapshot { Enabled = true });

        Assert.Empty(context.Notifications);
    }

    [Fact]
    public async Task PollSnapshotAsync_MirrorsServiceCurrent()
    {
        var service = new StubTelegramService();
        var plugin = CreatePlugin(service: service);
        await plugin.InitializeAsync(new RecordingContext(), CancellationToken.None);

        Assert.Null(await plugin.PollSnapshotAsync(CancellationToken.None));

        var snapshot = new TelegramSnapshot { Enabled = true, Available = true };
        service.Current = snapshot;

        var polled = await plugin.PollSnapshotAsync(CancellationToken.None);
        Assert.NotNull(polled);
        Assert.Equal("telegram", polled!.PluginId);
        Assert.Same(snapshot, polled.Payload);
    }

    [Fact]
    public async Task HandleActionAsync_UnknownAction_ReturnsFalse()
    {
        var plugin = CreatePlugin();
        using var doc = JsonDocument.Parse("{\"id\":42}");

        var handled = await plugin.HandleActionAsync("whatever", doc.RootElement);

        Assert.False(handled);
    }

    [Fact]
    public async Task HandleActionAsync_MissingId_ReturnsFalse()
    {
        var plugin = CreatePlugin();
        using var doc = JsonDocument.Parse("{}");

        var handled = await plugin.HandleActionAsync(TelegramPlugin.OpenChatAction, doc.RootElement);

        Assert.False(handled);
    }

    [Fact]
    public async Task HandleActionAsync_UnknownChatId_ReturnsFalse()
    {
        var plugin = CreatePlugin();
        using var doc = JsonDocument.Parse("{\"id\":777}");

        var handled = await plugin.HandleActionAsync(TelegramPlugin.OpenChatAction, doc.RootElement);

        Assert.False(handled);
    }

    private static TelegramPlugin CreatePlugin(
        AppConfig? config = null,
        StubTelegramService? service = null)
    {
        var stubConfig = new StubConfigManager(config ?? AppConfig.CreateDefault());
        return new TelegramPlugin(service ?? new StubTelegramService(), stubConfig, NullLogger<TelegramPlugin>.Instance);
    }

    private sealed class StubTelegramService : ITelegramUnreadService
    {
        public TelegramSnapshot? Current { get; set; }
        public event EventHandler<TelegramSnapshot>? Updated;

        public void RaiseUpdated(TelegramSnapshot snapshot)
        {
            Current = snapshot;
            Updated?.Invoke(this, snapshot);
        }

        public Uri? ResolveChatUri(long id, out string? reason)
        {
            reason = "Chat id is not in the latest snapshot.";
            return null;
        }
    }

    private sealed class RecordingContext : IPluginContext
    {
        public List<(string PluginId, object Data)> Notifications { get; } = new();

        public string? GetCredential(string targetKey) => null;

        public void NotifyUpdated(string pluginId, object data) => Notifications.Add((pluginId, data));
    }

    private sealed class StubConfigManager : IConfigManager
    {
        public StubConfigManager(AppConfig config) => CurrentConfig = config;

        public AppConfig CurrentConfig { get; private set; }
        public string ConfigFilePath => string.Empty;
        public event EventHandler<AppConfig>? ConfigChanged;

        public AppConfig Load() => CurrentConfig;

        public void Save(AppConfig config)
        {
            CurrentConfig = config;
            ConfigChanged?.Invoke(this, config);
        }
    }
}
