using System;
using System.Text.Json.Nodes;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Server;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public class ServerMetricsTests
{
    [Fact]
    public void Parser_ValidLine_ReturnsRawMetrics()
    {
        string line = "LW|{\"t\":1730000000,\"cpu\":[100,10,50,840,0,0,0,0],\"mem\":{\"total\":8000000,\"avail\":4000000},\"swap\":{\"total\":2000000,\"free\":1500000},\"load\":[1.2,0.9,0.7],\"disk\":{\"total\":100000000,\"used\":50000000},\"net\":{\"rx\":1048576,\"tx\":524288},\"up\":9331200}";

        bool success = ServerMetricsParser.TryParse(line, out var metrics);

        Assert.True(success);
        Assert.NotNull(metrics);
        Assert.Equal(1730000000, metrics.T);
        Assert.Equal(8, metrics.Cpu.Length);
        Assert.Equal(8000000, metrics.Mem.Total);
        Assert.Equal(4000000, metrics.Mem.Avail);
        Assert.Equal(2000000, metrics.Swap.Total);
        Assert.Equal(1500000, metrics.Swap.Free);
        Assert.Equal(3, metrics.Load.Length);
        Assert.Equal(1.2, metrics.Load[0]);
        Assert.Equal(100000000, metrics.Disk.Total);
        Assert.Equal(50000000, metrics.Disk.Used);
        Assert.Equal(1048576, metrics.Net.Rx);
        Assert.Equal(524288, metrics.Net.Tx);
        Assert.Equal(9331200, metrics.Up);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("random noise")]
    [InlineData("LW|")]
    [InlineData("LW|not a json")]
    [InlineData("LW|{\"t\":0}")] // missing timestamp
    [InlineData("LW|{\"cpu\":[1,2]}")]
    public void Parser_InvalidOrCorruptLine_ReturnsFalse(string? line)
    {
        bool success = ServerMetricsParser.TryParse(line, out var metrics);

        Assert.False(success);
        Assert.Null(metrics);
    }

    [Fact]
    public void Calculator_ComputesCpuAndNetworkDeltas()
    {
        var prev = new RawServerMetrics
        {
            T = 1000,
            Cpu = new long[] { 100, 0, 50, 850, 0, 0, 0, 0 }, // Total = 1000, Idle = 850
            Mem = new RawMemoryMetrics { Total = 8192, Avail = 4096 },
            Swap = new RawSwapMetrics { Total = 2048, Free = 1024 },
            Load = new[] { 0.42 },
            Disk = new RawDiskMetrics { Total = 100000, Used = 40000 },
            Net = new RawNetworkMetrics { Rx = 102400, Tx = 51200 }
        };

        var cur = new RawServerMetrics
        {
            T = 1002, // 2 seconds delta
            Cpu = new long[] { 140, 0, 60, 900, 0, 0, 0, 0 }, // Total = 1100 (+100), Idle = 900 (+50). Busy = 50%
            Mem = new RawMemoryMetrics { Total = 8192, Avail = 4096 },
            Swap = new RawSwapMetrics { Total = 2048, Free = 1024 },
            Load = new[] { 0.50 },
            Disk = new RawDiskMetrics { Total = 100000, Used = 40000 },
            Net = new RawNetworkMetrics { Rx = 102400 + 20480, Tx = 51200 + 10240 } // rx delta = 20KB/2s = 10KB/s, tx = 5KB/s
        };

        var point = ServerMetricCalculator.Calculate(cur, prev);

        Assert.Equal(1002, point.T);
        Assert.Equal(50.0, point.Cpu);
        Assert.Equal(50.0, point.Mem);
        Assert.Equal(50.0, point.Swap);
        Assert.Equal(40.0, point.DiskPct);
        Assert.Equal(0.50, point.Load1);
        Assert.Equal(10.0, point.RxKbps);
        Assert.Equal(5.0, point.TxKbps);
    }

    [Fact]
    public void Calculator_CounterWrapOrReboot_ReturnsSafeZeroes()
    {
        var prev = new RawServerMetrics
        {
            T = 1000,
            Cpu = new long[] { 10000, 0, 5000, 85000 },
            Net = new RawNetworkMetrics { Rx = 5000000, Tx = 3000000 }
        };

        var cur = new RawServerMetrics
        {
            T = 1002,
            Cpu = new long[] { 100, 0, 50, 850 }, // lower counters due to reboot
            Net = new RawNetworkMetrics { Rx = 1000, Tx = 500 }
        };

        var point = ServerMetricCalculator.Calculate(cur, prev);

        Assert.Equal(0.0, point.Cpu);
        Assert.Equal(0.0, point.RxKbps);
        Assert.Equal(0.0, point.TxKbps);
    }

    [Fact]
    public void Migration_V2ToV3_AddsServerConfig()
    {
        var v2 = new JsonObject
        {
            ["SchemaVersion"] = 2,
            ["Launcher"] = new JsonObject { ["Enabled"] = true }
        };

        var pipeline = ConfigMigrationPipeline.CreateDefault();
        var migrated = pipeline.ApplyMigrations(v2, 3);

        Assert.Equal(3, migrated["SchemaVersion"]?.GetValue<int>());
        Assert.NotNull(migrated["Server"]);
        var server = migrated["Server"]!.AsObject();
        Assert.True(server["Enabled"]?.GetValue<bool>());
        Assert.Equal("myserver", server["SshAlias"]?.GetValue<string>());
    }
}
