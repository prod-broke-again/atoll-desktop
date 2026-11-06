using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RedragonRgb;
using CoreRgb = LiveWallpaper.Core.Collage.RgbColor;
using HidRgb = RedragonRgb.RgbColor;

namespace LiveWallpaper.Services.SetupPalette;

/// <summary>
/// Applies setup-palette accent to Redragon/Sinowealth keyboards (VID 258A / PID 0049)
/// via direct HID — no OpenRGB required.
/// </summary>
public sealed class RedragonHidClient : IDisposable
{
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private RedragonKeyboard? _keyboard;
    private bool _disposed;

    public RedragonHidClient(ILogger? logger = null)
    {
        _logger = logger;
    }

    public Task<bool> ApplyAccentAsync(CoreRgb accent, float intensity = 0.85f, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => ApplyAccent(accent, intensity), ct);
    }

    public bool ApplyAccent(CoreRgb accent, float intensity = 0.85f) => ApplyAccent(accent, intensity, log: true);

    /// <summary>Smoothly blends the keyboard from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public Task<bool> FadeAsync(CoreRgb from, CoreRgb to, int steps = 10, int stepMs = 90, float intensity = 0.85f, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            var ok = true;
            for (var i = 1; i <= steps && !ct.IsCancellationRequested; i++)
            {
                var t = i / (double)steps;
                t = t * t * (3 - 2 * t); // smoothstep
                var c = new CoreRgb(
                    (byte)Math.Round(from.R + (to.R - from.R) * t),
                    (byte)Math.Round(from.G + (to.G - from.G) * t),
                    (byte)Math.Round(from.B + (to.B - from.B) * t));
                ok = ApplyAccent(c, intensity, log: i == steps);
                if (!ok) break;
                if (i < steps) await Task.Delay(stepMs, ct).ConfigureAwait(false);
            }
            return ok;
        }, ct);
    }

    private bool ApplyAccent(CoreRgb accent, float intensity, bool log)
    {
        if (_disposed) return false;

        lock (_gate)
        {
            try
            {
                if (_keyboard is null || !_keyboard.IsConnected)
                {
                    _keyboard?.Dispose();
                    _keyboard = new RedragonKeyboard();
                    if (!_keyboard.Connect())
                    {
                        _logger?.LogDebug("Redragon HID keyboard not found (258A:0049).");
                        _keyboard.Dispose();
                        _keyboard = null;
                        return false;
                    }
                }

                var color = new HidRgb(accent.R, accent.G, accent.B);
                if (!_keyboard.SetAll(color, intensity))
                {
                    _logger?.LogDebug("Redragon HID SetAll failed; reconnecting next time.");
                    _keyboard.Dispose();
                    _keyboard = null;
                    return false;
                }

                if (log)
                {
                    _logger?.LogInformation(
                        "Redragon keyboard updated to accent #{R:X2}{G:X2}{B:X2} at {Intensity:P0}.",
                        accent.R, accent.G, accent.B, intensity);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Redragon HID update failed.");
                try { _keyboard?.Dispose(); } catch { /* ignore */ }
                _keyboard = null;
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _keyboard?.Dispose();
            _keyboard = null;
        }
    }
}
