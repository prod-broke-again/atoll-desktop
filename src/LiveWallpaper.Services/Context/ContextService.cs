using System;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Context;
using LiveWallpaper.Native;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Context;

public sealed class ContextService : BackgroundService, IContextService
{
    private readonly IConfigManager _configManager;
    private readonly ILogger<ContextService> _logger;

    private System.Threading.Timer? _checkTimer;
    private string _selectedMode = ContextMode.Auto;
    private string _effectiveMode = ContextMode.Work;
    private string _currentReason = "init";
    private ModeWidgetsConfig _currentWidgets = new();
    private string _lastActiveProcess = string.Empty;

    public string SelectedMode => _selectedMode;
    public string EffectiveMode => _effectiveMode;
    public string CurrentReason => _currentReason;
    public ModeWidgetsConfig CurrentWidgets => _currentWidgets;

    public event EventHandler<ContextModeChangedEventArgs>? ModeChanged;

    public ContextService(IConfigManager configManager, ILogger<ContextService> logger)
    {
        _configManager = configManager;
        _logger = logger;

        _selectedMode = _configManager.CurrentConfig.Context.Mode;
        _currentWidgets = _configManager.CurrentConfig.Context.WorkWidgets;

        _configManager.ConfigChanged += OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, AppConfig newConfig)
    {
        var configuredMode = newConfig.Context.Mode;
        if (!string.Equals(_selectedMode, configuredMode, StringComparison.OrdinalIgnoreCase))
        {
            _selectedMode = configuredMode;
            EvaluateContext();
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EvaluateContext();

        _checkTimer = new System.Threading.Timer(
            _ => EvaluateContext(),
            null,
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromSeconds(1.5));

        return Task.CompletedTask;
    }

    public void SetMode(string mode)
    {
        var normalized = ContextMode.Normalize(mode);
        _selectedMode = normalized;

        try
        {
            var cfg = _configManager.CurrentConfig;
            cfg.Context.Mode = normalized;
            _configManager.Save(cfg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist new context mode {Mode}", normalized);
        }

        EvaluateContext();
    }

    private void EvaluateContext()
    {
        try
        {
            var config = _configManager.CurrentConfig.Context;
            var activeProc = ForegroundAppDetector.GetForegroundProcessName();
            if (!string.IsNullOrEmpty(activeProc))
            {
                _lastActiveProcess = activeProc;
            }

            var result = ContextEvaluator.Evaluate(
                config,
                DateTime.Now,
                _lastActiveProcess,
                _configManager.CurrentConfig.Performance.BlockedProcesses);

            bool modeChanged = !string.Equals(_effectiveMode, result.EffectiveMode, StringComparison.OrdinalIgnoreCase);
            bool reasonChanged = !string.Equals(_currentReason, result.Reason, StringComparison.OrdinalIgnoreCase);
            bool selectedChanged = !string.Equals(_selectedMode, result.SelectedMode, StringComparison.OrdinalIgnoreCase);

            if (modeChanged || selectedChanged || reasonChanged)
            {
                _selectedMode = result.SelectedMode;
                _effectiveMode = result.EffectiveMode;
                _currentReason = result.Reason;
                _currentWidgets = result.Widgets;

                _logger.LogInformation("Context mode updated: Selected={Selected}, Effective={Effective}, Reason={Reason}",
                    _selectedMode, _effectiveMode, _currentReason);

                ModeChanged?.Invoke(this, new ContextModeChangedEventArgs(
                    _selectedMode,
                    _effectiveMode,
                    _currentReason,
                    _currentWidgets));
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error evaluating context mode");
        }
    }

    public override void Dispose()
    {
        _checkTimer?.Dispose();
        _configManager.ConfigChanged -= OnConfigChanged;
        base.Dispose();
    }
}
