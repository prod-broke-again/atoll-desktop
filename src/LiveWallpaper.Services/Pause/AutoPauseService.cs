using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Native;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace LiveWallpaper.Services.Pause;

public sealed class PauseStateEventArgs : EventArgs
{
    public bool IsPaused { get; }
    public string Reason { get; }

    public PauseStateEventArgs(bool isPaused, string reason)
    {
        IsPaused = isPaused;
        Reason = reason;
    }
}

public interface IAutoPauseService
{
    bool IsPaused { get; }
    string CurrentReason { get; }
    event EventHandler<PauseStateEventArgs>? PauseStateChanged;
    void SetUserPause(bool pause);
    void ToggleUserPause();
}

public sealed class AutoPauseService : BackgroundService, IAutoPauseService
{
    private readonly IConfigManager _configManager;
    private readonly ILogger<AutoPauseService> _logger;

    private bool _userPaused;
    private bool _sessionLocked;
    private bool _isCurrentlyPaused;
    private string _currentReason = "none";
    private System.Threading.Timer? _checkTimer;

    public bool IsPaused => _isCurrentlyPaused;
    public string CurrentReason => _currentReason;
    public event EventHandler<PauseStateEventArgs>? PauseStateChanged;

    public AutoPauseService(IConfigManager configManager, ILogger<AutoPauseService> logger)
    {
        _configManager = configManager;
        _logger = logger;

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to SystemEvents.SessionSwitch.");
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _checkTimer = new System.Threading.Timer(
            _ => EvaluatePauseConditions(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        return Task.CompletedTask;
    }

    public void SetUserPause(bool pause)
    {
        _userPaused = pause;
        EvaluatePauseConditions();
    }

    public void ToggleUserPause()
    {
        _userPaused = !_userPaused;
        EvaluatePauseConditions();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _sessionLocked = true;
            EvaluatePauseConditions();
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _sessionLocked = false;
            EvaluatePauseConditions();
        }
    }

    private void EvaluatePauseConditions()
    {
        try
        {
            var perf = _configManager.CurrentConfig.Performance;

            // 1. User manual override
            if (_userPaused)
            {
                UpdateState(true, "user");
                return;
            }

            // 2. Session lock
            if (_sessionLocked)
            {
                UpdateState(true, "locked");
                return;
            }

            // 3. Fullscreen check (exclusive D3D or borderless fullscreen)
            if (perf.AutoPauseFullscreen && FullscreenDetector.IsAnyApplicationFullscreen())
            {
                if (!IsPaused)
                {
                    _logger.LogInformation("Fullscreen detected: {Reason}", FullscreenDetector.LastReason);
                }
                UpdateState(true, "fullscreen");
                return;
            }

            // 4. Battery check
            if (perf.AutoPauseBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
            {
                UpdateState(true, "battery");
                return;
            }

            // 5. Blocked process check
            if (perf.BlockedProcesses.Count > 0)
            {
                var blocked = CheckBlockedProcesses(perf.BlockedProcesses);
                if (blocked)
                {
                    UpdateState(true, "blocked_process");
                    return;
                }
            }

            // Everything clear -> unpause
            UpdateState(false, "none");
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error evaluating auto-pause conditions.");
        }
    }

    private static bool CheckBlockedProcesses(System.Collections.Generic.List<string> blockedList)
    {
        try
        {
            var running = Process.GetProcesses();
            foreach (var p in running)
            {
                try
                {
                    var name = p.ProcessName;
                    if (blockedList.Any(b =>
                        b.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        b.Equals(name + ".exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        return false;
    }

    private void UpdateState(bool shouldPause, string reason)
    {
        if (_isCurrentlyPaused == shouldPause && _currentReason == reason) return;

        _isCurrentlyPaused = shouldPause;
        _currentReason = reason;

        _logger.LogInformation("AutoPause state changed: IsPaused={IsPaused}, Reason={Reason}", shouldPause, reason);
        PauseStateChanged?.Invoke(this, new PauseStateEventArgs(shouldPause, reason));
    }

    public override void Dispose()
    {
        _checkTimer?.Dispose();
        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch { }

        base.Dispose();
    }
}
