using System.Text.Json.Serialization;

namespace LiveWallpaper.Core.Server;

public sealed record MetricPoint
{
    [JsonPropertyName("t")]
    public long T { get; init; }

    [JsonPropertyName("cpu")]
    public double Cpu { get; init; }

    [JsonPropertyName("mem")]
    public double Mem { get; init; }

    [JsonPropertyName("swap")]
    public double Swap { get; init; }

    [JsonPropertyName("load1")]
    public double Load1 { get; init; }

    [JsonPropertyName("diskPct")]
    public double DiskPct { get; init; }

    [JsonPropertyName("rxKbps")]
    public double RxKbps { get; init; }

    [JsonPropertyName("txKbps")]
    public double TxKbps { get; init; }
}
