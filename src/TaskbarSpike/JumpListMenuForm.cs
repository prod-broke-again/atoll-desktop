using System.Runtime.InteropServices;

namespace TaskbarSpike;

/// <summary>
/// App Jump List. Dock chrome matches PopupHost widgets (rect window + 14dip painted card);
/// Win10 bar is square like body.v-bar .glass. Items show a leading icon like Explorer.
/// </summary>
internal sealed class JumpListMenuForm : Form
{
    private const int DockRadiusDip = 14;

    private readonly Font _itemFont = ThemeFont.Text(9.5f);
    private readonly Font _sectionFont = ThemeFont.Text(8.5f);
    private readonly Font _glyphFont = new("Segoe MDL2 Assets", 11f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly List<Row> _rows = new();
    private readonly List<Image> _ownedIcons = new();
    private AppButton? _app;
    private long _openedAt;
    private int _hover = -1;
    private bool _bar;
    private bool _glass;
    private bool _buttonWasDown;
    private System.Windows.Forms.Timer? _outsideTimer;

    public event Action? ClosedByUser;
    public event Action? Changed;

    /// <summary>Kept for DockForm wiring; click-outside closes even over the dock (unlike popups).</summary>
    public Func<Point, bool>? IsOverDock { get; set; }

    public JumpListMenuForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Same as PreviewForm/PopupHost — avoids focus steal that immediately dismisses the menu (esp. Win10 bar).</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 | 0x00000008; // toolwindow | topmost
            PopupChrome.UseShadow(cp);
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PopupChrome.ClearSystemChrome(Handle);
        NativeMethods.ExcludeFromPeek(Handle);
    }

    public void ShowFor(AppButton app, JumpListModel model, int anchorX, int dockTop, bool bar, bool glass)
    {
        _app = app;
        _bar = bar;
        _glass = glass;
        _openedAt = Environment.TickCount64;
        _hover = -1;
        ClearRows();

        foreach (var section in model.Sections)
        {
            if (section.Items.Count == 0)
            {
                continue;
            }

            _rows.Add(Row.Header(section.Title));
            foreach (var item in section.Items.Take(10))
            {
                _rows.Add(Row.Destination(item, ResolveIcon(item.IconPath, item.IconIndex, app.IconDataUrl)));
            }
        }

        if (_rows.Count > 0)
        {
            _rows.Add(Row.Divider());
        }

        // Windows: app/window identity with icon, then pin/unpin (+ close when running).
        var identity = app.Hwnds.Count > 0
            ? app.Titles.FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)) ?? app.Name
            : app.Name;
        _rows.Add(Row.App(identity, ResolveIcon(null, 0, app.IconDataUrl)));

        if (app.Pinned)
        {
            _rows.Add(Row.Command("Открепить от панели задач", MenuAction.Unpin, Glyph.Unpin));
        }
        else if (!string.IsNullOrWhiteSpace(app.TargetPath) || !string.IsNullOrWhiteSpace(app.LaunchPath))
        {
            _rows.Add(Row.Command("Закрепить на панели задач", MenuAction.Pin, Glyph.Pin));
        }

        if (app.Hwnds.Count == 1)
        {
            _rows.Add(Row.Command("Закрыть окно", MenuAction.Close, Glyph.Close));
        }
        else if (app.Hwnds.Count > 1)
        {
            _rows.Add(Row.Command("Закрыть все окна", MenuAction.CloseAll, Glyph.Close));
        }

        LayoutRows(anchorX, dockTop);
        if (!Visible)
        {
            Show();
        }

        ApplyShape();
        NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopmost, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        StartOutsideWatch();
        Invalidate();
    }

    private void StartOutsideWatch()
    {
        _buttonWasDown = AnyMouseButtonDown();
        _outsideTimer ??= new System.Windows.Forms.Timer { Interval = 40 };
        _outsideTimer.Tick -= OnOutsideTick;
        _outsideTimer.Tick += OnOutsideTick;
        _outsideTimer.Start();
    }

    private void StopOutsideWatch()
    {
        _outsideTimer?.Stop();
    }

    private void OnOutsideTick(object? sender, EventArgs e)
    {
        if (!Visible)
        {
            StopOutsideWatch();
            return;
        }

        var down = AnyMouseButtonDown();
        var pressed = down && !_buttonWasDown;
        _buttonWasDown = down;
        if (!pressed || Environment.TickCount64 - _openedAt < 250)
        {
            return;
        }

        // Close on any click outside the menu — including the taskbar/dock itself.
        if (!Bounds.Contains(Cursor.Position))
        {
            HideMenu();
        }
    }

    private static bool AnyMouseButtonDown()
    {
        return (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void RefreshChrome(bool bar, bool glass)
    {
        _bar = bar;
        _glass = glass;
        if (!IsHandleCreated)
        {
            return;
        }

        ApplyShape();
        Invalidate();
    }

    private Image? ResolveIcon(string? path, int index, string? fallbackDataUrl)
    {
        Image? image = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            image = WindowCatalog.TryDecodeIcon(WindowCatalog.IconForPath(path, index));
        }

        image ??= WindowCatalog.TryDecodeIcon(fallbackDataUrl);
        if (image != null)
        {
            _ownedIcons.Add(image);
        }

        return image;
    }

    private void ClearRows()
    {
        _rows.Clear();
        foreach (var icon in _ownedIcons)
        {
            icon.Dispose();
        }

        _ownedIcons.Clear();
    }

    private void LayoutRows(int anchorX, int dockTop)
    {
        var scale = DeviceDpi / 96.0;
        var padX = (int)Math.Round(12 * scale);
        var rowH = (int)Math.Round(32 * scale);
        var headerH = (int)Math.Round(24 * scale);
        var dividerH = (int)Math.Round(9 * scale);
        var width = (int)Math.Round(280 * scale);
        var y = (int)Math.Round(10 * scale);
        foreach (var row in _rows)
        {
            row.Bounds = row.Kind switch
            {
                RowKind.Header => new Rectangle(padX, y, width - padX * 2, headerH),
                RowKind.Divider => new Rectangle(padX, y + 3, width - padX * 2, 1),
                _ => new Rectangle(0, y, width, rowH),
            };
            y += row.Kind switch
            {
                RowKind.Header => headerH,
                RowKind.Divider => dividerH,
                _ => rowH,
            };
        }

        y += (int)Math.Round(10 * scale);
        var height = Math.Max(y, (int)Math.Round(48 * scale));
        var screen = Screen.FromPoint(new Point(anchorX, dockTop)).WorkingArea;
        var x = Math.Clamp(anchorX - width / 2, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - width - 4));
        // Dock widgets sit 12 DIP above islands; bar panels sit flush (PopupHost).
        var gap = _bar ? (int)Math.Round(0 * scale) : (int)Math.Round(12 * scale);
        var top = dockTop - height - gap;
        if (top < screen.Top + 4)
        {
            top = Math.Min(dockTop + (int)Math.Round(8 * scale), screen.Bottom - height - 4);
        }

        SetBounds(x, top, width, height);
    }

    private int ChromeRadiusPx()
    {
        if (_bar)
        {
            return 0;
        }

        // PopupHost panels use 14 DIP (start uses 16). Match network/calendar.
        return Math.Max(8, (int)Math.Round(DockRadiusDip * DeviceDpi / 96.0, MidpointRounding.AwayFromZero));
    }

    private void ApplyShape()
    {
        if (!IsHandleCreated || Width <= 1 || Height <= 1)
        {
            return;
        }

        // PopupHost keeps a rectangular region; rounding is painted (CSS there, GDI+ here).
        var region = NativeMethods.CreateRectRgn(0, 0, Width, Height);
        NativeMethods.SetWindowRgn(Handle, region, true);

        if (_bar)
        {
            BackColor = Color.FromArgb(242, 32, 32, 34);
            PopupChrome.DisableAccent(Handle);
            return;
        }

        BackColor = Theme.Surface;
        if (_glass)
        {
            PopupChrome.EnableAcrylic(Handle, PopupChrome.DockGlassTint());
        }
        else
        {
            PopupChrome.DisableAccent(Handle);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (_bar)
        {
            using var fill = new SolidBrush(Color.FromArgb(242, 32, 32, 34));
            g.FillRectangle(fill, ClientRectangle);
            using var edge = new Pen(Color.FromArgb(18, 255, 255, 255));
            g.DrawRectangle(edge, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
        else
        {
            g.Clear(Theme.Surface);
            PopupChrome.Paint(g, ClientSize, ChromeRadiusPx());
        }

        var scale = DeviceDpi / 96.0;
        var iconSize = (int)Math.Round(16 * scale);
        var iconLeft = (int)Math.Round(14 * scale);
        var textLeft = (int)Math.Round(40 * scale);

        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.Kind == RowKind.Divider)
            {
                using var pen = new Pen(Theme.Stroke);
                g.DrawLine(pen, row.Bounds.Left, row.Bounds.Top, row.Bounds.Right, row.Bounds.Top);
                continue;
            }

            if (row.Kind == RowKind.Header)
            {
                TextRenderer.DrawText(g, row.Text, _sectionFont, row.Bounds, Theme.Text3,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                continue;
            }

            if (i == _hover)
            {
                using var fill = new SolidBrush(Theme.Hover);
                var inset = (int)Math.Round(6 * scale);
                var hot = new Rectangle(inset, row.Bounds.Y + 1, Width - inset * 2, row.Bounds.Height - 2);
                var radius = _bar ? 0 : (int)Math.Round(8 * scale);
                if (radius <= 0)
                {
                    g.FillRectangle(fill, hot);
                }
                else
                {
                    using var path = PopupChrome.Rounded(hot, radius);
                    g.FillPath(fill, path);
                }
            }

            var iconBounds = new Rectangle(
                iconLeft,
                row.Bounds.Y + (row.Bounds.Height - iconSize) / 2,
                iconSize,
                iconSize);
            if (row.Icon != null)
            {
                g.DrawImage(row.Icon, iconBounds);
            }
            else if (!string.IsNullOrEmpty(row.Glyph))
            {
                TextRenderer.DrawText(g, row.Glyph, _glyphFont, iconBounds, Theme.Text2,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            var textBounds = new Rectangle(textLeft, row.Bounds.Y, row.Bounds.Right - textLeft - iconLeft, row.Bounds.Height);
            TextRenderer.DrawText(g, row.Text, _itemFont, textBounds, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var next = Hit(e.Location);
        if (next == _hover)
        {
            return;
        }

        _hover = next;
        Cursor = next >= 0 && _rows[next].Kind is RowKind.Item or RowKind.Action or RowKind.App
            ? Cursors.Hand
            : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0)
        {
            return;
        }

        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || Environment.TickCount64 - _openedAt < 180)
        {
            return;
        }

        var index = Hit(e.Location);
        if (index < 0)
        {
            return;
        }

        var row = _rows[index];
        if (row.Kind == RowKind.Item && row.Item != null)
        {
            HideMenu();
            JumpListCatalog.Activate(row.Item);
            return;
        }

        if (row.Kind == RowKind.App && _app != null)
        {
            var id = _app.Id;
            HideMenu();
            WindowCatalog.ActivateApp(id);
            return;
        }

        if (row.Kind != RowKind.Action || _app == null)
        {
            return;
        }

        var app = _app;
        HideMenu();
        switch (row.Action)
        {
            case MenuAction.Pin:
                if (TaskbarPin.Pin(app))
                {
                    Changed?.Invoke();
                }

                break;
            case MenuAction.Unpin:
                if (TaskbarPin.Unpin(app))
                {
                    Changed?.Invoke();
                }

                break;
            case MenuAction.Close:
                if (app.Hwnds.Count > 0)
                {
                    NativeMethods.PostMessage(new IntPtr(app.Hwnds[0]), NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
                }

                break;
            case MenuAction.CloseAll:
                foreach (var hwnd in app.Hwnds)
                {
                    NativeMethods.PostMessage(new IntPtr(hwnd), NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
                }

                break;
        }
    }

    private void HideMenu()
    {
        StopOutsideWatch();
        Hide();
        ClosedByUser?.Invoke();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            StopOutsideWatch();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!Visible || Environment.TickCount64 - _openedAt < 250)
        {
            return;
        }

        // Do not keep the menu open when the click landed on the dock — only when still over us.
        if (Bounds.Contains(Cursor.Position))
        {
            return;
        }

        HideMenu();
    }

    private int Hit(Point point)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.Kind is RowKind.Item or RowKind.Action or RowKind.App && row.Bounds.Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopOutsideWatch();
            _outsideTimer?.Dispose();
            _outsideTimer = null;
            ClearRows();
            _itemFont.Dispose();
            _sectionFont.Dispose();
            _glyphFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private static class Glyph
    {
        public const string Pin = "\uE718";
        public const string Unpin = "\uE77A";
        public const string Close = "\uE8BB";
    }

    private enum RowKind
    {
        Header,
        Item,
        App,
        Action,
        Divider,
    }

    private enum MenuAction
    {
        None,
        Pin,
        Unpin,
        Close,
        CloseAll,
    }

    private sealed class Row
    {
        public RowKind Kind;
        public string Text = string.Empty;
        public JumpListItem? Item;
        public MenuAction Action;
        public Rectangle Bounds;
        public Image? Icon;
        public string Glyph = string.Empty;

        public static Row Header(string text) => new() { Kind = RowKind.Header, Text = text };
        public static Row Divider() => new() { Kind = RowKind.Divider };
        public static Row Destination(JumpListItem item, Image? icon) =>
            new() { Kind = RowKind.Item, Text = item.Label, Item = item, Icon = icon };
        public static Row App(string text, Image? icon) =>
            new() { Kind = RowKind.App, Text = text, Icon = icon };
        public static Row Command(string text, MenuAction action, string glyph) =>
            new() { Kind = RowKind.Action, Text = text, Action = action, Glyph = glyph };
    }
}
