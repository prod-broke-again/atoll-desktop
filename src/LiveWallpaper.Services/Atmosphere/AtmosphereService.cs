using System;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Atmosphere;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Server;
using LiveWallpaper.Services.Server;
using LiveWallpaper.Services.Today;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Atmosphere;

public sealed class AtmosphereService : BackgroundService, IAtmosphereService
{
    private readonly IConfigManager _configManager;
    private readonly IServerWidgetService _serverService;
    private readonly ITodayService _todayService;
    private readonly ILogger<AtmosphereService> _logger;

    private ServerStateSnapshot? _latestServerSnapshot;
    private AtmosphereSignalsState _currentState = AtmosphereSignalsState.Empty;
    private System.Threading.Timer? _periodicTimer;

    // Manual test triggers
    private bool? _testProdAlert;
    private bool? _testUpcomingCall;
    private bool? _testLowBalance;

    public AtmosphereSignalsState CurrentState => _currentState;

    public event EventHandler<AtmosphereSignalsState>? AtmosphereUpdated;

    public AtmosphereService(
        IConfigManager configManager,
        IServerWidgetService serverService,
        ITodayService todayService,
        ILogger<AtmosphereService> logger)
    {
        _configManager = configManager;
        _serverService = serverService;
        _todayService = todayService;
        _logger = logger;

        _serverService.StateSnapshotUpdated += OnServerStateSnapshotUpdated;
        _todayService.TodayUpdated += OnTodayUpdated;
        _configManager.ConfigChanged += OnConfigChanged;
    }

    private void OnTodayUpdated(object? sender, Core.Today.TodaySnapshot e) => EvaluateAtmosphere();

    private void OnConfigChanged(object? sender, AppConfig e)
    {
        EvaluateAtmosphere();
    }

    private void OnServerStateSnapshotUpdated(ServerStateSnapshot snapshot)
    {
        _latestServerSnapshot = snapshot;
        EvaluateAtmosphere();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EvaluateAtmosphere();

        // Check every 3 seconds (e.g. for upcoming call timer or alert changes)
        _periodicTimer = new System.Threading.Timer(
            _ => EvaluateAtmosphere(),
            null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3));

        return Task.CompletedTask;
    }

    public void TriggerTestSignal(string signalType, bool active)
    {
        var st = signalType.Trim().ToLowerInvariant();
        if (st.Contains("prod"))
        {
            _testProdAlert = active;
        }
        else if (st.Contains("call"))
        {
            _testUpcomingCall = active;
        }
        else if (st.Contains("balance"))
        {
            _testLowBalance = active;
        }

        _logger.LogInformation("Atmosphere test signal triggered: {Signal}={Active}", signalType, active);
        EvaluateAtmosphere();
    }

    private void EvaluateAtmosphere()
    {
        try
        {
            var config = _configManager.CurrentConfig.Atmosphere;

            // Merge test flags
            var effectiveConfig = new AtmosphereConfig
            {
                Enabled = config.Enabled,
                EnableProdAlert = config.EnableProdAlert,
                EnableUpcomingCall = config.EnableUpcomingCall,
                EnableLowBalanceBeacon = config.EnableLowBalanceBeacon,
                UpcomingCallMinutesBefore = config.UpcomingCallMinutesBefore,
                TestProdAlert = _testProdAlert.HasValue ? _testProdAlert.Value : config.TestProdAlert,
                TestUpcomingCall = _testUpcomingCall.HasValue ? _testUpcomingCall.Value : config.TestUpcomingCall,
                TestLowBalance = _testLowBalance.HasValue ? _testLowBalance.Value : config.TestLowBalance
            };

            var newState = AtmosphereEvaluator.Evaluate(
                effectiveConfig,
                _latestServerSnapshot,
                _todayService.NextUpcomingEventLocal,
                _todayService.NextUpcomingEventTitle);

            if (_currentState != newState)
            {
                _currentState = newState;
                _logger.LogInformation("Atmosphere signals updated: ProdAlert={Prod}, UpcomingCall={Call}, LowBalance={Balance}",
                    newState.ProdAlert, newState.UpcomingCall, newState.LowBalance);

                AtmosphereUpdated?.Invoke(this, newState);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error evaluating atmosphere signals");
        }
    }

    public override void Dispose()
    {
        _periodicTimer?.Dispose();
        _serverService.StateSnapshotUpdated -= OnServerStateSnapshotUpdated;
        _todayService.TodayUpdated -= OnTodayUpdated;
        _configManager.ConfigChanged -= OnConfigChanged;
        base.Dispose();
    }
}
