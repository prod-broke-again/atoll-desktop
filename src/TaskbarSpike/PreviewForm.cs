using System.Runtime.InteropServices;

namespace TaskbarSpike;

/// <summary>
/// Live window previews via the same DWM thumbnails the Windows taskbar uses.
/// </summary>
internal sealed class PreviewForm : Form
{
    private const int MaxEdge = 220;
    private const int TitleHeight = 26;
    private const int Pad = 10;
    private const int Gap = 8;
    private const int NameBand = 6; // top padding; the window title (with its close button) is the card header
    private const int PreviewRadiusDip = 14;

    private const uint DwmTnpRectDestination = 0x00000001;
    private const uint DwmTnpOpacity = 0x00000004;
    private const uint DwmTnpVisible = 0x00000008;

    private readonly List<IntPtr> _thumbs = new();
    private readonly List<PreviewCard> _cards = new();

    public event Action<IntPtr>? WindowChosen;
    public event Action<IntPtr>? WindowClosed;

    private readonly List<(IntPtr Hwnd, string Title)> _windows = new();
    private readonly System.Windows.Forms.Timer _hoverTimer;
    private readonly System.Windows.Forms.Timer _leaveTimer;
    private readonly System.Windows.Forms.Timer _lingerTimer;
    private readonly System.Windows.Forms.Timer _reassertTimer;
    private readonly PeekCover _cover;
    private int _anchorX;
    private int _anchorTop;
    private IntPtr _hovered;
    private IntPtr _peekTarget;
    private bool _peeking;
    private bool _peekFaultLogged;
    private bool _committing;
    private int _commitGeneration;
    private int _reassertIndex;
    private IntPtr _reassertHwnd;
    private IntPtr _mouseHook;
    private LowLevelMouseProc? _mouseProc;
    private bool _hookEatUp;
    private bool _hookFaultLogged;
    private string _appName = string.Empty;
    private bool _bar;
    private bool _glass = true;

    /// <summary>
    /// Screen points that belong to the dock and its popups. A click there
    /// is a real panel click, not a click on the peeked window.
    /// </summary>
    public Func<Point, bool>? HitIsChrome { get; set; }

    public event Action? PeekShown;

    /// <summary>Raised whenever the sheet becomes hidden, by any path.</summary>
    public event Action? Hidden;

    public PreviewForm()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = ThemeFont.Text(9f);
        DoubleBuffered = true;
        Padding = new Padding(Pad);
        _hoverTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _hoverTimer.Tick += (_, _) => OnHoverTick();
        _leaveTimer = new System.Windows.Forms.Timer { Interval = 60 };
        _leaveTimer.Tick += (_, _) => OnLeaveTick();
        _lingerTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _lingerTimer.Tick += (_, _) => OnLingerTick();
        _reassertTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _reassertTimer.Tick += (_, _) => OnReassertTick();
        _cover = new PeekCover();
        _cover.Clicked += OnCoverClick;
        // A previous run can exit while DWM peek is still armed. The shell
        // taskbar is the only thing that used to clear it.
        CancelLivePreview();
    }

    protected override bool ShowWithoutActivation => true;

    private System.Windows.Forms.Timer? _outsideTimer;
    private bool _buttonWasDown;

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _buttonWasDown = AnyMouseButtonDown();
            _outsideTimer ??= new System.Windows.Forms.Timer { Interval = 40 };
            _outsideTimer.Tick -= OnOutsideTick;
            _outsideTimer.Tick += OnOutsideTick;
            _outsideTimer.Start();
        }
        else
        {
            _outsideTimer?.Stop();
            Hidden?.Invoke();
        }
    }

    private static bool AnyMouseButtonDown()
    {
        return (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>Click-outside dismissal plus the hover-only close button of each card.</summary>
    private void OnOutsideTick(object? sender, EventArgs e)
    {
        if (!Visible || _committing)
        {
            return;
        }

        var cursor = Cursor.Position;
        var down = AnyMouseButtonDown();
        if (down && !_buttonWasDown && !_peeking && !Bounds.Contains(cursor) && HitIsChrome?.Invoke(cursor) != true)
        {
            Dismiss();
            return;
        }

        _buttonWasDown = down;
        var local = PointToClient(cursor);
        foreach (var card in _cards)
        {
            var header = new Rectangle(card.Title.Left, NameBand, card.Canvas.Width, TitleHeight);
            var show = header.Contains(local);
            if (card.Close.Visible != show)
            {
                card.Close.Visible = show;
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PopupChrome.ClearSystemChrome(Handle);
        ApplyShape();
        NativeMethods.ExcludeFromPeek(Handle);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyShape();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_bar)
        {
            using var fill = new SolidBrush(Color.FromArgb(242, 32, 32, 34));
            e.Graphics.FillRectangle(fill, ClientRectangle);
            using var pen = new Pen(Color.FromArgb(18, 255, 255, 255));
            e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
        else
        {
            base.OnPaint(e);
            PopupChrome.Paint(e.Graphics, ClientSize, ChromeRadiusPx());
        }
    }

    /// <summary>Dock glass with a 14 DIP corner, or the flat Win10 fill with square corners.</summary>
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

    private int ChromeRadiusPx()
    {
        if (_bar)
        {
            return 0;
        }

        return Math.Max(8, (int)Math.Round(PreviewRadiusDip * DeviceDpi / 96.0, MidpointRounding.AwayFromZero));
    }

    private void ApplyShape()
    {
        if (!IsHandleCreated || Width <= 1 || Height <= 1)
        {
            return;
        }

        if (_bar)
        {
            BackColor = Color.FromArgb(242, 32, 32, 34);
            var region = NativeMethods.CreateRectRgn(0, 0, Width, Height);
            NativeMethods.SetWindowRgn(Handle, region, true);
            PopupChrome.DisableAccent(Handle);
            return;
        }

        BackColor = Theme.Surface;
        var radius = ChromeRadiusPx();
        var diameter = Math.Max(2, radius * 2);
        var inset = Math.Max(2, diameter - 2);
        var round = NativeMethods.CreateRoundRectRgn(1, 1, Width - 1, Height - 1, inset, inset);
        NativeMethods.SetWindowRgn(Handle, round, true);
        if (_glass)
        {
            PopupChrome.EnableAcrylic(Handle, PopupChrome.DockGlassTint());
        }
        else
        {
            PopupChrome.DisableAccent(Handle);
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExTopMost = 0x00000008;
            const int wsExNoActivate = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow | wsExTopMost | wsExNoActivate;
            PopupChrome.UseShadow(cp);
            return cp;
        }
    }

    public void ShowFor(IReadOnlyList<(IntPtr Hwnd, string Title)> windows, int anchorX, int anchorTop, string appName, bool bar, bool glass)
    {
        _appName = string.IsNullOrWhiteSpace(appName) ? "Приложение" : appName;
        _bar = bar;
        _glass = glass;
        var items = windows.Where(item => NativeMethods.IsWindow(item.Hwnd)).ToList();
        _hoverTimer.Stop();
        _leaveTimer.Stop();
        _lingerTimer.Stop();
        _hovered = IntPtr.Zero;
        EndPeek();
        ClearThumbs();
        while (Controls.Count > 0)
        {
            Controls[0].Dispose();
        }

        _cards.Clear();
        _windows.Clear();
        _windows.AddRange(items);
        _anchorX = anchorX;
        _anchorTop = anchorTop;

        if (items.Count == 0)
        {
            Hide();
            return;
        }

        var x = Pad;
        var seen = new HashSet<IntPtr>();
        foreach (var item in items)
        {
            // Per-iteration copy. A deconstructed foreach variable is shared by
            // every closure on some compilers, which would point every card at
            // the last hwnd of this process.
            var hwnd = item.Hwnd;
            var title = item.Title;
            if (!seen.Add(hwnd))
            {
                continue;
            }

            var label = new Label
            {
                AutoEllipsis = true,
                Text = string.IsNullOrWhiteSpace(title) ? "Окно" : title,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(x, NameBand, 120, TitleHeight),
            };
            var closeHwnd = hwnd;
            var close = new GlassButton
            {
                Text = "", // Segoe MDL2 Cancel: centred glyph, unlike the text ×
                Font = new Font("Segoe MDL2 Assets", 8f),
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = Padding.Empty,
                UseCompatibleTextRendering = false,
                Bounds = new Rectangle(x + 100, NameBand + (TitleHeight - 20) / 2, 20, 20),
                ForeColor = Theme.Text2,
                BackColor = Color.Transparent,
                Visible = false,
                Cursor = Cursors.Hand,
                TabStop = false,
                Tag = closeHwnd,
            };
            close.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 17, 35);
            close.FlatAppearance.MouseDownBackColor = Color.FromArgb(180, 12, 28);
            close.MouseEnter += (_, _) => close.ForeColor = Color.White;
            close.MouseLeave += (_, _) => close.ForeColor = Theme.Text2;
            close.Click += (_, _) =>
            {
                if (close.Tag is not IntPtr closing)
                {
                    return;
                }

                WindowClosed?.Invoke(closing);
                var left = _windows.Where(window => window.Hwnd != closing).ToList();
                BeginInvoke(new Action(() => ShowFor(left, _anchorX, _anchorTop, _appName, _bar, _glass)));
            };
            var canvas = new Panel
            {
                Bounds = new Rectangle(x, NameBand + TitleHeight, 140, 88),
                BackColor = Theme.CanvasA,
                Cursor = Cursors.Hand,
                Tag = hwnd,
            };
            BindChoose(label, hwnd);
            Watch(close, closeHwnd);
            BindChoose(canvas, hwnd);
            Controls.Add(label);
            Controls.Add(close);
            Controls.Add(canvas);
            _cards.Add(new PreviewCard(label, close, canvas));
            x += 140 + Gap;
        }

        if (_cards.Count == 0)
        {
            Hide();
            return;
        }

        if (!Visible)
        {
            Show();
        }

        LayoutToImages();
    }

    public void HidePreviews()
    {
        if (_committing)
        {
            return;
        }

        // The pointer left the card onto the window peek is drawing.
        // Keep that target until a click selects it or the linger restores
        // the previous z-order without activating anything.
        if (_peeking && !IsOnPreview(Cursor.Position) && !IsChrome(Cursor.Position))
        {
            _hoverTimer.Stop();
            ArmLinger();
            return;
        }

        Dismiss();
    }

    /// <summary>
    /// Icon activation must not run while the peek sheet is still mapped.
    /// Mouse-leave linger is the wrong delay here: the sheet would still be
    /// the topmost window when the target is raised.
    /// </summary>
    public void CloseForActivate()
    {
        if (_committing)
        {
            return;
        }

        Dismiss();
    }

    public bool IsOwnHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (IsHandleCreated && Handle == hwnd)
        {
            return true;
        }

        return _cover.IsHandleCreated && _cover.Handle == hwnd;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _hoverTimer.Stop();
        _leaveTimer.Stop();
        _lingerTimer.Stop();
        _reassertTimer.Stop();
        EndPeek();
        _hoverTimer.Dispose();
        _leaveTimer.Dispose();
        _lingerTimer.Dispose();
        _reassertTimer.Dispose();
        _cover.Dispose();
        ClearThumbs();
        base.OnFormClosed(e);
    }

    private void BindChoose(Control control, IntPtr hwnd)
    {
        var chosen = hwnd;
        control.Tag = chosen;
        control.Click += (_, _) =>
        {
            if (control.Tag is IntPtr tagged)
            {
                Commit(tagged);
                return;
            }

            Commit(chosen);
        };
        Watch(control, chosen);
    }

    private void Watch(Control control, IntPtr hwnd)
    {
        var watched = hwnd;
        control.MouseEnter += (_, _) => Hover(watched);
        control.MouseLeave += (_, _) =>
        {
            _leaveTimer.Stop();
            _leaveTimer.Start();
        };
    }

    private void Hover(IntPtr hwnd)
    {
        _lingerTimer.Stop();
        if (hwnd == IntPtr.Zero || hwnd == _hovered)
        {
            return;
        }

        _hovered = hwnd;
        if (_peeking)
        {
            BeginPeek(hwnd);
            return;
        }

        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void OnHoverTick()
    {
        _hoverTimer.Stop();
        if (_committing || _hovered == IntPtr.Zero || !IsOnPreview(Cursor.Position))
        {
            return;
        }

        BeginPeek(_hovered);
    }

    private void OnLeaveTick()
    {
        _leaveTimer.Stop();
        if (_committing || (Visible && IsOnPreview(Cursor.Position)))
        {
            return;
        }

        _hoverTimer.Stop();
        if (!_peeking)
        {
            _hovered = IntPtr.Zero;
            return;
        }

        if (IsChrome(Cursor.Position))
        {
            Dismiss();
            return;
        }

        ArmLinger();
    }

    private void OnLingerTick()
    {
        _lingerTimer.Stop();
        if (_committing || (Visible && IsOnPreview(Cursor.Position)))
        {
            return;
        }

        _hovered = IntPtr.Zero;
        Dismiss();
    }

    private void ArmLinger()
    {
        if (!_lingerTimer.Enabled)
        {
            _lingerTimer.Start();
        }
    }

    private void Dismiss()
    {
        _hoverTimer.Stop();
        _leaveTimer.Stop();
        _lingerTimer.Stop();
        _hookEatUp = false;
        _hovered = IntPtr.Zero;
        EndPeek();
        ClearThumbs();
        if (Visible)
        {
            Hide();
        }
    }

    private void Commit(IntPtr hwnd)
    {
        if (_committing || hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return;
        }

        _committing = true;
        _hoverTimer.Stop();
        _leaveTimer.Stop();
        _lingerTimer.Stop();
        _hookEatUp = false;
        _hovered = IntPtr.Zero;
        var chosen = hwnd;
        Console.WriteLine("[spike] preview commit " + chosen.ToInt64().ToString("X"));
        try
        {
            // Drop peek completely, then activate. Ending peek onto the
            // chosen hwnd keeps DWM's cloak/transparency on this build, so
            // the window paints for one frame and is hidden again. A null
            // target restores every window; Activate puts this hwnd in front
            // afterwards.
            EndPeek();
            WindowChosen?.Invoke(chosen);
            ClearThumbs();
            if (Visible)
            {
                Hide();
            }
        }
        finally
        {
            var generation = ++_commitGeneration;
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(() => FinishCommit(chosen, generation)));
            }
            else
            {
                _committing = false;
            }
        }

        _reassertHwnd = chosen;
        _reassertIndex = 0;
        _reassertTimer.Interval = 30;
        _reassertTimer.Start();
    }

    private void FinishCommit(IntPtr hwnd, int generation)
    {
        if (generation != _commitGeneration)
        {
            return;
        }

        if (NativeMethods.IsWindow(hwnd) && !WindowCatalog.IsInFront(hwnd))
        {
            WindowChosen?.Invoke(hwnd);
        }

        _committing = false;
    }

    private void OnReassertTick()
    {
        _reassertTimer.Stop();
        var hwnd = _reassertHwnd;
        _reassertIndex++;
        if (!NativeMethods.IsWindow(hwnd))
        {
            return;
        }

        if (!WindowCatalog.IsInFront(hwnd))
        {
            WindowChosen?.Invoke(hwnd);
        }

        var next = _reassertIndex switch
        {
            1 => 60,
            2 => 110,
            _ => 0,
        };
        if (next > 0 && (_reassertIndex < 2 || !WindowCatalog.IsInFront(hwnd)))
        {
            _reassertTimer.Interval = next;
            _reassertTimer.Start();
        }
    }

    private void OnCoverClick()
    {
        if (_committing || !_peeking)
        {
            return;
        }

        var target = _peekTarget != IntPtr.Zero ? _peekTarget : _hovered;
        Commit(target);
    }

    private bool IsOnPreview(Point screen)
    {
        return Visible && Bounds.Contains(screen);
    }

    private bool IsChrome(Point screen)
    {
        if (IsOnPreview(screen))
        {
            return true;
        }

        try
        {
            if (HitIsChrome?.Invoke(screen) == true)
            {
                return true;
            }
        }
        catch
        {
            // A chrome check must not break the mouse hook.
        }

        var hwnd = NativeMethods.WindowFromPoint(screen);
        if (hwnd == IntPtr.Zero || IsOwnHwnd(hwnd))
        {
            return false;
        }

        if (_cover.IsHandleCreated && hwnd == _cover.Handle)
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
        if (root == IntPtr.Zero)
        {
            root = hwnd;
        }

        if (_cover.IsHandleCreated && root == _cover.Handle)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(root, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private void BeginPeek(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return;
        }

        var hr = DwmpActivateLivePreview(true, hwnd, Handle, 3, IntPtr.Zero);
        if (hr < 0)
        {
            if (!_peekFaultLogged)
            {
                _peekFaultLogged = true;
                Console.WriteLine("[spike] live preview: 0x" + unchecked((uint)hr).ToString("X8"));
            }

            return;
        }

        _peeking = true;
        _peekTarget = hwnd;
        EnsureHook();
        ShowCover();
    }

    private void EndPeek()
    {
        var wasPeeking = _peeking;
        _peeking = false;
        _peekTarget = IntPtr.Zero;
        HideCover();
        ReleaseHook();
        if (!wasPeeking)
        {
            return;
        }

        // A non-null target does not "land" on that window here. DWM keeps
        // the peek session, the other windows stay cloaked or transparent,
        // and the next ShowWindow only lasts one frame. Null hwnds clear it.
        // The caller activates the chosen window after this returns.
        CancelLivePreview();
    }

    private static void CancelLivePreview()
    {
        // Superbar (3) is what BeginPeek arms. Alt-tab (4) is the cancel
        // that actually restores composition on current Windows builds.
        DwmpActivateLivePreview(false, IntPtr.Zero, IntPtr.Zero, 4, IntPtr.Zero);
        DwmpActivateLivePreview(false, IntPtr.Zero, IntPtr.Zero, 3, IntPtr.Zero);
    }

    private void ShowCover()
    {
        var area = SystemInformation.VirtualScreen;
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        if (!_cover.IsHandleCreated)
        {
            _ = _cover.Handle;
        }

        NativeMethods.ExcludeFromPeek(_cover.Handle);
        if (!_cover.ApplyAlpha())
        {
            return;
        }

        if (!_cover.Visible)
        {
            _cover.Show();
        }

        NativeMethods.SetWindowPos(
            _cover.Handle,
            NativeMethods.HwndTopmost,
            area.Left,
            area.Top,
            area.Width,
            area.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        _cover.ApplyAlpha();
        PeekShown?.Invoke();
        if (IsHandleCreated)
        {
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTopmost,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }
    }

    private void HideCover()
    {
        if (!_cover.IsHandleCreated)
        {
            return;
        }

        // SWP_HIDEWINDOW against HWND_TOP leaves a topmost layered window
        // mapped. WinForms then keeps Visible and the sheet stays above
        // every ordinary window until this process exits.
        NativeMethods.ShowWindow(_cover.Handle, NativeMethods.SwHide);
        if (_cover.Visible)
        {
            _cover.Hide();
        }
    }

    private void EnsureHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }

        _mouseProc ??= OnMouseHook;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, IntPtr.Zero, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        }

        if (_mouseHook == IntPtr.Zero && !_hookFaultLogged)
        {
            _hookFaultLogged = true;
            Console.WriteLine("[spike] peek hook: " + Marshal.GetLastWin32Error());
        }
    }

    private void ReleaseHook()
    {
        _hookEatUp = false;
        if (_mouseHook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _peeking && !_committing && lParam != IntPtr.Zero)
            {
                var msg = wParam.ToInt32();
                if (msg is WmLButtonDown or WmLButtonUp or WmLButtonDblClk)
                {
                    var info = Marshal.PtrToStructure<LowLevelMouse>(lParam);
                    var point = new Point(info.X, info.Y);
                    var onChrome = IsChrome(point);
                    if (msg == WmLButtonUp)
                    {
                        var eat = _hookEatUp && !onChrome;
                        _hookEatUp = false;
                        if (eat)
                        {
                            var target = _peekTarget != IntPtr.Zero ? _peekTarget : _hovered;
                            if (target != IntPtr.Zero && IsHandleCreated && !IsDisposed)
                            {
                                BeginInvoke(new Action(() => Commit(target)));
                            }

                            return (IntPtr)1;
                        }
                    }
                    else if (!onChrome)
                    {
                        _hookEatUp = true;
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch
        {
            // A throwing low-level hook stalls the mouse.
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void LayoutToImages()
    {
        var x = Pad;
        var bottom = NameBand + TitleHeight;
        foreach (var card in _cards)
        {
            var size = new Size(160, 96);
            if (card.Canvas.Tag is IntPtr hwnd &&
                DwmRegisterThumbnail(Handle, hwnd, out var thumb) == 0 &&
                thumb != IntPtr.Zero)
            {
                card.Thumb = thumb;
                _thumbs.Add(thumb);
                if (DwmQueryThumbnailSourceSize(thumb, out var source) == 0)
                {
                    size = FrameSize(source.Width, source.Height);
                }
            }

            var width = size.Width;
            card.Title.Bounds = new Rectangle(x, NameBand, Math.Max(0, width - 22), TitleHeight);
            card.Close.Bounds = new Rectangle(x + width - 20, NameBand + (TitleHeight - 20) / 2, 20, 20);
            card.Canvas.SetBounds(x, NameBand + TitleHeight, width, size.Height);
            if (card.Thumb != IntPtr.Zero)
            {
                var props = new ThumbnailProperties
                {
                    Flags = DwmTnpRectDestination | DwmTnpVisible | DwmTnpOpacity,
                    Destination = ToRect(card.Canvas.Bounds),
                    Opacity = 255,
                    Visible = true,
                };
                DwmUpdateThumbnailProperties(card.Thumb, ref props);
            }

            x += width + Gap;
            bottom = Math.Max(bottom, card.Canvas.Bottom);
        }

        ClientSize = new Size(Math.Max(Pad * 2, x - Gap + Pad), bottom + Pad);
        Place();
    }

    private static Size FrameSize(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return new Size(160, 96);
        }

        var scale = MaxEdge / (double)Math.Max(sourceWidth, sourceHeight);
        return new Size(
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    private void Place()
    {
        var screen = Screen.FromPoint(new Point(_anchorX, _anchorTop)).WorkingArea;
        var left = Math.Clamp(_anchorX - Width / 2, screen.Left + 8, Math.Max(screen.Left + 8, screen.Right - Width - 8));
        var top = _anchorTop - Height - 8;
        if (top < screen.Top + 8)
        {
            top = _anchorTop + 8;
        }

        Location = new Point(left, top);
    }

    private void ClearThumbs()
    {
        foreach (var thumb in _thumbs)
        {
            DwmUnregisterThumbnail(thumb);
        }

        _thumbs.Clear();
    }

    private sealed class PreviewCard
    {
        public PreviewCard(Label title, Button close, Panel canvas)
        {
            Title = title;
            Close = close;
            Canvas = canvas;
        }

        public Label Title { get; }
        public Button Close { get; }
        public Panel Canvas { get; }
        public IntPtr Thumb { get; set; }
    }

    private static NativeMethods.Rect ToRect(Rectangle rectangle)
    {
        return new NativeMethods.Rect
        {
            Left = rectangle.Left,
            Top = rectangle.Top,
            Right = rectangle.Right,
            Bottom = rectangle.Bottom,
        };
    }

    [DllImport("dwmapi.dll", EntryPoint = "#113", CallingConvention = CallingConvention.StdCall)]
    private static extern int DwmpActivateLivePreview(
        [MarshalAs(UnmanagedType.Bool)] bool activate,
        IntPtr target,
        IntPtr spare,
        uint trigger,
        IntPtr unused);

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbnailProperties props);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out ThumbnailSize size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint Flags;
        public NativeMethods.Rect Destination;
        public NativeMethods.Rect Source;
        public byte Opacity;
        public bool Visible;
        public bool SourceClientAreaOnly;
    }

    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouse
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>
    /// Almost invisible topmost sheet. Aero Peek only repaints the target;
    /// the real hit-test still lands on whatever was underneath. This sheet
    /// takes that click. Alpha 0 would pass the click through.
    /// </summary>
    private sealed class PeekCover : Form
    {
        public event Action? Clicked;

        public PeekCover()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            Text = string.Empty;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int wsExLayered = 0x00080000;
                const int wsExToolWindow = 0x00000080;
                const int wsExTopmost = 0x00000008;
                const int wsExNoActivate = 0x08000000;
                var cp = base.CreateParams;
                cp.ExStyle |= wsExLayered | wsExToolWindow | wsExTopmost | wsExNoActivate;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // WinForms reapplies this on every Show, so the sheet cannot come
            // back opaque and cover the desktop after peek ends.
            Opacity = 1d / 255d;
            NativeMethods.ExcludeFromPeek(Handle);
            ApplyAlpha();
        }

        public bool ApplyAlpha()
        {
            return IsHandleCreated && SetLayeredWindowAttributes(Handle, 0, 1, LwaAlpha);
        }

        protected override void WndProc(ref Message m)
        {
            const int wmMouseActivate = 0x0021;
            const int maNoActivate = 3;
            if (m.Msg == wmMouseActivate)
            {
                m.Result = (IntPtr)maNoActivate;
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                Capture = true;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            Capture = false;
            Clicked?.Invoke();
        }

        private const uint LwaAlpha = 0x2;

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    }
}
