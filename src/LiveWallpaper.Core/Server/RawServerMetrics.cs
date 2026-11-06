using System;
using System.Text.Json.Serialization;

namespace LiveWallpaper.Core.Server;

public sealed class RawServerMetrics
{
    [JsonPropertyName("t")]
    public long T { get; set; }

    [JsonPropertyName("cpu")]
    public long[] Cpu { get; set; } = Array.Empty<long>();

    [JsonPropertyName("mem")]
    public RawMemoryMetrics Mem { get; set; } = new();

    [JsonPropertyName("swap")]
    public RawSwapMetrics Swap { get; set; } = new();

    [JsonPropertyName("load")]
    public double[] Load { get; set; } = Array.Empty<double>();

    [JsonPropertyName("disk")]
    public RawDiskMetrics Disk { get; set; } = new();

    [JsonPropertyName("net")]
    public RawNetworkMetrics Net { get; set; } = new();

    [JsonPropertyName("up")]
    public long Up { get; set; }
}

public sealed class RawMemoryMetrics
{
    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("avail")]
    public long Avail { get; set; }
}

public sealed class RawSwapMetrics
{
    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("free")]
    public long Free { get; set; }
}

public sealed class RawDiskMetrics
{
    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("used")]
    public long Used { get; set; }
}

public sealed class RawNetworkMetrics
{
    [JsonPropertyName("rx")]
    public long Rx { get; set; }

    [JsonPropertyName("tx")]
    public long Tx { get; set; }
}
