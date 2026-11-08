using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiveWallpaper.App;

public sealed class WallpaperForm : Form
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_ERASEBKGND = 0x0014;

    private const int WM_HOTKEY = 0x0312;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;
    private const int MOD_ALT = 0x0001;
    private const int MOD_WIN = 0x0008;
    private const int VK_W = 0x57;
    private const int VK_P = 0x50;
    private const int VK_H = 0x48;
    private const int HOTKEY_ROTATE_ID = 9001;
    private const int HOTKEY_PAUSE_ID = 9002;
    private const int HOTKEY_PRIVACY_ID = 9003;

    private readonly IConfigManager _configManager;
    private readonly DesktopManager _desktopManager;
    private readonly MouseHookService _mouseHook;
    private readonly Services.Pins.ICollageEngineService _collageService;
    private readonly Services.Weather.IWeatherService _weatherService;
    private readonly Services.Telemetry.ITelemetryService _telemetryService;
    private readonly Services.Pause.IAutoPauseService _autoPauseService;
    private readonly Services.Media.IMediaService _mediaService;
    private readonly Services.Launcher.ILauncherService _launcherService;
    private readonly Services.Server.IServerWidgetService _serverService;
    private readonly Services.Context.IContextService _contextService;
    private readonly Services.Atmosphere.IAtmosphereService _atmosphereService;
    private readonly Services.Plugins.PluginHostService _pluginHost;
    private readonly ILogger<WallpaperForm> _logger;
    private readonly uint _taskbarCreatedMsg;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    private WebView2? _webView;
    private bool _isAttached;
    private bool _isInitialized;
    private bool _isShuttingDown;
    private bool _engineStarting;
    private bool _recoverQueued;
    private Point _screenOrigin = Point.Empty;
    private string? _mappedPinsDir;
    private readonly WallpaperMediaHost _wallpaperMedia = new();
    private readonly System.Windows.Forms.Timer _displayChangeDebounceTimer = new() { Interval = 750 };
    private readonly System.Windows.Forms.Timer _desktopParentDebounceTimer = new() { Interval = 400 };
    private static readonly Color HostBackdrop = Color.FromArgb(7, 8, 13);

    public WallpaperForm(
        IConfigManager configManager,
        DesktopManager desktopManager,
        MouseHookService mouseHook,
        Services.Pins.ICollageEngineService collageService,
        Services.Weather.IWeatherService weatherService,
        Services.Telemetry.ITelemetryService telemetryService,
        Services.Pause.IAutoPauseService autoPauseService,
        Services.Media.IMediaService mediaService,
        Services.Launcher.ILauncherService launcherService,
        Services.Server.IServerWidgetService serverService,
        Services.Context.IContextService contextService,
        Services.Atmosphere.IAtmosphereService atmosphereService,
        Services.Plugins.PluginHostService pluginHost,
        ILogger<WallpaperForm> logger)
    {
        _configManager = configManager;
        _desktopManager = desktopManager;
        _mouseHook = mouseHook;
        _collageService = collageService;
        _weatherService = weatherService;
        _telemetryService = telemetryService;
        _autoPauseService = autoPauseService;
        _mediaService = mediaService;
        _launcherService = launcherService;
        _serverService = serverService;
        _contextService = contextService;
        _atmosphereService = atmosphereService;
        _pluginHost = pluginHost;
        _logger = logger;

        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        _displayChangeDebounceTimer.Tick += OnDisplayChangeDebounced;
        _desktopParentDebounceTimer.Tick += OnDesktopParentDebounced;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = HostBackdrop;
        // Only the primary monitor: a "phantom" second display (e.g. a plugged-in VGA adapter)
        // must not stretch the wallpaper and shift the collage off-screen.
        var primary = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        Bounds = primary;
        _screenOrigin = primary.Location;

        InitializeComponents();
    }

    private void InitializeComponents()
    {
        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            // Default host color is white — without this, every WorkerW reparent/resize flashes a white screen.
            DefaultBackgroundColor = HostBackdrop
        };
        Controls.Add(_webView);

        _mouseHook.MouseMoved += OnMouseMoved;
        _mouseHook.MouseDown += OnMouseDown;
        _mouseHook.MouseUp += OnMouseUp;
        _mouseHook.MouseWheel += OnMouseWheel;
        // Only swallow Explorer input while the cursor is actually over the desktop layer —
        // never block TopMost prompts / other windows even if the web capture flag is stale.
        _mouseHook.ShouldConsumeAt = IsDesktopAt;
        _configManager.ConfigChanged += OnConfigChanged;
        _collageService.CollageChanged += OnCollageChanged;
        _weatherService.WeatherUpdated += OnWeatherUpdated;
        // Telemetry reaches the web via SystemPlugin → plugin host (not a direct subscription).
        _autoPauseService.PauseStateChanged += OnPauseStateChanged;
        _mediaService.MediaUpdated += OnMediaUpdated;
        _launcherService.LauncherUpdated += OnLauncherUpdated;
        _contextService.ModeChanged += OnContextModeChanged;
        _atmosphereService.AtmosphereUpdated += OnAtmosphereUpdated;
        _pluginHost.SnapshotReceived += OnPluginSnapshotReceived;
    }

    private void OnPluginSnapshotReceived(Core.Plugins.PluginSnapshot snapshot)
    {
        SendPluginSnapshotToWeb(snapshot);
    }

    /// <summary>
    /// Plugin snapshots normally go to the web as <c>{ type = pluginId, data = payload }</c>.
    /// The server widget predates the plugin host and expects its own wire shapes: the raw
    /// <c>ServerStateSnapshot</c> (it carries <c>type: "serverState"</c>) and metric points
    /// as <c>{ type: "serverMetrics", point }</c>.
    /// </summary>
    private void SendPluginSnapshotToWeb(Core.Plugins.PluginSnapshot snapshot)
    {
        if (string.Equals(snapshot.PluginId, Services.Plugins.ServerPlugin.PluginId, StringComparison.Ordinal))
        {
            SendWebMessage(snapshot.Payload);
            return;
        }

        if (string.Equals(snapshot.PluginId, Services.Plugins.ServerPlugin.MetricsPluginId, StringComparison.Ordinal))
        {
            SendWebMessage(new { type = "serverMetrics", point = snapshot.Payload });
            return;
        }

        if (string.Equals(snapshot.PluginId, Services.Plugins.SystemPlugin.PluginId, StringComparison.Ordinal) &&
            snapshot.Payload is Services.Telemetry.TelemetryData tel)
        {
            SendWebMessage(new
            {
                type = "telemetry",
                cpuPercent = tel.CpuPercent,
                ramPercent = tel.RamPercent,
                ramUsedGb = tel.RamUsedGb,
                ramTotalGb = tel.RamTotalGb,
                gpuPercent = tel.GpuPercent,
                gpuTempCelsius = tel.GpuTempCelsius,
                netDownloadKbps = tel.NetDownloadKbps,
                netUploadKbps = tel.NetUploadKbps
            });
            return;
        }

        SendWebMessage(new
        {
            type = snapshot.PluginId,
            data = snapshot.Payload
        });
    }

    private void OnContextModeChanged(object? sender, Services.Context.ContextModeChangedEventArgs e)
    {
        SendContextModeMessage(e.SelectedMode, e.EffectiveMode, e.Reason, e.Widgets);
    }

    private void SendContextModeMessage(string selectedMode, string effectiveMode, string reason, Core.Context.ModeWidgetsConfig widgets)
    {
        SendWebMessage(new
        {
            type = "context_mode",
            selectedMode,
            effectiveMode,
            reason,
            widgets
        });
    }

    private void OnAtmosphereUpdated(object? sender, Core.Atmosphere.AtmosphereSignalsState state)
    {
        SendAtmosphereMessage(state);
    }

    private void SendAtmosphereMessage(Core.Atmosphere.AtmosphereSignalsState state)
    {
        SendWebMessage(new
        {
            type = "atmosphere_signals",
            signals = state
        });
    }

    private void OnMediaUpdated(object? sender, Services.Media.MediaTrackInfo track)
    {
        SendWebMessage(new
        {
            type = "media",
            data = track
        });
    }

    private void OnLauncherUpdated(object? sender, Core.Launcher.LauncherSnapshot snapshot)
    {
        SendLauncherSnapshot(snapshot);
    }

    private void SendLauncherSnapshot(Core.Launcher.LauncherSnapshot? snapshot)
    {
        if (snapshot == null) return;
        SendWebMessage(new
        {
            type = "launcher",
            pinned = snapshot.Pinned,
            groups = snapshot.Groups
        });
    }

    private void HandleServerOpen(JsonElement root)
    {
        if (!root.TryGetProperty("target", out var targetProp)) return;
        var target = targetProp.GetString();

        if (target == "timeweb")
        {
            // Handled by the Timeweb plugin: it owns the "open the cloud console" action.
            _ = _pluginHost.DispatchActionAsync(
                Services.Plugins.TimewebPlugin.PluginId,
                Services.Plugins.TimewebPlugin.OpenConsoleAction,
                root.Clone());
        }
        else if (target == "ssh" || target == "site")
        {
            // Owned by the Server plugin: it opens the SSH terminal or the site URL.
            _ = _pluginHost.DispatchActionAsync(
                Services.Plugins.ServerPlugin.PluginId,
                Services.Plugins.ServerPlugin.ServerOpenAction,
                root.Clone());
        }
    }

    private void HandleLauncherAction(JsonElement root)
    {
        if (!root.TryGetProperty("action", out var actionProp) ||
            !root.TryGetProperty("id", out var idProp))
        {
            return;
        }

        var action = actionProp.GetString();
        var id = idProp.GetString();
        if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(id)) return;

        var kind = root.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : "item";
        var isGroup = string.Equals(kind, "group", StringComparison.OrdinalIgnoreCase);

        string? path = null;
        if (isGroup)
        {
            _launcherService.TryGetGroupPath(id, out path);
        }
        else
        {
            _launcherService.TryGetItemPath(id, out path);
        }

        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("launcher_action '{Action}' rejected: unknown id '{Id}'", action, id);
            return;
        }

        switch (action)
        {
            case "open":
                if (isGroup) _launcherService.OpenGroup(id);
                else ShellFileActions.Open(path);
                break;
            case "runas":
                if (!isGroup) ShellFileActions.RunAsAdmin(path);
                break;
            case "properties":
                ShellFileActions.Properties(path);
                break;
            case "open_location":
                ShellFileActions.OpenFileLocation(path);
                break;
            case "copy_path":
                ShellFileActions.CopyAsPath(path);
                break;
            case "cut":
                if (!isGroup) ShellFileActions.Cut(path);
                break;
            case "copy":
                if (!isGroup) ShellFileActions.Copy(path);
                break;
            case "delete":
                if (isGroup)
                {
                    if (ShellFileActions.Delete(path)) _launcherService.Refresh();
                }
                else
                {
                    _launcherService.DeleteItem(id);
                }
                break;
            case "pin_start":
                if (!isGroup) ShellFileActions.PinToStart(path);
                break;
            case "more":
                if (root.TryGetProperty("x", out var xProp) && root.TryGetProperty("y", out var yProp) &&
                    xProp.TryGetInt32(out var relX) && yProp.TryGetInt32(out var relY))
                {
                    ShellFileActions.ShowExtendedContextMenu(path, relX + _screenOrigin.X, relY + _screenOrigin.Y);
                }
                break;
        }
    }

    private void OnPauseStateChanged(object? sender, Services.Pause.PauseStateEventArgs e)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(() => OnPauseStateChanged(sender, e)); } catch { }
            return;
        }

        SendWebMessage(new
        {
            type = "pause_state",
            isPaused = e.IsPaused,
            reason = e.Reason
        });

        _telemetryService.SetPaused(e.IsPaused);

        if (_webView?.CoreWebView2 != null)
        {
            try
            {
                if (e.IsPaused)
                {
                    _ = _webView.CoreWebView2.TrySuspendAsync();
                }
                else
                {
                    _webView.CoreWebView2.Resume();
                }
            }
            catch { }
        }
    }

    private void OnWeatherUpdated(object? sender, Services.Weather.WeatherData weather)
    {
        SendWebMessage(new
        {
            type = "weather",
            data = weather
        });
    }

    private static readonly string[] ShuffleStyles = { "fan", "bento", "masonry", "polaroid", "hex", "diagonal", "film" };
    private string _lastStyle = string.Empty;

    private string PickStyle()
    {
        var cfg = _configManager.CurrentConfig.Collage;
        if (!cfg.ShuffleStyle)
        {
            _lastStyle = cfg.Style;
            return cfg.Style;
        }
        var pool = ShuffleStyles.Where(x => !string.Equals(x, _lastStyle, StringComparison.OrdinalIgnoreCase)).ToArray();
        _lastStyle = pool[Random.Shared.Next(pool.Length)];
        return _lastStyle;
    }

    private void OnCollageChanged(object? sender, Core.Collage.CollageSelectionResult result)
    {
        SendWebMessage(new
        {
            type = "collage",
            style = PickStyle(),
            ambientColor = result.AverageBackdropColor.ToHex(),
            items = result.SelectedPins.Select(p => new
            {
                id = p.FileName,
                url = $"https://pins.livewallpaper/{Uri.EscapeDataString(p.FileName)}",
                width = p.Width,
                height = p.Height,
                dominantColor = p.DominantColor.ToHex()
            }).ToList()
        });
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await EnsureEngineAsync();
    }

    private bool IsWebViewLive() =>
        _webView != null && !_webView.IsDisposed && _webView.CoreWebView2 != null;

    private void EnsureWebViewControl()
    {
        if (_webView != null && !_webView.IsDisposed) return;
        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = HostBackdrop
        };
        Controls.Add(_webView);
    }

    private void TearDownWebViewControl()
    {
        try
        {
            if (_webView == null) return;
            try
            {
                if (!_webView.IsDisposed)
                {
                    Controls.Remove(_webView);
                    _webView.Dispose();
                }
            }
            catch { }
            _webView = null;
        }
        catch { }
    }

    private async Task EnsureEngineAsync()
    {
        if (_isShuttingDown || IsDisposed) return;
        if (_engineStarting) return;
        if (IsWebViewLive())
        {
            if (!_isAttached && IsHandleCreated) AttachToDesktop();
            return;
        }

        _engineStarting = true;
        try
        {
            EnsureWebViewControl();
            _logger.LogInformation("Initializing WebView2 engine...");
            var userDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveWallpaper",
                "WebView2_Data");

            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataPath, options);
            await _webView!.EnsureCoreWebView2Async(env);
            _webView.CoreWebView2.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Autoplay)
                    e.State = CoreWebView2PermissionState.Allow;
            };

            _webView.CoreWebView2.WebMessageReceived += (s, args) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                    if (doc.RootElement.TryGetProperty("type", out var typeProp))
                    {
                        var type = typeProp.GetString();
                        if (type == "ready")
                        {
                            _logger.LogInformation("Web frontend reported ready handshake. Dispatching initial state...");
                            if (_configManager.CurrentConfig.Collage.Enabled)
                            {
                                if (_collageService.CurrentCollage != null)
                                {
                                    OnCollageChanged(this, _collageService.CurrentCollage);
                                }
                                else
                                {
                                    _collageService.RotateNow();
                                }
                            }

                            if (_weatherService.CurrentWeather != null)
                            {
                                OnWeatherUpdated(this, _weatherService.CurrentWeather);
                            }

                            // Re-hydrate plugin widgets (telegram, today, ...) from the snapshots
                            // the plugin host has cached so far. Server state/metrics flow through
                            // the Server plugin but keep their legacy wire shapes: state is sent
                            // explicitly with history below, metrics are not re-sent on ready.
                            foreach (var pluginSnapshot in _pluginHost.CurrentSnapshots)
                            {
                                if (string.Equals(pluginSnapshot.Key, Services.Plugins.ServerPlugin.PluginId, StringComparison.Ordinal) ||
                                    string.Equals(pluginSnapshot.Key, Services.Plugins.ServerPlugin.MetricsPluginId, StringComparison.Ordinal))
                                {
                                    continue;
                                }
                                SendPluginSnapshotToWeb(pluginSnapshot.Value);
                            }

                            if (_autoPauseService.IsPaused)
                            {
                                OnPauseStateChanged(this, new Services.Pause.PauseStateEventArgs(_autoPauseService.IsPaused, _autoPauseService.CurrentReason));
                            }

                            if (_mediaService.CurrentTrack != null)
                            {
                                OnMediaUpdated(this, _mediaService.CurrentTrack);
                            }

                            if (_launcherService.CurrentSnapshot != null)
                            {
                                SendLauncherSnapshot(_launcherService.CurrentSnapshot);
                            }

                            if (_configManager.CurrentConfig.Server.Enabled)
                            {
                                var initialServerSnapshot = _serverService.CurrentSnapshot with
                                {
                                    History = _serverService.History.ToList()
                                };
                                SendWebMessage(initialServerSnapshot);
                            }

                            SendContextModeMessage(
                                _contextService.SelectedMode,
                                _contextService.EffectiveMode,
                                _contextService.CurrentReason,
                                _contextService.CurrentWidgets);

                            SendAtmosphereMessage(_atmosphereService.CurrentState);

                            // Config drives the clock preset/font and widget visibility; without
                            // this the web side only sees config after the first settings save.
                            SendConfigToWeb(_configManager.CurrentConfig);
                        }
                        else if (type == "request_next_collage")
                        {
                            _collageService.RotateNow();
                        }
                        else if (type == "media_toggle_play")
                        {
                            _ = _mediaService.TogglePlayPauseAsync();
                        }
                        else if (type == "media_next")
                        {
                            _ = _mediaService.NextTrackAsync();
                        }
                        else if (type == "media_prev")
                        {
                            _ = _mediaService.PreviousTrackAsync();
                        }
                        else if (type == "media_seek" && doc.RootElement.TryGetProperty("position", out var posProp) && posProp.TryGetDouble(out var seekPos))
                        {
                            _ = _mediaService.SeekToAsync(seekPos);
                        }
                        else if (type == "media_open_wave")
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = "yandexmusic://home", // main page with My Vibe. NOT play-vibe: that deeplink breaks the Yandex Music router until restart (their bug)
                                    UseShellExecute = true
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to open Yandex Music wave deeplink.");
                            }
                        }
                        else if (type == "media_open_collection")
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = "yandexmusic://users/me/tracks", // the app rewrites /users/*/tracks → Collection
                                    UseShellExecute = true
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to open Yandex Music collection deeplink.");
                            }
                        }
                        else if (type == "launch" && doc.RootElement.TryGetProperty("id", out var idProp))
                        {
                            var id = idProp.GetString();
                            if (!string.IsNullOrEmpty(id))
                            {
                                _launcherService.Launch(id);
                            }
                        }
                        else if (type == "open_group" && doc.RootElement.TryGetProperty("id", out var groupIdProp))
                        {
                            var groupId = groupIdProp.GetString();
                            if (!string.IsNullOrEmpty(groupId))
                            {
                                _launcherService.OpenGroup(groupId);
                            }
                        }
                        else if (type == "create_folder" && doc.RootElement.TryGetProperty("name", out var folderNameProp))
                        {
                            var folderName = folderNameProp.GetString();
                            if (!string.IsNullOrWhiteSpace(folderName))
                            {
                                _launcherService.CreateFolder(folderName);
                            }
                        }
                        else if (type == "rename_folder" &&
                                 doc.RootElement.TryGetProperty("id", out var renameGroupIdProp) &&
                                 doc.RootElement.TryGetProperty("name", out var renameGroupNameProp))
                        {
                            var groupId = renameGroupIdProp.GetString();
                            var newName = renameGroupNameProp.GetString();
                            if (!string.IsNullOrEmpty(groupId) && !string.IsNullOrWhiteSpace(newName))
                            {
                                _launcherService.RenameFolder(groupId, newName);
                            }
                        }
                        else if (type == "move_item" && doc.RootElement.TryGetProperty("id", out var moveItemIdProp))
                        {
                            var itemId = moveItemIdProp.GetString();
                            if (!string.IsNullOrEmpty(itemId))
                            {
                                string? targetGroupId = null;
                                if (doc.RootElement.TryGetProperty("targetGroupId", out var tgtProp) &&
                                    tgtProp.ValueKind != JsonValueKind.Null)
                                {
                                    targetGroupId = tgtProp.GetString();
                                }
                                _launcherService.MoveItem(itemId, targetGroupId);
                            }
                        }
                        else if (type == "rename_item" &&
                                 doc.RootElement.TryGetProperty("id", out var renameItemIdProp) &&
                                 doc.RootElement.TryGetProperty("name", out var renameItemNameProp))
                        {
                            var itemId = renameItemIdProp.GetString();
                            var newName = renameItemNameProp.GetString();
                            if (!string.IsNullOrEmpty(itemId) && !string.IsNullOrWhiteSpace(newName))
                            {
                                _launcherService.RenameItem(itemId, newName);
                            }
                        }
                        else if (type == "delete_item" && doc.RootElement.TryGetProperty("id", out var deleteItemIdProp))
                        {
                            var itemId = deleteItemIdProp.GetString();
                            if (!string.IsNullOrEmpty(itemId))
                            {
                                _launcherService.DeleteItem(itemId);
                            }
                        }
                        else if (type == "launcher_action")
                        {
                            HandleLauncherAction(doc.RootElement);
                        }
                        else if (type == "server_open")
                        {
                            HandleServerOpen(doc.RootElement);
                        }
                        else if (type == "open_task")
                        {
                            // Routed through the plugin host; TodayPlugin resolves the WEEEK link and opens it.
                            _ = _pluginHost.DispatchActionAsync(
                                Services.Plugins.TodayPlugin.PluginId,
                                Services.Plugins.TodayPlugin.OpenTaskAction,
                                doc.RootElement.Clone());
                        }
                        else if (type == "open_telegram_chat" && doc.RootElement.TryGetProperty("id", out var tgIdProp) && tgIdProp.TryGetInt64(out _))
                        {
                            // Routed through the plugin host; TelegramPlugin resolves the chat link and opens it.
                            _ = _pluginHost.DispatchActionAsync(
                                Services.Plugins.TelegramPlugin.PluginId,
                                Services.Plugins.TelegramPlugin.OpenChatAction,
                                doc.RootElement.Clone());
                        }
                        else if (type == "set_mode" && doc.RootElement.TryGetProperty("mode", out var modeProp))
                        {
                            var newMode = modeProp.GetString();
                            if (!string.IsNullOrWhiteSpace(newMode))
                            {
                                _contextService.SetMode(newMode);
                            }
                        }
                        else if (type == "test_signal" &&
                                 doc.RootElement.TryGetProperty("signal", out var sigProp) &&
                                 doc.RootElement.TryGetProperty("active", out var actProp))
                        {
                            var sig = sigProp.GetString();
                            var act = actProp.GetBoolean();
                            if (!string.IsNullOrEmpty(sig))
                            {
                                _atmosphereService.TriggerTestSignal(sig, act);
                            }
                        }
                        else if (type == "input_capture" && doc.RootElement.TryGetProperty("active", out var captureProp))
                        {
                            _mouseHook.SetConsumeInput(captureProp.GetBoolean());
                        }
                        else if (type == "prompt_name" &&
                                 doc.RootElement.TryGetProperty("requestId", out var promptIdProp))
                        {
                            var requestId = promptIdProp.GetString() ?? string.Empty;
                            var title = doc.RootElement.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "Имя" : "Имя";
                            var label = doc.RootElement.TryGetProperty("label", out var lProp) ? lProp.GetString() : null;
                            var initial = doc.RootElement.TryGetProperty("initial", out var iProp) ? iProp.GetString() : null;
                            var confirm = doc.RootElement.TryGetProperty("confirmLabel", out var cProp) ? cProp.GetString() ?? "ОК" : "ОК";
                            var cancel = doc.RootElement.TryGetProperty("cancelLabel", out var xProp) ? xProp.GetString() ?? "Отмена" : "Отмена";

                            // Must run on UI thread; no owner — wallpaper HWND lives under WorkerW.
                            void ShowPrompt()
                            {
                                string? value = null;
                                try
                                {
                                    value = NamePromptForm.Ask(title, label, initial, confirm!, cancel!);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "prompt_name failed");
                                }
                                SendWebMessage(new { type = "prompt_name_result", requestId, value });
                            }

                            if (IsHandleCreated && InvokeRequired)
                                BeginInvoke(ShowPrompt);
                            else
                                ShowPrompt();
                        }
                        else if (type == "confirm" &&
                                 doc.RootElement.TryGetProperty("requestId", out var confirmIdProp) &&
                                 doc.RootElement.TryGetProperty("message", out var confirmMsgProp))
                        {
                            var requestId = confirmIdProp.GetString() ?? string.Empty;
                            var message = confirmMsgProp.GetString() ?? string.Empty;
                            var caption = doc.RootElement.TryGetProperty("title", out var capProp)
                                ? capProp.GetString() ?? "Подтверждение"
                                : "Подтверждение";

                            void ShowConfirm()
                            {
                                var ok = false;
                                try
                                {
                                    ok = MessageBox.Show(
                                        message,
                                        caption,
                                        MessageBoxButtons.YesNo,
                                        MessageBoxIcon.Warning,
                                        MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "confirm failed");
                                }
                                SendWebMessage(new { type = "confirm_result", requestId, confirmed = ok });
                            }

                            if (IsHandleCreated && InvokeRequired)
                                BeginInvoke(ShowConfirm);
                            else
                                ShowConfirm();
                        }
                    }
                }
                catch { }
            };

            ConfigureWebView2Settings();
            SetupVirtualHosts();
            LoadDefaultHtml();

            // Attach to desktop behind icons
            AttachToDesktop();

            // Register global hotkeys (Win+Alt+W to rotate, Win+Alt+P to toggle pause, Win+Alt+H for privacy)
            _ = RegisterHotKey(Handle, HOTKEY_ROTATE_ID, MOD_WIN | MOD_ALT, VK_W);
            _ = RegisterHotKey(Handle, HOTKEY_PAUSE_ID, MOD_WIN | MOD_ALT, VK_P);
            _ = RegisterHotKey(Handle, HOTKEY_PRIVACY_ID, MOD_WIN | MOD_ALT, VK_H);

            _mouseHook.Start();
            _isInitialized = true;
            _logger.LogInformation("WallpaperForm successfully attached behind desktop icons.");

            if (_configManager.CurrentConfig.Launcher.HideDesktopIcons)
            {
                _logger.LogInformation("Config requested desktop icons hidden. Hiding desktop icons...");
                DesktopIconManager.SetIconsVisible(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to initialize WallpaperForm and WebView2.");
        }
        finally
        {
            _engineStarting = false;
        }
    }

    private void QueueEngineRecovery()
    {
        if (_isShuttingDown || _recoverQueued) return;
        _recoverQueued = true;
        try
        {
            BeginInvoke(new Action(async () =>
            {
                _recoverQueued = false;
                if (_isShuttingDown || IsDisposed || !IsHandleCreated) return;
                _logger.LogInformation("Recovering wallpaper engine after host HWND loss...");
                await EnsureEngineAsync();
            }));
        }
        catch
        {
            _recoverQueued = false;
        }
    }

    private void ConfigureWebView2Settings()
    {
        if (_webView?.CoreWebView2 == null) return;

        var s = _webView.CoreWebView2.Settings;
#if !DEBUG
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.IsZoomControlEnabled = false;
#endif
        s.IsStatusBarEnabled = false;
    }

    private void SetupVirtualHosts()
    {
        if (_webView?.CoreWebView2 == null) return;

        _wallpaperMedia.Attach(_webView.CoreWebView2);

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(wwwroot))
        {
            Directory.CreateDirectory(wwwroot);
        }

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.livewallpaper",
            wwwroot,
            CoreWebView2HostResourceAccessKind.Allow);

        var iconCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveWallpaper",
            "IconCache");
        if (!Directory.Exists(iconCacheDir))
        {
            Directory.CreateDirectory(iconCacheDir);
        }

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "icons.livewallpaper",
            iconCacheDir,
            CoreWebView2HostResourceAccessKind.Allow);

        MapPinsHost();
    }

    private void MapPinsHost()
    {
        if (_webView?.CoreWebView2 == null) return;

        var pinsDir = _configManager.CurrentConfig.Collage.PinsDirectory;
        if (string.Equals(pinsDir, _mappedPinsDir, StringComparison.OrdinalIgnoreCase)) return;

        try { _webView.CoreWebView2.ClearVirtualHostNameToFolderMapping("pins.livewallpaper"); } catch { }
        _mappedPinsDir = null;

        if (Directory.Exists(pinsDir))
        {
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "pins.livewallpaper",
                pinsDir,
                CoreWebView2HostResourceAccessKind.Allow);
            _mappedPinsDir = pinsDir;
            _logger.LogInformation("Pins virtual host mapped to '{Dir}'.", pinsDir);
        }
        else
        {
            _logger.LogWarning("Pins directory '{Dir}' does not exist; images will not load.", pinsDir);
        }
    }

    private void LoadDefaultHtml()
    {
        var indexPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        if (File.Exists(indexPath))
        {
            _webView?.CoreWebView2.Navigate("https://app.livewallpaper/index.html");
        }
        else
        {
            // Inline fallback scaffold until wwwroot is bundled
            var fallbackHtml = @"
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'/>
<style>
  body, html { margin:0; padding:0; width:100%; height:100%; overflow:hidden; background:#0d1117; font-family:'Segoe UI', sans-serif; color:white; }
  #canvas { position:absolute; top:0; left:0; width:100%; height:100%; }
  .widget { position:absolute; top:40px; right:60px; background:rgba(22,27,34,0.7); backdrop-filter:blur(24px); padding:24px 32px; border-radius:20px; border:1px solid rgba(255,255,255,0.1); box-shadow:0 16px 32px rgba(0,0,0,0.4); }
  .time { font-size:3.8rem; font-weight:700; line-height:1; }
  .date { font-size:1.1rem; color:#8b949e; margin-top:8px; }
  .badge { position:absolute; bottom:40px; left:60px; background:rgba(22,27,34,0.6); backdrop-filter:blur(16px); padding:14px 22px; border-radius:14px; border:1px solid rgba(255,255,255,0.08); font-size:0.95rem; }
  .badge b { color:#58a6ff; }
</style>
</head>
<body>
<canvas id='canvas'></canvas>
<div class='widget'>
  <div id='time' class='time'>00:00:00</div>
  <div id='date' class='date'>...</div>
</div>
<div class='badge'>
  <b>LiveWallpaper Engine</b> | Windows 11 Foundation (M1) Active
</div>
<script>
  function updateTime() {
    const now = new Date();
    document.getElementById('time').textContent = now.toLocaleTimeString();
    document.getElementById('date').textContent = now.toLocaleDateString(undefined, { weekday:'long', month:'long', day:'numeric' });
  }
  setInterval(updateTime, 1000);
  updateTime();

  const canvas = document.getElementById('canvas');
  const ctx = canvas.getContext('2d');
  let w, h;
  function resize() { w = canvas.width = window.innerWidth; h = canvas.height = window.innerHeight; }
  window.addEventListener('resize', resize);
  resize();

  let mouseX = w/2, mouseY = h/2;
  window.chrome.webview.addEventListener('message', e => {
    try {
      const data = e.data;
      if (data && data.type === 'mouse') {
        mouseX = data.x; mouseY = data.y;
      }
    } catch {}
  });

  const particles = Array.from({length: 60}, () => ({
    x: Math.random() * w, y: Math.random() * h,
    vx: (Math.random() - 0.5) * 0.8, vy: (Math.random() - 0.5) * 0.8,
    r: Math.random() * 2 + 1
  }));

  function draw() {
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = '#0d1117';
    ctx.fillRect(0, 0, w, h);

    const grad = ctx.createRadialGradient(mouseX, mouseY, 10, mouseX, mouseY, 300);
    grad.addColorStop(0, 'rgba(88, 166, 255, 0.15)');
    grad.addColorStop(1, 'rgba(0, 0, 0, 0)');
    ctx.fillStyle = grad;
    ctx.fillRect(0, 0, w, h);

    ctx.fillStyle = 'rgba(255, 255, 255, 0.6)';
    particles.forEach(p => {
      p.x += p.vx; p.y += p.vy;
      if (p.x < 0) p.x = w; if (p.x > w) p.x = 0;
      if (p.y < 0) p.y = h; if (p.y > h) p.y = 0;
      ctx.beginPath();
      ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
      ctx.fill();
    });
    requestAnimationFrame(draw);
  }
  draw();
</script>
</body>
</html>";
            _webView?.CoreWebView2.NavigateToString(fallbackHtml);
        }
    }

    public void AttachToDesktop()
    {
        if (_isAttached) return;
        _desktopManager.AttachWindow(Handle);
        _isAttached = true;
        ApplyPrimaryBounds();
    }

    private void EnsureOnDesktop()
    {
        if (_isShuttingDown || !IsHandleCreated || IsDisposed)
        {
            return;
        }

        if (!IsWebViewLive())
        {
            QueueEngineRecovery();
            return;
        }

        var parent = GetParent(Handle);
        var current = _desktopManager.FindExistingParent();
        if (current == IntPtr.Zero)
        {
            _isAttached = false;
            AttachToDesktop();
            return;
        }

        if (parent == current)
        {
            return;
        }

        _logger.LogInformation("Wallpaper parent changed after a desktop refresh. Reattaching.");
        _isAttached = false;
        AttachToDesktop();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_isShuttingDown || IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => OnDisplaySettingsChanged(sender, e)); } catch { }
            return;
        }

        _logger.LogInformation("Display settings change detected. Debouncing 750ms...");
        _displayChangeDebounceTimer.Stop();
        _displayChangeDebounceTimer.Start();
    }

    private void OnDisplayChangeDebounced(object? sender, EventArgs e)
    {
        _displayChangeDebounceTimer.Stop();
        if (_isShuttingDown || IsDisposed || !IsHandleCreated) return;

        _logger.LogInformation("Debounce timer elapsed. Re-evaluating desktop parent and primary bounds...");

        var currentParent = GetParent(Handle);
        // Prefer FindExistingParent: LocateWallpaperParent's 0x052C spawn fights Explorer
        // while it is already rebuilding WorkerW for a wallpaper/display change.
        var targetParent = _desktopManager.FindExistingParent();
        if (targetParent == IntPtr.Zero)
        {
            targetParent = _desktopManager.LocateWallpaperParent();
        }

        if (currentParent == IntPtr.Zero || !IsWindow(currentParent) || currentParent != targetParent || !_isAttached)
        {
            _logger.LogInformation("Wallpaper parent invalid or shifted after display configuration. Reattaching (old={Old}, new={New}).", currentParent, targetParent);
            _isAttached = false;
            _desktopManager.AttachWindow(Handle);
            _isAttached = true;
        }

        ApplyPrimaryBounds();
    }

    private void OnDesktopParentDebounced(object? sender, EventArgs e)
    {
        _desktopParentDebounceTimer.Stop();
        EnsureOnDesktop();
    }

    private void ScheduleEnsureOnDesktop()
    {
        if (_isShuttingDown || IsDisposed) return;
        _desktopParentDebounceTimer.Stop();
        _desktopParentDebounceTimer.Start();
    }

    /// <summary>
    /// Positions the window over the primary monitor only. After SetParent the coordinates are
    /// relative to the WorkerW/Progman parent, whose origin is the virtual-screen top-left.
    /// </summary>
    private void ApplyPrimaryBounds()
    {
        var primary = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        _screenOrigin = primary.Location;

        var parent = GetParent(Handle);
        var x = primary.X;
        var y = primary.Y;
        if (parent != IntPtr.Zero && GetWindowRect(parent, out var pr))
        {
            x = primary.X - pr.Left;
            y = primary.Y - pr.Top;
        }

        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_NOCOPYBITS = 0x0100;

        _ = SetWindowPos(
            Handle,
            IntPtr.Zero,
            x,
            y,
            primary.Width,
            primary.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED | SWP_NOCOPYBITS);

        Width = primary.Width;
        Height = primary.Height;
        if (_webView != null)
        {
            _webView.Size = new Size(primary.Width, primary.Height);
        }

        _logger.LogInformation("Wallpaper bounds set to primary monitor {Bounds} (offset in parent {X},{Y}, size {Width}x{Height}).", primary, x, y, primary.Width, primary.Height);
    }

    private bool IsDesktopAt(int screenX, int screenY)
    {
        var primary = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        if (screenX < _screenOrigin.X || screenX > _screenOrigin.X + primary.Width ||
            screenY < _screenOrigin.Y || screenY > _screenOrigin.Y + primary.Height)
        {
            return false;
        }

        var hwnd = WindowFromPoint(new POINT { X = screenX, Y = screenY });
        if (hwnd == IntPtr.Zero) return false;
        if (hwnd == Handle) return true;
        var root = GetAncestor(hwnd, 2 /* GA_ROOT */);
        var buf = new char[64];
        var len = GetClassName(root, buf, buf.Length);
        var cls = len > 0 ? new string(buf, 0, len) : string.Empty;
        return cls is "Progman" or "WorkerW";
    }

    public void SendWebMessage(object payload)
    {
        if (IsDisposed || !IsHandleCreated) return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => SendWebMessage(payload));
            }
            catch { }
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            _webView?.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch { }
    }

    // Coordinates are sent in physical pixels relative to the primary monitor;
    // the web side divides them by devicePixelRatio.
    private void OnMouseMoved(object? sender, MouseCoordinates coords)
    {
        var primary = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        var isOutside = coords.X < _screenOrigin.X || coords.X > _screenOrigin.X + primary.Width ||
                        coords.Y < _screenOrigin.Y || coords.Y > _screenOrigin.Y + primary.Height;

        var overDesktop = !isOutside && IsDesktopAt(coords.X, coords.Y);

        var clampedX = Math.Clamp(coords.X, _screenOrigin.X, _screenOrigin.X + primary.Width);
        var clampedY = Math.Clamp(coords.Y, _screenOrigin.Y, _screenOrigin.Y + primary.Height);

        SendWebMessage(new
        {
            type = "mouse",
            x = clampedX - _screenOrigin.X,
            y = clampedY - _screenOrigin.Y,
            overDesktop
        });
    }

    private void OnMouseDown(object? sender, MouseButtonCoordinates coords)
    {
        if (IsHandleCreated && InvokeRequired)
        {
            try { BeginInvoke(() => OnMouseDown(sender, coords)); } catch { }
            return;
        }

        var primary = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        if (coords.X < _screenOrigin.X || coords.X > _screenOrigin.X + primary.Width ||
            coords.Y < _screenOrigin.Y || coords.Y > _screenOrigin.Y + primary.Height)
        {
            return;
        }

        // Ignore clicks that land on regular application windows above the desktop.
        if (!IsDesktopAt(coords.X, coords.Y)) return;
        SendWebMessage(new { type = "mouse_down", x = coords.X - _screenOrigin.X, y = coords.Y - _screenOrigin.Y, button = coords.Button });
    }

    private void OnMouseWheel(object? sender, MouseWheelCoordinates coords)
    {
        if (IsHandleCreated && InvokeRequired)
        {
            try { BeginInvoke(() => OnMouseWheel(sender, coords)); } catch { }
            return;
        }

        var primary = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        if (coords.X < _screenOrigin.X || coords.X > _screenOrigin.X + primary.Width ||
            coords.Y < _screenOrigin.Y || coords.Y > _screenOrigin.Y + primary.Height)
        {
            return;
        }

        if (!IsDesktopAt(coords.X, coords.Y)) return;
        SendWebMessage(new { type = "wheel", x = coords.X - _screenOrigin.X, y = coords.Y - _screenOrigin.Y, delta = coords.Delta });
    }

    private void OnMouseUp(object? sender, MouseButtonCoordinates coords)
    {
        if (IsHandleCreated && InvokeRequired)
        {
            try { BeginInvoke(() => OnMouseUp(sender, coords)); } catch { }
            return;
        }

        var primary = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        if (coords.X < _screenOrigin.X || coords.X > _screenOrigin.X + primary.Width ||
            coords.Y < _screenOrigin.Y || coords.Y > _screenOrigin.Y + primary.Height)
        {
            return;
        }

        if (!IsDesktopAt(coords.X, coords.Y)) return;
        SendWebMessage(new { type = "mouse_up", x = coords.X - _screenOrigin.X, y = coords.Y - _screenOrigin.Y, button = coords.Button });
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        if (IsHandleCreated && InvokeRequired)
        {
            try { BeginInvoke(() => OnConfigChanged(sender, config)); } catch { }
            return;
        }

        MapPinsHost();
        DesktopIconManager.SetIconsVisible(!config.Launcher.HideDesktopIcons);
        if (!config.Collage.Enabled)
        {
            SendWebMessage(new { type = "collage_clear" });
        }
        SendConfigToWeb(config);
    }

    private void SendConfigToWeb(AppConfig config)
    {
        if (_webView?.CoreWebView2 != null)
        {
            _wallpaperMedia.Attach(_webView.CoreWebView2);
        }

        SendWebMessage(new { type = "config", config = ConfigWebMapper.ToWebConfig(config, _wallpaperMedia) });
    }

    public void ForceClose()
    {
        _isShuttingDown = true;
        Close();
    }

    protected override void WndProc(ref Message m)
    {
        // Fresh window surfaces erase with the class brush (white) unless we claim the erase.
        if (m.Msg == WM_ERASEBKGND)
        {
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xFFF0) == SC_CLOSE)
        {
            if (!_isShuttingDown)
            {
                _logger.LogInformation("Ignored SC_CLOSE on WallpaperForm (e.g. Alt+F4 while desktop/webview has focus).");
                return;
            }
        }

        if (m.Msg == (int)_taskbarCreatedMsg)
        {
            _logger.LogInformation("Explorer restart detected via TaskbarCreated. Reattaching to WorkerW...");
            _isAttached = false;
            if (!IsWebViewLive())
            {
                QueueEngineRecovery();
                return;
            }

            AttachToDesktop();
            return;
        }

        if (m.Msg == WM_SETTINGCHANGE && !_isShuttingDown)
        {
            var setting = Marshal.PtrToStringUni(m.LParam);
            if (setting is "ImmersiveColorSet" or "SPI_SETDESKWALLPAPER")
            {
                // Coalesce Explorer's wallpaper/theme storm — immediate EnsureOnDesktop floods SetParent
                // and paints the default white WebView host between frames.
                try { BeginInvoke(ScheduleEnsureOnDesktop); } catch { }
            }
        }

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DPICHANGED)
        {
            _logger.LogInformation("Display resolution or DPI message received. Debouncing bounds update...");
            OnDisplaySettingsChanged(this, EventArgs.Empty);
            return;
        }

        if (m.Msg == WM_HOTKEY)
        {
            var id = m.WParam.ToInt32();
            if (id == HOTKEY_ROTATE_ID)
            {
                _logger.LogInformation("Global hotkey Win+Alt+W pressed: rotating collage now.");
                _collageService.RotateNow();
                return;
            }
            if (id == HOTKEY_PAUSE_ID)
            {
                _logger.LogInformation("Global hotkey Win+Alt+P pressed: toggling pause state.");
                _autoPauseService.ToggleUserPause();
                return;
            }
            if (id == HOTKEY_PRIVACY_ID)
            {
                _logger.LogInformation("Global hotkey Win+Alt+H pressed: toggling privacy mode.");
                var cfg = _configManager.CurrentConfig;
                cfg.Server.PrivacyMode = !cfg.Server.PrivacyMode;
                _configManager.Save(cfg);
                _serverService.SetPrivacyMode(cfg.Server.PrivacyMode);
                return;
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_isShuttingDown && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _logger.LogInformation("Ignored UserClosing on WallpaperForm (Alt+F4 or external WM_CLOSE).");
            return;
        }

        _isShuttingDown = true;
        _logger.LogWarning("WallpaperForm.OnFormClosing called! CloseReason: {CloseReason}. Stack: {StackTrace}", e.CloseReason, Environment.StackTrace);
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
        try { _configManager.ConfigChanged -= OnConfigChanged; } catch { }
        try { _collageService.CollageChanged -= OnCollageChanged; } catch { }
        try { _autoPauseService.PauseStateChanged -= OnPauseStateChanged; } catch { }
        try { _pluginHost.SnapshotReceived -= OnPluginSnapshotReceived; } catch { }

        _displayChangeDebounceTimer.Stop();
        _displayChangeDebounceTimer.Dispose();
        _desktopParentDebounceTimer.Stop();
        _desktopParentDebounceTimer.Dispose();

        // Unhook first: a stuck LL mouse hook freezes input for the whole desktop during teardown.
        try { _mouseHook.MouseMoved -= OnMouseMoved; } catch { }
        try { _mouseHook.MouseDown -= OnMouseDown; } catch { }
        try { _mouseHook.MouseUp -= OnMouseUp; } catch { }
        try { _mouseHook.MouseWheel -= OnMouseWheel; } catch { }
        _mouseHook.ShouldConsumeAt = null;
        _mouseHook.Dispose();

        _ = UnregisterHotKey(Handle, HOTKEY_ROTATE_ID);
        _ = UnregisterHotKey(Handle, HOTKEY_PAUSE_ID);
        _ = UnregisterHotKey(Handle, HOTKEY_PRIVACY_ID);

        try
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Stop();
            }
        }
        catch { }

        if (_isAttached)
        {
            DesktopManager.DetachWindow(Handle);
            _isAttached = false;
            DesktopManager.RefreshDesktopWallpaper();
        }

        // Timed Explorer call (see DesktopIconManager) — still keep it off the critical path
        // if Explorer is mid-rebuild from the wallpaper detach above.
        try
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { DesktopIconManager.RestoreOriginalState(); } catch { }
            });
        }
        catch
        {
            try { DesktopIconManager.RestoreOriginalState(); } catch { }
        }

        try
        {
            if (_webView != null)
            {
                Controls.Remove(_webView);
                _webView.Dispose();
                _webView = null;
            }
        }
        catch { }

        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // First create is handled by OnShown. Later recreates (WorkerW killed the child HWND)
        // need a fresh WebView2 — otherwise we paint only HostBackdrop (black desktop).
        if (_isShuttingDown || !_isInitialized) return;
        if (IsWebViewLive()) return;
        QueueEngineRecovery();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (!_isShuttingDown)
        {
            _logger.LogWarning(
                "WallpaperForm HWND destroyed unexpectedly (Explorer WorkerW rebuild?). Stack: {StackTrace}",
                Environment.StackTrace);
            _isAttached = false;
            TearDownWebViewControl();
        }
        else
        {
            _logger.LogWarning("WallpaperForm.OnHandleDestroyed called! Stack: {StackTrace}", Environment.StackTrace);
        }

        base.OnHandleDestroyed(e);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
