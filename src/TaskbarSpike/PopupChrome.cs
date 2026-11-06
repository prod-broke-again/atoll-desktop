using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TaskbarSpike;

/// <summary>
/// Popup shell in the widget visual language: 24dip corners, a 160deg veil,
/// a soft accent glow, top sheen, stroke and highlight. The window backdrop
/// is the acrylic accent — DWM blurs whatever is behind the popup; every
/// background in the popup must stay translucent (Theme.*A) or the blur is
/// hidden behind an opaque fill.
/// </summary>
internal static class PopupChrome
{
    /// <summary>Dock window region radius — matches dock.css --radius.</summary>
    public const int RadiusDip = 16;

    /// <summary>Popup radius — the same 24px the desktop widget cards use.</summary>
    public const int PopupRadiusDip = 24;
    private const int CsDropShadow = 0x00020000;

    public static void UseShadow(CreateParams cp)
    {
        cp.ClassStyle |= CsDropShadow;
    }

    // static readonly (not const) so the fallback transparency path below stays reachable for the compiler;
    // it can be reactivated by flipping this switch back to false.
    private static readonly bool SolidPanels = true;

    /// <summary>True when Windows transparency effects are on and battery saver is off.</summary>
    public static bool TransparencyAvailable()
    {
        // Panels are a solid dark fill: acrylic read as a murky haze and its window-region edge cannot be anti-aliased.
        if (SolidPanels)
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("EnableTransparency") is int on && on == 0)
            {
                return false;
            }

            if (NativeMethods.GetSystemPowerStatus(out var status) && (status.SystemStatusFlag & 1) != 0)
            {
                return false;
            }
        }
        catch
        {
            // Treat unreadable settings as "available".
        }

        return true;
    }

    public static void ClearSystemChrome(IntPtr handle)
    {
        var backdrop = NativeMethods.DwmsbtNone;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        var corner = NativeMethods.DwmwcpDoNotRound;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));
        var margins = new NativeMethods.Margins();
        NativeMethods.DwmExtendFrameIntoClientArea(handle, ref margins);
        EnableAcrylic(handle);
    }

    /// <summary>ACCENT_ENABLE_ACRYLICBLURBEHIND over the window client area.</summary>
    public static void EnableAcrylic(IntPtr handle)
    {
        EnableAcrylic(handle, Theme.CanvasA);
    }

    /// <summary>Dock glass tint: rgba(16,18,26,.34) mixed 7 % toward the accent (HANDOFF 2).</summary>
    public static Color DockGlassTint()
    {
        var a = Theme.Accent;
        int Ch(int baseValue, int accent) => (int)Math.Round(baseValue + ((accent - baseValue) * 0.07));
        return Color.FromArgb(0x57, Ch(16, a.R), Ch(18, a.G), Ch(26, a.B));
    }

    /// <summary>Win10 bar: rgba(16,16,18,.92) with a faint accent tint.</summary>
    public static Color BarTint()
    {
        var a = Theme.Accent;
        int Ch(int baseValue, int accent) => (int)Math.Round(baseValue + ((accent - baseValue) * 0.03));
        return Color.FromArgb(0xEB, Ch(16, a.R), Ch(16, a.G), Ch(18, a.B));
    }

    public static void DisableAccent(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var policy = new NativeMethods.AccentPolicy { AccentState = 0 };
        var data = new NativeMethods.WindowCompositionAttributeData
        {
            Attribute = NativeMethods.WcaAccentPolicy,
            SizeOfData = Marshal.SizeOf<NativeMethods.AccentPolicy>(),
        };
        var pointer = Marshal.AllocHGlobal(data.SizeOfData);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            data.Data = pointer;
            NativeMethods.SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static void EnableAcrylic(IntPtr handle, Color tint)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var gradient = (tint.A << 24) | (tint.B << 16) | (tint.G << 8) | tint.R;
        var policy = new NativeMethods.AccentPolicy
        {
            AccentState = NativeMethods.AccentEnableAcrylicBlurBehind,
            AccentFlags = 0,
            GradientColor = gradient,
            AnimationId = 0,
        };
        var data = new NativeMethods.WindowCompositionAttributeData
        {
            Attribute = NativeMethods.WcaAccentPolicy,
            SizeOfData = Marshal.SizeOf<NativeMethods.AccentPolicy>(),
        };
        var pointer = Marshal.AllocHGlobal(data.SizeOfData);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            data.Data = pointer;
            NativeMethods.SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static int RadiusPx(Control control)
    {
        var radius = (int)Math.Round(RadiusDip * control.DeviceDpi / 96.0, MidpointRounding.AwayFromZero);
        return Math.Max(8, radius);
    }

    public static int PopupRadiusPx(Control control)
    {
        var radius = (int)Math.Round(PopupRadiusDip * control.DeviceDpi / 96.0, MidpointRounding.AwayFromZero);
        return Math.Max(10, radius);
    }

    public static void ApplyRegion(Form form)
    {
        if (!form.IsHandleCreated || form.Width <= 1 || form.Height <= 1)
        {
            return;
        }

        var radius = Math.Min(PopupRadiusPx(form), Math.Min(form.Width, form.Height) / 2);
        var diameter = Math.Max(2, radius * 2);
        // One-device-pixel inset — see DockForm.ApplyRoundedRegion.
        var insetDiameter = Math.Max(2, diameter - 2);
        var region = NativeMethods.CreateRoundRectRgn(1, 1, form.Width - 1, form.Height - 1, insetDiameter, insetDiameter);
        NativeMethods.SetWindowRgn(form.Handle, region, true);
    }

    public static void Paint(Graphics graphics, Size size, int radius)
    {
        if (size.Width <= 2 || size.Height <= 2)
        {
            return;
        }

        radius = Math.Min(radius, Math.Min(size.Width, size.Height) / 2);
        var previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
        using var path = Rounded(bounds, radius);
        var surface = new RectangleF(0, 0, size.Width, size.Height);
        graphics.SetClip(path);

        // 160deg veil — the same linear-gradient(160deg, rgba(255,255,255,.07), transparent 42%)
        // the widget cards layer over their glass.
        using (var veil = new LinearGradientBrush(
            new PointF(0, 0),
            new PointF(size.Width * 0.34f, size.Height),
            Color.FromArgb(18, 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255)))
        {
            graphics.FillRectangle(veil, surface);
        }

        // soft accent glow in the corner, like .widget::before.
        var glowEdge = Math.Max(48f, size.Width * 0.7f);
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(new RectangleF(-glowEdge * 0.30f, -glowEdge * 0.40f, glowEdge, glowEdge));
            using var glow = new PathGradientBrush(glowPath);
            glow.CenterColor = Color.FromArgb(44, Theme.Accent);
            glow.SurroundColors = new[] { Color.FromArgb(0, Theme.Accent) };
            graphics.FillRectangle(glow, surface);
        }

        // top sheen band
        var bandHeight = Math.Max(2f, size.Height * 0.42f);
        var band = new RectangleF(0, 0, size.Width, bandHeight);
        using (var sheen = new LinearGradientBrush(band, Theme.Sheen, Color.FromArgb(0, 255, 255, 255), 70f))
        {
            graphics.FillRectangle(sheen, band);
        }

        graphics.ResetClip();

        using var stroke = new Pen(Theme.Stroke, 1f);
        graphics.DrawPath(stroke, path);
        using var highlight = new Pen(Theme.TopLight, 1f);
        var inset = Math.Max(8f, radius * 0.65f);
        graphics.DrawLine(highlight, inset, 1.25f, size.Width - inset, 1.25f);
        graphics.SmoothingMode = previous;
    }

    public static void PaintFocus(Graphics graphics, Rectangle bounds)
    {
        using var pen = new Pen(Theme.AccentInk, 2f);
        var y = bounds.Bottom - 2;
        graphics.DrawLine(pen, bounds.Left + 10, y, bounds.Right - 10, y);
    }

    public static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        if (bounds.Width < diameter || bounds.Height < diameter)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath Rounded(RectangleF bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        if (bounds.Width < diameter || bounds.Height < diameter)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
