using System;

namespace LiveWallpaper.Services.Telemetry;

public sealed class TelemetryData
{
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    public double GpuPercent { get; set; }
    public double? GpuTempCelsius { get; set; }
    public double NetDownloadKbps { get; set; }
    public double NetUploadKbps { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
