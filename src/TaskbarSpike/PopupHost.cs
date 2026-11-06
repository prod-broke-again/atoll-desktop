using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TaskbarSpike;

/// <summary>Where the panel goes: right edge, or centred on a point; <c>BottomPx</c> is the lower edge in screen pixels.</summary>
internal readonly record struct PopupPlacement(string Anchor, int AnchorXPx, int BottomPx, int EdgeMarginPx);

/// <summary>
/// One borderless top-most window with one WebView2 that holds every popup panel
/// (HANDOFF 8.2). It is created and navigated at start-up and afterwards only shown
/// and hidden — never destroyed — so opening a panel costs no process start and no
/// system query: the page already holds the latest data, pushed while the window was hidden.
///
/// Opening is a short handshake: C# says "popup" → the page renders the panel
/// synchronously and answers "popup-size" → C# sizes, places and clips the window,
/// shows it and says "popup-shown" so the entrance animation plays on a visible surface.
/// </summary>
internal sealed class PopupHost : Form
{
    private const int HiddenPollMs = 2500;
    private const int VisiblePollMs = 600;
    private const int PanelRadiusDip = 14;

    private readonly WebView2 _web;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _outsideTimer;
    private bool _buttonWasDown;
    private readonly System.Windows.Forms.Timer _sizeTimeout;
    private bool _ready;
    private bool _awaitingSize;
    private string _panel = string.Empty;
    private string _mode = "dock";
    private bool _glass = true;
    private PopupPlacement _placement;
    private long _openedAt;
    private string _lastData = string.Empty;
    private bool _shuttingDown;
    private bool _refitting;
    private int _splitPx; // first card height, device px; 0 = the panel is one shape
    private string _shownPanel = string.Empty;
    private bool _showQuiet; // Win10 search keeps the keystrokes in the bar; the panel must not take focus
    private string _lastStart = string.Empty;
    private Rectangle _panelRect; // visible panel in screen pixels; the window is this rectangle

    public PopupHost()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        // Same rules as the dock window: acrylic from DWM, transparent web content on top,
        // and nothing painted by GDI (an opaque erase would hide the blur).
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque, true);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "TaskbarSpike.Popup";
        Size = new Size(1, 1);

        _web = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.Transparent,
        };
        Controls.Add(_web);

        _pollTimer = new System.Windows.Forms.Timer { Interval = HiddenPollMs };
        _pollTimer.Tick += (_, _) => PushData(false);
        _outsideTimer = new System.Windows.Forms.Timer { Interval = 40 };
        _outsideTimer.Tick += (_, _) => OnOutsideTick();
        _outsideTimer.Start();
        _sizeTimeout = new System.Windows.Forms.Timer { Interval = 600 };
        _sizeTimeout.Tick += (_, _) =>
        {
            _sizeTimeout.Stop();
            if (_awaitingSize)
            {
                _awaitingSize = false;
                _panel = string.Empty;
                Console.WriteLine("[spike] popup: the page did not report a size");
            }
        };

        QuickSettings.Changed += () =>
        {
            if (!_shuttingDown && IsHandleCreated)
            {
                BeginInvoke(new Action(() => PushData(true)));
            }
        };
        StartFeed.Prime(this);
        StartFeed.Changed += () =>
        {
            if (!_shuttingDown)
            {
                PublishStart(false);
            }
        };
    }

    /// <summary>The window was shown or hidden by its own logic (deactivation, Esc, a tile) — refresh dock button states.</summary>
    public event Action? Dismissed;

    /// <summary>Overflow panel asked to switch dock/bar.</summary>
    public event Action? ModeRequested;

    /// <summary>Overflow panel asked to restore the Windows taskbar and exit.</summary>
    public event Action? ExitRequested;

    /// <summary>Right-click on a hidden tray icon.</summary>
    public event Action<TrayButton>? TrayContextRequested;

    /// <summary>While an app's own tray menu is open the panel must stay visible.</summary>
    public bool HoldOpen { get; set; }

    /// <summary>True when a screen point belongs to a dock island; a click there toggles the panel instead of dismissing it.</summary>
    public Func<Point, bool>? IsDockPoint { get; set; }

    public bool IsOpen(string panel) => Visible && _panel == panel;

    public bool ContainsPoint(Point screenPoint) => Visible && _panelRect.Contains(screenPoint);

    protected override bool ShowWithoutActivation => _showQuiet;

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExTopMost = 0x00000008;
            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow | wsExTopMost;
            return cp;
        }
    }

    public async Task InitAsync(CoreWebView2Environment environment)
    {
        // The handle must exist before the control can host the browser, but the window stays hidden.
        _ = Handle;
        _web.DefaultBackgroundColor = Color.Transparent;
        await _web.EnsureCoreWebView2Async(environment);
        _web.DefaultBackgroundColor = Color.Transparent;

        var core = _web.CoreWebView2;
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
        core.SetVirtualHostNameToFolderMapping(
            "taskbar.spike",
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.Allow);
        core.NavigationStarting += (_, args) =>
        {
            if (args.Uri != null && !args.Uri.StartsWith("https://taskbar.spike/", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };
        core.WebMessageReceived += OnWebMessage;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.__spikeAccent = " + JsonSerializer.Serialize(Theme.AccentHex) + ";");
        core.NavigationCompleted += (_, _) =>
        {
            _ready = true;
            PostTheme();
            PostGlass();
            QuickSettings.Refresh();
            PushData(true);
            _pollTimer.Start();
        };
        core.Navigate("https://taskbar.spike/popups.html");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ClearSystemChrome();
    }

    protected override void WndProc(ref Message m)
    {
        const int wmWindowPosChanging = 0x0046;
        if (_refitting && m.Msg == wmWindowPosChanging && m.LParam != IntPtr.Zero)
        {
            // WinForms' own SetWindowPos (if the managed bounds still need catching up) must
            // discard bits too, or the second move paints the old surface white.
            var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.WindowPos>(m.LParam);
            pos.flags |= NativeMethods.SwpNoCopyBits | NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder;
            System.Runtime.InteropServices.Marshal.StructureToPtr(pos, m.LParam, false);
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// DWM rounding, system border and backdrop off; the whole client area is "glass" (alpha 0).
    /// Must be repeated after every resize — a freshly allocated window surface is otherwise an opaque white fill.
    /// </summary>
    private void ClearSystemChrome()
    {
        var backdrop = NativeMethods.DwmsbtNone;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        var corner = NativeMethods.DwmwcpDoNotRound;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));
        var borderNone = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        NativeMethods.DwmSetWindowAttribute(Handle, 34, ref borderNone, sizeof(int)); // DWMWA_BORDER_COLOR
        var margins = new NativeMethods.Margins { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(Handle, ref margins);
        NativeMethods.ExcludeFromPeek(Handle);
    }

    /// <summary>Ask the page to open a panel. The window appears when the page has reported the panel size.</summary>
    /// <param name="activate">False keeps focus where it is (the Win10 search box, which owns the keystrokes).</param>
    public void Open(string panel, PopupPlacement placement, string mode, bool glass, bool activate = true)
    {
        if (!_ready || _shuttingDown)
        {
            return;
        }

        _panel = panel;
        _placement = placement;
        _mode = mode;
        _glass = glass;
        _showQuiet = !activate;
        _openedAt = Environment.TickCount64;
        _awaitingSize = true;
        _sizeTimeout.Stop();
        _sizeTimeout.Start();
        ApplyMaterial();
        PushData(true);
        PublishStart(true);
        Post(new { type = "popup", panel, mode, glass, maxHeight = MaxPanelHeightDip(placement) });
    }

    /// <summary>Replace the Start search query. Results arrive as <c>start-data</c> before the next paint.</summary>
    public void SetSearch(string? query)
    {
        StartFeed.SetQuery(query);
        PublishStart(true);
    }

    /// <summary>Hide the window if it is showing; raises <see cref="Dismissed"/> only then.</summary>
    public void Dismiss()
    {
        _awaitingSize = false;
        if (!Visible)
        {
            _panel = string.Empty;
            return;
        }

        base.Hide();
        _panel = string.Empty;
        _shownPanel = string.Empty;
        _splitPx = 0;
        _pollTimer.Interval = HiddenPollMs;
        Post(new { type = "popup-hide" });
        Dismissed?.Invoke();
    }

    public void SetGlass(bool glass)
    {
        _glass = glass;
        ApplyMaterial();
        PostGlass();
    }

    public void PostTheme()
    {
        Post(new { type = "theme", accent = Theme.AccentHex });
        if (IsHandleCreated)
        {
            ApplyMaterial();
        }
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        _pollTimer.Stop();
        _outsideTimer.Stop();
        _sizeTimeout.Stop();
        base.Hide();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// Click outside closes the panel even when the window never took focus (quiet-opened
    /// panels, or a click on a non-activating surface such as the wallpaper).
    /// </summary>
    private void OnOutsideTick()
    {
        var down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        var pressed = down && !_buttonWasDown;
        _buttonWasDown = down;
        if (HoldOpen || !pressed || !Visible || _awaitingSize || _shuttingDown || _refitting)
        {
            return;
        }

        var cursor = Cursor.Position;
        if (Environment.TickCount64 - _openedAt < 250 || _panelRect.Contains(cursor) || IsDockPoint?.Invoke(cursor) == true)
        {
            return;
        }

        Dismiss();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (HoldOpen || !Visible || _awaitingSize || _shuttingDown || _refitting)
        {
            return;
        }

        var cursor = Cursor.Position;
        if (Environment.TickCount64 - _openedAt < 250 || _panelRect.Contains(cursor) || IsDockPoint?.Invoke(cursor) == true)
        {
            return;
        }

        Dismiss();
    }

    // ---- page → host ------------------------------------------------------------------

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

            string Str(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            double Num(string name) => root.TryGetProperty(name, out var p) && p.TryGetDouble(out var n) ? n : 0;

            switch (typeProperty.GetString())
            {
                case "popup-size":
                    OnPanelSize(Str("panel"), Num("w"), Num("h"), Num("split"));
                    break;
                case "popup-close":
                    Dismiss();
                    break;
                case "notif-open":
                    Dismiss();
                    NotificationLog.Launch(Str("id"));
                    break;
                case "notif-dismiss":
                    if (long.TryParse(Str("id"), out var dismissId))
                    {
                        NotificationLog.Dismiss(dismissId);
                        PushData(true);
                    }

                    break;
                case "notif-clear":
                    NotificationLog.Clear();
                    PushData(true);
                    break;
                case "notif-center":
                    Dismiss();
                    NotificationCenter.Toggle();
                    break;
                case "tray":
                    Dismiss();
                    TrayCatalog.Open(Str("id"));
                    break;
                case "tray-context":
                    if (TrayCatalog.TryGet(Str("id"), out var trayButton) && trayButton != null)
                    {
                        TrayContextRequested?.Invoke(trayButton);
                    }

                    break;
                case "mode":
                    ModeRequested?.Invoke();
                    break;
                case "exit":
                    ExitRequested?.Invoke();
                    break;
                case "desktop":
                    VirtualDesktops.Switch((int)Math.Round(Num("index")));
                    RefreshSoon(180);
                    break;
                case "desktop-add":
                    VirtualDesktops.Create();
                    RefreshSoon(180);
                    break;
                case "desktop-close":
                    VirtualDesktops.Close((int)Math.Round(Num("index")));
                    RefreshSoon(180);
                    break;
                case "volume":
                    AudioDevices.SetVolume((int)Math.Round(Num("value")));
                    break;
                case "mute":
                    AudioDevices.SetMuted(root.TryGetProperty("value", out var muteValue) && muteValue.ValueKind == JsonValueKind.True);
                    break;
                case "audio-device":
                    AudioDevices.SetDefault(Str("id"));
                    // The default endpoint changes asynchronously; look again shortly.
                    RefreshSoon(180);
                    break;
                case "session-volume":
                    AudioDevices.SetSession(Str("key"), (int)Math.Round(Num("value")));
                    break;
                case "quick-toggle":
                    OnQuickToggle(Str("key"));
                    break;
                case "quick-open":
                    OnQuickOpen(Str("key"));
                    break;
                case "start-launch":
                case "start-file":
                    Dismiss();
                    StartFeed.Activate(Str("id"));
                    break;
                case "start-unpin":
                    StartFeed.UnpinTile(Str("id"));
                    break;
                case "start-pin":
                    StartFeed.PinTile(Str("id"));
                    break;
                case "start-power":
                    Dismiss();
                    StartFeed.Power(Str("action"));
                    break;
                case "start-search":
                    StartFeed.SetQuery(Str("q"));
                    PublishStart(true);
                    break;
                case "phone-toggle":
                    StartFeed.SetPhone(root.TryGetProperty("visible", out var phoneVisible) && phoneVisible.ValueKind == JsonValueKind.True);
                    PublishStart(true);
                    break;
                case "phone-open":
                    Dismiss();
                    StartFeed.OpenPhone(Str("launch"));
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] popup message: {ex.Message}");
        }
    }

    private void OnQuickToggle(string key)
    {
        if (!QuickSettings.Toggle(key))
        {
            // No reliable switch for this tile: open its Settings page and get out of the way.
            Dismiss();
            QuickSettings.OpenUri(QuickSettings.SettingsUri(key));
            return;
        }

        RefreshSoon(400);
    }

    private void OnQuickOpen(string key)
    {
        Dismiss();
        if (key == "cast")
        {
            // Win+K needs the shell to have focus, which it has once the popup has gone.
            NotificationCenter.Cast();
            return;
        }

        QuickSettings.OpenUri(QuickSettings.SettingsUri(key == "settings" ? "settings" : key));
    }

    private void RefreshSoon(int delayMs)
    {
        var timer = new System.Windows.Forms.Timer { Interval = delayMs };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (_shuttingDown)
            {
                return;
            }

            QuickSettings.Refresh();
            PushData(true);
        };
        timer.Start();
    }

    /// <summary>Tallest panel in DIP: from 8 DIP below the top of the screen down to the placement's lower edge.</summary>
    private int MaxPanelHeightDip(PopupPlacement placement)
    {
        var scale = Math.Max(1.0, DeviceDpi / 96.0);
        var screenTop = (Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080)).Top;
        return Math.Max(240, (int)Math.Floor((placement.BottomPx - screenTop - (8 * scale)) / scale));
    }

    /// <summary>
    /// The window is the panel: same width and height, bottom edge on the placement, so a taller
    /// page grows upward. Acrylic blurs the window rectangle and ignores the region, so a taller
    /// host would leave a glass strip above the panel. A visible move used to leave the previous
    /// surface as an opaque white block; <see cref="Refit"/> discards those bits and rebuilds the glass.
    /// </summary>
    private void OnPanelSize(string panel, double width, double height, double split)
    {
        if (_shuttingDown || panel != _panel || width < 1 || height < 1)
        {
            return;
        }

        _sizeTimeout.Stop();
        _awaitingSize = false;
        var scale = Math.Max(1.0, DeviceDpi / 96.0);
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var maxHeight = (int)Math.Ceiling(MaxPanelHeightDip(_placement) * scale);
        var w = (int)Math.Ceiling(width * scale);
        var h = Math.Min((int)Math.Ceiling(height * scale), maxHeight);

        var x = _placement.Anchor switch
        {
            "right" => screen.Right - _placement.EdgeMarginPx - w,
            "left" => screen.Left + _placement.EdgeMarginPx,
            _ => _placement.AnchorXPx - (w / 2),
        };
        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - w));
        var bounds = new Rectangle(x, _placement.BottomPx - h, w, h);
        _splitPx = split > 1 ? (int)Math.Round(split * scale) : 0;
        var firstShow = !Visible;
        if (Bounds != bounds)
        {
            if (firstShow)
            {
                // Still hidden: a plain move cannot leave a white block on screen.
                Bounds = bounds;
                ClearSystemChrome();
            }
            else
            {
                Refit(bounds);
            }
        }

        _panelRect = bounds;
        ApplyRegion(w, h, scale);
        var reveal = _shownPanel != panel;
        if (firstShow)
        {
            ApplyMaterial();
            Show();
            var flags = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow;
            if (_showQuiet)
            {
                flags |= NativeMethods.SwpNoActivate;
            }

            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopmost, 0, 0, 0, 0, flags);
            if (!_showQuiet)
            {
                Activate();
                _web.Focus();
            }

            _pollTimer.Interval = VisiblePollMs;
            _shownPanel = panel;
            Post(new { type = "popup-shown", panel });
            Dismissed?.Invoke(); // dock buttons light up while a panel is open
        }
        else if (reveal)
        {
            // Another panel took the same window: play its entrance without a hide/show.
            _shownPanel = panel;
            if (!_showQuiet)
            {
                Activate();
                _web.Focus();
            }

            Post(new { type = "popup-shown", panel });
            Dismissed?.Invoke();
        }
    }

    /// <summary>
    /// Resize a visible window onto <paramref name="bounds"/> (bottom edge fixed by the caller).
    /// SWP_NOCOPYBITS drops the old surface, then the glass frame and the accent policy are
    /// created again — otherwise the previous rectangle stays an opaque white block.
    /// </summary>
    private void Refit(Rectangle bounds)
    {
        _refitting = true;
        try
        {
            NativeMethods.SetWindowPos(
                Handle,
                IntPtr.Zero,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                NativeMethods.SwpNoCopyBits | NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
            if (Bounds != bounds)
            {
                Bounds = bounds;
            }
        }
        finally
        {
            _refitting = false;
        }

        // Extend the glass frame over the new client (ClearSystemChrome), then drop and
        // reinstall the accent policy so the blur follows this rectangle.
        ClearSystemChrome();
        ApplyMaterial(rebuild: true);
    }

    /// <summary>Start is 16 DIP (HANDOFF 5.2); the other panels are 14. The bar is square and does not use this.</summary>
    private int ShapeRadiusDip() => _panel == "start" ? 16 : PanelRadiusDip;

    private bool _splitShape;

    /// <summary>Window shape = panel shape. A two-card panel punches a hole; acrylic ignores the region, so that shape uses blur-behind on the cards only.</summary>
    private void ApplyRegion(int width, int height, double scale)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var gap = (int)Math.Round(10 * scale);
        var split = _splitPx;
        _splitShape = split > 1 && split + gap < height - 1;
        IntPtr region;
        if (_splitShape)
        {
            // The gap between the two cards stays part of the window (transparent, but it takes clicks):
            // a hole in the region let clicks fall through to whatever lies beneath the panel.
            region = NativeMethods.CreateRectRgn(0, 0, width, height);
        }
        else if (_mode == "bar")
        {
            region = NativeMethods.CreateRectRgn(0, 0, width, height);
        }
        else
        {
            // Exactly the CSS shape (GDI end coordinates are exclusive, hence +1): an inset region left a
            // 1-2px band of bare acrylic around the rounded glass, which read as a square backdrop.
            // Rounded corners come from the page (anti-aliased); the window region stays rectangular.
            region = NativeMethods.CreateRectRgn(0, 0, width, height);
        }

        NativeMethods.SetWindowRgn(Handle, region, true);
        // Same acrylic as every other panel; the region (one rounded card, or two with a gap) is the shape.
        var behind = new NativeMethods.DwmBlurBehind { dwFlags = NativeMethods.DwmBbEnable, fEnable = 0 };
        NativeMethods.DwmEnableBlurBehindWindow(Handle, ref behind);
    }

    private void CombineCard(IntPtr combined, int top, int bottom, int width, double scale)
    {
        IntPtr part;
        if (_mode == "bar")
        {
            part = NativeMethods.CreateRectRgn(0, top, width, bottom);
        }
        else
        {
            part = NativeMethods.CreateRectRgn(0, top, width, bottom);
        }

        NativeMethods.CombineRgn(combined, combined, part, NativeMethods.RgnOr);
        NativeMethods.DeleteObject(part);
    }

    private void ApplyMaterial(bool rebuild = false)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        // A live resize has to drop the old accent policy before installing a new one,
        // or the blur stays attached to the previous window rectangle.
        // Two cards keep blur-behind on the region instead: accent blur would fill the hole.
        if (rebuild || !_glass)
        {
            PopupChrome.DisableAccent(Handle);
        }

        if (!_glass)
        {
            return;
        }

        PopupChrome.EnableAcrylic(Handle, _mode == "bar" ? PopupChrome.BarTint() : PopupChrome.DockGlassTint());
    }

    // ---- host → page ------------------------------------------------------------------

    private void PostGlass() => Post(new { type = "glass", on = _glass });

    /// <summary>Start catalog, phone column and the current search results. Posted on its own so a volume change does not resend it.</summary>
    private void PublishStart(bool force)
    {
        if (!_ready || _shuttingDown || _web.CoreWebView2 == null)
        {
            return;
        }

        var json = StartFeed.PayloadJson();
        if (json.Length == 0 || (!force && json == _lastStart))
        {
            return;
        }

        _lastStart = json;
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch
        {
            // The page may be navigating or the control is closing.
        }
    }

    private void Post(object message)
    {
        var core = _web.CoreWebView2;
        if (core == null || _shuttingDown)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
        catch
        {
            // The page may be navigating or the control is closing.
        }
    }

    /// <summary>Push audio, network and tile state. Skipped when nothing changed unless forced.</summary>
    private void PushData(bool force)
    {
        if (!_ready || _shuttingDown || _web.CoreWebView2 == null)
        {
            return;
        }

        if (Visible)
        {
            QuickSettings.Refresh();
        }

        StartFeed.Touch();
        PublishStart(false);
        var json = JsonSerializer.Serialize(BuildData());
        if (!force && json == _lastData)
        {
            return;
        }

        _lastData = json;
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch
        {
            // The page may be navigating or the control is closing.
        }
    }

    private static readonly Regex DeviceSplit = new(@"^(?<name>.*?)\s*\((?<sub>.+)\)\s*$", RegexOptions.Compiled);

    private static object BuildData()
    {
        var audio = AudioDevices.Details();
        var network = NetworkStatus.Read();
        var quick = QuickSettings.State;

        static (string Name, string Sub) Split(string friendly)
        {
            var match = DeviceSplit.Match(friendly);
            return match.Success ? (match.Groups["name"].Value, match.Groups["sub"].Value) : (friendly, string.Empty);
        }

        static string Kind(int formFactor) => formFactor switch
        {
            3 or 5 => "head",
            9 => "monitor",
            _ => "speaker",
        };

        var active = Split(audio.DeviceName);
        return new
        {
            type = "data",
            quick = new
            {
                bluetooth = quick.Bluetooth,
                hotspot = quick.Hotspot,
                night = quick.Night,
                dnd = quick.Dnd,
                available = new
                {
                    bluetooth = quick.BluetoothAvailable,
                    hotspot = quick.HotspotAvailable,
                    night = quick.NightAvailable,
                    dnd = quick.DndAvailable,
                },
            },
            audio = new
            {
                volume = audio.Volume,
                muted = audio.Muted,
                device = string.IsNullOrEmpty(active.Sub) ? active.Name : active.Name + " · " + active.Sub,
                devices = audio.Devices.Select(device =>
                {
                    var parts = Split(device.Name);
                    return new
                    {
                        id = device.Id,
                        name = parts.Name,
                        sub = parts.Sub,
                        kind = Kind(device.FormFactor),
                        current = device.Active,
                    };
                }),
                sessions = audio.Sessions.Select(session => new
                {
                    key = session.Key,
                    name = session.Name,
                    icon = WindowCatalog.IconForExe(session.ExePath),
                    volume = session.Volume,
                }),
            },
            network = new
            {
                kind = network.Kind,
                connected = network.Connected,
                name = network.Name,
                status = network.Status,
                internet = network.Connected,
                vpn = VpnConnected(),
            },
            notifications = new
            {
                ok = !NotificationLog.Unavailable,
                items = NotificationLog.Current().Select(item => new
                {
                    id = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    app = item.App,
                    appId = item.AppId,
                    title = item.Title,
                    text = item.Text,
                    time = item.Time,
                }),
            },
            tray = TrayCatalog.Hidden().Select(icon => new
            {
                id = icon.Id,
                name = icon.Name,
                icon = icon.IconDataUrl,
            }),
            desktops = VirtualDesktops.List().Select(desk => new
            {
                index = desk.Index,
                name = string.IsNullOrWhiteSpace(desk.Name) || desk.Name.All(char.IsDigit) ? "Рабочий стол " + (desk.Index + 1) : desk.Name,
                current = desk.Current,
            }),
            wallpaper = DesktopWallpaper.DataUrl(),
        };
    }

    private static bool VpnConnected()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ppp);
        }
        catch
        {
            return false;
        }
    }
}
