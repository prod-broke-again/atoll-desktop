using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Server;

namespace LiveWallpaper.Services.Server;

public interface IServerWidgetService
{
    ServerStateSnapshot CurrentSnapshot { get; }
    IReadOnlyList<MetricPoint> History { get; }
    event Action<MetricPoint>? MetricPointReceived;
    event Action<ServerStateSnapshot>? StateSnapshotUpdated;
    Task<string> RunDiagnosticsAsync(CancellationToken cancellationToken);
    void SetPrivacyMode(bool enabled);
}
