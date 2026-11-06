using System;
using System.Collections.Generic;
using System.Linq;
using LiveWallpaper.Core.Collage;

namespace LiveWallpaper.Core.SetupPalette;

public static class PaletteColorUtils
{
    private static readonly RgbColor DefaultAccent = new(99, 102, 241); // Indigo

    /// <summary>Night accent value is capped here so LEDs stay dim before the HID intensity scale.</summary>
    public const float NightMaxValue = 0.5f;

    /// <summary>Dim amber (hue of #B45309, value capped). Used when a night collage has no warm pin.</summary>
    public static readonly RgbColor NightAmber = RgbColor.FromHsv(26f, 0.95f, NightMaxValue);

    /// <summary>Red through yellow, wrapping past 0°. Excludes green, cyan, blue, and purple.</summary>
    public static bool IsWarmHue(float hue)
    {
        hue = (hue % 360f + 360f) % 360f;
        return hue >= 345f || hue < 70f;
    }

    /// <summary>
    /// Selects the most expressive, vivid accent color from a list of collage pins.
    /// </summary>
    public static RgbColor ExtractVividAccent(IReadOnlyList<PinItem>? pins, HueGroup primaryHue = HueGroup.NeutralDark)
    {
        if (pins == null || pins.Count == 0)
        {
            return DefaultAccent;
        }

        // Find pin with highest saturation and good brightness
        PinItem? bestPin = null;
        double bestScore = -1;

        foreach (var pin in pins)
        {
            var (h, s, v) = pin.DominantColor.ToHsv();
            if (s < 0.10) continue; // skip nearly grayscale pins

            // Score favors high saturation and medium-high brightness
            var score = s * 1.5 + (1.0 - Math.Abs(v - 0.75));
            if (score > bestScore)
            {
                bestScore = score;
                bestPin = pin;
            }
        }

        var candidate = bestPin != null ? bestPin.DominantColor : pins[0].DominantColor;
        return MakeVivid(candidate);
    }

    /// <summary>
    /// Night accent: warm pins only (red, orange, amber, yellow), value capped at <see cref="NightMaxValue"/>.
    /// Cool or gray collages fall back to <see cref="NightAmber"/>.
    /// </summary>
    public static RgbColor ExtractNightAccent(IReadOnlyList<PinItem>? pins)
    {
        if (pins == null || pins.Count == 0)
            return NightAmber;

        PinItem? bestPin = null;
        double bestScore = -1;

        foreach (var pin in pins)
        {
            var (h, s, v) = pin.DominantColor.ToHsv();
            if (s < 0.10f || !IsWarmHue(h)) continue;

            var score = s * 1.5 + (1.0 - Math.Abs(v - 0.45));
            if (score > bestScore)
            {
                bestScore = score;
                bestPin = pin;
            }
        }

        return bestPin == null ? NightAmber : ToneForNight(bestPin.DominantColor);
    }

    /// <summary>
    /// Keeps a warm hue and caps brightness. Cool or gray colors become <see cref="NightAmber"/>.
    /// </summary>
    public static RgbColor ToneForNight(RgbColor c)
    {
        var (h, s, v) = c.ToHsv();
        if (s < 0.08f || !IsWarmHue(h))
            return NightAmber;

        s = Math.Clamp(s, 0.45f, 1f);
        v = Math.Clamp(v, 0.22f, NightMaxValue);
        return RgbColor.FromHsv(h, s, v);
    }

    /// <summary>
    /// Ensures color has sufficient saturation and pleasant brightness for UI and LED hardware.
    /// </summary>
    public static RgbColor MakeVivid(RgbColor c, float targetBrightness = 0.85f, float minSaturation = 0.65f)
    {
        var (h, s, v) = c.ToHsv();
        if (s < 0.08f)
        {
            // Cool slate/indigo fallback for pure grays
            h = 225f;
            s = 0.55f;
        }
        else
        {
            s = Math.Clamp(s * 1.35f, minSaturation, 1.0f);
        }

        v = Math.Clamp(targetBrightness, 0.55f, 1.0f);
        return RgbColor.FromHsv(h, s, v);
    }

    /// <summary>
    /// Generates the 32-byte AccentPalette array (8 x RGBA) used by Windows 11 Explorer/DWM.
    /// </summary>
    public static byte[] GenerateWindowsAccentPalette(RgbColor baseColor)
    {
        var bytes = new byte[32];

        // 8 shades: 3 tints (lighter), base, 3 shades (darker), and 1 accent highlight
        var palette = new[]
        {
            Blend(baseColor, new RgbColor(255, 255, 255), 0.72), // 0: Lightest
            Blend(baseColor, new RgbColor(255, 255, 255), 0.48), // 1: Lighter
            Blend(baseColor, new RgbColor(255, 255, 255), 0.24), // 2: Light
            baseColor,                                           // 3: Base Accent
            Blend(baseColor, new RgbColor(0, 0, 0), 0.20),       // 4: Dark
            Blend(baseColor, new RgbColor(0, 0, 0), 0.40),       // 5: Darker
            Blend(baseColor, new RgbColor(0, 0, 0), 0.60),       // 6: Darkest
            MakeVivid(baseColor, targetBrightness: 0.95f, minSaturation: 0.85f) // 7: Highlight
        };

        for (int i = 0; i < 8; i++)
        {
            var col = palette[i];
            int offset = i * 4;
            bytes[offset] = col.R;
            bytes[offset + 1] = col.G;
            bytes[offset + 2] = col.B;
            bytes[offset + 3] = 0x00; // Alpha is stored as 0 in AccentPalette
        }

        return bytes;
    }

    /// <summary>
    /// Blends two colors linearly with factor t in [0..1].
    /// </summary>
    public static RgbColor Blend(RgbColor a, RgbColor b, double t)
    {
        var k = Math.Clamp(t, 0.0, 1.0);
        var r = (byte)Math.Clamp(Math.Round(a.R + (b.R - a.R) * k), 0, 255);
        var g = (byte)Math.Clamp(Math.Round(a.G + (b.G - a.G) * k), 0, 255);
        var bl = (byte)Math.Clamp(Math.Round(a.B + (b.B - a.B) * k), 0, 255);
        return new RgbColor(r, g, bl);
    }

    /// <summary>
    /// Converts RGB color to ABGR uint (Windows DWM AccentColor).
    /// </summary>
    public static uint ToAbgr(RgbColor color, byte alpha = 0xFF)
    {
        return ((uint)alpha << 24) | ((uint)color.B << 16) | ((uint)color.G << 8) | color.R;
    }

    /// <summary>
    /// Converts RGB color to ARGB uint (Windows DWM ColorizationColor).
    /// </summary>
    public static uint ToArgb(RgbColor color, byte alpha = 0xC4)
    {
        return ((uint)alpha << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
    }

    /// <summary>
    /// Converts to standard hex string "#RRGGBB".
    /// </summary>
    public static string ToHex(RgbColor color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public static RgbColor? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 &&
            byte.TryParse(clean.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(clean.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(clean.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return new RgbColor(r, g, b);
        }
        return null;
    }
}
