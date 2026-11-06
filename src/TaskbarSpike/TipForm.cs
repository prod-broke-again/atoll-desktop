using System.Drawing.Drawing2D;

namespace TaskbarSpike;

/// <summary>
/// Dock label plate (HANDOFF 3.2): the app name above the hovered icon when the
/// app has no windows to preview. Small acrylic plate, 12px text, radius 8.
/// </summary>
internal sealed class TipForm : Form
{
    private string _text = string.Empty;
    private readonly Font _font = ThemeFont.Text(9f);

    public TipForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 | 0x08000000 | 0x00000008; // toolwindow | noactivate | topmost
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PopupChrome.ClearSystemChrome(Handle);
        PopupChrome.EnableAcrylic(Handle, PopupChrome.DockGlassTint());
        NativeMethods.ExcludeFromPeek(Handle);
    }

    public void ShowAbove(string text, int centerX, int iconTop, bool glass)
    {
        _text = text;
        var scale = DeviceDpi / 96.0;
        var size = TextRenderer.MeasureText(text, _font);
        var width = size.Width + (int)Math.Round(20 * scale);
        var height = size.Height + (int)Math.Round(10 * scale);
        var x = centerX - (width / 2);
        var y = iconTop - (int)Math.Round(14 * scale) - height;
        var screen = Screen.FromPoint(new Point(centerX, iconTop)).Bounds;
        x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - width - 4));
        SetBounds(x, y, width, height);
        var radius = (int)Math.Round(8 * scale);
        var region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        NativeMethods.SetWindowRgn(Handle, region, true);
        if (glass)
        {
            PopupChrome.EnableAcrylic(Handle, PopupChrome.DockGlassTint());
        }
        else
        {
            PopupChrome.DisableAccent(Handle);
        }

        if (!Visible)
        {
            Show();
        }

        NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopmost, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width, Height);
        using (var fill = new LinearGradientBrush(rect, Color.FromArgb(36, 255, 255, 255), Color.FromArgb(12, 255, 255, 255), 90f))
        {
            g.FillRectangle(fill, rect);
        }

        var radius = (int)Math.Round(8 * DeviceDpi / 96.0);
        using var path = PopupChrome.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), radius);
        using var pen = new Pen(Color.FromArgb(50, 255, 255, 255), 1f);
        g.DrawPath(pen, path);
        TextRenderer.DrawText(g, _text, _font, rect, Color.FromArgb(0xF2, 0xF3, 0xF7),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }

        base.Dispose(disposing);
    }
}
