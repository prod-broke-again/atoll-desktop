using System.Text.Json;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Services.Plugins;
using LiveWallpaper.Services.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveWallpaper.Core.Tests.Plugins;

public sealed class TimewebPluginTests
{
    [Fact]
    public void Id_And_DisplayName_AreStable()
    {
        var plugin = CreatePlugin();

        Assert.Equal("timeweb", plugin.Id);
        Assert.Equal("Timeweb Cloud", plugin.DisplayName);
    }

    [Fact]
    public void IsEnabled_MirrorsServerConfigGate()
    {
        var config = AppConfig.CreateDefault();
        var plugin = CreatePlugin(config);
        Assert.True(plugin.IsEnabled);

        config.Server.TimewebEnabled = false;
        Assert.False(plugin.IsEnabled);

        config.Server.TimewebEnabled = true;
        config.Server.Enabled = false;
        Assert.False(plugin.IsEnabled);
    }

    [Fact]
    public async Task PollSnapshotAsync_WhenDisabled_ReturnsNullWithoutTouchingClient()
    {
        var config = AppConfig.CreateDefault();
        config.Server.TimewebEnabled = false;
        var client = new StubTimewebClient();
        var plugin = CreatePlugin(config, client);
        await plugin.InitializeAsync(new StubPluginContext { Credential = "token" }, CancellationToken.None);

        var snapshot = await plugin.PollSnapshotAsync(CancellationToken.None);

        Assert.Null(snapshot);
        Assert.Equal(0, client.PollCount);
    }

    [Fact]
    public async Task PollSnapshotAsync_WithoutToken_ReturnsNull()
    {
        var client = new StubTimewebClient();
        var plugin = CreatePlugin(AppConfig.CreateDefault(), client);
        await plugin.InitializeAsync(new StubPluginContext { Credential = null }, CancellationToken.None);

        var snapshot = await plugin.PollSnapshotAsync(CancellationToken.None);

        Assert.Null(snapshot);
        Assert.Equal(0, client.PollCount);
    }

    [Fact]
    public async Task PollSnapshotAsync_WhenEnabled_ReturnsClientSnapshot()
    {
        var timeweb = new TimewebSnapshot { TokenConfigured = true, StaleMinutes = 3 };
        var client = new StubTimewebClient { Next = timeweb };
        var plugin = CreatePlugin(AppConfig.CreateDefault(), client);
        await plugin.InitializeAsync(new StubPluginContext { Credential = "token" }, CancellationToken.None);

        var snapshot = await plugin.PollSnapshotAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("timeweb", snapshot!.PluginId);
        Assert.Same(timeweb, snapshot.Payload);
        Assert.Equal(1, client.PollCount);
    }

    [Fact]
    public async Task HandleActionAsync_UnknownAction_ReturnsFalse()
    {
        var plugin = CreatePlugin();
        using var doc = JsonDocument.Parse("{}");

        var handled = await plugin.HandleActionAsync("whatever", doc.RootElement);

        Assert.False(handled);
    }

    private static TimewebPlugin CreatePlugin(
        AppConfig? config = null,
        StubTimewebClient? client = null)
    {
        var stubConfig = new StubConfigManager(config ?? AppConfig.CreateDefault());
        return new TimewebPlugin(client ?? new StubTimewebClient(), stubConfig, NullLogger<TimewebPlugin>.Instance);
    }

    private sealed class StubTimewebClient : ITimewebClient
    {
        public TimewebSnapshot Next { get; set; } = new();
        public int PollCount { get; private set; }

        public Task<TimewebSnapshot> PollAsync(CancellationToken cancellationToken)
        {
            PollCount++;
            return Task.FromResult(Next);
        }

        public Task<string> CheckConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult("ok");
    }

    private sealed class StubPluginContext : IPluginContext
    {
        public string? Credential { get; init; }

        public string? GetCredential(string targetKey) => Credential;

        public void NotifyUpdated(string pluginId, object data)
        {
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
}
