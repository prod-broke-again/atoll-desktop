using System.Drawing.Drawing2D;

namespace TaskbarSpike;

/// <summary>
/// Own Start menu, laid out like the Windows one: pinned grid, recommended row,
/// then the full app list. The Windows Start window is not used.
/// The page scrolls with the wheel. There is no WinForms scrollbar.
/// </summary>
internal sealed class StartForm : Form
{
    private const int FooterHeight = 60;
    private static Color Surface => Theme.Surface;
    private static Color Field => Theme.FieldA;
    private static Color Hover => Theme.HoverA;
    private static Color Selected => Theme.SelectedA;
    private static Color MutedText => Theme.Text2;

    private static bool _pinsExpanded = true;
    private static bool _more;
    private static bool _gridMode;

    private readonly TextBox _box;
    private readonly Panel _field;
    private readonly Page _page;
    private readonly Panel _footer;
    private readonly Panel _powerMenu;
    private readonly Font _icons;
    private readonly Font _sectionFont;
    private readonly Font _letterFont;
    private readonly Font _rowFont;
    private readonly Font _detailFont;
    private readonly Wheel _wheel;
    private readonly System.Windows.Forms.Timer _thumbTimer;
    private readonly PhonePane _phonePane;
    private readonly List<AppLine> _lines = new();
    private AppGrid? _appGrid;
    private PhoneSnapshot? _phone;
    private Rectangle _dockBounds;
    private int _anchorX;
    private int _anchorTop;
    private int _width = 720;
    private long _openedAt;
    private int _selected = -1;

    public event Action? Dismissed;

    public StartForm()
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
        ClientSize = new Size(_width, 640);
        _icons = new Font("Segoe MDL2 Assets", 14f);
        _sectionFont = ThemeFont.Text(12f, FontStyle.Bold);
        _letterFont = ThemeFont.Text(11f, FontStyle.Bold);
        _rowFont = ThemeFont.Text(10f);
        _detailFont = ThemeFont.Text(8f);

        _field = new Panel
        {
            BackColor = Field,
            Bounds = new Rectangle(16, 16, _width - 32, 40),
        };
        _field.Controls.Add(new Label
        {
            Font = _icons,
            Text = "\uE721",
            ForeColor = MutedText,
            BackColor = Field,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(6, 4, 32, 32),
        });
        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            Font = Font,
            Bounds = new Rectangle(42, 10, _width - 96, 22),
        };
        _field.Paint += (_, e) =>
        {
            if (_box.Focused)
            {
                PopupChrome.PaintFocus(e.Graphics, _field.ClientRectangle);
            }
        };
        _box.GotFocus += (_, _) => _field.Invalidate();
        _box.LostFocus += (_, _) => _field.Invalidate();
        _box.TextChanged += (_, _) => RebuildList();
        _box.KeyDown += OnBoxKeyDown;
        _field.Controls.Add(_box);
        _field.SizeChanged += (_, _) => Round(_field, 10);

        _page = new Page { BackColor = Surface };
        _footer = new Panel { BackColor = Surface, Height = FooterHeight };
        _footer.Controls.Add(new Avatar(StartLayout.AccountImage(), _icons)
        {
            Bounds = new Rectangle(18, 12, 36, 36),
        });
        _footer.Controls.Add(new Label
        {
            Name = "account",
            Text = StartMenu.AccountName(),
            ForeColor = Theme.Text,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(62, 12, 400, 36),
        });

        _powerMenu = new Panel
        {
            BackColor = Field,
            Visible = false,
            Size = new Size(196, 148),
        };
        _powerMenu.SizeChanged += (_, _) => Round(_powerMenu, 10);
        AddPowerItem("Сон", "sleep", 0);
        AddPowerItem("Завершение работы", "shutdown", 1);
        AddPowerItem("Перезагрузка", "restart", 2);
        AddPowerItem("Выход", "signout", 3);

        var power = new GlassButton
        {
            Text = "\uE7E8",
            Font = _icons,
            ForeColor = Theme.Text,
            BackColor = Surface,
            Bounds = new Rectangle(_width - 56, 12, 36, 36),
            Cursor = Cursors.Hand,
        };
        power.FlatAppearance.MouseOverBackColor = Field;
        power.Click += (_, _) =>
        {
            _powerMenu.Visible = !_powerMenu.Visible;
            _powerMenu.BringToFront();
        };
        _footer.Controls.Add(power);

        _phonePane = new PhonePane { Bounds = new Rectangle(_width, 0, 0, 640), Visible = false };
        _phonePane.AppLaunched += () =>
        {
            Hide();
            Dismissed?.Invoke();
        };
        Controls.Add(_field);
        Controls.Add(_page);
        Controls.Add(_footer);
        Controls.Add(_phonePane);
        Controls.Add(_powerMenu);

        _wheel = new Wheel(this);
        Application.AddMessageFilter(_wheel);
        _thumbTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _thumbTimer.Tick += (_, _) =>
        {
            _page.Note(_page.RectangleToScreen(_page.ClientRectangle).Contains(Cursor.Position));
            _phonePane.Note(_phonePane.Visible && _phonePane.RectangleToScreen(_phonePane.ClientRectangle).Contains(Cursor.Position));
        };
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
        _powerMenu.Visible = false;
        LoadPhone();
        SizeToScreen();
        if (_box.Text.Length == 0)
        {
            RebuildList();
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
            if (!Visible)
            {
                return;
            }

            Activate();
            _box.Focus();
            SendCue();
            LoadVisibleIcons();
        }));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PopupChrome.ClearSystemChrome(Handle);
        SendCue();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _thumbTimer.Start();
        }
        else
        {
            _thumbTimer.Stop();
            _page.Note(false);
            _phonePane.Note(false);
        }
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
            Application.RemoveMessageFilter(_wheel);
            _thumbTimer.Dispose();
            _icons.Dispose();
            _sectionFont.Dispose();
            _letterFont.Dispose();
            _rowFont.Dispose();
            _detailFont.Dispose();
            _phonePane.Bind(null);
            _phone?.Dispose();
            _phone = null;
        }

        base.Dispose(disposing);
    }

    private void SizeToScreen()
    {
        var screen = Screen.FromPoint(new Point(Math.Max(0, _anchorX), Math.Max(0, _anchorTop - 1))).Bounds;
        var left = 720;
        var phone = _phonePane.HasDevice ? 344 : 0;
        var max = Math.Max(560, screen.Width - 24);
        if (left + phone > max)
        {
            left = Math.Max(560, max - phone);
            if (left + phone > max)
            {
                phone = 0;
            }
        }

        _width = left;
        var height = Math.Clamp(screen.Height - 108, 620, Math.Max(620, screen.Height - 88));
        ClientSize = new Size(_width + phone, height);
        _field.Bounds = new Rectangle(16, 16, _width - 32, 40);
        _box.Bounds = new Rectangle(42, 10, Math.Max(40, _field.Width - 56), 22);
        Round(_field, 10);
        _page.Bounds = new Rectangle(0, 68, _width, height - 68 - FooterHeight);
        _footer.Bounds = new Rectangle(0, height - FooterHeight, _width, FooterHeight);
        var account = _footer.Controls.Find("account", false).FirstOrDefault();
        if (account != null)
        {
            account.Width = Math.Max(80, _width - 140);
        }

        var power = _footer.Controls.OfType<Button>().FirstOrDefault();
        if (power != null)
        {
            power.Left = _width - 56;
        }

        _powerMenu.Location = new Point(_width - 210, height - FooterHeight - _powerMenu.Height - 6);
        _phonePane.Bounds = new Rectangle(_width, 0, phone, height);
        _phonePane.Visible = phone > 0;
        Place(_anchorX, _anchorTop);
    }

    private void LoadPhone()
    {
        var next = PhoneLink.TryRead(out var problem);
        if (next == null && !string.IsNullOrEmpty(problem))
        {
            Console.WriteLine("[spike] phone: " + problem);
        }

        var previous = _phone;
        _phone = next;
        _phonePane.Bind(next);
        previous?.Dispose();
    }

    private void Place(int anchorX, int anchorTop)
    {
        var x = anchorX - 28;
        var y = anchorTop - Height - 8;
        var area = Screen.FromPoint(new Point(anchorX, Math.Max(0, anchorTop - 1))).Bounds;
        x = Math.Clamp(x, area.Left + 8, Math.Max(area.Left, area.Right - Width - 8));
        y = Math.Clamp(y, area.Top + 8, Math.Max(area.Top, area.Bottom - Height - 8));
        var next = new Point(x, y);
        if (Location != next)
        {
            Location = next;
        }
    }

    private void RebuildList()
    {
        var query = _box.Text.Trim();
        var body = _page.Body;
        body.SuspendLayout();
        _appGrid = null;
        foreach (var child in body.Controls.Cast<Control>().ToList())
        {
            child.Dispose();
        }

        _lines.Clear();
        body.Width = Math.Max(200, _page.ClientSize.Width - 12);
        var y = 8;
        if (query.Length == 0)
        {
            var pins = StartLayout.Pins();
            if (pins.Count > 0)
            {
                var toggle = pins.Count > 16 ? (_pinsExpanded ? "Меньше" : "Больше") : null;
                y = AddCaption("Закреплено", toggle, () => _pinsExpanded = !_pinsExpanded, _sectionFont, y);
                var shown = _pinsExpanded ? pins : pins.Take(16).ToList();
                y = AddTiles(shown, 8, 90, y);
            }

            var added = StartLayout.Added(9);
            var files = StartLayout.Files(9);
            if (added.Count > 0 || files.Count > 0)
            {
                var more = added.Count > 3 || files.Count > 3;
                var toggle = more ? (_more ? "Меньше" : "Показать все") : null;
                y = AddCaption("Рекомендуем", toggle, () => _more = !_more, _sectionFont, y);
                var cards = (_more ? added : added.Take(3)).Concat(_more ? files : files.Take(3)).ToList();
                y = AddCards(cards, y);
            }

            y = AddCaption("Все", _gridMode ? "Вид: Сетка" : "Вид: Список", () => _gridMode = !_gridMode, _sectionFont, y);
            y = AddAllApps(SearchCatalog.Applications(), y);
        }
        else
        {
            var hits = SearchCatalog.Find(query);
            var apps = hits.Where(hit => hit.Kind == "app").ToList();
            var settings = hits.Where(hit => hit.Kind == "setting").ToList();
            var documents = hits.Where(hit => hit.Kind == "file").ToList();
            if (apps.Count == 0 && settings.Count == 0 && documents.Count == 0)
            {
                y = AddCaption("Ничего не найдено", null, null, _sectionFont, y);
            }

            y = AddGroup("Приложения", apps, y);
            y = AddGroup("Параметры", settings, y);
            y = AddGroup("Документы", documents, y);
        }

        body.Height = y + 16;
        _selected = -1;
        body.ResumeLayout();
        _page.Reset();
        if (IsHandleCreated)
        {
            BeginInvoke(new Action(LoadVisibleIcons));
        }
    }

    /// <summary>
    /// The "All apps" section as one windowless virtual list: the page scrolls a
    /// single control and only the rows in the viewport are painted, so a catalog
    /// of hundreds of apps no longer means hundreds of HWND children moving on
    /// every wheel tick.
    /// </summary>
    private int AddAllApps(IReadOnlyList<SearchHit> apps, int y)
    {
        var grid = new AppGrid(apps, _gridMode, _rowFont, _detailFont, _letterFont, _icons)
        {
            Bounds = new Rectangle(8, y, Math.Max(200, _page.Body.Width - 16), 100),
        };
        grid.ItemClicked += Open;
        _appGrid = grid;
        _page.Body.Controls.Add(grid);
        return y + grid.ContentHeight + 8;
    }

    private int AddGroup(string title, IReadOnlyList<SearchHit> hits, int y)
    {
        if (hits.Count == 0)
        {
            return y;
        }

        y = AddCaption(title, null, null, _sectionFont, y);
        foreach (var hit in hits)
        {
            y = AddLine(hit, y);
        }

        return y;
    }

    private int AddCaption(string title, string? action, Action? toggle, Font font, int y)
    {
        var caption = new Caption(title, action, font, MutedText)
        {
            Bounds = new Rectangle(8, y, Math.Max(80, _page.Body.Width - 16), 32),
        };
        if (toggle != null)
        {
            caption.ActionClicked += () => BeginInvoke(new Action(() =>
            {
                toggle();
                RebuildList();
            }));
        }

        _page.Body.Controls.Add(caption);
        return y + 34;
    }

    private int AddTiles(IReadOnlyList<SearchHit> hits, int columns, int cellHeight, int y)
    {
        var cell = Math.Max(64, (_page.Body.Width - 8) / columns);
        var column = 0;
        var top = y;
        foreach (var hit in hits)
        {
            var tile = new PinTile(hit, _detailFont)
            {
                Bounds = new Rectangle(6 + (column * cell), top, cell - 6, cellHeight - 4),
            };
            tile.Click += (_, _) => Open(hit);
            _page.Body.Controls.Add(tile);
            column++;
            if (column == columns)
            {
                column = 0;
                top += cellHeight;
            }
        }

        return top + (column == 0 ? 4 : cellHeight);
    }

    private int AddCards(IReadOnlyList<SearchHit> hits, int y)
    {
        const int columns = 3;
        var cell = Math.Max(120, (_page.Body.Width - 16) / columns);
        var column = 0;
        var top = y;
        foreach (var hit in hits)
        {
            var card = new RecommendCard(hit, _rowFont, _detailFont)
            {
                Bounds = new Rectangle(8 + (column * cell), top, cell - 8, 62),
            };
            card.Click += (_, _) => Open(hit);
            _page.Body.Controls.Add(card);
            column++;
            if (column == columns)
            {
                column = 0;
                top += 68;
            }
        }

        return top + (column == 0 ? 8 : 70);
    }

    private int AddLine(SearchHit hit, int y)
    {
        var line = new AppLine(hit, _rowFont, _icons)
        {
            Bounds = new Rectangle(4, y, Math.Max(80, _page.Body.Width - 16), 42),
        };
        var index = _lines.Count;
        line.MouseEnter += (_, _) => Select(index);
        line.Click += (_, _) =>
        {
            Select(index);
            OpenSelected();
        };
        _lines.Add(line);
        _page.Body.Controls.Add(line);
        return y + 42;
    }

    private void LoadVisibleIcons()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        _appGrid?.EnsureVisibleIcons();
        if (_lines.Count == 0)
        {
            return;
        }

        var top = _page.Offset - 24;
        var bottom = _page.Offset + _page.ClientSize.Height + 48;
        foreach (var line in _lines)
        {
            if (line.Bottom >= top && line.Top <= bottom)
            {
                line.EnsureIcon();
            }
        }
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            if (_powerMenu.Visible)
            {
                _powerMenu.Visible = false;
            }
            else
            {
                Hide();
                Dismissed?.Invoke();
            }

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

    private void MoveSelection(int delta)
    {
        if (_box.Text.Trim().Length == 0)
        {
            if (_appGrid == null)
            {
                return;
            }

            _appGrid.MoveSelection(delta);
            if (_appGrid.SelectedIndex >= 0)
            {
                var top = _appGrid.Top + _appGrid.ItemTop(_appGrid.SelectedIndex);
                _page.RevealBand(top, top + AppGrid.RowHeight);
                _appGrid.EnsureVisibleIcons();
            }

            return;
        }

        if (_lines.Count == 0)
        {
            return;
        }

        var next = _selected < 0
            ? (delta > 0 ? 0 : _lines.Count - 1)
            : Math.Clamp(_selected + delta, 0, _lines.Count - 1);
        Select(next);
        _page.Reveal(_lines[next]);
        LoadVisibleIcons();
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _lines.Count)
        {
            return;
        }

        _selected = index;
        for (var i = 0; i < _lines.Count; i++)
        {
            _lines[i].Chosen = i == _selected;
        }
    }

    private void OpenSelected()
    {
        if (_box.Text.Trim().Length == 0)
        {
            if (_appGrid != null && _appGrid.SelectedIndex >= 0)
            {
                Open(_appGrid.Items[_appGrid.SelectedIndex]);
            }

            return;
        }

        if (_selected < 0 || _selected >= _lines.Count)
        {
            return;
        }

        Open(_lines[_selected].Hit);
    }

    private void Open(SearchHit hit)
    {
        _powerMenu.Visible = false;
        Hide();
        Dismissed?.Invoke();
        try
        {
            SearchCatalog.Launch(hit);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] start launch: " + ex.Message);
        }
    }

    private void AddPowerItem(string text, string action, int index)
    {
        var button = new GlassButton
        {
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            BackColor = Field,
            Font = Font,
            Bounds = new Rectangle(4, 4 + (index * 36), 188, 34),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.MouseOverBackColor = Selected;
        button.Click += (_, _) =>
        {
            _powerMenu.Visible = false;
            Hide();
            Dismissed?.Invoke();
            try
            {
                StartMenu.Power(action);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[spike] start power: " + ex.Message);
            }
        };
        _powerMenu.Controls.Add(button);
    }

    private static string GroupLetter(string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0)
        {
            return "#";
        }

        var letter = char.ToUpper(trimmed[0], System.Globalization.CultureInfo.CurrentCulture);
        return char.IsLetter(letter) ? letter.ToString() : "#";
    }

    private void SendCue()
    {
        if (!_box.IsHandleCreated)
        {
            return;
        }

        SendMessage(_box.Handle, 0x1501, (IntPtr)1, "Поиск приложений, параметров и документов");
    }

    private static void Round(Control control, int radius)
    {
        if (control.Width < radius * 2 || control.Height < radius * 2)
        {
            return;
        }

        using var path = new GraphicsPath();
        var bounds = new Rectangle(0, 0, control.Width, control.Height);
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        control.Region = new Region(path);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    private sealed class Wheel : IMessageFilter
    {
        private readonly StartForm _form;

        public Wheel(StartForm form)
        {
            _form = form;
        }

        public bool PreFilterMessage(ref Message message)
        {
            if (message.Msg != 0x020A || !_form.Visible)
            {
                return false;
            }

            var delta = (short)((message.WParam.ToInt64() >> 16) & 0xFFFF);
            if (_form._phonePane.Visible &&
                _form._phonePane.RectangleToScreen(_form._phonePane.ClientRectangle).Contains(Cursor.Position))
            {
                _form._phonePane.Wheel(delta);
                return true;
            }

            if (!_form._page.RectangleToScreen(_form._page.ClientRectangle).Contains(Cursor.Position))
            {
                return false;
            }

            _form._page.Wheel(delta);
            _form.LoadVisibleIcons();
            return true;
        }
    }

    private sealed class Page : Control
    {
        private int _offset;
        private bool _thumb;

        public Page()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            Body = new Panel { BackColor = Surface, Location = Point.Empty };
            Controls.Add(Body);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        public Panel Body { get; }

        public int Offset => _offset;

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

            MoveBy(delta > 0 ? -78 : 78);
        }

        public void Reveal(Control control)
        {
            RevealBand(control.Top, control.Bottom);
        }

        public void RevealBand(int top, int bottom)
        {
            if (top < _offset)
            {
                MoveTo(top);
            }
            else if (bottom > _offset + ClientSize.Height)
            {
                MoveTo(bottom - ClientSize.Height);
            }
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

        private void MoveBy(int delta)
        {
            MoveTo(_offset + delta);
        }

        private void MoveTo(int offset)
        {
            var span = Math.Max(0, Body.Height - ClientSize.Height);
            _offset = Math.Clamp(offset, 0, span);
            Body.Top = -_offset;
            Invalidate();
        }
    }

    private sealed class Caption : Control
    {
        private readonly string _title;
        private readonly string? _action;
        private readonly Font _font;
        private readonly Color _actionColor;

        public Caption(string title, string? action, Font font, Color actionColor)
        {
            _title = title;
            _action = action;
            _font = font;
            _actionColor = actionColor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Cursor = action == null ? Cursors.Default : Cursors.Hand;
        }

        public event Action? ActionClicked;

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(
                e.Graphics,
                _title,
                _font,
                new Rectangle(4, 0, Width - 150, Height),
                Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (_action != null)
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    _action,
                    SystemFonts.MessageBoxFont,
                    new Rectangle(Width - 148, 0, 140, Height),
                    _actionColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_action != null && e.Button == MouseButtons.Left && e.X > Width - 156)
            {
                ActionClicked?.Invoke();
            }
        }
    }

    private sealed class PinTile : Control
    {
        private readonly Font _detailFont;
        private Image? _image;
        private bool _hot;

        public PinTile(SearchHit hit, Font detailFont)
        {
            Hit = hit;
            _detailFont = detailFont;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            _image = SearchCatalog.IconCached(hit.IconPath);
            if (_image == null)
            {
                RequestIcon();
            }
        }

        public SearchHit Hit { get; }

        private void RequestIcon()
        {
            if (string.IsNullOrWhiteSpace(Hit.IconPath))
            {
                return;
            }

            SearchCatalog.IconAsync(Hit.IconPath).ContinueWith(task =>
            {
                if (task is { IsCompletedSuccessfully: true, Result: not null } && !IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _image = task.Result;
                            Invalidate();
                        }));
                    }
                    catch
                    {
                        // The tile was disposed while the icon was loading.
                    }
                }
            });
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hot = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_hot)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 6);
                using var brush = new SolidBrush(Hover);
                e.Graphics.FillPath(brush, path);
            }

            if (_image != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(_image, (Width - 32) / 2, 10, 32, 32);
            }

            TextRenderer.DrawText(
                e.Graphics,
                Hit.Title,
                _detailFont,
                new Rectangle(4, 46, Width - 8, 36),
                Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
    }

    private sealed class RecommendCard : Control
    {
        private readonly Font _titleFont;
        private readonly Font _detailFont;
        private Image? _image;
        private bool _hot;

        public RecommendCard(SearchHit hit, Font titleFont, Font detailFont)
        {
            Hit = hit;
            _titleFont = titleFont;
            _detailFont = detailFont;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            _image = SearchCatalog.IconCached(hit.IconPath);
            if (_image == null)
            {
                RequestIcon();
            }
        }

        public SearchHit Hit { get; }

        private void RequestIcon()
        {
            if (string.IsNullOrWhiteSpace(Hit.IconPath))
            {
                return;
            }

            SearchCatalog.IconAsync(Hit.IconPath).ContinueWith(task =>
            {
                if (task is { IsCompletedSuccessfully: true, Result: not null } && !IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _image = task.Result;
                            Invalidate();
                        }));
                    }
                    catch
                    {
                        // The card was disposed while the icon was loading.
                    }
                }
            });
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hot = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_hot)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 8);
                using var brush = new SolidBrush(Hover);
                e.Graphics.FillPath(brush, path);
            }

            if (_image != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(_image, 8, 14, 32, 32);
            }

            TextRenderer.DrawText(
                e.Graphics,
                Hit.Title,
                _titleFont,
                new Rectangle(48, 8, Width - 56, 22),
                Theme.Text,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(
                e.Graphics,
                Hit.Detail,
                _detailFont,
                new Rectangle(48, 30, Width - 56, 20),
                MutedText,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private sealed class AppLine : Control
    {
        private readonly Font _titleFont;
        private readonly Font _icons;
        private Image? _image;
        private bool _tried;
        private bool _chosen;

        public AppLine(SearchHit hit, Font titleFont, Font icons)
        {
            Hit = hit;
            _titleFont = titleFont;
            _icons = icons;
            Height = 42;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
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

        public void EnsureIcon()
        {
            if (_tried || Hit.Kind == "setting")
            {
                return;
            }

            _tried = true;
            _image = SearchCatalog.IconCached(Hit.IconPath);
            if (_image != null)
            {
                Invalidate();
                return;
            }

            SearchCatalog.IconAsync(Hit.IconPath).ContinueWith(task =>
            {
                if (task is { IsCompletedSuccessfully: true, Result: not null } && !IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _image = task.Result;
                            Invalidate();
                        }));
                    }
                    catch
                    {
                        // The line was disposed while the icon was loading.
                    }
                }
            });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var ink = Theme.TextOn(_chosen ? Selected : Surface);
            if (_chosen)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(new Rectangle(0, 1, Width - 1, Height - 3), 6);
                using var brush = new SolidBrush(Selected);
                e.Graphics.FillPath(brush, path);
                using var mark = new SolidBrush(Theme.AccentInk);
                e.Graphics.FillRectangle(mark, 4, 8, 3, Math.Max(1, Height - 16));
            }

            if (_image != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(_image, 10, 7, 28, 28);
            }
            else if (Hit.Kind == "setting")
            {
                TextRenderer.DrawText(e.Graphics, "\uE713", _icons, new Rectangle(10, 7, 28, 28), ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            TextRenderer.DrawText(
                e.Graphics,
                Hit.Title,
                _titleFont,
                new Rectangle(48, 0, Width - 56, Height),
                ink,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>
    /// Owner-drawn, windowless list for the "All apps" catalog. Hundreds of
    /// apps used to mean hundreds of AppLine HWNDs the page had to move on
    /// every wheel tick; here a single control draws only the rows inside the
    /// viewport and pulls their icons from the shared cache asynchronously.
    /// </summary>
    private sealed class AppGrid : Control
    {
        public const int RowHeight = 42;
        private const int TileHeight = 86;
        private const int GridColumns = 6;
        private const int LetterHeight = 30;

        private readonly IReadOnlyList<SearchHit> _items;
        private readonly bool _tiles;
        private readonly Font _rowFont;
        private readonly Font _detailFont;
        private readonly Font _letterFont;
        private readonly Font _icons;
        private readonly List<Entry> _entries = new();
        private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);
        private int _itemWidth;
        private int _hover = -1;
        private int _pressedAt = -1;
        private int _selected = -1;

        public AppGrid(IReadOnlyList<SearchHit> items, bool tiles, Font rowFont, Font detailFont, Font letterFont, Font icons)
        {
            _items = items;
            _tiles = tiles;
            _rowFont = rowFont;
            _detailFont = detailFont;
            _letterFont = letterFont;
            _icons = icons;
            TabStop = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        public IReadOnlyList<SearchHit> Items => _items;

        public int ContentHeight { get; private set; }

        public int SelectedIndex => _selected;

        public event Action<SearchHit>? ItemClicked;

        public void SetWidth(int width)
        {
            _itemWidth = width;
            LayoutEntries();
            Height = Math.Max(1, ContentHeight);
        }

        public int ItemTop(int index) => index >= 0 && index < _entries.Count ? _entries[index].Top : 0;

        public void MoveSelection(int delta)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            var next = _selected < 0
                ? (delta > 0 ? 0 : _entries.Count - 1)
                : Math.Clamp(_selected + delta, 0, _entries.Count - 1);
            if (next != _selected)
            {
                _selected = next;
                Invalidate();
            }
        }

        /// <summary>Kicks async icon loads for every item in the page viewport.</summary>
        public void EnsureVisibleIcons()
        {
            if (_items.Count == 0)
            {
                return;
            }

            var view = ViewportBand(48);
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.ItemIndex < 0 || entry.Bottom < view.Top || entry.Top > view.Bottom)
                {
                    continue;
                }

                RequestIcon(_items[entry.ItemIndex]);
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width > 0 && _itemWidth != Width)
            {
                SetWidth(Width);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var view = ViewportBand(60);
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Bottom < view.Top || entry.Top > view.Bottom)
                {
                    continue;
                }

                if (entry.ItemIndex < 0)
                {
                    PaintLetter(e.Graphics, entry);
                }
                else
                {
                    PaintItem(e.Graphics, entry, i);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hover = EntryAt(e.Location);
            if (hover != _hover)
            {
                _hover = hover;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover >= 0)
            {
                _hover = -1;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressedAt = EntryAt(e.Location);
                if (_pressedAt >= 0)
                {
                    Invalidate();
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var pressed = _pressedAt;
            _pressedAt = -1;
            if (pressed >= 0 && pressed < _entries.Count)
            {
                _selected = pressed;
                ItemClicked?.Invoke(_items[_entries[pressed].ItemIndex]);
                return;
            }

            if (pressed >= 0)
            {
                Invalidate();
            }
        }

        private (int Top, int Bottom) ViewportBand(int margin)
        {
            var body = Parent;
            var page = body?.Parent;
            if (body == null || page == null)
            {
                return (0, Height);
            }

            var offset = -body.Top;
            var top = offset - Top - margin;
            var bottom = offset + page.ClientSize.Height - Top + margin;
            return (top, bottom);
        }

        private void RequestIcon(SearchHit hit)
        {
            if (string.IsNullOrWhiteSpace(hit.IconPath) || !_requested.Add(hit.IconPath))
            {
                return;
            }

            SearchCatalog.IconAsync(hit.IconPath).ContinueWith(task =>
            {
                if (task is { IsCompletedSuccessfully: true, Result: not null } && !IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(Invalidate));
                    }
                    catch
                    {
                        // The grid was disposed while the icon was loading.
                    }
                }
            });
        }

        private int EntryAt(Point point)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.ItemIndex < 0)
                {
                    continue;
                }

                if (point.Y >= entry.Top && point.Y < entry.Bottom &&
                    point.X >= entry.Left && point.X < entry.Left + entry.Width)
                {
                    return i;
                }
            }

            return -1;
        }

        private void LayoutEntries()
        {
            _entries.Clear();
            var y = 0;
            string? letter = null;
            var column = 0;
            var rowTop = 0;
            var cell = Math.Max(72, (_itemWidth - 8) / GridColumns);
            var itemHeight = _tiles ? TileHeight : RowHeight;
            for (var i = 0; i < _items.Count; i++)
            {
                var next = GroupLetter(_items[i].Title);
                if (!string.Equals(next, letter, StringComparison.Ordinal))
                {
                    if (_tiles && column != 0)
                    {
                        rowTop += TileHeight;
                        column = 0;
                    }

                    letter = next;
                    y = _tiles ? rowTop : y;
                    _entries.Add(Entry.LetterAt(y, next));
                    y += LetterHeight;
                    rowTop = y;
                }

                if (_tiles)
                {
                    _entries.Add(Entry.ItemAt(rowTop, 6 + (column * cell), cell - 6, itemHeight, i));
                    column++;
                    if (column == GridColumns)
                    {
                        column = 0;
                        rowTop += TileHeight;
                    }

                    y = rowTop + (column == 0 ? 0 : TileHeight);
                }
                else
                {
                    _entries.Add(Entry.ItemAt(y, 0, _itemWidth, itemHeight, i));
                    y += RowHeight;
                }
            }

            ContentHeight = y + 8;
        }

        private void PaintLetter(Graphics graphics, Entry entry)
        {
            TextRenderer.DrawText(
                graphics,
                entry.Letter ?? string.Empty,
                _letterFont,
                new Rectangle(8, entry.Top, Math.Max(80, _itemWidth - 16), LetterHeight),
                MutedText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void PaintItem(Graphics graphics, Entry entry, int entryIndex)
        {
            var hit = _items[entry.ItemIndex];
            var rect = new Rectangle(entry.Left + 2, entry.Top + 1, entry.Width - 5, entry.Height - 3);
            var chosen = entryIndex == _selected;
            var pressed = entryIndex == _pressedAt;
            var hot = entryIndex == _hover;
            if (chosen || hot || pressed)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(rect, _tiles ? 10 : 8);
                using var plate = new SolidBrush(pressed || chosen ? Selected : Hover);
                graphics.FillPath(plate, path);
                if (pressed || chosen)
                {
                    using var mark = new SolidBrush(Theme.AccentInk);
                    graphics.FillRectangle(mark, rect.Left + 2, rect.Top + 8, 3, Math.Max(1, rect.Height - 16));
                }
            }

            var ink = Theme.TextOn(chosen ? Theme.Selected : Theme.Surface);
            var image = SearchCatalog.IconCached(hit.IconPath);
            if (_tiles)
            {
                var x = entry.Left + ((entry.Width - 32) / 2);
                if (image != null)
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(image, x, entry.Top + 8, 32, 32);
                }
                else
                {
                    PaintChip(graphics, hit, x, entry.Top + 8, 32);
                    RequestIcon(hit);
                }

                TextRenderer.DrawText(
                    graphics,
                    hit.Title,
                    _detailFont,
                    new Rectangle(entry.Left + 4, entry.Top + 44, entry.Width - 8, 36),
                    ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                return;
            }

            if (image != null)
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, entry.Left + 10, entry.Top + 7, 28, 28);
            }
            else if (hit.Kind == "setting")
            {
                TextRenderer.DrawText(graphics, "\uE713", _icons, new Rectangle(entry.Left + 10, entry.Top + 7, 28, 28), ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                PaintChip(graphics, hit, entry.Left + 10, entry.Top + 7, 28);
                RequestIcon(hit);
            }

            TextRenderer.DrawText(
                graphics,
                hit.Title,
                _rowFont,
                new Rectangle(entry.Left + 48, entry.Top, entry.Width - 56, entry.Height),
                ink,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static void PaintChip(Graphics graphics, SearchHit hit, int x, int y, int size)
        {
            var rect = new Rectangle(x, y, size, size);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(rect, 7);
            using var brush = new LinearGradientBrush(rect, Color.FromArgb(102, Theme.Accent), Color.FromArgb(71, Theme.AccentInk), 135f);
            graphics.FillPath(brush, path);
            var letter = string.IsNullOrWhiteSpace(hit.Title) ? "?" : hit.Title.Trim()[..1].ToUpperInvariant();
            TextRenderer.DrawText(
                graphics,
                letter,
                SystemFonts.DefaultFont,
                rect,
                Theme.OnAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private readonly record struct Entry(int Top, int Height, int Left, int Width, int ItemIndex, string? Letter)
        {
            public int Bottom => Top + Height;

            public static Entry LetterAt(int top, string letter) => new(top, LetterHeight, 0, 0, -1, letter);

            public static Entry ItemAt(int top, int left, int width, int height, int itemIndex) => new(top, height, left, width, itemIndex, null);
        }
    }

    private sealed class Avatar : Control
    {
        private readonly Image? _image;
        private readonly Font _icons;

        public Avatar(Image? image, Font icons)
        {
            _image = image;
            _icons = icons;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
            if (_image == null)
            {
                TextRenderer.DrawText(e.Graphics, "\uE77B", _icons, bounds, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            using var path = new GraphicsPath();
            path.AddEllipse(bounds);
            e.Graphics.SetClip(path);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(_image, bounds);
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

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
