using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Plugins;
using LiveWallpaper.Native;
using LiveWallpaper.Services.Autostart;
using LiveWallpaper.Services.Server;
using LiveWallpaper.Services.Weather;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiveWallpaper.App;

/// <summary>
/// Settings window rendered with WebView2 (wwwroot/settings.html) so it shares the look of the
/// desktop widgets. All reads/writes go through JSON messages; the page never touches the disk.
/// </summary>
public sealed class WebSettingsForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConfigManager _configManager;
    private readonly IWeatherService _weatherService;
    private readonly IServerWidgetService _serverService;
    private readonly Services.Context.IContextService _contextService;
    private readonly Services.Atmosphere.IAtmosphereService _atmosphereService;
    private readonly Services.SetupPalette.ISetupPaletteService _setupPaletteService;
    private readonly IEnumerable<IWidgetPlugin> _plugins;
    private readonly ILogger _logger;
    private readonly WebView2 _webView;
    private readonly WallpaperMediaHost _mediaHost = new();

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    public WebSettingsForm(
        IConfigManager configManager,
        IWeatherService weatherService,
        IServerWidgetService serverService,
        Services.Context.IContextService contextService,
        Services.Atmosphere.IAtmosphereService atmosphereService,
        Services.SetupPalette.ISetupPaletteService setupPaletteService,
        IEnumerable<IWidgetPlugin> plugins,
        ILogger logger)
    {
        _configManager = configManager;
        _weatherService = weatherService;
        _serverService = serverService;
        _contextService = contextService;
        _atmosphereService = atmosphereService;
        _setupPaletteService = setupPaletteService;
        _plugins = plugins;
        _logger = logger;

        Text = "LiveWallpaper — настройки";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(940, 680);
        MinimumSize = new Size(780, 560);
        BackColor = Color.FromArgb(11, 12, 18);
        ShowInTaskbar = true;
        Icon = AppBrand.Icon;

        _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(11, 12, 18) };
        Controls.Add(_webView);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar + Mica, so the window chrome matches the page.
        try
        {
            int on = 1;
            _ = DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
            int caption = 0x00120C0B; // COLORREF 0x00BBGGRR → #0B0C12
            _ = DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        }
        catch { /* older Windows */ }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveWallpaper", "WebView2_Settings");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await _webView.EnsureCoreWebView2Async(env);
            var wv = _webView.CoreWebView2;
#if !DEBUG
            wv.Settings.AreDevToolsEnabled = false;
            wv.Settings.AreDefaultContextMenusEnabled = false;
#endif
            wv.Settings.IsStatusBarEnabled = false;
            wv.Settings.IsZoomControlEnabled = false;
            wv.SetVirtualHostNameToFolderMapping("app.livewallpaper", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Allow);
            _mediaHost.Attach(wv);
            wv.WebMessageReceived += OnWebMessage;
            wv.Navigate("https://app.livewallpaper/settings.html");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise web settings window.");
            MessageBox.Show("Не удалось открыть окно настроек: " + ex.Message, "LiveWallpaper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void Post(object payload)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => Post(payload)); } catch { }
            return;
        }
        try { _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions)); } catch { }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "settings_get":
                    SendState();
                    break;

                case "settings_save":
                    Save(root.GetProperty("data"));
                    break;

                case "update_app":
                    if (!WallpaperUpdate.TryStart(out var updateError))
                    {
                        MessageBox.Show(updateError, "LiveWallpaper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }

                    break;

                case "settings_close":
                    Close();
                    break;

                case "settings_minimize":
                    WindowState = FormWindowState.Minimized;
                    break;

                case "settings_maximize":
                    WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized;
                    break;

                case "settings_drag":
                    if (WindowState == FormWindowState.Maximized) break;
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                    break;

                case "pick_wallpaper_video":
                    PickWallpaperFile(isVideo: true);
                    break;

                case "pick_wallpaper_image":
                    PickWallpaperFile(isVideo: false);
                    break;

                case "pick_folder":
                    using (var dlg = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Папка с картинками для коллажа" })
                    {
                        var current = _configManager.CurrentConfig.Collage.PinsDirectory;
                        dlg.SelectedPath = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                        {
                            Post(new { type = "folder_picked", path = dlg.SelectedPath, count = CountImages(dlg.SelectedPath) });
                        }
                    }
                    break;

                case "geocode":
                    {
                        var city = root.GetProperty("city").GetString() ?? string.Empty;
                        var r = await _weatherService.GeocodeCityAsync(city);
                        Post(r is { } g
                            ? new { type = "geocode_result", ok = true, name = g.ResolvedName, lat = g.Latitude, lon = g.Longitude }
                            : (object)new { type = "geocode_result", ok = false });
                    }
                    break;

                case "token_set":
                    {
                        var token = root.GetProperty("token").GetString()?.Trim() ?? string.Empty;
                        var ok = token.Length > 0 && CredentialManager.WriteSecret(token);
                        Post(new { type = "token_status", exists = CredentialManager.Exists(), saved = ok });
                    }
                    break;

                case "token_delete":
                    CredentialManager.DeleteSecret();
                    Post(new { type = "token_status", exists = CredentialManager.Exists(), saved = false });
                    break;

                case "weeek_token_set":
                    {
                        var token = root.GetProperty("token").GetString()?.Trim() ?? string.Empty;
                        var ok = token.Length > 0 && CredentialManager.WriteSecret(token, CredentialManager.WeeekTarget);
                        Post(new { type = "weeek_token_status", exists = CredentialManager.Exists(CredentialManager.WeeekTarget), saved = ok });
                    }
                    break;

                case "weeek_token_delete":
                    CredentialManager.DeleteSecret(CredentialManager.WeeekTarget);
                    Post(new { type = "weeek_token_status", exists = false, saved = false });
                    break;

                case "calendar_ics_set":
                    {
                        var url = root.GetProperty("url").GetString()?.Trim() ?? string.Empty;
                        var ok = url.Length > 0 &&
                                 Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                                 (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
                                 CredentialManager.WriteSecret(url, CredentialManager.GoogleCalendarIcsTarget, "ics");
                        Post(new { type = "calendar_ics_status", exists = CredentialManager.Exists(CredentialManager.GoogleCalendarIcsTarget), saved = ok });
                    }
                    break;

                case "calendar_ics_delete":
                    CredentialManager.DeleteSecret(CredentialManager.GoogleCalendarIcsTarget);
                    Post(new { type = "calendar_ics_status", exists = false, saved = false });
                    break;

                case "diagnostics":
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        string text;
                        try { text = await _serverService.RunDiagnosticsAsync(cts.Token); }
                        catch (Exception ex) { text = "Ошибка проверки: " + ex.Message; }
                        Post(new { type = "diagnostics_result", text });
                    }
                    break;

                case "test_signal":
                    {
                        var signal = root.TryGetProperty("signal", out var sProp) ? sProp.GetString() : null;
                        var active = root.TryGetProperty("active", out var aProp) && aProp.GetBoolean();
                        if (!string.IsNullOrEmpty(signal))
                        {
                            _atmosphereService.TriggerTestSignal(signal, active);
                        }
                    }
                    break;

                case "set_mode":
                    {
                        var mode = root.TryGetProperty("mode", out var mProp) ? mProp.GetString() : null;
                        if (!string.IsNullOrEmpty(mode))
                        {
                            _contextService.SetMode(mode);
                        }
                    }
                    break;

                case "apply_palette":
                    {
                        _ = _setupPaletteService.ApplyCurrentAsync();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settings message failed.");
            Post(new { type = "error", message = ex.Message });
        }
    }

    private void PickWallpaperFile(bool isVideo)
    {
        using var dlg = new OpenFileDialog
        {
            Title = isVideo ? "Видео для обоев" : "Изображение для обоев",
            Filter = isVideo
                ? "Видео|*.mp4;*.webm;*.mkv;*.mov;*.avi|Все файлы|*.*"
                : "Изображения|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp|Все файлы|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var path = dlg.FileName;
            Post(new
            {
                type = "wallpaper_file_picked",
                mediaType = isVideo ? "video" : "image",
                path,
                previewUrl = _mediaHost.TryGetMediaUrl(path),
            });
        }
    }

    private static int CountImages(string dir)
    {
        try
        {
            var ext = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            return Directory.EnumerateFiles(dir).Count(f => ext.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }

    private void SendState()
    {
        var c = _configManager.CurrentConfig;
        var plugins = _plugins
            .Select(p => p.GetSettingsManifest())
            .Where(m => m != null)
            .Select(m => m!)
            .ToList();
        Post(new
        {
            type = "settings",
            data = new
            {
                collage = new
                {
                    c.Collage.Enabled,
                    c.Collage.PinsDirectory,
                    c.Collage.Style,
                    c.Collage.ShuffleStyle,
                    c.Collage.IntervalMinutes,
                    c.Collage.EnableColorHarmony
                },
                plugins,
                pinsCount = CountImages(c.Collage.PinsDirectory),
                weather = new { c.Weather.Enabled, c.Weather.City, c.Weather.Latitude, c.Weather.Longitude },
                performance = new { c.Performance.AutoPauseFullscreen, c.Performance.AutoPauseBattery, c.Performance.FpsIdle },
                launcher = new { c.Launcher.Enabled, c.Launcher.HideDesktopIcons },
                server = new { c.Server.Enabled, c.Server.SshAlias, c.Server.TimewebEnabled, Sites = c.Server.Sites.Select(s => new { s.Name, s.Url }) },
                widgets = new { c.Widgets.ShowClock, c.Widgets.ClockPosition, c.Widgets.ClockFont },
                context = new { c.Context.Mode },
                atmosphere = new
                {
                    c.Atmosphere.Enabled,
                    c.Atmosphere.EnableProdAlert,
                    c.Atmosphere.EnableUpcomingCall,
                    c.Atmosphere.EnableLowBalanceBeacon
                },
                setupPalette = new
                {
                    c.SetupPalette.Enabled,
                    c.SetupPalette.SyncWindowsAccent,
                    c.SetupPalette.SyncWindowsTerminal,
                    c.SetupPalette.SyncRedragonHid,
                    c.SetupPalette.SyncOpenRgb,
                    c.SetupPalette.SyncRazerChroma,
                    c.SetupPalette.DesktopMinIntervalMinutes,
                    c.SetupPalette.KeyboardIntensity,
                    c.SetupPalette.NightKeyboardIntensity,
                    currentAccentHex = _setupPaletteService.CurrentAccent.HasValue
                        ? Core.SetupPalette.PaletteColorUtils.ToHex(_setupPaletteService.CurrentAccent.Value)
                        : "#6366F1"
                },
                taskbar = new { enabled = c.Taskbar?.Enabled == true },
                wallpaper = new
                {
                    enabled = c.Wallpaper?.Enabled != false,
                    youtubeUrl = c.Wallpaper?.YoutubeUrl ?? "",
                    soundEnabled = c.Wallpaper?.SoundEnabled == true,
                    rotationSeconds = c.Wallpaper?.RotationSeconds ?? 300,
                    shuffle = c.Wallpaper?.Shuffle == true,
                    sources = (c.Wallpaper?.Sources ?? new List<WallpaperSourceConfig>())
                        .Select(s =>
                        {
                            string? preview = null;
                            if (string.Equals(s.Type, "image", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Type, "video", StringComparison.OrdinalIgnoreCase))
                            {
                                preview = _mediaHost.TryGetMediaUrl(s.Value);
                            }
                            return new
                            {
                                type = s.Type,
                                value = s.Value,
                                durationSeconds = s.DurationSeconds,
                                previewUrl = preview,
                            };
                        })
                        .ToList(),
                },
                autostart = AutostartManager.IsAutostartEnabled(),
                tokenExists = CredentialManager.Exists(),
                weeekTokenExists = CredentialManager.Exists(CredentialManager.WeeekTarget),
                calendarIcsExists = CredentialManager.Exists(CredentialManager.GoogleCalendarIcsTarget),
            },
        });
    }

    private void Save(JsonElement d)
    {
        var cfg = _configManager.CurrentConfig;

        var col = d.GetProperty("collage");
        if (col.TryGetProperty("enabled", out var colEn)) cfg.Collage.Enabled = colEn.GetBoolean();
        cfg.Collage.PinsDirectory = col.GetProperty("pinsDirectory").GetString() ?? cfg.Collage.PinsDirectory;
        cfg.Collage.Style = col.GetProperty("style").GetString() ?? "fan";
        cfg.Collage.ShuffleStyle = col.GetProperty("shuffleStyle").GetBoolean();
        cfg.Collage.IntervalMinutes = Math.Clamp(col.GetProperty("intervalMinutes").GetInt32(), 1, 1440);
        cfg.Collage.EnableColorHarmony = col.GetProperty("enableColorHarmony").GetBoolean();

        var w = d.GetProperty("weather");
        cfg.Weather.Enabled = w.GetProperty("enabled").GetBoolean();
        cfg.Weather.City = w.GetProperty("city").GetString() ?? string.Empty;
        cfg.Weather.Latitude = w.GetProperty("latitude").GetDouble();
        cfg.Weather.Longitude = w.GetProperty("longitude").GetDouble();

        var p = d.GetProperty("performance");
        cfg.Performance.AutoPauseFullscreen = p.GetProperty("autoPauseFullscreen").GetBoolean();
        cfg.Performance.AutoPauseBattery = p.GetProperty("autoPauseBattery").GetBoolean();
        cfg.Performance.FpsIdle = Math.Clamp(p.GetProperty("fpsIdle").GetInt32(), 20, 240);

        var l = d.GetProperty("launcher");
        cfg.Launcher.Enabled = l.GetProperty("enabled").GetBoolean();
        cfg.Launcher.HideDesktopIcons = l.GetProperty("hideDesktopIcons").GetBoolean();

        if (d.TryGetProperty("server", out var s) && s.TryGetProperty("sites", out var sitesEl))
        {
            cfg.Server.Sites = sitesEl.EnumerateArray()
                .Select(x => new ServerSiteConfig { Name = x.GetProperty("name").GetString() ?? "", Url = x.GetProperty("url").GetString() ?? "" })
                .Where(x => x.Name.Length > 0 && Uri.TryCreate(x.Url, UriKind.Absolute, out _))
                .ToList();
        }

        if (d.TryGetProperty("widgets", out var widgetsProp))
        {
            if (widgetsProp.TryGetProperty("showClock", out var sc)) cfg.Widgets.ShowClock = sc.GetBoolean();
            if (widgetsProp.TryGetProperty("clockPosition", out var cp) && cp.GetString() is { Length: > 0 } pos)
                cfg.Widgets.ClockPosition = pos;
            if (widgetsProp.TryGetProperty("clockFont", out var cf) && cf.GetString() is { Length: > 0 } font)
                cfg.Widgets.ClockFont = font;
        }

        if (d.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("mode", out var modeVal))
        {
            cfg.Context.Mode = modeVal.GetString() ?? cfg.Context.Mode;
        }

        if (d.TryGetProperty("atmosphere", out var atm))
        {
            if (atm.TryGetProperty("enabled", out var atmEn)) cfg.Atmosphere.Enabled = atmEn.GetBoolean();
            if (atm.TryGetProperty("enableProdAlert", out var prodEn)) cfg.Atmosphere.EnableProdAlert = prodEn.GetBoolean();
            if (atm.TryGetProperty("enableUpcomingCall", out var callEn)) cfg.Atmosphere.EnableUpcomingCall = callEn.GetBoolean();
            if (atm.TryGetProperty("enableLowBalanceBeacon", out var balEn)) cfg.Atmosphere.EnableLowBalanceBeacon = balEn.GetBoolean();
        }

        if (d.TryGetProperty("setupPalette", out var spProp))
        {
            if (spProp.TryGetProperty("enabled", out var spEn)) cfg.SetupPalette.Enabled = spEn.GetBoolean();
            if (spProp.TryGetProperty("syncWindowsAccent", out var winEn)) cfg.SetupPalette.SyncWindowsAccent = winEn.GetBoolean();
            if (spProp.TryGetProperty("syncWindowsTerminal", out var termEn)) cfg.SetupPalette.SyncWindowsTerminal = termEn.GetBoolean();
            if (spProp.TryGetProperty("syncRedragonHid", out var rdEn)) cfg.SetupPalette.SyncRedragonHid = rdEn.GetBoolean();
            if (spProp.TryGetProperty("syncOpenRgb", out var rgbEn)) cfg.SetupPalette.SyncOpenRgb = rgbEn.GetBoolean();
            if (spProp.TryGetProperty("desktopMinIntervalMinutes", out var dmi) && dmi.TryGetInt32(out var dmiVal)) cfg.SetupPalette.DesktopMinIntervalMinutes = Math.Clamp(dmiVal, 1, 1440);
            if (spProp.TryGetProperty("syncRazerChroma", out var rzEn)) cfg.SetupPalette.SyncRazerChroma = rzEn.GetBoolean();
            if (spProp.TryGetProperty("keyboardIntensity", out var kb) && kb.TryGetDouble(out var kbVal))
                cfg.SetupPalette.KeyboardIntensity = (float)kbVal;
            if (spProp.TryGetProperty("nightKeyboardIntensity", out var nkb) && nkb.TryGetDouble(out var nkbVal))
                cfg.SetupPalette.NightKeyboardIntensity = (float)nkbVal;
        }

        ApplyPluginFields(cfg, d);

        cfg.Taskbar ??= new TaskbarConfig();
        if (d.TryGetProperty("taskbar", out var taskbarProp) &&
            taskbarProp.TryGetProperty("enabled", out var taskbarEnabled))
        {
            cfg.Taskbar.Enabled = taskbarEnabled.GetBoolean();
        }

        cfg.Wallpaper ??= new WallpaperBackgroundConfig();
        if (d.TryGetProperty("wallpaper", out var wallpaperProp))
        {
            if (wallpaperProp.TryGetProperty("enabled", out var wpEn))
                cfg.Wallpaper.Enabled = wpEn.GetBoolean();
            if (wallpaperProp.TryGetProperty("soundEnabled", out var ytSound))
                cfg.Wallpaper.SoundEnabled = ytSound.GetBoolean();
            if (wallpaperProp.TryGetProperty("rotationSeconds", out var rot) && rot.TryGetInt32(out var rotVal))
                cfg.Wallpaper.RotationSeconds = Math.Clamp(rotVal, 15, 86400);
            if (wallpaperProp.TryGetProperty("shuffle", out var shuf))
                cfg.Wallpaper.Shuffle = shuf.GetBoolean();
            if (wallpaperProp.TryGetProperty("sources", out var srcArr))
            {
                cfg.Wallpaper.Sources = srcArr.EnumerateArray()
                    .Select(x => new WallpaperSourceConfig
                    {
                        Type = x.TryGetProperty("type", out var t) ? t.GetString() ?? "youtube" : "youtube",
                        Value = x.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "",
                        DurationSeconds = x.TryGetProperty("durationSeconds", out var dsec) && dsec.TryGetInt32(out var ds)
                            ? Math.Max(0, ds)
                            : 0,
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                    .ToList();
            }

            var firstYt = cfg.Wallpaper.Sources.FirstOrDefault(s =>
                string.Equals(s.Type, "youtube", StringComparison.OrdinalIgnoreCase));
            cfg.Wallpaper.YoutubeUrl = firstYt?.Value ?? string.Empty;
        }

        _configManager.Save(cfg);
        DesktopIconManager.SetIconsVisible(!cfg.Launcher.HideDesktopIcons);
        AutostartManager.SetAutostartEnabled(d.GetProperty("autostart").GetBoolean());
        _logger.LogInformation("Settings saved from web settings window.");
        Post(new { type = "settings_saved" });
    }

    private static void ApplyPluginFields(AppConfig cfg, JsonElement data)
    {
        if (!data.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object) return;

        cfg.Telegram ??= new Core.Telegram.TelegramConfig();

        foreach (var plugin in plugins.EnumerateObject())
        {
            var id = plugin.Name;
            var fields = plugin.Value;
            switch (id)
            {
                case "telegram":
                    if (fields.TryGetProperty("enabled", out var tgEn)) cfg.Telegram.Enabled = tgEn.GetBoolean();
                    if (fields.TryGetProperty("baseUrl", out var tgUrl))
                        cfg.Telegram.BaseUrl = tgUrl.GetString() ?? cfg.Telegram.BaseUrl;
                    break;

                case "today":
                    if (fields.TryGetProperty("enabled", out var todayEn)) cfg.Today.Enabled = todayEn.GetBoolean();
                    if (fields.TryGetProperty("googleCalendarIcsUrl", out var icsUrl))
                    {
                        var url = icsUrl.GetString()?.Trim() ?? string.Empty;
                        if (url.Length > 0 &&
                            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                        {
                            cfg.Today.GoogleCalendarIcsUrl = url;
                            CredentialManager.WriteSecret(url, CredentialManager.GoogleCalendarIcsTarget, "ics");
                        }
                    }
                    if (fields.TryGetProperty("weeekToken", out var weeekTok))
                    {
                        var token = weeekTok.GetString()?.Trim() ?? string.Empty;
                        if (token.Length > 0) CredentialManager.WriteSecret(token, CredentialManager.WeeekTarget);
                    }
                    break;

                case "server":
                    if (fields.TryGetProperty("enabled", out var srvEn)) cfg.Server.Enabled = srvEn.GetBoolean();
                    if (fields.TryGetProperty("sshAlias", out var alias))
                    {
                        var a = alias.GetString();
                        cfg.Server.SshAlias = string.IsNullOrWhiteSpace(a) ? cfg.Server.SshAlias : a.Trim();
                    }
                    if (fields.TryGetProperty("privacyMode", out var priv)) cfg.Server.PrivacyMode = priv.GetBoolean();
                    break;

                case "system":
                    if (fields.TryGetProperty("enabled", out var sysEn))
                        cfg.Widgets.ShowSystemMetrics = sysEn.GetBoolean();
                    break;

                case "timeweb":
                    if (fields.TryGetProperty("timewebEnabled", out var twEn)) cfg.Server.TimewebEnabled = twEn.GetBoolean();
                    if (fields.TryGetProperty("token", out var twTok))
                    {
                        var token = twTok.GetString()?.Trim() ?? string.Empty;
                        if (token.Length > 0) CredentialManager.WriteSecret(token, CredentialManager.DefaultTimewebTarget);
                    }
                    break;
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
