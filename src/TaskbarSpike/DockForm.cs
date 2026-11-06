using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TaskbarSpike;

internal sealed class DockForm : Form
{
    private readonly bool _useAppBar;
    private readonly WebView2 _webView;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _rehideTimer;
    private readonly System.Windows.Forms.Timer _previewHideTimer;
    private readonly PreviewForm _preview;
    private readonly TrayMenuForm _trayMenu;
    private readonly QuickForm _quick; // legacy WinForms panel, superseded by _popup; delete after acceptance
    private readonly PopupHost _popup;
    private readonly SearchForm _search;
    private readonly StartForm _start;
    private readonly ShowDesktopForm _desktop;
    private readonly TipForm _tip;
    private readonly JumpListMenuForm _jumpList;
    private List<(double X, double Y, double W, double H, double R)>? _layoutRects;
    private bool _glass = true;
    private readonly System.Windows.Forms.Timer _menuTimer;
    private readonly HashSet<IntPtr> _menuBefore = new();
    private int _menuTicks;
    private long _menuAt;
    private string _mode = "dock";
    private int _trayCount;
    private bool _appBarRegistered;
    private uint _appBarCallback;
    private bool _hotkeyRegistered;
    private uint _shellHook;
    private bool _shellHookRegistered;
    private bool _shuttingDown;
    private bool _placing;
    private readonly System.Windows.Forms.Timer _glassSettle;
    private string? _previewForId;
    private long _previewShownAt;

    public DockForm(bool useAppBar)
    {
        AccentWatch.Prime();
        _useAppBar = useAppBar;
        if (_useAppBar)
        {
            _mode = "bar";
        }

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        // One material only: DWM acrylic on this window (PopupChrome.EnableAcrylic),
        // a transparent WebView2 on top that paints just the sheen, the 1px edge and
        // the content, and a window region cut to the islands. The form must stay a
        // plain, non-layered window — a layered/color-keyed one turns the
        // semi-transparent web content click-through.
        BackColor = Color.Black;
        // Never paint the client area: anything GDI draws (a white class-brush erase after
        // a resize, for one) sits opaque on top of the acrylic and shows as a solid fill.
        SetStyle(ControlStyles.Opaque, true);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "TaskbarSpike";

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.Transparent,
        };
        Controls.Add(_webView);

        _preview = new PreviewForm();
        _popup = new PopupHost { IsDockPoint = IsDockIslandPoint };
        _preview.Hidden += () => _previewForId = null;
        // Commit ends peek before this runs, then asks for this hwnd.
        _preview.WindowChosen += WindowCatalog.Activate;
        _preview.WindowClosed += hwnd => NativeMethods.PostMessage(hwnd, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
        _preview.HitIsChrome = point =>
            Bounds.Contains(point)
            || FormContains(_desktop, point)
            || FormContains(_trayMenu, point)
            || FormContains(_jumpList, point)
            || FormContains(_quick, point)
            || _popup.ContainsPoint(point)
            || FormContains(_search, point)
            || FormContains(_start, point);
        _preview.PeekShown += RaiseAbovePeek;
        _trayMenu = new TrayMenuForm();
        _trayMenu.ModeRequested += () => BeginInvoke(new Action(() => SetMode(_mode == "bar" ? "dock" : "bar")));
        _trayMenu.ExitRequested += () => BeginInvoke(new Action(RequestExit));
        _trayMenu.AppMenuRequested += ShowAppMenu;
        _desktop = new ShowDesktopForm();
        _tip = new TipForm();
        _jumpList = new JumpListMenuForm { IsOverDock = IsDockIslandPoint };
        _jumpList.ClosedByUser += () => BeginInvoke(new Action(() =>
        {
            _popup.HoldOpen = false;
            SetOwnTopMost(true);
        }));
        _jumpList.Changed += () => BeginInvoke(new Action(PublishSnapshot));
        QuickSettings.Changed += () =>
        {
            if (!_shuttingDown && IsHandleCreated)
            {
                BeginInvoke(new Action(PublishSnapshot));
            }
        };
        _menuTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _menuTimer.Tick += (_, _) => OnMenuTick();
        _quick = new QuickForm();
        _quick.Dismissed += () =>
        {
            if (!_shuttingDown)
            {
                PublishSnapshot();
            }
        };
        _popup.Dismissed += () =>
        {
            if (!_shuttingDown)
            {
                PublishSnapshot();
            }
        };
        _popup.ModeRequested += () => BeginInvoke(new Action(() => SetMode(_mode == "bar" ? "dock" : "bar")));
        _popup.ExitRequested += () => BeginInvoke(new Action(RequestExit));
        _popup.TrayContextRequested += button => BeginInvoke(new Action(() => ShowAppMenu(button)));
        _search = new SearchForm();
        _search.Dismissed += () =>
        {
            if (!_shuttingDown)
            {
                PublishSnapshot();
            }
        };
        _start = new StartForm();
        _start.Dismissed += () =>
        {
            if (!_shuttingDown)
            {
                PublishSnapshot();
            }
        };

        _previewHideTimer = new System.Windows.Forms.Timer { Interval = 280 };
        _previewHideTimer.Tick += (_, _) =>
        {
            if (_preview.Visible && _preview.Bounds.Contains(Cursor.Position))
            {
                return;
            }

            _previewHideTimer.Stop();
            HidePreviews();
        };

        _pollTimer = new System.Windows.Forms.Timer { Interval = 800 };
        _pollTimer.Tick += (_, _) => PublishSnapshot();

        _glassSettle = new System.Windows.Forms.Timer { Interval = 160 };
        _glassSettle.Tick += (_, _) =>
        {
            _glassSettle.Stop();
            RebuildGlass();
            _webView.Invalidate();
        };

        _rehideTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _rehideTimer.Tick += (_, _) =>
        {
            var hadTray = !TaskbarGuard.PrimaryTrayBounds.IsEmpty;
            TaskbarGuard.Hide();
            if (!hadTray && !TaskbarGuard.PrimaryTrayBounds.IsEmpty)
            {
                ApplyPlacement();
            }

            RefreshGlass(false);
        };

        Load += async (_, _) =>
        {
            try
            {
                await InitWebAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[spike] WebView2 failed: {ex}");
            }
        };

        Theme.Changed += OnThemeChanged;
        ApplyPlacement();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExTopmost = 0x00000008;
            var cp = base.CreateParams;
            // No WS_EX_LAYERED here — see the ctor comment. The window region
            // (ApplyRegion) cuts the shape; transparency comes from
            // the WebView2 itself.
            cp.ExStyle |= wsExToolWindow | wsExTopmost;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AccentWatch.Attach(this);
        _glass = PopupChrome.TransparencyAvailable();
        ClearSystemChrome();
        ApplyGlass();
        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            Handle,
            NativeMethods.HotkeyExitId,
            NativeMethods.ModWin | NativeMethods.ModAlt | NativeMethods.ModNoRepeat,
            NativeMethods.VkBack);
        if (!_hotkeyRegistered)
        {
            Console.WriteLine("[spike] Win+Alt+Backspace was not registered. Use the dock button or close the console.");
        }

        // Alt+Shift and Win+Space already belong to Windows. The shell tells us the
        // moment the layout or the foreground window changes, so the label is not
        // waiting for the window poll.
        _shellHook = NativeMethods.RegisterWindowMessage("SHELLHOOK");
        _shellHookRegistered = _shellHook != 0 && NativeMethods.RegisterShellHookWindow(Handle);
        if (!_shellHookRegistered)
        {
            Console.WriteLine("[spike] language indicator is following the window poll.");
        }

        if (_useAppBar)
        {
            RegisterAppBar();
        }

        ApplyPlacement();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyPlacement();
        _rehideTimer.Start();
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        SyncDesktopStrip();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _shuttingDown = true;
        _pollTimer.Stop();
        _rehideTimer.Stop();
        _previewHideTimer.Stop();
        _menuTimer.Stop();
        HidePreviews();
        _trayMenu.Hide();
        _jumpList.Hide();
        _desktop.Hide();
        _tip.Hide();
        _quick.Hide();
        _popup.Shutdown();
        _search.Hide();
        _start.Hide();
        if (_hotkeyRegistered && IsHandleCreated)
        {
            NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyExitId);
            _hotkeyRegistered = false;
        }

        if (_shellHookRegistered && IsHandleCreated)
        {
            NativeMethods.DeregisterShellHookWindow(Handle);
            _shellHookRegistered = false;
        }

        RemoveAppBar();
        TaskbarGuard.Restore();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pollTimer.Dispose();
        _rehideTimer.Dispose();
        _previewHideTimer.Dispose();
        _menuTimer.Dispose();
        Theme.Changed -= OnThemeChanged;
        AccentWatch.Stop();
        _preview.Dispose();
        _trayMenu.Dispose();
        _jumpList.Dispose();
        _desktop.Dispose();
        _tip.Dispose();
        _quick.Dispose();
        _popup.Dispose();
        _search.Dispose();
        _start.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // intentionally empty — see ctor
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0014) // WM_ERASEBKGND
        {
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == NativeMethods.WmHotkey && m.WParam.ToInt32() == NativeMethods.HotkeyExitId)
        {
            RequestExit();
            return;
        }

        if (m.Msg == NativeMethods.WmSettingChange &&
            string.Equals(Marshal.PtrToStringUni(m.LParam), "ImmersiveColorSet", StringComparison.Ordinal))
        {
            AccentWatch.Notify();
        }

        if (_shellHookRegistered && m.Msg == unchecked((int)_shellHook))
        {
            var code = m.WParam.ToInt32() & ~NativeMethods.HsHighBit;
            if (code == NativeMethods.HsLanguage || code == NativeMethods.HsWindowActivated)
            {
                PushLanguage();
            }
        }

        if (_appBarCallback != 0 && m.Msg == unchecked((int)_appBarCallback))
        {
            ApplyPlacement();
        }

        base.WndProc(ref m);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyPlacement();
    }

    private void HidePreviews()
    {
        _preview.HidePreviews();
        if (!_tip.IsDisposed)
        {
            _tip.Hide();
        }
    }

    private void RequestExit()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        TaskbarGuard.Restore();
        Close();
    }

    private async Task InitWebAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "TaskbarSpike_WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        _ = InitPopupAsync(environment);
        _webView.DefaultBackgroundColor = Color.Transparent;
        await _webView.EnsureCoreWebView2Async(environment);
        _webView.DefaultBackgroundColor = Color.Transparent;

        var core = _webView.CoreWebView2;
        if (core == null)
        {
            return;
        }

        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(
            "taskbar.spike",
            wwwroot,
            CoreWebView2HostResourceAccessKind.Allow);
        core.NavigationStarting += (_, eventArgs) =>
        {
            if (eventArgs.Uri != null &&
                !eventArgs.Uri.StartsWith("https://taskbar.spike/", StringComparison.OrdinalIgnoreCase))
            {
                eventArgs.Cancel = true;
            }
        };
        core.WebMessageReceived += OnWebMessage;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.__spikeAccent = " + JsonSerializer.Serialize(Theme.AccentHex) + ";");
        core.NavigationCompleted += (_, _) =>
        {
            if (_shuttingDown)
            {
                return;
            }

            _pollTimer.Start();
            PostTheme();
            PostGlass();
            PublishSnapshot();
        };
        core.Navigate("https://taskbar.spike/index.html");
    }

    /// <summary>The popup window is warmed up once, next to the dock page, on the same browser environment.</summary>
    private async Task InitPopupAsync(CoreWebView2Environment environment)
    {
        try
        {
            await _popup.InitAsync(environment);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] popup WebView2 failed: {ex}");
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty))
            {
                return;
            }

            switch (typeProperty.GetString())
            {
                case "exit":
                    BeginInvoke(new Action(RequestExit));
                    break;
                case "layout":
                    OnLayoutMessage(root);
                    break;
                case "desks-view":
                    var deskAnchor = root.TryGetProperty("anchor", out var deskAnchorProperty) && deskAnchorProperty.TryGetDouble(out var deskAnchorValue)
                        ? deskAnchorValue
                        : 0.08;
                    BeginInvoke(new Action(() => ShowDesks(deskAnchor)));
                    break;
                case "desktop-toggle":
                    BeginInvoke(new Action(() =>
                    {
                        HidePreviews();
                        _trayMenu.Hide();
                        _quick.Hide();
                        _popup.Dismiss();
                        _search.Hide();
                        _start.Hide();
                        DesktopToggle.Toggle();
                        PublishSnapshot();
                    }));
                    break;
                case "reorder" when root.TryGetProperty("ids", out var orderProperty) && orderProperty.ValueKind == JsonValueKind.Array:
                    var order = new List<string>();
                    foreach (var item in orderProperty.EnumerateArray())
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value) && value.Length == 16)
                        {
                            order.Add(value);
                        }
                    }

                    WindowCatalog.SetVisibleOrder(order);
                    PublishSnapshot();
                    break;
                case "desktop" when root.TryGetProperty("index", out var deskProperty) && deskProperty.TryGetInt32(out var deskIndex):
                    VirtualDesktops.Switch(deskIndex);
                    PublishSnapshot();
                    break;
                case "desktop-add":
                    VirtualDesktops.Create();
                    PublishSnapshot();
                    break;
                case "desktop-close" when root.TryGetProperty("index", out var closeProperty) && closeProperty.TryGetInt32(out var closeIndex):
                    VirtualDesktops.Close(closeIndex);
                    PublishSnapshot();
                    break;
                case "app" when root.TryGetProperty("id", out var idProperty):
                    var id = idProperty.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        _tip.Hide();
                        _jumpList.Hide();
                        _preview.CloseForActivate();
                        _quick.Hide();
                        _popup.Dismiss();
                        _search.Hide();
                        _start.Hide();
                        WindowCatalog.ActivateApp(id);
                    }

                    break;
                case "app-context" when root.TryGetProperty("id", out var appContextProperty):
                    var appContextId = appContextProperty.GetString();
                    var appContextAnchor = root.TryGetProperty("anchor", out var appContextAnchorProperty) &&
                                          appContextAnchorProperty.TryGetDouble(out var appContextAnchorValue)
                        ? appContextAnchorValue
                        : 0.5;
                    if (!string.IsNullOrWhiteSpace(appContextId))
                    {
                        BeginInvoke(new Action(() => ShowJumpList(appContextId, appContextAnchor)));
                    }

                    break;
                case "preview" when root.TryGetProperty("id", out var previewIdProperty):
                    var previewId = previewIdProperty.GetString();
                    var anchor = root.TryGetProperty("anchor", out var anchorProperty) && anchorProperty.TryGetDouble(out var anchorValue)
                        ? anchorValue
                        : 0.5;
                    var previewName = root.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? string.Empty : string.Empty;
                    var previewTop = root.TryGetProperty("top", out var topProperty) && topProperty.TryGetDouble(out var topValue) ? topValue : 0;
                    if (!string.IsNullOrWhiteSpace(previewId))
                    {
                        BeginInvoke(new Action(() => ShowPreview(previewId, anchor, previewName, previewTop)));
                    }

                    break;
                case "preview-hide":
                    _tip.Hide();
                    _previewHideTimer.Stop();
                    _previewHideTimer.Start();
                    break;
                case "tray-context" when root.TryGetProperty("id", out var trayContextProperty):
                    var trayContextId = trayContextProperty.GetString();
                    if (!string.IsNullOrWhiteSpace(trayContextId))
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (TrayCatalog.TryGet(trayContextId, out var trayButton) && trayButton != null)
                            {
                                ShowAppMenu(trayButton);
                            }
                        }));
                    }

                    break;
                case "tray" when root.TryGetProperty("id", out var trayIdProperty):
                    var trayId = trayIdProperty.GetString();
                    var trayAnchor = root.TryGetProperty("anchor", out var trayAnchorProperty) && trayAnchorProperty.TryGetDouble(out var trayAnchorValue)
                        ? trayAnchorValue
                        : 0.5;
                    if (!string.IsNullOrWhiteSpace(trayId))
                    {
                        BeginInvoke(new Action(() => ShowTrayMenu(trayId, trayAnchor)));
                    }

                    break;
                case "lang":
                    BeginInvoke(new Action(() =>
                    {
                        HidePreviews();
                        _trayMenu.Hide();
                        _quick.Hide();
                        _popup.Dismiss();
                        _search.Hide();
                        _start.Hide();
                        InputLayout.Cycle();
                        PublishSnapshot();
                    }));
                    break;
                case "quick":
                    var quickAnchor = root.TryGetProperty("anchor", out var quickAnchorProperty) && quickAnchorProperty.TryGetDouble(out var quickAnchorValue)
                        ? quickAnchorValue
                        : 0.5;
                    BeginInvoke(new Action(() => ShowQuick(quickAnchor)));
                    break;
                case "search":
                    var hasQuery = root.TryGetProperty("q", out var searchQuery);
                    var typed = hasQuery ? searchQuery.GetString() ?? "" : null;
                    // No "q": the dock search button opens Start. With "q": the Win10 box is typing.
                    BeginInvoke(new Action(() => ShowStart(toggle: false, typed)));
                    break;
                case "search-blur":
                    BeginInvoke(new Action(() =>
                    {
                        if (_mode != "bar" || !_popup.IsOpen("start"))
                        {
                            return;
                        }

                        if (_popup.ContainsPoint(Cursor.Position))
                        {
                            return;
                        }

                        _popup.Dismiss();
                        PublishSnapshot();
                    }));
                    break;
                case "search-close":
                    BeginInvoke(new Action(() =>
                    {
                        _popup.SetSearch("");
                        ClearBarSearch();
                        if (_popup.IsOpen("start"))
                        {
                            _popup.Dismiss();
                        }

                        PublishSnapshot();
                    }));
                    break;
                case "start":
                    BeginInvoke(new Action(() => ShowStart(toggle: true, null)));
                    break;
                case "clock":
                    BeginInvoke(new Action(ShowClock));
                    break;
                case "mode" when root.TryGetProperty("value", out var modeProperty):
                    var mode = modeProperty.GetString();
                    if (mode is "dock" or "bar")
                    {
                        BeginInvoke(new Action(() => SetMode(mode)));
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] web message: {ex.Message}");
        }
    }

    private void ShowQuick(double anchor)
    {
        HidePreviews();
        _trayMenu.Hide();
        _jumpList.Hide();
        _quick.Hide();
        _search.Hide();
        _start.Hide();
        if (_popup.IsOpen("quick"))
        {
            _popup.Dismiss();
            PublishSnapshot();
            return;
        }

        // Network and sound sit on the right edge in both modes (HANDOFF 3.4, 4).
        _popup.Open("quick", PopupPlacementFor("right", 0), _mode, _glass);
    }

    /// <summary>Panel lower edge: 12 DIP above the islands (dock) or flush with the bar; right edge lines up with the rightmost island.</summary>
    private PopupPlacement PopupPlacementFor(string anchor, int anchorXPx, bool lifted = false)
    {
        var scale = DpiScale;
        var topY = 0.0;
        var rightX = (double)Width / scale;
        if (_layoutRects is { Count: > 0 } rects)
        {
            topY = rects.Min(rect => rect.Y);
            rightX = rects.Max(rect => rect.X + rect.W);
        }

        // Dock: 12 DIP above the islands. Bar: flush, except the overflow menu and task view, which sit 8 DIP up.
        var gap = _mode == "bar"
            ? (lifted ? (int)Math.Round(8 * scale) : 0)
            : (int)Math.Round(12 * scale);
        var bottom = Top + (int)Math.Round(topY * scale) - gap;
        var margin = Math.Max(0, ScreenBounds.Right - (Left + (int)Math.Round(rightX * scale)));
        return new PopupPlacement(anchor, anchorXPx, bottom, margin);
    }

    /// <summary>True over a dock island (not the transparent gaps): a click there toggles a panel instead of dismissing it.</summary>
    private bool IsDockIslandPoint(Point screenPoint)
    {
        if (!Bounds.Contains(screenPoint))
        {
            return false;
        }

        if (_layoutRects == null)
        {
            return true;
        }

        var scale = DpiScale;
        var x = (screenPoint.X - Left) / scale;
        var y = (screenPoint.Y - Top) / scale;
        return _layoutRects.Any(rect => x >= rect.X && x <= rect.X + rect.W && y >= rect.Y && y <= rect.Y + rect.H);
    }

    private void ShowSearch(double anchor)
    {
        HidePreviews();
        _trayMenu.Hide();
        _jumpList.Hide();
        _quick.Hide();
        _popup.Dismiss();
        _start.Hide();
        if (_search.Visible)
        {
            _search.Hide();
            PublishSnapshot();
            return;
        }

        var anchorX = Left + (int)(Math.Clamp(anchor, 0, 1) * Width);
        _search.ShowFor(anchorX, Top, Bounds);
        PublishSnapshot();
    }

    /// <summary>
    /// Start lives in <see cref="PopupHost"/>. The button toggles it. The dock search button and the
    /// Win10 search box only open it; a query (including empty, from the box) fills results.
    /// Dock: centred on the middle island, and the panel takes focus. Bar: flush left, and the box keeps focus.
    /// </summary>
    private void ShowStart(bool toggle, string? query)
    {
        HidePreviews();
        _trayMenu.Hide();
        _jumpList.Hide();
        _quick.Hide();
        _search.Hide();
        _start.Hide();
        if (toggle && _popup.IsOpen("start"))
        {
            _popup.Dismiss();
            _popup.SetSearch("");
            ClearBarSearch();
            PublishSnapshot();
            return;
        }

        if (query == null)
        {
            _popup.SetSearch("");
            ClearBarSearch();
        }
        else
        {
            _popup.SetSearch(query);
        }

        if (_popup.IsOpen("start"))
        {
            PublishSnapshot();
            return;
        }

        var placement = _mode == "bar"
            ? PopupPlacementFor("left", 0)
            : PopupPlacementFor("center", CenterIslandScreenX());
        // The Win10 box sends the query and must keep the caret. Everything else focuses the panel.
        var activate = query == null || _mode != "bar";
        _popup.Open("start", placement, _mode, _glass, activate);
    }

    private int CenterIslandScreenX()
    {
        var scale = DpiScale;
        if (_layoutRects == null || _layoutRects.Count == 0)
        {
            return Left + (Width / 2);
        }

        var mid = Width / 2.0;
        var best = _layoutRects[0];
        var bestDist = double.MaxValue;
        foreach (var rect in _layoutRects)
        {
            var center = (rect.X + (rect.W / 2.0)) * scale;
            var dist = Math.Abs(center - mid);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = rect;
            }
        }

        return Left + (int)Math.Round((best.X + (best.W / 2.0)) * scale);
    }

    private void ClearBarSearch()
    {
        var core = _webView.CoreWebView2;
        if (core == null || _shuttingDown)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "search-clear" }));
        }
        catch
        {
            // The page may be navigating or the control is closing.
        }
    }

    private void ShowClock()
    {
        HidePreviews();
        _trayMenu.Hide();
        _quick.Hide();
        _search.Hide();
        _start.Hide();
        if (_popup.IsOpen("clock"))
        {
            _popup.Dismiss();
            PublishSnapshot();
            return;
        }

        _popup.Open("clock", PopupPlacementFor("right", 0), _mode, _glass);
    }

    private void ShowDesks(double anchor)
    {
        HidePreviews();
        _trayMenu.Hide();
        _quick.Hide();
        _search.Hide();
        _start.Hide();
        if (_popup.IsOpen("desks"))
        {
            _popup.Dismiss();
            PublishSnapshot();
            return;
        }

        var anchorX = Left + (int)(Math.Clamp(anchor, 0, 1) * Width);
        _popup.Open("desks", PopupPlacementFor("center", anchorX, lifted: true), _mode, _glass);
    }

    private void SetMode(string mode)
    {
        if (_mode != mode)
        {
            _mode = mode;
            // Old island geometry is wrong for the new mode; stay cut to nothing
            // until the page reports the new layout.
            _layoutRects = null;
        }

        HidePreviews();
        _quick.Hide();
        _jumpList.Hide();
        _popup.Dismiss();
        _search.Hide();
        _start.Hide();
        ApplyPlacement();
        RebuildGlass();
        _glassSettle.Stop();
        _glassSettle.Start();
        PublishSnapshot();
    }

    /// <summary>A resized window gets a fresh surface; without this the islands come back opaque white.</summary>
    private void RebuildGlass()
    {
        if (!IsHandleCreated || _shuttingDown)
        {
            return;
        }

        PopupChrome.DisableAccent(Handle);
        ClearSystemChrome();
        ApplyGlass();
    }

    private void ShowTrayMenu(string id, double anchor)
    {
        HidePreviews();
        _quick.Hide();
        _search.Hide();
        _start.Hide();
        if (id != TrayCatalog.OverflowId)
        {
            _popup.Dismiss();
            _trayMenu.Hide();
            TrayCatalog.Open(id);
            return;
        }

        if (_popup.IsOpen("tray"))
        {
            _popup.Dismiss();
            PublishSnapshot();
            return;
        }

        _trayMenu.Hide();
        var anchorX = Left + (int)(Math.Clamp(anchor, 0, 1) * Width);
        _popup.Open("tray", PopupPlacementFor("center", anchorX, lifted: true), _mode, _glass);
    }

    private void SyncDesktopStrip()
    {
        // The show-desktop strip is part of the page now (HANDOFF 4, item 12).
        _desktop.Hide();
    }

    private void ShowTip(string name, double anchor, double top)
    {
        if (string.IsNullOrWhiteSpace(name) || _shuttingDown)
        {
            return;
        }

        var scale = DeviceDpi / 96.0;
        var centerX = Left + (int)Math.Round(Math.Clamp(anchor, 0, 1) * Width);
        var iconTop = Top + (int)Math.Round(top * scale);
        _tip.ShowAbove(name, centerX, iconTop, _glass);
    }

    private void ShowPreview(string id, double anchor, string name, double top)
    {
        // Keep the pending hide timer stopped before the early-out below: a
        // re-requested hover must cancel the hide even when the preview
        // itself is not rebuilt.
        _previewHideTimer.Stop();

        // The webview can re-enter the same icon without the pointer ever
        // leaving (snapshot re-render, focus changes). Rebuilding an
        // identical preview made the card flash on every poll tick.
        if (_preview.Visible &&
            string.Equals(_previewForId, id, StringComparison.OrdinalIgnoreCase) &&
            Environment.TickCount64 - _previewShownAt < 1200)
        {
            return;
        }

        if (!WindowCatalog.TryGetApp(id, out var app) || app == null || app.Hwnds.Count < 1)
        {
            // No windows to preview: just the name plate over the icon.
            _preview.HidePreviews();
            ShowTip(string.IsNullOrWhiteSpace(name) ? app?.Name ?? string.Empty : name, anchor, top);
            return;
        }

        _tip.Hide();

        var windows = new List<(IntPtr Hwnd, string Title)>(app.Hwnds.Count);
        for (var i = 0; i < app.Hwnds.Count; i++)
        {
            var title = i < app.Titles.Count ? app.Titles[i] : app.Name;
            windows.Add((new IntPtr(app.Hwnds[i]), title));
        }

        _trayMenu.Hide();
        _quick.Hide();
        _popup.Dismiss();
        _search.Hide();
        _start.Hide();
        var anchorX = Left + (int)(Math.Clamp(anchor, 0, 1) * Width);
        var caption = string.IsNullOrWhiteSpace(name) ? app.Name : name;
        _preview.ShowFor(windows, anchorX, Top, caption, _mode == "bar", _glass);
        _previewForId = id;
        _previewShownAt = Environment.TickCount64;
    }

    private void PushLanguage()
    {
        if (_shuttingDown || !InputLayout.Observe())
        {
            return;
        }

        var core = _webView.CoreWebView2;
        if (core == null)
        {
            return;
        }

        var label = InputLayout.Label;
        if (label.Length == 0)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "language", language = label }));
        }
        catch
        {
            // The page may be navigating or the control is closing.
        }
    }

    private void PublishSnapshot()
    {
        var core = _webView.CoreWebView2;
        if (core == null || _shuttingDown)
        {
            return;
        }

        InputLayout.Observe();
        QuickSettings.RefreshLocal();
        var network = NetworkStatus.Read();
        var audio = AudioDevices.Icon();
        if (_quick.Visible)
        {
            _quick.Reload();
        }

        var apps = WindowCatalog.Snapshot(Handle);
        var tray = TrayCatalog.Snapshot();
        _trayCount = tray.Count;

        var payload = new
        {
            type = "snapshot",
            mode = _mode,
            language = InputLayout.Label,
            desktops = VirtualDesktops.List().Select(desk => new
            {
                index = desk.Index,
                name = desk.Name,
                current = desk.Current,
            }),
            dnd = QuickSettings.State.Dnd,
            quick = _quick.Visible || _popup.IsOpen("quick"),
            clock = _popup.IsOpen("clock"),
            trayOpen = _popup.IsOpen("tray"),
            deskOpen = _popup.IsOpen("desks"),
            search = false,
            start = _popup.IsOpen("start"),
            network = new
            {
                kind = network.Kind,
                name = network.Name,
                status = network.Status,
                connected = network.Connected,
            },
            audio = new
            {
                device = audio.Device,
                volume = audio.Volume,
                muted = audio.Muted,
            },
            apps = apps.Select(app => new
            {
                id = app.Id,
                name = app.Name,
                icon = app.IconDataUrl,
                pinned = app.Pinned,
                running = app.Running,
                active = app.Active,
                count = app.Hwnds.Count,
                badge = app.Badge,
                titles = app.Titles,
            }),
            tray = tray.Select(icon => new
            {
                id = icon.Id,
                name = icon.Name,
                icon = icon.IconDataUrl,
                overflow = icon.Overflow,
            }),
        };

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch
        {
            // The page may be navigating or the control is closing.
        }
    }

    private double DpiScale => Math.Max(1.0, DeviceDpi / 96.0);

    private Rectangle ScreenBounds => Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

    /// <summary>Window height: dock = 52 DIP, bar = 48 DIP (the hidden stock taskbar keeps reserving 48, a shorter bar leaves a gap).</summary>
    private int WindowHeightPx => (int)Math.Round(52 * DpiScale);

    /// <summary>Height reserved at the screen edge when running as a real appbar.</summary>
    private int ReservedHeightPx => (int)Math.Round((_mode == "bar" ? 48 : 52) * DpiScale);

    private void ApplyPlacement()
    {
        if (_placing || _shuttingDown)
        {
            return;
        }

        _placing = true;
        try
        {
            var bounds = _useAppBar && _appBarRegistered
                ? ReserveAppBar(ReservedHeightPx)
                : ComputeFloatingBounds();
            if (bounds.Width > 0 && bounds.Height > 0 && Bounds != bounds)
            {
                if (IsHandleCreated && Visible)
                {
                    // Drop the old surface bits: a plain resize leaves the new area as an opaque white
                    // class-brush fill that the islands then show through their region.
                    NativeMethods.SetWindowPos(
                        Handle,
                        IntPtr.Zero,
                        bounds.X,
                        bounds.Y,
                        bounds.Width,
                        bounds.Height,
                        NativeMethods.SwpNoCopyBits | NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
                    RebuildGlass();
                    _glassSettle.Stop();
                    _glassSettle.Start();
                }

                if (Bounds != bounds)
                {
                    Bounds = bounds;
                }
            }

            if (IsHandleCreated)
            {
                ApplyRegion();
                ClearSystemChrome();
            }

            SyncDesktopStrip();
        }
        finally
        {
            _placing = false;
        }
    }

    /// <summary>Full-width strip on the primary screen's bottom edge; the islands are cut out by the region.</summary>
    private Rectangle ComputeFloatingBounds()
    {
        var screen = ScreenBounds;
        var height = WindowHeightPx;
        return new Rectangle(screen.Left, screen.Bottom - height, screen.Width, height);
    }

    private void ClearSystemChrome()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        // DWM rounding, the system border and the extended frame would each paint their
        // own edge. The only fill is the acrylic set in ApplyGlass; the shape is the region.
        var backdrop = NativeMethods.DwmsbtNone;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        var corner = NativeMethods.DwmwcpDoNotRound;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));
        var borderNone = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        NativeMethods.DwmSetWindowAttribute(Handle, 34, ref borderNone, sizeof(int)); // DWMWA_BORDER_COLOR
        // Sheet of glass: the whole client area starts transparent (alpha 0), so a freshly
        // (re)allocated window surface after a resize is not an opaque white fill.
        var margins = new NativeMethods.Margins { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(Handle, ref margins);
        NativeMethods.ExcludeFromPeek(Handle);
    }

    private void ApplyGlass()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        if (!_glass)
        {
            PopupChrome.DisableAccent(Handle);
            return;
        }

        var tint = _mode == "bar" ? PopupChrome.BarTint() : PopupChrome.DockGlassTint();
        PopupChrome.EnableAcrylic(Handle, tint);
    }

    private void RefreshGlass(bool force)
    {
        var on = PopupChrome.TransparencyAvailable();
        if (!force && on == _glass)
        {
            return;
        }

        _glass = on;
        ApplyGlass();
        PostGlass();
        _popup.SetGlass(on);
        _preview.RefreshChrome(_mode == "bar", on);
        _jumpList.RefreshChrome(_mode == "bar", on);
    }

    private void PostGlass()
    {
        var core = _webView.CoreWebView2;
        if (core == null || _shuttingDown)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "glass", on = _glass }));
        }
        catch
        {
            // The page may be navigating or the control is closing.
        }
    }

    private void OnLayoutMessage(JsonElement root)
    {
        var mode = root.TryGetProperty("mode", out var modeProperty) ? modeProperty.GetString() : null;
        if (mode != null && mode != _mode)
        {
            return; // stale report from before a mode switch
        }

        var rects = new List<(double X, double Y, double W, double H, double R)>();
        if (root.TryGetProperty("rects", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                double Get(string name) => item.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;
                var w = Get("w");
                var h = Get("h");
                if (w >= 1 && h >= 1)
                {
                    rects.Add((Get("x"), Get("y"), w, h, Get("r")));
                }
            }
        }

        BeginInvoke(new Action(() =>
        {
            _layoutRects = rects;
            ApplyRegion();
        }));
    }

    /// <summary>
    /// Window region = union of the island rounded rectangles reported by the page
    /// (HANDOFF 8.1). Until the first report the region is empty, so nothing flashes.
    /// The bar is one square rectangle in the same region (the window is not resized between modes).
    /// </summary>
    private void ApplyRegion()
    {
        if (!IsHandleCreated || Width <= 1 || Height <= 1)
        {
            return;
        }

        var scale = DeviceDpi / 96.0;
        // Before the first layout report: a single pixel instead of an empty region
        // (SetWindowRgn ignores an empty region and the whole strip would show).
        var combined = NativeMethods.CreateRectRgn(0, 0, 1, 1);
        if (_layoutRects != null)
        {
            NativeMethods.DeleteObject(combined);
            combined = NativeMethods.CreateRectRgn(0, 0, 0, 0);
            foreach (var rect in _layoutRects)
            {
                var left = (int)Math.Round(rect.X * scale);
                var top = (int)Math.Round(rect.Y * scale);
                var right = (int)Math.Round((rect.X + rect.W) * scale);
                var bottom = (int)Math.Round((rect.Y + rect.H) * scale);
                var diameter = Math.Max(2, (int)Math.Round(rect.R * 2 * scale));
                // Inset by 1px: the aliased region edge then falls inside the glass, not on the border ring.
                // No inset: the 1px CSS border sits on the outermost pixel row, so the aliased region edge
                // runs along the border instead of leaving a bare acrylic band outside it.
                // Plain rectangle, a hair larger than the island: the rounded shape is drawn by the page
                // (anti-aliased, on a transparent surface). A GDI round-rect region is binary and showed
                // as stair-stepped corners. Only the few corner pixels outside the curve eat clicks.
                var part = NativeMethods.CreateRectRgn(left - 1, top - 1, right + 2, bottom + 2);
                NativeMethods.CombineRgn(combined, combined, part, NativeMethods.RgnOr);
                NativeMethods.DeleteObject(part);
            }
        }

        // SetWindowRgn owns the region from here on.
        NativeMethods.SetWindowRgn(Handle, combined, true);
    }

    private void OnThemeChanged()
    {
        if (_shuttingDown || IsDisposed)
        {
            return;
        }

        Theme.Retint(_desktop);
        Theme.Retint(_preview);
        Theme.Retint(_trayMenu);
        Theme.Retint(_jumpList);
        Theme.Retint(_quick);
        Theme.Retint(_search);
        Theme.Retint(_start);
        PopupChrome.EnableAcrylic(_desktop.Handle);
        _preview.RefreshChrome(_mode == "bar", _glass);
        _jumpList.RefreshChrome(_mode == "bar", _glass);
        PopupChrome.EnableAcrylic(_trayMenu.Handle);
        PopupChrome.EnableAcrylic(_quick.Handle);
        _popup.PostTheme();
        PopupChrome.EnableAcrylic(_search.Handle);
        PopupChrome.EnableAcrylic(_start.Handle);
        ApplyGlass();
        PostTheme();
    }

    private void PostTheme()
    {
        var core = _webView.CoreWebView2;
        if (core == null || _shuttingDown)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "theme", accent = Theme.AccentHex }));
        }
        catch
        {
            // The page may be navigating or the control is closing.
        }
    }

    private void ShowJumpList(string id, double anchor)
    {
        var now = Environment.TickCount64;
        if (now - _menuAt < 400)
        {
            return;
        }

        _menuAt = now;
        if (!WindowCatalog.TryGetApp(id, out var app) || app == null)
        {
            return;
        }

        HidePreviews();
        _tip.Hide();
        _trayMenu.Hide();
        _quick.Hide();
        _popup.Dismiss();
        _search.Hide();
        _start.Hide();
        if (_jumpList.Visible)
        {
            _jumpList.Hide();
        }

        var model = JumpListCatalog.ForApp(app.Aumid, app.TargetPath);
        var anchorX = Left + (int)(Math.Clamp(anchor, 0, 1) * Width);
        SetOwnTopMost(false);
        _popup.HoldOpen = true;
        try
        {
            _jumpList.ShowFor(app, model, anchorX, Top, _mode == "bar", _glass);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] jump list: " + ex.Message);
            _popup.HoldOpen = false;
            SetOwnTopMost(true);
        }
    }

    private void ShowAppMenu(TrayButton button)
    {
        var now = Environment.TickCount64;
        if (now - _menuAt < 600)
        {
            return;
        }

        _menuAt = now;
        HidePreviews();
        _jumpList.Hide();
        _quick.Hide();
        _search.Hide();
        _start.Hide();
        if (string.IsNullOrWhiteSpace(button.ExecutablePath))
        {
            return;
        }

        _menuTimer.Stop();
        _menuBefore.Clear();
        foreach (var hwnd in VisibleTopLevel())
        {
            _menuBefore.Add(hwnd);
        }

        SetOwnTopMost(false);
        _trayMenu.HoldOpen = true;
        _popup.HoldOpen = true;
        var opened = false;
        try
        {
            opened = TrayIconMenu.TryShow(button);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] tray menu: " + ex.Message);
        }

        if (!opened)
        {
            ReleaseMenuHold();
            SetOwnTopMost(true);
            return;
        }

        // The tray panel stays visible under the app's own menu; it is released when that menu closes.
        _menuTicks = 0;
        _menuTimer.Start();
    }

    private void OnMenuTick()
    {
        _menuTicks++;
        var stillOpen = false;
        foreach (var hwnd in VisibleTopLevel())
        {
            if (!_menuBefore.Contains(hwnd) && !IsSpikeWindow(hwnd))
            {
                stillOpen = true;
                break;
            }
        }

        if (stillOpen && _menuTicks < 40)
        {
            return;
        }

        if (!stillOpen && _menuTicks < 2)
        {
            return;
        }

        _menuTimer.Stop();
        ReleaseMenuHold();
        SetOwnTopMost(true);
    }

    private void ReleaseMenuHold()
    {
        _trayMenu.HoldOpen = false;
        _popup.HoldOpen = false;
        // Closed menu + click landed outside the panel: close the small tray grid like a normal flyout.
        if (_trayMenu.Visible && !_trayMenu.Bounds.Contains(Cursor.Position))
        {
            _trayMenu.Hide();
        }
    }

    private void SetOwnTopMost(bool top)
    {
        var after = top ? NativeMethods.HwndTopmost : NativeMethods.HwndNotopmost;
        var flags = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate;
        void Place(Form form)
        {
            if (form.IsHandleCreated)
            {
                NativeMethods.SetWindowPos(form.Handle, after, 0, 0, 0, 0, flags);
            }
        }

        Place(this);
        Place(_desktop);
        if (top)
        {
            SyncDesktopStrip();
        }
    }

    private bool IsSpikeWindow(IntPtr hwnd)
    {
        return SameHandle(this, hwnd)
            || SameHandle(_desktop, hwnd)
            || _preview.IsOwnHwnd(hwnd)
            || SameHandle(_tip, hwnd)
            || SameHandle(_trayMenu, hwnd)
            || SameHandle(_jumpList, hwnd)
            || SameHandle(_quick, hwnd)
            || SameHandle(_popup, hwnd)
            || SameHandle(_search, hwnd)
            || SameHandle(_start, hwnd);
    }

    private static bool SameHandle(Form form, IntPtr hwnd)
    {
        return form.IsHandleCreated && form.Handle == hwnd;
    }

    private static bool FormContains(Form? form, Point point)
    {
        return form is { Visible: true } && form.Bounds.Contains(point);
    }

    private void RaiseAbovePeek()
    {
        var flags = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate;
        void Raise(Form form)
        {
            if (!form.IsHandleCreated || !form.Visible)
            {
                return;
            }

            NativeMethods.ExcludeFromPeek(form.Handle);
            NativeMethods.SetWindowPos(form.Handle, NativeMethods.HwndTopmost, 0, 0, 0, 0, flags);
        }

        Raise(this);
        Raise(_desktop);
        Raise(_tip);
        Raise(_trayMenu);
        Raise(_jumpList);
        Raise(_quick);
        Raise(_popup);
        Raise(_search);
        Raise(_start);
    }

    private static List<IntPtr> VisibleTopLevel()
    {
        var found = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hwnd))
            {
                found.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void RegisterAppBar()
    {
        _appBarCallback = NativeMethods.RegisterWindowMessage("TaskbarSpike.AppBar");
        var data = NewAppBarData();
        var result = NativeMethods.SHAppBarMessage(NativeMethods.AbmNew, ref data);
        _appBarRegistered = result != IntPtr.Zero;
        if (!_appBarRegistered)
        {
            Console.WriteLine("[spike] SHAppBarMessage(ABM_NEW) failed. Continuing as a floating dock.");
        }
    }

    private Rectangle ReserveAppBar(int height)
    {
        var screen = Screen.PrimaryScreen?.Bounds ?? Bounds;
        var data = NewAppBarData();
        data.uEdge = NativeMethods.AbeBottom;
        data.rc = new NativeMethods.Rect
        {
            Left = screen.Left,
            Top = screen.Bottom - height,
            Right = screen.Right,
            Bottom = screen.Bottom,
        };
        NativeMethods.SHAppBarMessage(NativeMethods.AbmQueryPos, ref data);
        // The stock taskbar is hidden but still an appbar: QueryPos stacks us above it. Pin to the screen edge.
        data.rc.Bottom = screen.Bottom;
        data.rc.Top = screen.Bottom - height;
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetPos, ref data);
        // The shell may hand back a rectangle trimmed against the hidden stock taskbar (we saw a
        // 4px strip). The reservation is made; the window itself always covers the full strip.
        return new Rectangle(screen.Left, screen.Bottom - height, screen.Width, height);
    }

    private void RemoveAppBar()
    {
        if (!_appBarRegistered || !IsHandleCreated)
        {
            return;
        }

        var data = NewAppBarData();
        NativeMethods.SHAppBarMessage(NativeMethods.AbmRemove, ref data);
        _appBarRegistered = false;
    }

    private NativeMethods.AppBarData NewAppBarData()
    {
        return new NativeMethods.AppBarData
        {
            cbSize = Marshal.SizeOf<NativeMethods.AppBarData>(),
            hWnd = Handle,
            uCallbackMessage = _appBarCallback,
        };
    }
}
