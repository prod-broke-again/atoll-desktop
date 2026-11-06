using System;
using System.Collections.Generic;
using System.Linq;
using LiveWallpaper.Core.Server;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public class ServerAlertTests
{
    [Fact]
    public void HealthyBaseline_ReturnsNoAlerts()
    {
        var input = new ServerStateInput
        {
            SshState = "live",
            ServerStatus = "on",
            IsAccountBlocked = false,
            HoursLeft = 360,
            IsAutopayEnabled = true,
            CurrentRamPct = 47.0,
            CurrentDiskPct = 57.0,
            Sites = new List<SiteAlertInput>
            {
                new() { Name = "api.example.com", State = "up", ResponseMs = 84, TlsDays = 61, ConsecutiveDownCount = 0 }
            },
            BackupAgeHours = 24.0,
            BucketUsedPct = 52.0,
            TimewebStaleMinutes = 5.0
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Critical_SshOfflineOverTwoMinutesWithInternet_TriggersCritical()
    {
        var input = new ServerStateInput
        {
            SshState = "offline",
            SshOfflineDuration = TimeSpan.FromMinutes(2.1),
            IsInternetAlive = true,
            ServerStatus = "on"
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        var alert = Assert.Single(alerts);
        Assert.Equal("critical", alert.Level);
        Assert.Contains("SSH", alert.Text);
    }

    [Fact]
    public void Critical_SshOfflineWithoutInternet_DoesNotTriggerSshAlert()
    {
        var input = new ServerStateInput
        {
            SshState = "offline",
            SshOfflineDuration = TimeSpan.FromMinutes(5),
            IsInternetAlive = false, // Internet itself is down on the client PC
            ServerStatus = "on"
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        Assert.DoesNotContain(alerts, a => a.Text.Contains("SSH"));
    }

    [Fact]
    public void Critical_SiteDownTwice_TriggersCritical()
    {
        var input = new ServerStateInput
        {
            ServerStatus = "on",
            Sites = new List<SiteAlertInput>
            {
                new() { Name = "api.example.com", State = "down", ConsecutiveDownCount = 2 }
            }
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        var alert = Assert.Single(alerts);
        Assert.Equal("critical", alert.Level);
        Assert.Contains("api.example.com", alert.Text);
    }

    [Fact]
    public void Critical_SiteDownOnce_DoesNotTriggerCriticalYet()
    {
        var input = new ServerStateInput
        {
            ServerStatus = "on",
            Sites = new List<SiteAlertInput>
            {
                new() { Name = "api.example.com", State = "down", ConsecutiveDownCount = 1 }
            }
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        Assert.DoesNotContain(alerts, a => a.Level == "critical");
    }

    [Fact]
    public void Critical_ServerNotOn_TriggersCritical()
    {
        var input = new ServerStateInput
        {
            ServerStatus = "rebooting"
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        var alert = Assert.Single(alerts);
        Assert.Equal("critical", alert.Level);
        Assert.Contains("rebooting", alert.Text);
    }

    [Fact]
    public void Critical_AccountBlocked_TriggersCritical()
    {
        var input = new ServerStateInput
        {
            ServerStatus = "on",
            IsAccountBlocked = true
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        var alert = Assert.Single(alerts);
        Assert.Equal("critical", alert.Level);
        Assert.Contains("заблокирован", alert.Text);
    }

    [Fact]
    public void Critical_HoursLeftLowWithoutAutopay_TriggersCritical()
    {
        var input = new ServerStateInput
        {
            ServerStatus = "on",
            HoursLeft = 48.0,
            IsAutopayEnabled = false
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        var alert = Assert.Single(alerts);
        Assert.Equal("critical", alert.Level);
        Assert.Contains("Баланс", alert.Text);
    }

    [Fact]
    public void Critical_HoursLeftLowWithAutopay_DoesNotTriggerCritical()
    {
        var input = new ServerStateInput
        {
            ServerStatus = "on",
            HoursLeft = 48.0,
            IsAutopayEnabled = true // Autopay enabled
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        Assert.DoesNotContain(alerts, a => a.Text.Contains("Баланс"));
    }

    [Fact]
    public void Warning_HighRamAndDisk_TriggersWarnings()
    {
        var input = new ServerStateInput
        {
            ServerStatus = "on",
            CurrentRamPct = 92.5,
            CurrentDiskPct = 82.0
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal("warning", a.Level));
        Assert.Contains(alerts, a => a.Text.Contains("RAM"));
        Assert.Contains(alerts, a => a.Text.Contains("Диск"));
    }

    [Fact]
    public void Warning_SlowSiteAndExpiringTls_TriggersWarnings()
    {
        var input = new ServerStateInput
        {
            ServerStatus = "on",
            Sites = new List<SiteAlertInput>
            {
                new() { Name = "portal", State = "slow", ResponseMs = 1850, TlsDays = 12, ConsecutiveDownCount = 0 }
            }
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Text.Contains("Медленный") && a.Level == "warning");
        Assert.Contains(alerts, a => a.Text.Contains("SSL") && a.Level == "warning");
    }

    [Fact]
    public void Warning_OldBackupAndFullBucketAndStaleTimeweb_TriggersWarnings()
    {
        var input = new ServerStateInput
        {
            ServerStatus = "on",
            BackupAgeHours = 40.0,
            BucketUsedPct = 85.0,
            TimewebStaleMinutes = 35.0
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        Assert.Equal(3, alerts.Count);
        Assert.Contains(alerts, a => a.Text.Contains("бэкап"));
        Assert.Contains(alerts, a => a.Text.Contains("S3-бакет"));
        Assert.Contains(alerts, a => a.Text.Contains("устарели"));
    }

    [Fact]
    public void Warning_CpuSustainedHigh_TriggersWarning()
    {
        var points = new List<MetricPoint>();
        for (int i = 0; i <= 65; i++)
        {
            points.Add(new MetricPoint { T = 1000 + i * 2, Cpu = 88.0 });
        }

        var input = new ServerStateInput
        {
            ServerStatus = "on",
            RecentCpuPoints = points
        };

        var alerts = ServerAlertEvaluator.Evaluate(input);

        var alert = Assert.Single(alerts);
        Assert.Equal("warning", alert.Level);
        Assert.Contains("CPU", alert.Text);
    }
}
