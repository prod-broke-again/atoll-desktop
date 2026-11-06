namespace TaskbarSpike;

internal sealed class ShowDesktopForm : Form
{
    private readonly Panel _hit;

    public ShowDesktopForm()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.SurfaceA;
        Text = "Показать рабочий стол";

        _hit = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Cursor = Cursors.Hand,
        };
        _hit.MouseEnter += (_, _) => _hit.Invalidate();
        _hit.MouseLeave += (_, _) => _hit.Invalidate();
        _hit.Paint += (_, e) =>
        {
            var hot = _hit.ClientRectangle.Contains(_hit.PointToClient(Cursor.Position));
            using (var brush = new SolidBrush(hot ? Theme.HoverA : Theme.SurfaceA))
            {
                e.Graphics.FillRectangle(brush, _hit.ClientRectangle);
            }

            if (!hot && _hit.Width > 1 && _hit.Height > 2)
            {
                var band = new Rectangle(0, 0, _hit.Width, Math.Max(1, _hit.Height / 2));
                using var sheen = new System.Drawing.Drawing2D.LinearGradientBrush(
                    band,
                    Theme.Sheen,
                    Color.FromArgb(0, 255, 255, 255),
                    System.Drawing.Drawing2D.LinearGradientMode.Vertical);
                e.Graphics.FillRectangle(sheen, band);
            }

            using var highlight = new Pen(Theme.TopLight);
            e.Graphics.DrawLine(highlight, 0, 0, _hit.Width, 0);
        };
        _hit.Click += (_, _) => DesktopToggle.Toggle();
        Controls.Add(_hit);
        new ToolTip().SetToolTip(_hit, "Показать рабочий стол");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PopupChrome.ClearSystemChrome(Handle);
    }

    public static Rectangle StripBounds(Rectangle tray)
    {
        if (tray.Width >= tray.Height)
        {
            var thickness = Thickness(tray.Height);
            return new Rectangle(tray.Right - thickness, tray.Top, thickness, tray.Height);
        }

        var height = Thickness(tray.Width);
        return new Rectangle(tray.Left, tray.Bottom - height, tray.Width, height);
    }

    public static Rectangle BarRemainder(Rectangle tray)
    {
        var strip = StripBounds(tray);
        if (tray.Width >= tray.Height)
        {
            return new Rectangle(tray.Left, tray.Top, Math.Max(80, tray.Width - strip.Width), tray.Height);
        }

        return new Rectangle(tray.Left, tray.Top, tray.Width, Math.Max(80, tray.Height - strip.Height));
    }

    public void Place(Rectangle bounds)
    {
        if (!Visible)
        {
            Bounds = bounds;
            Show();
        }

        if (!IsHandleCreated)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow | wsExNoActivate;
            return cp;
        }
    }

    private static int Thickness(int cross)
    {
        return Math.Clamp(Math.Max(6, cross / 6), 6, 12);
    }
}
