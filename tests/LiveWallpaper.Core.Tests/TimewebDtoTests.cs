using System;
using System.Text.Json;
using LiveWallpaper.Services.Server;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public class TimewebDtoTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void ServersResponse_DeserializesRealFixture_AndComputesDiskPct()
    {
        string json = """
        {
          "servers": [
            {
              "id": 5350637,
              "name": "api.example.com",
              "status": "on",
              "created_at": "2025-07-24T10:00:00Z",
              "disks": [
                {
                  "id": 1001,
                  "size": 81920,
                  "used": 46848,
                  "size_gb": 80.0,
                  "used_gb": 45.74,
                  "status": "done"
                }
              ]
            }
          ]
        }
        """;

        var res = JsonSerializer.Deserialize<ServersResponse>(json, Options);

        Assert.NotNull(res);
        Assert.Single(res.Servers);
        var s = res.Servers[0];
        Assert.Equal(5350637, s.Id);
        Assert.Equal("api.example.com", s.Name);
        Assert.Equal("on", s.Status);
        Assert.Single(s.Disks);
        Assert.Equal(80.0, s.Disks[0].SizeGb);
        Assert.Equal(45.74, s.Disks[0].UsedGb);

        // 45.74 / 80 * 100 = 57.175%
        double pct = s.ComputeDiskPct();
        Assert.InRange(pct, 57.1, 57.25);
    }

    [Fact]
    public void ServersResponse_UnknownStatus_FallsBackCleanly()
    {
        string json = """
        {
          "servers": [
            {
              "id": 9999,
              "name": "staging",
              "status": "quantum_rebooting",
              "disks": []
            }
          ]
        }
        """;

        var res = JsonSerializer.Deserialize<ServersResponse>(json, Options);

        Assert.NotNull(res);
        var s = Assert.Single(res.Servers);
        Assert.Equal("quantum_rebooting", s.Status);
        Assert.Equal(0.0, s.ComputeDiskPct());
    }

    [Fact]
    public void FinancesResponse_DeserializesRealInventoryFixture_WithSbpAutopay()
    {
        string json = """
        {
          "finances": {
            "balance": 1170.16,
            "currency": "RUB",
            "hourly_cost": 3.47,
            "hours_left": 362,
            "days_left": 15.08,
            "autopay_card_info": "sbp"
          }
        }
        """;

        var res = JsonSerializer.Deserialize<FinancesResponse>(json, Options);

        Assert.NotNull(res);
        Assert.NotNull(res.Finances);
        Assert.Equal(1170.16, res.Finances.Balance);
        Assert.Equal("RUB", res.Finances.Currency);
        Assert.True(res.Finances.HasAutopay());
        Assert.Equal(15.08, res.Finances.ComputeDaysLeft());
    }

    [Fact]
    public void FinancesResponse_NullAutopay_ReturnsFalse()
    {
        string json = """
        {
          "finances": {
            "balance": 500.0,
            "currency": "RUB",
            "hourly_cost": 5.0,
            "hours_left": 100,
            "autopay_card_info": null
          }
        }
        """;

        var res = JsonSerializer.Deserialize<FinancesResponse>(json, Options);

        Assert.NotNull(res);
        Assert.False(res.Finances.HasAutopay());
        Assert.Equal(100.0 / 24.0, res.Finances.ComputeDaysLeft()!.Value, 2);
    }

    [Fact]
    public void AccountStatusResponse_DeserializesCorrectly()
    {
        string unblockedJson = """
        {
          "status": {
            "is_blocked": false,
            "is_permanent_blocked": false,
            "company_is_blocked": false
          }
        }
        """;

        var unblocked = JsonSerializer.Deserialize<AccountStatusResponse>(unblockedJson, Options);
        Assert.NotNull(unblocked);
        Assert.False(unblocked.Status.IsAnyBlocked());

        string blockedJson = """
        {
          "status": {
            "is_blocked": true,
            "is_permanent_blocked": false
          }
        }
        """;

        var blocked = JsonSerializer.Deserialize<AccountStatusResponse>(blockedJson, Options);
        Assert.NotNull(blocked);
        Assert.True(blocked.Status.IsAnyBlocked());
    }

    [Fact]
    public void BackupsResponse_DeserializesCorrectly()
    {
        string json = """
        {
          "backups": [
            {
              "id": 401,
              "status": "done",
              "created_at": "2026-10-27T01:06:00Z",
              "size_gb": 80.0
            }
          ]
        }
        """;

        var res = JsonSerializer.Deserialize<BackupsResponse>(json, Options);

        Assert.NotNull(res);
        var b = Assert.Single(res.Backups);
        Assert.Equal("done", b.Status);
        Assert.Equal(80.0, b.SizeGb);
        Assert.NotNull(b.CreatedAt);
    }

    [Fact]
    public void BucketsResponse_DeserializesRealFixture_AndComputesUsedPct()
    {
        string json = """
        {
          "buckets": [
            {
              "id": 467765,
              "name": "documents-api",
              "status": "created",
              "object_amount": 2705,
              "size_gb": 10.0,
              "used_gb": 5.24,
              "disk_stats": {
                "size": 10485760,
                "used": 5494538
              }
            }
          ]
        }
        """;

        var res = JsonSerializer.Deserialize<BucketsResponse>(json, Options);

        Assert.NotNull(res);
        var bucket = Assert.Single(res.Buckets);
        Assert.Equal("documents-api", bucket.Name);
        Assert.Equal(2705, bucket.ObjectAmount);
        // 5.24 / 10.0 * 100 = 52.4%
        Assert.Equal(52.4, bucket.ComputeUsedPct(), 1);
    }
}
