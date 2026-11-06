namespace TaskbarSpike;

/// <summary>
/// Own search panel. Both the empty page and the typed results stay in this window.
/// Windows Search (SearchHost, Win+S) is not used.
/// </summary>
internal sealed class SearchForm : Form
{
    private const int PanelWidth = 380;
    private const int ListTop = 64;
    private static Color Surface => Theme.Surface;
    private static Color Field => Theme.FieldA;
    private static Color Selected => Theme.SelectedA;
    private static Color MutedText => Theme.Text2;

    private readonly TextBox _box;
    private readonly Panel _list;
    private readonly Font _icons;
    private readonly Font _titleFont;
    private readonly Font _detailFont;
    private readonly List<SearchRow> _rows = new();
    private Rectangle _dockBounds;
    private int _anchorX;
    private int _anchorTop;
    private long _openedAt;
    private int _selected = -1;

    public event Action? Dismissed;

    public SearchForm()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Surface;
        ForeColor = Theme.Text;
        Font = ThemeFont.Text(10f);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        ClientSize = new Size(PanelWidth, 280);
        _icons = new Font("Segoe MDL2 Assets", 14f);
        _titleFont = new Font(Font, FontStyle.Bold);
        _detailFont = ThemeFont.Text(8f);

        var field = new Panel
        {
            BackColor = Field,
            Bounds = new Rectangle(14, 12, PanelWidth - 28, 40),
        };
        field.SizeChanged += (_, _) => Round(field, 10);
        Round(field, 10);
        var glyph = new Label
        {
            Font = _icons,
            Text = "\uE721",
            ForeColor = MutedText,
            BackColor = Field,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(4, 4, 32, 32),
        };
        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            Font = Font,
            Bounds = new Rectangle(40, 10, PanelWidth - 86, 22),
        };
        _box.TextChanged += (_, _) => Rebuild();
        _box.KeyDown += OnBoxKeyDown;
        field.Paint += (_, e) =>
        {
            if (_box.Focused)
            {
                PopupChrome.PaintFocus(e.Graphics, field.ClientRectangle);
            }
        };
        _box.GotFocus += (_, _) => field.Invalidate();
        _box.LostFocus += (_, _) => field.Invalidate();
        field.Controls.Add(glyph);
        field.Controls.Add(_box);
        _list = new Panel
        {
            BackColor = Surface,
            Bounds = new Rectangle(8, ListTop, PanelWidth - 16, 200),
            AutoScroll = true,
        };
        Controls.Add(field);
        Controls.Add(_list);
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

    public void ShowFor(int anchorX, int anchorTop, Rectangle dockBounds)
    {
        _anchorX = anchorX;
        _anchorTop = anchorTop;
        _dockBounds = dockBounds;
        _openedAt = Environment.TickCount64;
        if (_box.Text.Length == 0)
        {
            Rebuild();
        }
        else
        {
            _box.Text = string.Empty;
        }

        Show();
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow);
        Activate();
        _box.Focus();
        BeginInvoke(new Action(() =>
        {
            if (Visible)
            {
                Activate();
                _box.Focus();
                SendCue();
            }
        }));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PopupChrome.ClearSystemChrome(Handle);
        SendCue();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!Visible || Environment.TickCount64 - _openedAt < 250 || Bounds.Contains(Cursor.Position) || _dockBounds.Contains(Cursor.Position))
        {
            return;
        }

        Hide();
        Dismissed?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            Dismissed?.Invoke();
            e.Handled = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PopupChrome.Paint(e.Graphics, ClientSize, PopupChrome.PopupRadiusPx(this));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!IsHandleCreated || Width <= 1 || Height <= 1)
        {
            return;
        }

        PopupChrome.ApplyRegion(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icons.Dispose();
            _titleFont.Dispose();
            _detailFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            Dismissed?.Invoke();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Down)
        {
            MoveSelection(1);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Enter)
        {
            OpenSelected();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void Rebuild()
    {
        var query = _box.Text.Trim();
        var hits = query.Length == 0 ? SearchCatalog.Home() : SearchCatalog.Find(query);
        _list.SuspendLayout();
        foreach (var child in _list.Controls.Cast<Control>().ToList())
        {
            child.Dispose();
        }

        _rows.Clear();
        var y = 4;
        if (query.Length == 0)
        {
            y = AddHeader("Лучшие приложения", y);
            if (hits.Count == 0)
            {
                y = AddNote("Введите название приложения", y);
            }

            foreach (var hit in hits)
            {
                y = AddRow(hit, y);
            }
        }
        else
        {
            var apps = hits.Where(hit => hit.Kind == "app").ToList();
            var settings = hits.Where(hit => hit.Kind == "setting").ToList();
            var files = hits.Where(hit => hit.Kind == "file").ToList();
            var best = apps.FirstOrDefault() ?? settings.FirstOrDefault() ?? files.FirstOrDefault();
            if (best != null)
            {
                y = AddHeader("Лучшее соответствие", y);
                y = AddRow(best, y);
            }
            else
            {
                y = AddNote("Ничего не найдено", y);
            }

            y = AddGroup("Приложения", best?.Kind == "app" ? apps.Skip(1) : apps, y);
            y = AddGroup("Параметры", best?.Kind == "setting" ? settings.Skip(1) : settings, y);
            y = AddGroup("Документы", best?.Kind == "file" ? files.Skip(1) : files, y);
            y = AddHeader("Поиск в Интернете", y);
            y = AddRow(SearchCatalog.Web(query), y);
        }

        _list.AutoScrollMinSize = new Size(0, y);
        _list.AutoScrollPosition = new Point(0, 0);
        _selected = _rows.Count > 0 ? 0 : -1;
        ApplySelection();
        _list.ResumeLayout();
        ResizeTo(y);
    }

    private int AddGroup(string title, IEnumerable<SearchHit> hits, int y)
    {
        var list = hits.ToList();
        if (list.Count == 0)
        {
            return y;
        }

        y = AddHeader(title, y);
        foreach (var hit in list)
        {
            y = AddRow(hit, y);
        }

        return y;
    }

    private int AddHeader(string text, int y)
    {
        var label = new Label
        {
            Text = text,
            ForeColor = MutedText,
            BackColor = Surface,
            Bounds = new Rectangle(8, y, PanelWidth - 48, 26),
            TextAlign = ContentAlignment.BottomLeft,
        };
        _list.Controls.Add(label);
        return y + 28;
    }

    private int AddNote(string text, int y)
    {
        var label = new Label
        {
            Text = text,
            ForeColor = MutedText,
            BackColor = Surface,
            Bounds = new Rectangle(8, y + 6, PanelWidth - 48, 28),
        };
        _list.Controls.Add(label);
        return y + 40;
    }

    private int AddRow(SearchHit hit, int y)
    {
        var row = new SearchRow(hit, _icons, _titleFont, _detailFont)
        {
            Bounds = new Rectangle(0, y, PanelWidth - 36, 48),
        };
        row.MouseEnter += (_, _) => Select(_rows.IndexOf(row));
        row.Click += (_, _) =>
        {
            Select(_rows.IndexOf(row));
            OpenSelected();
        };
        _rows.Add(row);
        _list.Controls.Add(row);
        return y + 48;
    }

    private void ResizeTo(int contentHeight)
    {
        var screen = Screen.FromPoint(new Point(Math.Max(0, _anchorX), Math.Max(0, _anchorTop - 1))).Bounds;
        var maxList = Math.Max(180, screen.Height - 220);
        var listHeight = Math.Clamp(contentHeight + 8, 72, maxList);
        _list.Bounds = new Rectangle(8, ListTop, PanelWidth - 16, listHeight);
        ClientSize = new Size(PanelWidth, ListTop + listHeight + 10);
        Place(_anchorX, _anchorTop);
    }

    private void Place(int anchorX, int anchorTop)
    {
        var x = anchorX - 36;
        var y = anchorTop - Height - 8;
        var area = Screen.FromPoint(new Point(anchorX, Math.Max(0, anchorTop - 1))).Bounds;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        var next = new Point(x, y);
        if (Location != next)
        {
            Location = next;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        Select(Math.Clamp(_selected + delta, 0, _rows.Count - 1));
        _list.ScrollControlIntoView(_rows[_selected]);
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        _selected = index;
        ApplySelection();
    }

    private void ApplySelection()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].Chosen = i == _selected;
        }
    }

    private void OpenSelected()
    {
        if (_selected < 0 || _selected >= _rows.Count)
        {
            return;
        }

        var hit = _rows[_selected].Hit;
        Hide();
        Dismissed?.Invoke();
        try
        {
            SearchCatalog.Launch(hit);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] search launch: " + ex.Message);
        }
    }

    private void SendCue()
    {
        if (!_box.IsHandleCreated)
        {
            return;
        }

        SendMessage(_box.Handle, 0x1501, (IntPtr)1, "Поиск");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    private static void Round(Control control, int radius)
    {
        if (control.Width < radius * 2 || control.Height < radius * 2)
        {
            return;
        }

        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var bounds = new Rectangle(0, 0, control.Width, control.Height);
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        control.Region = new Region(path);
    }

    private sealed class SearchRow : Control
    {
        private readonly Font _icons;
        private readonly Font _titleFont;
        private readonly Font _detailFont;
        private readonly Image? _image;
        private bool _chosen;

        public SearchRow(SearchHit hit, Font icons, Font titleFont, Font detailFont)
        {
            Hit = hit;
            _icons = icons;
            _titleFont = titleFont;
            _detailFont = detailFont;
            Height = 48;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, false);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            _image = hit.Kind is "setting" or "web" ? null : SearchCatalog.LoadIcon(hit.IconPath);
        }

        public SearchHit Hit { get; }

        public bool Chosen
        {
            get => _chosen;
            set
            {
                if (_chosen == value)
                {
                    return;
                }

                _chosen = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // The unchosen row stays unpainted: the acrylic shows through it.
            // Only the selection is a fill — a rounded plate with an accent bar.
            if (_chosen)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = PopupChrome.Rounded(new Rectangle(2, 1, Width - 5, Height - 3), 8);
                using var plate = new SolidBrush(Selected);
                e.Graphics.FillPath(plate, path);
                using var mark = new SolidBrush(Theme.AccentInk);
                e.Graphics.FillRectangle(mark, 4, 10, 3, Math.Max(1, Height - 20));
            }

            var background = _chosen ? Theme.Selected : Theme.Surface;
            if (_image != null)
            {
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(_image, 10, 10, 28, 28);
            }
            else
            {
                var glyph = Hit.Kind switch
                {
                    "setting" => "\uE713",
                    "web" => "\uE774",
                    "file" => "\uE8A5",
                    _ => "\uECAA",
                };
                var glyphColor = Hit.Kind == "web" ? Theme.AccentInk : Theme.TextOn(background);
                TextRenderer.DrawText(e.Graphics, glyph, _icons, new Rectangle(10, 10, 28, 28), glyphColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            TextRenderer.DrawText(
                e.Graphics,
                Hit.Title,
                _titleFont,
                new Rectangle(48, 4, Width - 56, 22),
                Theme.TextOn(background),
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(
                e.Graphics,
                Hit.Detail,
                _detailFont,
                new Rectangle(48, 24, Width - 56, 18),
                Theme.MutedOn(background),
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
}
