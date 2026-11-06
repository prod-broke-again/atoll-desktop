using System.Text.Json;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Services.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveWallpaper.Core.Tests.Plugins;

public sealed class PluginHostServiceTests
{
    [Fact]
    public async Task StartAsync_InitializesEveryPlugin_WithHostAsContext()
    {
        var first = new FakePlugin("first");
        var second = new FakePlugin("second");
        var host = CreateHost(first, second);

        await host.StartAsync(CancellationToken.None);

        Assert.Equal(1, first.InitializeCount);
        Assert.Equal(1, second.InitializeCount);
        Assert.Same(host, first.Context);
        Assert.Same(host, second.Context);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_ShutsEveryPluginDown()
    {
        var plugin = new FakePlugin("first");
        var host = CreateHost(plugin);

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(1, plugin.ShutdownCount);
    }

    [Fact]
    public async Task DispatchActionAsync_RoutesPayloadToMatchingPlugin()
    {
        var timeweb = new FakePlugin("timeweb");
        var telegram = new FakePlugin("telegram");
        var host = CreateHost(timeweb, telegram);
        using var doc = JsonDocument.Parse("{\"type\":\"open_telegram_chat\",\"id\":42}");
        await host.StartAsync(CancellationToken.None);

        var handled = await host.DispatchActionAsync("telegram", "open_telegram_chat", doc.RootElement);

        Assert.True(handled);
        var call = Assert.Single(telegram.Actions);
        Assert.Equal("open_telegram_chat", call.Action);
        Assert.Equal(42, call.Payload.GetProperty("id").GetInt64());
        Assert.Empty(timeweb.Actions);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatchActionAsync_UnknownPlugin_ReturnsFalse()
    {
        var host = CreateHost(new FakePlugin("timeweb"));
        using var doc = JsonDocument.Parse("{}");
        await host.StartAsync(CancellationToken.None);

        var handled = await host.DispatchActionAsync("unknown", "act", doc.RootElement);

        Assert.False(handled);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void NotifyUpdated_CachesSnapshot_AndRaisesSnapshotReceived()
    {
        var host = CreateHost();
        PluginSnapshot? received = null;
        host.SnapshotReceived += snapshot => received = snapshot;
        var payload = new object();

        host.NotifyUpdated("telegram", payload);

        Assert.NotNull(received);
        Assert.Equal("telegram", received!.PluginId);
        Assert.Same(payload, received.Payload);
        Assert.Same(received, host.GetCurrentSnapshot("telegram"));
        Assert.True(host.CurrentSnapshots.ContainsKey("telegram"));
    }

    [Fact]
    public void NotifyUpdated_WithSamePayloadInstance_PublishesOnce()
    {
        var host = CreateHost();
        var count = 0;
        host.SnapshotReceived += _ => count++;
        var payload = new object();

        host.NotifyUpdated("telegram", payload);
        host.NotifyUpdated("telegram", payload);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PollLoop_PublishesSnapshotOfEnabledPlugin()
    {
        var plugin = new FakePlugin("timeweb")
        {
            NextPoll = new PluginSnapshot { PluginId = "timeweb", Payload = "telemetry" }
        };
        var host = CreateHost(plugin);
        var tcs = new TaskCompletionSource<PluginSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SnapshotReceived += snapshot => tcs.TrySetResult(snapshot);

        await host.StartAsync(CancellationToken.None);
        var snapshot = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("timeweb", snapshot.PluginId);
        Assert.Same(snapshot, host.GetCurrentSnapshot("timeweb"));

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PollLoop_SkipsDisabledPlugins()
    {
        var plugin = new FakePlugin("timeweb")
        {
            IsEnabled = false,
            NextPoll = new PluginSnapshot { PluginId = "timeweb", Payload = "telemetry" }
        };
        var host = CreateHost(new IWidgetPlugin[] { plugin }, new PluginHostOptions { PollInterval = TimeSpan.FromMilliseconds(50) });
        var count = 0;
        host.SnapshotReceived += _ => Interlocked.Increment(ref count);

        await host.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Equal(0, plugin.PollCount);
    }

    [Fact]
    public void GetCredential_UnknownTarget_ReturnsNull()
    {
        var host = CreateHost();

        Assert.Null(host.GetCredential("LiveWallpaper/Tests/DoesNotExist"));
    }

    private static PluginHostService CreateHost(params IWidgetPlugin[] plugins) =>
        CreateHost(plugins, new PluginHostOptions { PollInterval = TimeSpan.FromHours(1) });

    private static PluginHostService CreateHost(IEnumerable<IWidgetPlugin> plugins, PluginHostOptions options) =>
        new(plugins, options, NullLogger<PluginHostService>.Instance);

    private sealed class FakePlugin : IWidgetPlugin
    {
        public FakePlugin(string id) => Id = id;

        public string Id { get; }
        public string DisplayName => $"Fake {Id}";
        public bool IsEnabled { get; set; } = true;
        public IPluginContext? Context { get; private set; }
        public int InitializeCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public int PollCount { get; private set; }
        public PluginSnapshot? NextPoll { get; set; }
        public List<(string Action, JsonElement Payload)> Actions { get; } = new();

        public Task InitializeAsync(IPluginContext context, CancellationToken ct)
        {
            Context = context;
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken ct)
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }

        public Task<PluginSnapshot?> PollSnapshotAsync(CancellationToken ct)
        {
            PollCount++;
            return Task.FromResult(NextPoll);
        }

        public Task<bool> HandleActionAsync(string action, JsonElement payload)
        {
            Actions.Add((action, payload));
            return Task.FromResult(true);
        }

        public PluginSettingsManifest? GetSettingsManifest() => null;
    }
}
