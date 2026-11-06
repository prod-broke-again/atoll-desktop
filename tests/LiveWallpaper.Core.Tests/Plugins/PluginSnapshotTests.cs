using LiveWallpaper.Core.Plugins;
using Xunit;

namespace LiveWallpaper.Core.Tests.Plugins;

public sealed class PluginSnapshotTests
{
    [Fact]
    public void CarriesPluginIdAndPayload_WithRecentTimestamp()
    {
        var payload = new object();

        var snapshot = new PluginSnapshot { PluginId = "timeweb", Payload = payload };

        Assert.Equal("timeweb", snapshot.PluginId);
        Assert.Same(payload, snapshot.Payload);
        Assert.True(snapshot.Timestamp >= DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        Assert.True(snapshot.Timestamp <= DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));
    }
}
