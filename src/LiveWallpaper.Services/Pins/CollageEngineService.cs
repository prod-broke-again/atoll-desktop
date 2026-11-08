using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Collage;
using LiveWallpaper.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Pins;

public interface ICollageEngineService
{
    CollageSelectionResult? CurrentCollage { get; }
    void RotateNow();
    event EventHandler<CollageSelectionResult>? CollageChanged;
}

public sealed class CollageEngineService : BackgroundService, ICollageEngineService
{
    private readonly PinsIndexerService _indexer;
    private readonly IConfigManager _configManager;
    private readonly ILogger<CollageEngineService> _logger;
    private readonly CollageSelector _selector;
    private System.Threading.Timer? _rotationTimer;

    public CollageSelectionResult? CurrentCollage { get; private set; }
    public event EventHandler<CollageSelectionResult>? CollageChanged;

    public CollageEngineService(
        PinsIndexerService indexer,
        IConfigManager configManager,
        ILogger<CollageEngineService> logger)
    {
        _indexer = indexer;
        _configManager = configManager;
        _logger = logger;
        _selector = new CollageSelector(_configManager.CurrentConfig.Collage.MaxHistoryCount);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _indexer.IndexUpdated += (s, e) =>
        {
            if (_configManager.CurrentConfig.Collage.Enabled && CurrentCollage == null)
            {
                RotateNow();
            }
        };

        _configManager.ConfigChanged += (s, cfg) =>
        {
            if (!cfg.Collage.Enabled)
            {
                _rotationTimer?.Dispose();
                _rotationTimer = null;
                CurrentCollage = null;
                // WallpaperForm also listens to ConfigChanged and pushes collage_clear to the web.
                return;
            }
            ResetTimer();
            if (CurrentCollage == null) RotateNow();
        };
        if (_configManager.CurrentConfig.Collage.Enabled)
            ResetTimer();

        return Task.CompletedTask;
    }

    public void RotateNow()
    {
        var cfg = _configManager.CurrentConfig.Collage;
        if (!cfg.Enabled)
        {
            _logger.LogInformation("Collage rotation skipped: collage is disabled.");
            return;
        }

        var pins = _indexer.IndexedPins;
        if (pins.Count == 0)
        {
            _logger.LogWarning("Cannot rotate collage: no pins indexed yet.");
            return;
        }

        var result = _selector.SelectHarmoniousCollage(
            pins,
            count: 18,
            enableHarmony: cfg.EnableColorHarmony,
            targetCategory: cfg.SelectedCategory,
            targetHue: cfg.SelectedHueGroup);
        if (result != null)
        {
            CurrentCollage = result;
            _logger.LogInformation(
                "Rotated collage: {Count} pins selected (Primary Hue: {Hue}, Ambient: {Color})",
                result.SelectedPins.Count,
                result.PrimaryHue,
                result.AverageBackdropColor.ToHex());

            CollageChanged?.Invoke(this, result);
        }
    }

    private void ResetTimer()
    {
        _rotationTimer?.Dispose();
        _rotationTimer = null;
        if (!_configManager.CurrentConfig.Collage.Enabled) return;
        var intervalMs = (long)_configManager.CurrentConfig.Collage.IntervalMinutes * 60 * 1000;
        _rotationTimer = new System.Threading.Timer(_ => RotateNow(), null, intervalMs, intervalMs);
    }

    public override void Dispose()
    {
        _rotationTimer?.Dispose();
        base.Dispose();
    }
}
