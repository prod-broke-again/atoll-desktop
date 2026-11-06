using System.Text.Json;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Core.Today;
using LiveWallpaper.Services.Plugins;
using LiveWallpaper.Services.Today;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveWallpaper.Core.Tests.Plugins;

public sealed class TodayPluginTests
{
    [Fact]
    public void Id_And_DisplayName_AreStable()
    {
        var plugin = CreatePlugin();

        Assert.Equal("today", plugin.Id);
        Assert.Equal("Today Agenda & WEEEK", plugin.DisplayName);
    }

    [Fact]
    public void IsEnabled_MirrorsTodayConfig()
    {
        var config = AppConfig.CreateDefault();
        var plugin = CreatePlugin(config);
        Assert.True(plugin.IsEnabled);

        config.Today.Enabled = false;
        Assert.False(plugin.IsEnabled);
    }

    [Fact]
    public async Task ServiceUpdate_ForwardedThroughContextNotify()
    {
        var service = new StubTodayService();
        var plugin = CreatePlugin(service: service);
        var context = new RecordingContext();
        await plugin.InitializeAsync(context, CancellationToken.None);
        var snapshot = new TodaySnapshot { Enabled = true, DateLabel = "3 ноября, понедельник" };

        service.RaiseUpdated(snapshot);

        var call = Assert.Single(context.Notifications);
        Assert.Equal("today", call.PluginId);
        Assert.Same(snapshot, call.Data);
    }

    [Fact]
    public async Task Shutdown_DetachesFromServiceUpdates()
    {
        var service = new StubTodayService();
        var plugin = CreatePlugin(service: service);
        var context = new RecordingContext();
        await plugin.InitializeAsync(context, CancellationToken.None);

        await plugin.ShutdownAsync(CancellationToken.None);
        service.RaiseUpdated(new TodaySnapshot { Enabled = true });

        Assert.Empty(context.Notifications);
    }

    [Fact]
    public async Task PollSnapshotAsync_MirrorsServiceCurrent()
    {
        var service = new StubTodayService();
        var plugin = CreatePlugin(service: service);
        await plugin.InitializeAsync(new RecordingContext(), CancellationToken.None);

        Assert.Null(await plugin.PollSnapshotAsync(CancellationToken.None));

        var snapshot = new TodaySnapshot { Enabled = true };
        service.CurrentSnapshot = snapshot;

        var polled = await plugin.PollSnapshotAsync(CancellationToken.None);
        Assert.NotNull(polled);
        Assert.Equal("today", polled!.PluginId);
        Assert.Same(snapshot, polled.Payload);
    }

    [Fact]
    public async Task HandleActionAsync_UnknownAction_ReturnsFalse()
    {
        var plugin = CreatePlugin();
        using var doc = JsonDocument.Parse("{\"id\":\"42\"}");

        var handled = await plugin.HandleActionAsync("whatever", doc.RootElement);

        Assert.False(handled);
    }

    [Fact]
    public async Task HandleActionAsync_MissingId_ReturnsFalse()
    {
        var service = new StubTodayService();
        var plugin = CreatePlugin(service: service);
        using var doc = JsonDocument.Parse("{}");

        var handled = await plugin.HandleActionAsync(TodayPlugin.OpenTaskAction, doc.RootElement);

        Assert.False(handled);
        Assert.Null(service.LastResolvedTaskId);
    }

    [Fact]
    public async Task HandleActionAsync_UnresolvableTaskId_ReturnsFalse()
    {
        var service = new StubTodayService();
        var plugin = CreatePlugin(service: service);
        using var doc = JsonDocument.Parse("{\"id\":\"task-77\"}");

        var handled = await plugin.HandleActionAsync(TodayPlugin.OpenTaskAction, doc.RootElement);

        Assert.False(handled);
        Assert.Equal("task-77", service.LastResolvedTaskId);
    }

    private static TodayPlugin CreatePlugin(
        AppConfig? config = null,
        StubTodayService? service = null)
    {
        var stubConfig = new StubConfigManager(config ?? AppConfig.CreateDefault());
        return new TodayPlugin(service ?? new StubTodayService(), stubConfig, NullLogger<TodayPlugin>.Instance);
    }

    private sealed class StubTodayService : ITodayService
    {
        public TodaySnapshot? CurrentSnapshot { get; set; }
        public string? LastResolvedTaskId { get; private set; }
        public event EventHandler<TodaySnapshot>? TodayUpdated;

        public DateTime? NextUpcomingEventLocal => null;
        public string? NextUpcomingEventTitle => null;

        public void RaiseUpdated(TodaySnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            TodayUpdated?.Invoke(this, snapshot);
        }

        public Uri? ResolveTaskUri(string? taskId, out string? reason)
        {
            LastResolvedTaskId = taskId;
            reason = "Task id is not in the latest snapshot.";
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
