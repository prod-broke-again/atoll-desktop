using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LiveWallpaper.Core.Server;

public sealed record ServerStateSnapshot
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "serverState";

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("ssh")]
    public string Ssh { get; init; } = "offline"; // "connecting", "live", "stale", "offline"

    [JsonPropertyName("server")]
    public ServerStatusInfo? Server { get; init; }

    [JsonPropertyName("sites")]
    public List<ServerSiteInfo> Sites { get; init; } = new();

    [JsonPropertyName("account")]
    public AccountFinanceInfo? Account { get; init; }

    [JsonPropertyName("backup")]
    public BackupInfo? Backup { get; init; }

    [JsonPropertyName("bucket")]
    public BucketInfo? Bucket { get; init; }

    [JsonPropertyName("alerts")]
    public List<ServerAlert> Alerts { get; init; } = new();

    [JsonPropertyName("history")]
    public List<MetricPoint>? History { get; init; }

    [JsonPropertyName("privacyMode")]
    public bool PrivacyMode { get; init; }
}

public sealed record ServerStatusInfo
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "unknown";

    [JsonPropertyName("uptimeSec")]
    public long UptimeSec { get; init; }

    [JsonPropertyName("diskPct")]
    public double DiskPct { get; init; }
}

public sealed record ServerSiteInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = "unknown"; // "up", "slow", "down", "unknown"

    [JsonPropertyName("ms")]
    public double? Ms { get; init; }

    [JsonPropertyName("tlsDays")]
    public int? TlsDays { get; init; }
}

public sealed record AccountFinanceInfo
{
    [JsonPropertyName("balance")]
    public double Balance { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "RUB";

    [JsonPropertyName("daysLeft")]
    public double? DaysLeft { get; init; }

    [JsonPropertyName("autopay")]
    public bool Autopay { get; init; }

    [JsonPropertyName("staleMin")]
    public double StaleMin { get; init; }
}

public sealed record BackupInfo
{
    [JsonPropertyName("ageHours")]
    public double? AgeHours { get; init; }
}

public sealed record BucketInfo
{
    [JsonPropertyName("usedPct")]
    public double UsedPct { get; init; }

    [JsonPropertyName("objects")]
    public int Objects { get; init; }
}
