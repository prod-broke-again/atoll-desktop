using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveWallpaper.Services.Server;

public sealed class ServersResponse
{
    [JsonPropertyName("servers")]
    public List<ServerDto> Servers { get; set; } = new();
}

public sealed class ServerDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("last_started")]
    public DateTimeOffset? LastStarted { get; set; }

    [JsonPropertyName("disks")]
    public List<DiskDto> Disks { get; set; } = new();

    public double ComputeDiskPct()
    {
        if (Disks.Count == 0) return 0.0;
        var d = Disks[0];

        if (d.SizeGb.HasValue && d.SizeGb.Value > 0 && d.UsedGb.HasValue)
        {
            return Math.Clamp(d.UsedGb.Value * 100.0 / d.SizeGb.Value, 0.0, 100.0);
        }

        if (d.Size.HasValue && d.Size.Value > 0 && d.Used.HasValue)
        {
            return Math.Clamp(d.Used.Value * 100.0 / d.Size.Value, 0.0, 100.0);
        }

        return 0.0;
    }
}

public sealed class DiskDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("size")]
    public double? Size { get; set; }

    [JsonPropertyName("used")]
    public double? Used { get; set; }

    [JsonPropertyName("size_gb")]
    public double? SizeGb { get; set; }

    [JsonPropertyName("used_gb")]
    public double? UsedGb { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";
}

public sealed class FinancesResponse
{
    [JsonPropertyName("finances")]
    public FinancesDto Finances { get; set; } = new();
}

public sealed class FinancesDto
{
    [JsonPropertyName("balance")]
    public double Balance { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "RUB";

    [JsonPropertyName("hourly_cost")]
    public double? HourlyCost { get; set; }

    [JsonPropertyName("hours_left")]
    public double? HoursLeft { get; set; }

    [JsonPropertyName("days_left")]
    public double? DaysLeft { get; set; }

    [JsonPropertyName("autopay_card_info")]
    public JsonElement? AutopayCardInfo { get; set; }

    public bool HasAutopay()
    {
        if (!AutopayCardInfo.HasValue) return false;
        var el = AutopayCardInfo.Value;
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined) return false;
        if (el.ValueKind == JsonValueKind.String)
        {
            var str = el.GetString();
            return !string.IsNullOrWhiteSpace(str) && !str.Equals("none", StringComparison.OrdinalIgnoreCase);
        }
        return el.ValueKind == JsonValueKind.Object;
    }

    public double? ComputeDaysLeft()
    {
        if (DaysLeft.HasValue && DaysLeft.Value > 0) return DaysLeft.Value;
        if (HoursLeft.HasValue) return HoursLeft.Value / 24.0;
        if (HourlyCost.HasValue && HourlyCost.Value > 0) return Balance / (HourlyCost.Value * 24.0);
        return null;
    }
}

public sealed class AccountStatusResponse
{
    [JsonPropertyName("status")]
    public AccountStatusDto Status { get; set; } = new();
}

public sealed class AccountStatusDto
{
    [JsonPropertyName("is_blocked")]
    public bool? IsBlocked { get; set; }

    [JsonPropertyName("is_permanent_blocked")]
    public bool? IsPermanentBlocked { get; set; }

    [JsonPropertyName("company_is_blocked")]
    public bool? CompanyIsBlocked { get; set; }

    public bool IsAnyBlocked()
    {
        return IsBlocked == true || IsPermanentBlocked == true || CompanyIsBlocked == true;
    }
}

public sealed class BackupsResponse
{
    [JsonPropertyName("backups")]
    public List<BackupDto> Backups { get; set; } = new();
}

public sealed class BackupDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("size")]
    public double? Size { get; set; }

    [JsonPropertyName("size_gb")]
    public double? SizeGb { get; set; }
}

public sealed class BucketsResponse
{
    [JsonPropertyName("buckets")]
    public List<BucketDto> Buckets { get; set; } = new();
}

public sealed class BucketDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("object_amount")]
    public int? ObjectAmount { get; set; }

    [JsonPropertyName("disk_stats")]
    public BucketDiskStatsDto? DiskStats { get; set; }

    [JsonPropertyName("size_gb")]
    public double? SizeGb { get; set; }

    [JsonPropertyName("used_gb")]
    public double? UsedGb { get; set; }

    public double ComputeUsedPct()
    {
        if (UsedGb.HasValue && SizeGb.HasValue && SizeGb.Value > 0)
        {
            return Math.Clamp(UsedGb.Value * 100.0 / SizeGb.Value, 0.0, 100.0);
        }

        if (DiskStats != null && DiskStats.Size.HasValue && DiskStats.Size.Value > 0 && DiskStats.Used.HasValue)
        {
            return Math.Clamp(DiskStats.Used.Value * 100.0 / DiskStats.Size.Value, 0.0, 100.0);
        }

        return 0.0;
    }
}

public sealed class BucketDiskStatsDto
{
    [JsonPropertyName("size")]
    public double? Size { get; set; }

    [JsonPropertyName("used")]
    public double? Used { get; set; }

    [JsonPropertyName("is_unlimited")]
    public bool? IsUnlimited { get; set; }
}
