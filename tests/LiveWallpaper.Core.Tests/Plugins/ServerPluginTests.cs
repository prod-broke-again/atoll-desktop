using System.Text.Json;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Core.Server;
using LiveWallpaper.Services.Plugins;
using LiveWallpaper.Services.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveWallpaper.Core.Tests.Plugins;

public sealed class ServerPluginTests
{
    [Fact]
    public void Id_And_DisplayName_AreStable()
    {
        var plugin = CreatePlugin();

        Assert.Equal("server", plugin.Id);
        Assert.Equal("DevOps Server Monitor", plugin.DisplayName);
    }

    [Fact]
    public void IsEnabled_MirrorsServerConfig()
    {
        var config = AppConfig.CreateDefault();
        var plugin = CreatePlugin(config);
        Assert.True(plugin.IsEnabled);

        config.Server.Enabled = false;
        Assert.False(plugin.IsEnabled);
    }

    [Fact]
    public async Task StateSnapshotUpdate_ForwardedThroughContextNotify()
    {
        var service = new StubServerService();
        var plugin = CreatePlugin(service: service);
        var context = new RecordingContext();
        await plugin.InitializeAsync(context, CancellationToken.None);
        var snapshot = new ServerStateSnapshot { Host = "api.example.com", Ssh = "live" };

        service.RaiseStateUpdated(snapshot);

        var call = Assert.Single(context.Notifications);
        Assert.Equal("server", call.PluginId);
        Assert.Same(snapshot, call.Data);
    }

    [Fact]
    public async Task MetricPoint_ForwardedUnderMetricsPluginId()
    {
        var service = new StubServerService();
        var plugin = CreatePlugin(service: service);
        var context = new RecordingContext();
        await plugin.InitializeAsync(context, CancellationToken.None);
        var point = new MetricPoint { T = 42, Cpu = 12.5 };

        service.RaiseMetricPoint(point);

        var call = Assert.Single(context.Notifications);
        Assert.Equal("serverMetrics", call.PluginId);
        Assert.Same(point, call.Data);
    }

    [Fact]
    public async Task Shutdown_DetachesFromServiceUpdates()
    {
        var service = new StubServerService();
        var plugin = CreatePlugin(service: service);
        var context = new RecordingContext();
        await plugin.InitializeAsync(context, CancellationToken.None);

        await plugin.ShutdownAsync(CancellationToken.None);
        service.RaiseStateUpdated(new ServerStateSnapshot { Host = "h" });
        service.RaiseMetricPoint(new MetricPoint { T = 1 });

        Assert.Empty(context.Notifications);
    }

    [Fact]
    public async Task PollSnapshotAsync_MirrorsServiceCurrent()
    {
        var service = new StubServerService();
        var plugin = CreatePlugin(service: service);
        await plugin.InitializeAsync(new RecordingContext(), CancellationToken.None);

        var snapshot = new ServerStateSnapshot { Host = "api.example.com" };
        service.CurrentSnapshot = snapshot;

        var polled = await plugin.PollSnapshotAsync(CancellationToken.None);
        Assert.NotNull(polled);
        Assert.Equal("server", polled!.PluginId);
        Assert.Same(snapshot, polled.Payload);
    }

    [Fact]
    public async Task HandleActionAsync_UnknownAction_ReturnsFalse()
    {
        var plugin = CreatePlugin();
        using var doc = JsonDocument.Parse("{\"target\":\"ssh\"}");

        var handled = await plugin.HandleActionAsync("whatever", doc.RootElement);

        Assert.False(handled);
    }

    [Fact]
    public async Task HandleActionAsync_ServerOpenWithUnknownTarget_ReturnsFalse()
    {
        var plugin = CreatePlugin();
        using var doc = JsonDocument.Parse("{\"target\":\"timeweb\"}");

        var handled = await plugin.HandleActionAsync(ServerPlugin.ServerOpenAction, doc.RootElement);

        Assert.False(handled);
    }

    [Fact]
    public async Task HandleActionAsync_ServerOpenSiteNotInConfig_IsHandledWithoutLaunching()
    {
        var plugin = CreatePlugin();
        using var doc = JsonDocument.Parse("{\"target\":\"site\",\"name\":\"not-a-site\"}");

        var handled = await plugin.HandleActionAsync(ServerPlugin.ServerOpenAction, doc.RootElement);

        Assert.True(handled);
    }

    [Fact]
    public async Task HandleActionAsync_TogglePrivacy_FlipsConfigAndNotifiesService()
    {
        var config = AppConfig.CreateDefault();
        Assert.False(config.Server.PrivacyMode);
        var service = new StubServerService();
        var plugin = CreatePlugin(config, service);
        using var doc = JsonDocument.Parse("{}");

        var handled = await plugin.HandleActionAsync(ServerPlugin.TogglePrivacyAction, doc.RootElement);

        Assert.True(handled);
        Assert.True(config.Server.PrivacyMode);
        Assert.True(service.PrivacyMode);
    }

    [Fact]
    public async Task HandleActionAsync_SetPrivacyModeAlias_TogglesPrivacy()
    {
        var config = AppConfig.CreateDefault();
        var service = new StubServerService();
        var plugin = CreatePlugin(config, service);
        using var doc = JsonDocument.Parse("{}");

        var handled = await plugin.HandleActionAsync(ServerPlugin.SetPrivacyModeAction, doc.RootElement);

        Assert.True(handled);
        Assert.True(service.PrivacyMode);
    }

    private static ServerPlugin CreatePlugin(
        AppConfig? config = null,
        StubServerService? service = null)
    {
        var stubConfig = new StubConfigManager(config ?? AppConfig.CreateDefault());
        return new ServerPlugin(service ?? new StubServerService(), stubConfig, NullLogger<ServerPlugin>.Instance);
    }

    private sealed class StubServerService : IServerWidgetService
    {
        public ServerStateSnapshot CurrentSnapshot { get; set; } = new();
        public IReadOnlyList<MetricPoint> History { get; } = new List<MetricPoint>();
        public bool? PrivacyMode { get; private set; }
        public event Action<MetricPoint>? MetricPointReceived;
        public event Action<ServerStateSnapshot>? StateSnapshotUpdated;

        public void RaiseStateUpdated(ServerStateSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            StateSnapshotUpdated?.Invoke(snapshot);
        }

        public void RaiseMetricPoint(MetricPoint point) => MetricPointReceived?.Invoke(point);

        public Task<string> RunDiagnosticsAsync(CancellationToken cancellationToken) =>
            Task.FromResult("ok");

        public void SetPrivacyMode(bool enabled) => PrivacyMode = enabled;
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
