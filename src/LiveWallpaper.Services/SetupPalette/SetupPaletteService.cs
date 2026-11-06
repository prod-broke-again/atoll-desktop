using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Collage;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Context;
using LiveWallpaper.Core.SetupPalette;
using LiveWallpaper.Native;
using LiveWallpaper.Services.Context;
using LiveWallpaper.Services.Pins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.SetupPalette;

public sealed class SetupPaletteService : BackgroundService, ISetupPaletteService
{
    private readonly IConfigManager _configManager;
    private readonly ICollageEngineService _collageService;
    private readonly IContextService _contextService;
    private readonly ILogger<SetupPaletteService> _logger;
    private readonly OpenRgbClient _openRgbClient;
    private readonly RazerChromaClient _razerClient;
    private readonly RedragonHidClient _redragonClient;

    private RgbColor? _currentAccent;
    private RgbColor? _keyboardColor;
    private RgbColor? _desktopAccent;
    private DateTime _desktopAppliedAt = DateTime.MinValue;
    private CancellationTokenSource? _pending;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private string _paletteMode = "";
    private bool _appliedNight;
    private float _appliedIntensity = -1f;
    private System.Threading.Timer? _nightWatch;

    public RgbColor? CurrentAccent => _currentAccent;

    public event EventHandler<RgbColor>? PaletteApplied;

    public SetupPaletteService(
        IConfigManager configManager,
        ICollageEngineService collageService,
        IContextService contextService,
        IHttpClientFactory httpFactory,
        ILogger<SetupPaletteService> logger)
    {
        _configManager = configManager;
        _collageService = collageService;
        _contextService = contextService;
        _logger = logger;
        _openRgbClient = new OpenRgbClient(logger);
        _razerClient = new RazerChromaClient(httpFactory.CreateClient(), logger);
        _redragonClient = new RedragonHidClient(logger);
        _paletteMode = _contextService.EffectiveMode;

        _collageService.CollageChanged += OnCollageChanged;
        _contextService.ModeChanged += OnContextModeChanged;
        _configManager.ConfigChanged += OnConfigChanged;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_collageService.CurrentCollage != null)
        {
            _ = ApplyFromCollageAsync(_collageService.CurrentCollage, stoppingToken);
        }

        // Flip the keyboard at 21:00 / 07:00 even if the collage has not changed yet.
        _nightWatch = new System.Threading.Timer(_ =>
        {
            var night = IsNight();
            if (_appliedIntensity >= 0 && night == _appliedNight) return;
            RequestApply(force: _appliedIntensity >= 0);
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _collageService.CollageChanged -= OnCollageChanged;
        _contextService.ModeChanged -= OnContextModeChanged;
        _configManager.ConfigChanged -= OnConfigChanged;
        _nightWatch?.Dispose();
        _redragonClient.Dispose();
        base.Dispose();
    }

    /// <summary>Clock, same window as Windows night light. Not the tray mode and not the foreground app.</summary>
    private bool IsNight() =>
        ContextEvaluator.IsNightHour(_configManager.CurrentConfig.Context, DateTime.Now);

    private static float KeyboardIntensity(SetupPaletteConfig cfg, bool night) =>
        Math.Clamp(night ? cfg.NightKeyboardIntensity : cfg.KeyboardIntensity, 0.05f, 1f);

    private void OnContextModeChanged(object? sender, ContextModeChangedEventArgs e)
    {
        if (string.Equals(_paletteMode, e.EffectiveMode, StringComparison.OrdinalIgnoreCase))
            return;
        _paletteMode = e.EffectiveMode;
        RequestApply(force: true);
    }

    private void OnConfigChanged(object? sender, AppConfig cfg)
    {
        var night = IsNight();
        var intensity = KeyboardIntensity(cfg.SetupPalette, night);
        if (night == _appliedNight && Math.Abs(intensity - _appliedIntensity) < 0.005f)
            return;
        RequestApply(force: false);
    }

    /// <summary>Repaint from the current collage without waiting out the collage debounce.</summary>
    private void RequestApply(bool force)
    {
        var collage = _collageService.CurrentCollage;
        if (collage == null) return;

        var night = IsNight();
        _appliedNight = night;
        _appliedIntensity = KeyboardIntensity(_configManager.CurrentConfig.SetupPalette, night);

        _ = Task.Run(async () =>
        {
            try { await ApplyFromCollageAsync(collage, force, CancellationToken.None).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        });
    }

    private void OnCollageChanged(object? sender, CollageSelectionResult result)
    {
        // Debounce + let the collage transition finish first (it decodes images and animates);
        // a newer collage cancels a pending apply.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _pending, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1800, cts.Token).ConfigureAwait(false);
                await ApplyFromCollageAsync(result, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    public Task ApplyCurrentAsync(CancellationToken ct = default)
    {
        if (_collageService.CurrentCollage != null)
            return ApplyFromCollageAsync(_collageService.CurrentCollage, force: true, ct);
        if (_currentAccent != null)
            return ApplyAccentAsync(_currentAccent.Value, ct);
        return ApplyAccentAsync(new RgbColor(99, 102, 241), ct);
    }

    private Task ApplyFromCollageAsync(CollageSelectionResult collage, CancellationToken ct) =>
        ApplyFromCollageAsync(collage, force: false, ct);

    private async Task ApplyFromCollageAsync(CollageSelectionResult collage, bool force, CancellationToken ct)
    {
        var cfg = _configManager.CurrentConfig.SetupPalette;
        if (!cfg.Enabled) return;

        var night = IsNight();
        RgbColor accent;
        if (!string.IsNullOrEmpty(cfg.CustomAccentHex) && PaletteColorUtils.ParseHex(cfg.CustomAccentHex) is { } custom)
        {
            accent = night ? PaletteColorUtils.ToneForNight(custom) : custom;
        }
        else if (night)
        {
            accent = PaletteColorUtils.ExtractNightAccent(collage.SelectedPins);
        }
        else
        {
            accent = PaletteColorUtils.ExtractVividAccent(collage.SelectedPins, collage.PrimaryHue);
        }

        await ApplyStagedAsync(accent, force, night, ct);
    }

    /// <summary>Manual "apply now" (settings button / tray): everything, immediately.</summary>
    public Task ApplyAccentAsync(RgbColor accent, CancellationToken ct = default)
    {
        var night = IsNight();
        if (night) accent = PaletteColorUtils.ToneForNight(accent);
        return ApplyStagedAsync(accent, force: true, night, ct);
    }

    /// <summary>
    /// Keyboard first (cheap, faded), then — only if due — Terminal and Windows accent,
    /// spaced out so nothing lands on the same frame as the wallpaper animation.
    /// </summary>
    private async Task ApplyStagedAsync(RgbColor accent, bool force, bool night, CancellationToken ct)
    {
        var cfg = _configManager.CurrentConfig.SetupPalette;
        if (!cfg.Enabled) return;

        var intensity = KeyboardIntensity(cfg, night);
        _appliedNight = night;
        _appliedIntensity = intensity;

        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _currentAccent = accent;
            var hex = PaletteColorUtils.ToHex(accent);

            // 1) Peripherals: cheap, follow every collage.
            if (cfg.SyncRedragonHid)
            {
                try
                {
                    var from = _keyboardColor ?? accent;
                    var ok = cfg.FadeKeyboard && _keyboardColor.HasValue && !force
                        ? await _redragonClient.FadeAsync(from, accent, intensity: intensity, ct: ct).ConfigureAwait(false)
                        : await _redragonClient.ApplyAccentAsync(accent, intensity, CancellationToken.None).ConfigureAwait(false);
                    if (ok) _keyboardColor = accent;
                    else _logger.LogDebug("Redragon HID sync failed for accent {Hex}.", hex);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Redragon HID sync error.");
                }
            }
            if (cfg.SyncOpenRgb)
            {
                _ = Task.Run(async () =>
                {
                    try { await _openRgbClient.ApplyAccentAsync(accent, cfg.OpenRgbHost, cfg.OpenRgbPort, ct); }
                    catch (Exception ex) { _logger.LogDebug(ex, "OpenRGB sync task error."); }
                }, ct);
            }
            if (cfg.SyncRazerChroma)
            {
                _ = Task.Run(async () =>
                {
                    try { await _razerClient.ApplyAccentAsync(accent, ct); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Razer Chroma sync task error."); }
                }, ct);
            }
            PaletteApplied?.Invoke(this, accent);
            try
            {
                WallpaperThemeFile.Write(accent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Wallpaper theme file was not written.");
            }

            // 2) Desktop (Terminal + Windows accent): expensive, throttled.
            if (!cfg.SyncWindowsTerminal && !cfg.SyncWindowsAccent) return;
            var minutes = (DateTime.UtcNow - _desktopAppliedAt).TotalMinutes;
            var hueMove = _desktopAccent.HasValue ? HueDistance(_desktopAccent.Value, accent) : 360;
            var due = force || !_desktopAccent.HasValue ||
                      (minutes >= Math.Max(1, cfg.DesktopMinIntervalMinutes) && hueMove >= cfg.DesktopHueThresholdDegrees);
            if (!due)
            {
                _logger.LogDebug("Desktop palette skipped ({Minutes:F0} min since last, hue Δ{Hue:F0}°).", minutes, hueMove);
                return;
            }

            _logger.LogInformation("Applying desktop palette {Hex} (Windows: {Win}, Terminal: {Term}).", hex, cfg.SyncWindowsAccent, cfg.SyncWindowsTerminal);
            if (cfg.SyncWindowsTerminal)
            {
                if (!force) await Task.Delay(1500, ct).ConfigureAwait(false);
                try { WindowsTerminalSync.ApplyAccent(accent, _logger); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to update Windows Terminal scheme."); }
            }
            if (cfg.SyncWindowsAccent)
            {
                if (!force) await Task.Delay(1500, ct).ConfigureAwait(false);
                try
                {
                    if (!WindowsAccentHelper.ApplyAccent(accent))
                        _logger.LogWarning("Windows accent apply returned false.");
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to apply Windows accent color."); }
            }
            _desktopAccent = accent;
            _desktopAppliedAt = DateTime.UtcNow;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private static double HueDistance(RgbColor a, RgbColor b)
    {
        static double Hue(RgbColor c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, bl = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl)), d = max - min;
            if (d < 1e-6) return 0;
            double h = max == r ? (g - bl) / d % 6 : max == g ? (bl - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
            return h < 0 ? h + 360 : h;
        }
        var diff = Math.Abs(Hue(a) - Hue(b));
        return Math.Min(diff, 360 - diff);
    }
}
