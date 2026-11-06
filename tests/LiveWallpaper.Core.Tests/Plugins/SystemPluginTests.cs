using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Services.Plugins;
using LiveWallpaper.Services.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveWallpaper.Core.Tests.Plugins;

public sealed class SystemPluginTests
{
    [Fact]
    public void Id_And_DisplayName_AreStable()
    {
        var plugin = CreatePlugin();
        Assert.Equal("system", plugin.Id);
        Assert.Equal("System Metrics", plugin.DisplayName);
    }

    [Fact]
    public void IsEnabled_MirrorsShowSystemMetrics()
    {
        var config = AppConfig.CreateDefault();
        var plugin = CreatePlugin(config);
        Assert.True(plugin.IsEnabled);

        config.Widgets.ShowSystemMetrics = false;
        Assert.False(plugin.IsEnabled);
    }

    [Fact]
    public async Task Telemetry_ForwardedWhenEnabled()
    {
        var telemetry = new StubTelemetry();
        var plugin = CreatePlugin(service: telemetry);
        var context = new RecordingContext();
        await plugin.InitializeAsync(context, CancellationToken.None);

        var data = new TelemetryData { CpuPercent = 42 };
        telemetry.Raise(data);

        var call = Assert.Single(context.Notifications);
        Assert.Equal("system", call.PluginId);
        Assert.Same(data, call.Data);
    }

    [Fact]
    public async Task Telemetry_SuppressedWhenDisabled()
    {
        var config = AppConfig.CreateDefault();
        config.Widgets.ShowSystemMetrics = false;
        var telemetry = new StubTelemetry();
        var plugin = CreatePlugin(config, telemetry);
        var context = new RecordingContext();
        await plugin.InitializeAsync(context, CancellationToken.None);

        telemetry.Raise(new TelemetryData { CpuPercent = 10 });
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void SettingsManifest_ExposesEnabledToggle()
    {
        var manifest = CreatePlugin().GetSettingsManifest();
        Assert.NotNull(manifest);
        Assert.Equal("Система", manifest!.DisplayName);
        Assert.Contains(manifest.Fields, f => f.Id == "enabled" && f.Kind == "toggle");
    }

    private static SystemPlugin CreatePlugin(AppConfig? config = null, ITelemetryService? service = null)
    {
        config ??= AppConfig.CreateDefault();
        return new SystemPlugin(
            service ?? new StubTelemetry(),
            new StubConfigManager(config),
            NullLogger<SystemPlugin>.Instance);
    }

    private sealed class StubTelemetry : ITelemetryService
    {
        public TelemetryData? CurrentTelemetry { get; private set; }
        public event EventHandler<TelemetryData>? TelemetryUpdated;
        public void SetPaused(bool isPaused) { }
        public void Raise(TelemetryData data)
        {
            CurrentTelemetry = data;
            TelemetryUpdated?.Invoke(this, data);
        }
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

    private sealed class RecordingContext : IPluginContext
    {
        public List<(string PluginId, object Data)> Notifications { get; } = new();
        public string? GetCredential(string targetKey) => null;
        public void NotifyUpdated(string pluginId, object data) => Notifications.Add((pluginId, data));
    }
}
