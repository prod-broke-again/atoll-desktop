using System;
using System.Text.Json;
using FluentAssertions;
using LiveWallpaper.Services.Telemetry;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public class TelemetryTests
{
    [Fact]
    public void TelemetryData_SerializationRoundTrip_PreservesMetrics()
    {
        var data = new TelemetryData
        {
            CpuPercent = 15.6,
            RamPercent = 52.3,
            RamUsedGb = 16.7,
            RamTotalGb = 31.9,
            GpuPercent = 24.1,
            GpuTempCelsius = 58.0,
            NetDownloadKbps = 1450.2,
            NetUploadKbps = 230.8,
            Timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(data);
        var deserialized = JsonSerializer.Deserialize<TelemetryData>(json);

        deserialized.Should().NotBeNull();
        deserialized!.CpuPercent.Should().Be(15.6);
        deserialized.RamPercent.Should().Be(52.3);
        deserialized.RamUsedGb.Should().Be(16.7);
        deserialized.RamTotalGb.Should().Be(31.9);
        deserialized.GpuPercent.Should().Be(24.1);
        deserialized.GpuTempCelsius.Should().Be(58.0);
        deserialized.NetDownloadKbps.Should().Be(1450.2);
        deserialized.NetUploadKbps.Should().Be(230.8);
    }
}
