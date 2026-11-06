using System;
using System.Threading;
using System.Threading.Tasks;

namespace LiveWallpaper.Services.Server;

public sealed record TimewebSnapshot
{
    public bool TokenConfigured { get; init; }
    public bool TokenValid { get; init; } = true;
    public ServerDto? Server { get; init; }
    public FinancesDto? Finances { get; init; }
    public bool IsAccountBlocked { get; init; }
    public BackupDto? LatestBackup { get; init; }
    public BucketDto? Bucket { get; init; }
    public double StaleMinutes { get; init; }
}

public interface ITimewebClient
{
    Task<TimewebSnapshot> PollAsync(CancellationToken cancellationToken);
    Task<string> CheckConnectionAsync(CancellationToken cancellationToken);
}
