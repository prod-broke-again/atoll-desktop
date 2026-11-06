namespace TaskbarSpike;

internal sealed class TrayMenuForm : Form
{
    private const int Cell = 36;
    private const int Gap = 2;
    private const int Pad = 8;
    private const int Columns = 6;

    private static Color Surface => Theme.Surface;
    private static Color Hover => Theme.HoverA;
    private readonly ToolTip _tips = new()
    {
        ShowAlways = true,
        InitialDelay = 350,
        AutoPopDelay = 10000,
        ReshowDelay = 100,
    };
    private long _openedAt;

    /// <summary>While an app's own tray menu is open the panel must stay visible.</summary>
    public bool HoldOpen { get; set; }

    public event Action<TrayButton>? AppMenuRequested;
    public event Action? ModeRequested;
    public event Action? ExitRequested;

    public TrayMenuForm()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Surface;
        ForeColor = Theme.Text;
        Font = ThemeFont.Text(9f);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExTopMost = 0x00000008;
            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow | wsExTopMost;
            PopupChrome.UseShadow(cp);
            return cp;
        }
    }

    public void ShowFor(IReadOnlyList<TrayButton> items, int anchorX, int anchorTop, string modeLabel)
    {
        _openedAt = Environment.TickCount64;
        _tips.RemoveAll();
        SuspendLayout();
        while (Controls.Count > 0)
        {
            Controls[0].Dispose();
        }

        var gridW = 0;
        var y = Pad;
        if (items.Count > 0)
        {
            var columns = Math.Min(Columns, items.Count);
            var rows = (items.Count + columns - 1) / columns;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var cell = new TrayCell(item)
                {
                    Location = new Point(Pad + (i % columns) * (Cell + Gap), Pad + (i / columns) * (Cell + Gap)),
                };
                if (!string.IsNullOrWhiteSpace(item.Name))
                {
                    _tips.SetToolTip(cell, item.Name);
                }

                if (item.ExecutablePath != null)
                {
                    cell.Click += (_, _) =>
                    {
                        Hide();
                        TrayCatalog.Open(item.Id);
                    };
                    cell.MouseUp += (_, e) =>
                    {
                        if (e.Button != MouseButtons.Right)
                        {
                            return;
                        }

                        AppMenuRequested?.Invoke(item);
                    };
                }

                Controls.Add(cell);
            }

            gridW = (Pad * 2) + (columns * Cell) + ((columns - 1) * Gap);
            y = (Pad * 2) + (rows * Cell) + ((rows - 1) * Gap) - Pad + 6;
            Controls.Add(new Panel { BackColor = Color.FromArgb(40, 255, 255, 255), Location = new Point(Pad, y), Size = new Size(Math.Max(gridW, 240) - (Pad * 2), 1) });
            y += 7;
        }

        var width = Math.Max(gridW, 240);
        foreach (var (text, act) in new (string, Action?)[] { (modeLabel, ModeRequested), ("Вернуть панель Windows", ExitRequested) })
        {
            var row = new ActionRow(text) { Location = new Point(Pad, y), Size = new Size(width - (Pad * 2), 36) };
            var handler = act;
            row.Click += (_, _) =>
            {
                Hide();
                handler?.Invoke();
            };
            Controls.Add(row);
            y += 40;
        }

        ClientSize = new Size(width, y - 4 + Pad);

        ResumeLayout();
        Place(anchorX, anchorTop);
        Show();
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PopupChrome.ClearSystemChrome(Handle);
        PopupChrome.ApplyRegion(this);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (HoldOpen || Environment.TickCount64 - _openedAt < 200)
        {
            return;
        }

        if (!Bounds.Contains(Cursor.Position))
        {
            Hide();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        PopupChrome.ApplyRegion(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PopupChrome.Paint(e.Graphics, ClientSize, PopupChrome.PopupRadiusPx(this));
    }

    private sealed class ActionRow : Control
    {
        private bool _hot;

        public ActionRow(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            Font = ThemeFont.Text(9f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = PopupChrome.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 9);
            using var brush = new SolidBrush(_hot ? Hover : Color.FromArgb(22, 255, 255, 255));
            e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(14, 0, Width - 14, Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
    }

    private sealed class TrayCell : Control
    {
        private readonly Image? _image;

        public TrayCell(TrayButton item)
        {
            Item = item;
            _image = ImageFrom(item.IconDataUrl);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(Cell, Cell);
            BackColor = Surface;
            Cursor = item.ExecutablePath == null ? Cursors.Default : Cursors.Hand;
        }

        public TrayButton Item { get; }

        protected override void OnPaint(PaintEventArgs e)
        {
            // No background fill: the acrylic shows through. Only the hover
            // plate is painted, as a rounded rect like the dock buttons.
            if (Theme.Same(BackColor, Hover))
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = PopupChrome.Rounded(new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3)), 8);
                using var brush = new SolidBrush(Hover);
                e.Graphics.FillPath(brush, path);
            }

            if (_image != null)
            {
                e.Graphics.DrawImage(_image, (Width - 16) / 2, (Height - 16) / 2, 16, 16);
                return;
            }

            var letter = string.IsNullOrWhiteSpace(Item.Name) ? "?" : Item.Name.Trim()[..1].ToUpperInvariant();
            TextRenderer.DrawText(
                e.Graphics,
                letter,
                Font,
                ClientRectangle,
                Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (Item.ExecutablePath != null)
            {
                BackColor = Hover;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            BackColor = Surface;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _image?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static Image? ImageFrom(string? dataUrl)
    {
        const string prefix = "data:image/png;base64,";
        if (dataUrl == null || !dataUrl.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }

    private void Place(int anchorX, int anchorTop)
    {
        var x = anchorX - (Width / 2);
        var y = anchorTop - Height - 8;
        var area = Screen.FromPoint(new Point(anchorX, Math.Max(0, anchorTop - 1))).WorkingArea;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new Point(x, y);
    }
}
