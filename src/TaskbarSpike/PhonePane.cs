using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace TaskbarSpike;

/// <summary>
/// Right column of Start, same fill as the menu.
/// A row launches the Phone Link page Windows Start would open. Nothing is sent from here.
/// The list moves with the wheel. There is no WinForms scrollbar.
/// </summary>
internal sealed class PhonePane : Control
{
    private static Color Surface => Theme.Surface;
    private static Color Field => Theme.FieldA;
    private static Color Hover => Theme.HoverA;
    private static Color MutedText => Theme.Text2;

    private readonly Scroller _scroller;
    private readonly ToolTip _tip;
    private readonly Font _nameFont;
    private readonly Font _headingFont;
    private readonly Font _rowFont;
    private readonly Font _subFont;
    private readonly List<Control> _status = new();
    private PhoneSnapshot? _snapshot;
    private Rectangle _thumbRect;
    private Rectangle _nameRect;
    private int _statusTop;
    private int _labelLeft = -1;
    private int _headerHeight = 168;

    public PhonePane()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Surface;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        _scroller = new Scroller();
        Controls.Add(_scroller);
        _tip = new ToolTip { ShowAlways = true, InitialDelay = 400 };
        _nameFont = ThemeFont.Text(12f, FontStyle.Bold);
        _headingFont = ThemeFont.Text(11f, FontStyle.Bold);
        _rowFont = ThemeFont.Text(10f);
        _subFont = ThemeFont.Text(8.5f);
    }

    public bool HasDevice => _snapshot != null;

    public event Action? AppLaunched;

    public void Bind(PhoneSnapshot? snapshot)
    {
        _snapshot = snapshot;
        ClearStatus();
        if (snapshot != null)
        {
            foreach (var glyph in snapshot.Status)
            {
                var box = new GlyphBox(glyph.Image)
                {
                    Size = new Size(glyph.Size, glyph.Size),
                };
                if (glyph.Tooltip.Length > 0)
                {
                    _tip.SetToolTip(box, glyph.Tooltip);
                }

                _status.Add(box);
                Controls.Add(box);
            }
        }

        _scroller.Reset();
        LayoutChrome();
        Reflow();
        Invalidate();
    }

    public void Wheel(int delta)
    {
        _scroller.Wheel(delta);
    }

    public void Note(bool inside)
    {
        _scroller.Note(inside);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_snapshot == null)
        {
            return;
        }

        LayoutChrome();
        Reflow();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var brush = new SolidBrush(Surface))
        {
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        if (_snapshot == null || Width < 40)
        {
            return;
        }

        PaintDevice(e.Graphics);
        using var pen = new Pen(Theme.Stroke);
        e.Graphics.DrawLine(pen, 0, 14, 0, Height - 14);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _nameFont.Dispose();
            _headingFont.Dispose();
            _rowFont.Dispose();
            _subFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void LayoutChrome()
    {
        Measure();
        _scroller.Bounds = new Rectangle(1, _headerHeight, Math.Max(0, Width - 1), Math.Max(0, Height - _headerHeight));
        PositionStatus();
    }

    private void Measure()
    {
        var y = 18;
        var hasFrame = _snapshot?.Wallpaper != null || _snapshot?.Bezel != null;
        _thumbRect = hasFrame ? new Rectangle(Math.Max(0, (Width - 36) / 2), y, 36, 64) : Rectangle.Empty;
        y = _thumbRect.IsEmpty ? 22 : _thumbRect.Bottom + 10;
        _nameRect = new Rectangle(12, y, Math.Max(0, Width - 24), 28);
        _statusTop = _nameRect.Bottom + 2;
        _headerHeight = _statusTop + 34;
    }

    private void PositionStatus()
    {
        if (_snapshot == null || _status.Count == 0 || Width < 40)
        {
            return;
        }

        var labelWidth = string.IsNullOrEmpty(_snapshot.StatusLabel) ? 0 : 36;
        var three = _status.Count == 3;
        var total = three
            ? 32 + 32 + 8 + _snapshot.Status[2].Size + labelWidth
            : _status.Sum(box => box.Width) + (10 * Math.Max(0, _status.Count - 1)) + labelWidth;
        var x = Math.Max(8, (Width - total) / 2);
        if (!three)
        {
            foreach (var box in _status)
            {
                box.Bounds = new Rectangle(x, _statusTop + Math.Max(0, (28 - box.Height) / 2), box.Width, box.Height);
                box.BringToFront();
                x += box.Width + 10;
            }

            _labelLeft = string.IsNullOrEmpty(_snapshot.StatusLabel) ? -1 : x;
            return;
        }

        PlaceSlot(0, ref x, 32);
        PlaceSlot(1, ref x, 32);
        x += 8;
        PlaceSlot(2, ref x, _snapshot.Status[2].Size);
        _labelLeft = string.IsNullOrEmpty(_snapshot.StatusLabel) ? -1 : x + 2;
    }

    private void PlaceSlot(int index, ref int x, int slot)
    {
        var box = _status[index];
        box.Bounds = new Rectangle(x + Math.Max(0, (slot - box.Width) / 2), _statusTop + Math.Max(0, (28 - box.Height) / 2), box.Width, box.Height);
        box.BringToFront();
        x += slot;
    }

    private void Reflow()
    {
        var body = _scroller.Body;
        body.SuspendLayout();
        foreach (var child in body.Controls.Cast<Control>().ToList())
        {
            child.Dispose();
        }

        if (_snapshot == null || body.Width < 80)
        {
            body.Height = 0;
            body.ResumeLayout();
            return;
        }

        var width = Math.Max(80, body.Width - 16);
        var y = 8;
        if (_snapshot.Features.Count > 0)
        {
            var list = new ListWell
            {
                Bounds = new Rectangle(8, y, width, (_snapshot.Features.Count * 42) + 8),
            };
            var rowY = 4;
            foreach (var feature in _snapshot.Features)
            {
                var row = new EntryRow(feature, _rowFont, _subFont, false)
                {
                    Bounds = new Rectangle(0, rowY, list.Width, 42),
                };
                var launch = feature.Launch;
                row.Invoked += () => BeginInvoke(new Action(() => Launch(launch)));
                RememberTip(row, feature.Tooltip);
                list.Controls.Add(row);
                rowY += 42;
            }

            Round(list, 8);
            body.Controls.Add(list);
            y += list.Height + 18;
        }

        var heading = new Heading(_snapshot.RecentHeading, _headingFont)
        {
            Bounds = new Rectangle(12, y, width, 26),
        };
        body.Controls.Add(heading);
        y += 30;
        if (_snapshot.Recent.Count > 0)
        {
            var list = new ListWell
            {
                Bounds = new Rectangle(8, y, width, (_snapshot.Recent.Count * 58) + 8),
            };
            var rowY = 4;
            foreach (var item in _snapshot.Recent)
            {
                var row = new EntryRow(item, _rowFont, _subFont, true)
                {
                    Bounds = new Rectangle(0, rowY, list.Width, 58),
                };
                var launch = item.Launch;
                row.Invoked += () => BeginInvoke(new Action(() => Launch(launch)));
                RememberTip(row, item.Tooltip);
                list.Controls.Add(row);
                rowY += 58;
            }

            Round(list, 8);
            body.Controls.Add(list);
            y += list.Height;
        }

        body.Height = y + 20;
        body.ResumeLayout();
        _scroller.Clamp();
    }

    private void Launch(string url)
    {
        if (url.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Console.WriteLine("[spike] phone launch: " + ex.GetType().Name);
            return;
        }

        AppLaunched?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = DeviceHot(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && DeviceHot(e.Location) && _snapshot != null)
        {
            Launch(_snapshot.DeviceLaunch);
        }
    }

    private bool DeviceHot(Point point)
    {
        return _snapshot != null && _snapshot.DeviceLaunch.Length > 0 &&
            (_thumbRect.Contains(point) || _nameRect.Contains(point));
    }

    private void RememberTip(Control control, string tooltip)
    {
        if (tooltip.Length > 0 && tooltip.Length <= 120)
        {
            _tip.SetToolTip(control, tooltip);
        }
    }

    private void ClearStatus()
    {
        foreach (var box in _status)
        {
            Controls.Remove(box);
            box.Dispose();
        }

        _status.Clear();
    }

    private void PaintDevice(Graphics graphics)
    {
        if (_snapshot == null)
        {
            return;
        }

        if (!_thumbRect.IsEmpty)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var clip = Rounded(_thumbRect, 8);
            var state = graphics.Save();
            graphics.SetClip(clip);
            if (_snapshot.Wallpaper != null)
            {
                graphics.DrawImage(_snapshot.Wallpaper, _thumbRect);
            }

            graphics.Restore(state);
            if (_snapshot.Bezel != null)
            {
                graphics.DrawImage(_snapshot.Bezel, _thumbRect);
            }
        }

        TextRenderer.DrawText(
            graphics,
            _snapshot.DeviceName,
            _nameFont,
            _nameRect,
            Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (_labelLeft >= 0 && _snapshot.StatusLabel.Length > 0)
        {
            TextRenderer.DrawText(
                graphics,
                _snapshot.StatusLabel,
                _subFont,
                new Rectangle(_labelLeft, _statusTop, 48, 28),
                MutedText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    private static void Round(Control control, int radius)
    {
        if (control.Width < radius * 2 || control.Height < radius * 2)
        {
            return;
        }

        using var path = Rounded(new Rectangle(0, 0, control.Width, control.Height), radius);
        control.Region = new Region(path);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class Scroller : Control
    {
        private int _offset;
        private bool _thumb;

        public Scroller()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            Body = new Panel { BackColor = Surface, Location = Point.Empty };
            Controls.Add(Body);
            BackColor = Surface;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        public Panel Body { get; }

        public void Reset()
        {
            _offset = 0;
            Body.Top = 0;
            Invalidate();
        }

        public void Wheel(int delta)
        {
            if (delta == 0)
            {
                return;
            }

            MoveTo(_offset + (delta > 0 ? -72 : 72));
        }

        public void Clamp()
        {
            MoveTo(_offset);
        }

        public void Note(bool inside)
        {
            if (_thumb == inside)
            {
                return;
            }

            _thumb = inside;
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Body.Width = Math.Max(0, ClientSize.Width - 12);
            MoveTo(_offset);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(Surface))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }

            var span = Math.Max(0, Body.Height - ClientSize.Height);
            if (!_thumb || span <= 0 || ClientSize.Height < 40)
            {
                return;
            }

            var track = ClientSize.Height - 16;
            var thumb = Math.Max(36, track * ClientSize.Height / Math.Max(ClientSize.Height, Body.Height));
            var travel = Math.Max(1, track - thumb);
            var y = 8 + (int)(travel * (_offset / (float)span));
            var bounds = new Rectangle(ClientSize.Width - 8, y, 4, thumb);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(bounds, 2);
            using var thumbBrush = new SolidBrush(Theme.AccentMark);
            e.Graphics.FillPath(thumbBrush, path);
        }

        private void MoveTo(int offset)
        {
            var span = Math.Max(0, Body.Height - ClientSize.Height);
            _offset = Math.Clamp(offset, 0, span);
            Body.Top = -_offset;
            Invalidate();
        }
    }

    private sealed class ListWell : Control
    {
        public ListWell()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Field;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var brush = new SolidBrush(Field);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }

    private sealed class Heading : Control
    {
        private readonly string _text;
        private readonly Font _font;

        public Heading(string text, Font font)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            _text = text;
            _font = font;
            BackColor = Surface;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(
                e.Graphics,
                _text,
                _font,
                ClientRectangle,
                Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    private sealed class GlyphBox : Control
    {
        private readonly Image _image;

        public GlyphBox(Image image)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            _image = image;
            BackColor = Surface;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(_image, ClientRectangle);
        }
    }

    private sealed class EntryRow : Control
    {
        private readonly PhoneEntry _entry;
        private readonly Font _titleFont;
        private readonly Font _subFont;
        private readonly bool _recent;
        private bool _hot;

        public EntryRow(PhoneEntry entry, Font titleFont, Font subFont, bool recent)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            _entry = entry;
            _titleFont = titleFont;
            _subFont = subFont;
            _recent = recent;
            BackColor = Field;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        public event Action? Invoked;

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(Field))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }

            if (_hot)
            {
                var hover = new Rectangle(4, 2, Math.Max(0, Width - 8), Math.Max(0, Height - 4));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(hover, 6);
                using var brush = new SolidBrush(Hover);
                e.Graphics.FillPath(brush, path);
            }

            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (_recent)
            {
                var icon = new Rectangle(12, (Height - 32) / 2, 32, 32);
                DrawIcon(e.Graphics, icon, _entry.Person ? 16 : 4);
                var textX = 54;
                var textWidth = Math.Max(20, Width - textX - 12);
                TextRenderer.DrawText(
                    e.Graphics,
                    _entry.Title,
                    _titleFont,
                    new Rectangle(textX, 8, textWidth, 22),
                    Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (_entry.Subtitle.Length > 0)
                {
                    TextRenderer.DrawText(
                        e.Graphics,
                        _entry.Subtitle,
                        _subFont,
                        new Rectangle(textX, 30, textWidth, 18),
                        MutedText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }

                return;
            }

            var mark = new Rectangle(14, (Height - 16) / 2, 16, 16);
            DrawIcon(e.Graphics, mark, 2);
            TextRenderer.DrawText(
                e.Graphics,
                _entry.Title,
                _titleFont,
                new Rectangle(42, 0, Math.Max(20, Width - 54), Height),
                Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
            {
                Invoked?.Invoke();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hot = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = false;
            Invalidate();
        }

        private void DrawIcon(Graphics graphics, Rectangle bounds, int radius)
        {
            if (_entry.Icon == null)
            {
                return;
            }

            var state = graphics.Save();
            using (var clip = radius >= bounds.Width / 2 ? Circle(bounds) : Rounded(bounds, radius))
            {
                graphics.SetClip(clip);
                graphics.DrawImage(_entry.Icon, bounds);
            }

            graphics.Restore(state);
        }

        private static GraphicsPath Circle(Rectangle bounds)
        {
            var path = new GraphicsPath();
            path.AddEllipse(bounds);
            return path;
        }
    }
}
