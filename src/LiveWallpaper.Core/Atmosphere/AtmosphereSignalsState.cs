using System.Text.Json.Serialization;

namespace LiveWallpaper.Core.Atmosphere;

public sealed record AtmosphereSignalsState
{
    [JsonPropertyName("prodAlert")]
    public bool ProdAlert { get; init; }

    [JsonPropertyName("prodAlertText")]
    public string? ProdAlertText { get; init; }

    [JsonPropertyName("upcomingCall")]
    public bool UpcomingCall { get; init; }

    [JsonPropertyName("upcomingCallMinutes")]
    public int? UpcomingCallMinutes { get; init; }

    [JsonPropertyName("upcomingCallTitle")]
    public string? UpcomingCallTitle { get; init; }

    [JsonPropertyName("lowBalance")]
    public bool LowBalance { get; init; }

    [JsonPropertyName("lowBalanceText")]
    public string? LowBalanceText { get; init; }

    public static readonly AtmosphereSignalsState Empty = new();
}
