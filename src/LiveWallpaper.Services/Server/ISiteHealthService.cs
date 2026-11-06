using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Server;

namespace LiveWallpaper.Services.Server;

public sealed record SiteStatusRecord
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string State { get; init; } = "unknown"; // "up", "slow", "down", "unknown"
    public double? ResponseMs { get; init; }
    public int? TlsDays { get; init; }
    public int ConsecutiveDownCount { get; init; }
}

public interface ISiteHealthService
{
    IReadOnlyList<SiteStatusRecord> GetCurrentStatus();
    Task CheckAllAsync(CancellationToken cancellationToken);
    Task<string> CheckSiteDiagnosticAsync(string name, string url, CancellationToken cancellationToken);
}
