namespace TaskbarSpike;

/// <summary>
/// Wallpaper widget tokens. Surface is only <see cref="SurfaceOf"/> — dock CSS uses the same mix.
/// </summary>
internal static class Theme
{
    public const double SurfaceMix = 0.07;
    public static readonly Color DefaultAccent = Color.FromArgb(140, 170, 255);

    private static readonly Color Glass = Color.FromArgb(12, 13, 20);
    private static readonly Color DarkInk = Color.FromArgb(22, 18, 16);
    private static readonly Color LightInk = Color.FromArgb(255, 255, 255);

    private static Swatch _previous;
    private static bool _has = true;

    public static Swatch Current { get; private set; } = Swatch.Create(DefaultAccent);

    public static event Action? Changed;

    public static Color Accent => Current.Accent;
    public static string AccentHex => Current.AccentHex;
    public static Color AccentInk => Current.AccentInk;
    public static Color Surface => Current.Surface;
    public static Color Field => Current.Field;
    public static Color Hover => Current.Hover;
    public static Color Selected => Current.Selected;
    public static Color Text => Current.Text;
    public static Color Text2 => Current.Text2;
    public static Color Text3 => Current.Text3;
    public static Color Track => Current.Track;
    public static Color Canvas => Current.Canvas;
    public static Color OnAccent => Current.OnAccent;

    /// <summary>Same RGB as the solid tokens, but translucent so the popup acrylic shows through.</summary>
    public static Color SurfaceA => Current.SurfaceA;
    public static Color FieldA => Current.FieldA;
    public static Color HoverA => Current.HoverA;
    public static Color SelectedA => Current.SelectedA;
    public static Color TrackA => Current.TrackA;
    public static Color CanvasA => Current.CanvasA;

    public static Color Stroke { get; } = Color.FromArgb(20, 255, 255, 255);
    public static Color TopLight { get; } = Color.FromArgb(23, 255, 255, 255);
    public static Color Sheen { get; } = Color.FromArgb(13, 255, 255, 255);

    public static Color AccentMark => Color.FromArgb(160, AccentInk.R, AccentInk.G, AccentInk.B);

    /// <summary>Color-key for the layered dock window: the wallpaper shows through it.</summary>
    public static readonly Color TransparentKey = Color.FromArgb(255, 0, 255);

    public static void SetAccent(Color accent)
    {
        var rgb = Color.FromArgb(accent.R, accent.G, accent.B);
        if (_has && Current.Accent.ToArgb() == rgb.ToArgb())
        {
            return;
        }

        _previous = Current;
        Current = Swatch.Create(rgb);
        _has = true;
        Changed?.Invoke();
    }

    public static Color SurfaceOf(Color accent) => Mix(Glass, accent, SurfaceMix);

    public static bool TryParseHex(string? hex, out Color color)
    {
        color = DefaultAccent;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var clean = hex.Trim();
        if (clean.StartsWith('#'))
        {
            clean = clean[1..];
        }

        if (clean.Length != 6 || !int.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out var packed))
        {
            return false;
        }

        color = Color.FromArgb((packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF);
        return true;
    }

    public static bool Same(Color a, Color b) => a.ToArgb() == b.ToArgb();

    public static double Luminance(Color color)
    {
        static double Lin(int channel)
        {
            var s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Lin(color.R)) + (0.7152 * Lin(color.G)) + (0.0722 * Lin(color.B));
    }

    public static Color TextOn(Color background) =>
        Contrast(DarkInk, background) >= Contrast(LightInk, background) ? DarkInk : LightInk;

    public static Color MutedOn(Color background) =>
        Luminance(background) > 0.55 ? Mix(background, DarkInk, 0.62) : Text2;

    public static Color Map(Color color)
    {
        if (color.IsEmpty || color.A == 0)
        {
            return color;
        }

        if (Same(color, _previous.Surface)) return Current.Surface;
        if (Same(color, _previous.Field)) return Current.Field;
        if (Same(color, _previous.Hover)) return Current.Hover;
        if (Same(color, _previous.Selected)) return Current.Selected;
        if (Same(color, _previous.SurfaceA)) return Current.SurfaceA;
        if (Same(color, _previous.FieldA)) return Current.FieldA;
        if (Same(color, _previous.HoverA)) return Current.HoverA;
        if (Same(color, _previous.SelectedA)) return Current.SelectedA;
        if (Same(color, _previous.TrackA)) return Current.TrackA;
        if (Same(color, _previous.CanvasA)) return Current.CanvasA;
        if (Same(color, _previous.Text)) return Current.Text;
        if (Same(color, _previous.Text2)) return Current.Text2;
        if (Same(color, _previous.Text3)) return Current.Text3;
        if (Same(color, _previous.Track)) return Current.Track;
        if (Same(color, _previous.Canvas)) return Current.Canvas;
        if (Same(color, _previous.OnAccent)) return Current.OnAccent;
        if (Same(color, _previous.AccentInk)) return Current.AccentInk;
        if (Same(color, _previous.Accent)) return Current.Accent;
        if (color.ToArgb() == Color.White.ToArgb()) return Current.Text;
        return color;
    }

    public static void Retint(Control root)
    {
        void Walk(Control control)
        {
            var back = Map(control.BackColor);
            var fore = Map(control.ForeColor);
            if (!Same(back, control.BackColor))
            {
                control.BackColor = back;
            }

            if (!Same(fore, control.ForeColor))
            {
                control.ForeColor = fore;
            }

            if (control is Button button)
            {
                var over = Map(button.FlatAppearance.MouseOverBackColor);
                if (!Same(over, button.FlatAppearance.MouseOverBackColor))
                {
                    button.FlatAppearance.MouseOverBackColor = over;
                }
            }

            control.Invalidate();
            foreach (Control child in control.Controls)
            {
                Walk(child);
            }
        }

        Walk(root);
    }

    private static Color Mix(Color from, Color to, double t) =>
        Color.FromArgb(Chan(from.R, to.R, t), Chan(from.G, to.G, t), Chan(from.B, to.B, t));

    private static int Chan(int from, int to, double t)
    {
        var value = from + ((to - from) * t);
        return (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
    }

    private static Color Over(Color fg, Color bg)
    {
        var a = fg.A / 255.0;
        int Ch(int f, int b) => (int)Math.Clamp(Math.Round((f * a) + (b * (1 - a)), MidpointRounding.AwayFromZero), 0, 255);
        return Color.FromArgb(Ch(fg.R, bg.R), Ch(fg.G, bg.G), Ch(fg.B, bg.B));
    }

    private static double Contrast(Color a, Color b)
    {
        var hi = Math.Max(Luminance(a), Luminance(b));
        var lo = Math.Min(Luminance(a), Luminance(b));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static Color Readable(Color color, Color background)
    {
        var ink = Color.FromArgb(color.R, color.G, color.B);
        if (Contrast(ink, background) >= 3.0)
        {
            return ink;
        }

        ink = Mix(ink, Color.White, 0.55);
        if (Contrast(ink, background) >= 3.0)
        {
            return ink;
        }

        return Mix(ink, Color.White, 0.45);
    }

    internal readonly record struct Swatch(
        Color Accent,
        string AccentHex,
        Color AccentInk,
        Color Surface,
        Color Field,
        Color Hover,
        Color Selected,
        Color Text,
        Color Text2,
        Color Text3,
        Color Track,
        Color Canvas,
        Color OnAccent,
        Color SurfaceA,
        Color FieldA,
        Color HoverA,
        Color SelectedA,
        Color TrackA,
        Color CanvasA)
    {
        public static Swatch Create(Color accent)
        {
            var surface = SurfaceOf(accent);
            var ink = Readable(accent, surface);
            return new Swatch(
                accent,
                $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}",
                ink,
                surface,
                Mix(surface, Color.White, 0.08),
                Mix(surface, Color.White, 0.06),
                Mix(surface, ink, 0.28),
                Over(Color.FromArgb(245, 255, 255, 255), surface),
                Over(Color.FromArgb(158, 235, 238, 255), surface),
                Over(Color.FromArgb(97, 235, 238, 255), surface),
                Mix(surface, Color.White, 0.16),
                Mix(Color.FromArgb(7, 8, 13), accent, 0.05),
                TextOn(ink),
                WithAlpha(205, surface),
                WithAlpha(210, Mix(surface, Color.White, 0.08)),
                WithAlpha(200, Mix(surface, Color.White, 0.06)),
                WithAlpha(215, Mix(surface, ink, 0.28)),
                WithAlpha(205, Mix(surface, Color.White, 0.16)),
                WithAlpha(200, Mix(Color.FromArgb(7, 8, 13), accent, 0.05)));
        }

        private static Color WithAlpha(int alpha, Color color) => Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}

internal static class ThemeFont
{
    public static Font Text(float size, FontStyle style = FontStyle.Regular)
    {
        foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI" })
        {
            try
            {
                var font = new Font(name, size, style, GraphicsUnit.Point);
                if (string.Equals(font.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return font;
                }

                font.Dispose();
            }
            catch (ArgumentException)
            {
            }
        }

        return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
    }
}
