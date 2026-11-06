using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LiveWallpaper.Core.Server;

public enum ServerAlertLevel
{
    Warning,
    Critical
}

public sealed record ServerAlert
{
    [JsonPropertyName("level")]
    public string Level { get; init; } = "warning";

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    public ServerAlert() { }

    public ServerAlert(ServerAlertLevel level, string text)
    {
        Level = level == ServerAlertLevel.Critical ? "critical" : "warning";
        Text = text;
    }
}

public sealed class SiteAlertInput
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string State { get; init; } = "unknown"; // "up", "slow", "down", "unknown"
    public int ConsecutiveDownCount { get; init; }
    public double? ResponseMs { get; init; }
    public int? TlsDays { get; init; }
}

public sealed class ServerStateInput
{
    public string SshState { get; init; } = "offline"; // "connecting", "live", "stale", "offline"
    public TimeSpan SshOfflineDuration { get; init; } = TimeSpan.Zero;
    public bool IsInternetAlive { get; init; } = true;

    public string? ServerStatus { get; init; } // e.g. "on", "off", "blocked"
    public bool IsAccountBlocked { get; init; }
    public double? HoursLeft { get; init; }
    public bool IsAutopayEnabled { get; init; }

    public double CurrentRamPct { get; init; }
    public double CurrentDiskPct { get; init; }
    public IReadOnlyList<MetricPoint> RecentCpuPoints { get; init; } = Array.Empty<MetricPoint>();

    public IReadOnlyList<SiteAlertInput> Sites { get; init; } = Array.Empty<SiteAlertInput>();

    public double? BackupAgeHours { get; init; }
    public double? BucketUsedPct { get; init; }
    public double TimewebStaleMinutes { get; init; }
}

public static class ServerAlertEvaluator
{
    public static List<ServerAlert> Evaluate(ServerStateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var alerts = new List<ServerAlert>();

        // ------------------ CRITICAL CHECKS ------------------

        // 1. SSH offline > 2 min when internet is alive
        if (input.SshState.Equals("offline", StringComparison.OrdinalIgnoreCase)
            && input.IsInternetAlive
            && input.SshOfflineDuration > TimeSpan.FromMinutes(2))
        {
            alerts.Add(new ServerAlert(ServerAlertLevel.Critical, "SSH офлайн более 2 мин"));
        }

        // 2. Site down 2 checks in a row
        foreach (var site in input.Sites)
        {
            if (site.ConsecutiveDownCount >= 2)
            {
                alerts.Add(new ServerAlert(ServerAlertLevel.Critical, $"Сайт {site.Name} недоступен"));
            }
        }

        // 3. Server status not "on"
        if (!string.IsNullOrEmpty(input.ServerStatus)
            && !input.ServerStatus.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(new ServerAlert(ServerAlertLevel.Critical, $"Сервер в статусе {input.ServerStatus}"));
        }

        // 4. Account blocked
        if (input.IsAccountBlocked)
        {
            alerts.Add(new ServerAlert(ServerAlertLevel.Critical, "Аккаунт Timeweb заблокирован"));
        }

        // 5. hours_left < 72h when autopay is disabled
        if (!input.IsAutopayEnabled && input.HoursLeft.HasValue && input.HoursLeft.Value < 72)
        {
            int hours = (int)Math.Max(0, Math.Round(input.HoursLeft.Value));
            alerts.Add(new ServerAlert(ServerAlertLevel.Critical, $"Баланс на исходе: {hours} ч (без автоплатежа)"));
        }

        // ------------------ WARNING CHECKS ------------------

        // 1. CPU > 85% longer than 2 min (at least 60 points of 2s each, all > 85%)
        if (IsCpuSustainedHigh(input.RecentCpuPoints, 85.0, TimeSpan.FromMinutes(2)))
        {
            alerts.Add(new ServerAlert(ServerAlertLevel.Warning, "Высокая нагрузка CPU > 85%"));
        }

        // 2. RAM > 90%
        if (input.CurrentRamPct > 90.0)
        {
            alerts.Add(new ServerAlert(ServerAlertLevel.Warning, $"Высокое потребление RAM {input.CurrentRamPct:F0}%"));
        }

        // 3. Disk > 80%
        if (input.CurrentDiskPct > 80.0)
        {
            alerts.Add(new ServerAlert(ServerAlertLevel.Warning, $"Диск сервера заполнен на {input.CurrentDiskPct:F0}%"));
        }

        // 4. Site slow (> 1500 ms) and not down
        foreach (var site in input.Sites)
        {
            if (site.ConsecutiveDownCount < 2 && site.State.Equals("slow", StringComparison.OrdinalIgnoreCase))
            {
                int ms = site.ResponseMs.HasValue ? (int)Math.Round(site.ResponseMs.Value) : 1500;
                alerts.Add(new ServerAlert(ServerAlertLevel.Warning, $"Медленный ответ {site.Name}: {ms} мс"));
            }
        }

        // 5. TLS < 14 days
        foreach (var site in input.Sites)
        {
            if (site.TlsDays.HasValue && site.TlsDays.Value < 14)
            {
                alerts.Add(new ServerAlert(ServerAlertLevel.Warning, $"SSL-сертификат {site.Name} истекает через {site.TlsDays.Value} д"));
            }
        }

        // 6. Last backup older than 36h
        if (input.BackupAgeHours.HasValue && input.BackupAgeHours.Value > 36.0)
        {
            int hours = (int)Math.Round(input.BackupAgeHours.Value);
            alerts.Add(new ServerAlert(ServerAlertLevel.Warning, $"Последний бэкап старше {hours} ч"));
        }

        // 7. S3 Bucket > 80%
        if (input.BucketUsedPct.HasValue && input.BucketUsedPct.Value > 80.0)
        {
            alerts.Add(new ServerAlert(ServerAlertLevel.Warning, $"S3-бакет заполнен на {input.BucketUsedPct.Value:F0}%"));
        }

        // 8. Timeweb data stale > 30 min
        if (input.TimewebStaleMinutes > 30.0)
        {
            int mins = (int)Math.Round(input.TimewebStaleMinutes);
            alerts.Add(new ServerAlert(ServerAlertLevel.Warning, $"Данные Timeweb устарели ({mins} мин)"));
        }

        return alerts;
    }

    private static bool IsCpuSustainedHigh(IReadOnlyList<MetricPoint> points, double threshold, TimeSpan duration)
    {
        if (points == null || points.Count == 0) return false;

        // Check if we have points spanning at least duration
        long minT = points[0].T;
        long maxT = points[^1].T;
        if (maxT - minT < duration.TotalSeconds) return false;

        // Filter points within the last 'duration' window
        long cutoff = maxT - (long)duration.TotalSeconds;
        int countInWindow = 0;
        for (int i = points.Count - 1; i >= 0; i--)
        {
            if (points[i].T < cutoff) break;
            countInWindow++;
            if (points[i].Cpu <= threshold)
            {
                return false;
            }
        }

        return countInWindow >= 10; // at least 10 points verified in the window
    }
}
