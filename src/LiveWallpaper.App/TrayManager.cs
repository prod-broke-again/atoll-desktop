using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Native;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.App;

public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly IConfigManager _configManager;
    private readonly Services.Pins.PinsIndexerService _indexer;
    private readonly Services.Context.IContextService _contextService;
    private readonly ILogger<TrayManager> _logger;
    private readonly Action _changeWallpaperCallback;
    private readonly Action _togglePauseCallback;
    private readonly Action _openSettingsCallback;
    private readonly Action _exitCallback;
    private bool _isPaused;

    private static readonly (string Key, string Title)[] LayoutNames =
    {
        ("fan", "Веер"),
        ("bento", "Бенто"),
        ("masonry", "Плитка (Pinterest)"),
        ("polaroid", "Полароиды"),
        ("hex", "Соты"),
        ("diagonal", "Диагональ"),
        ("film", "Киноплёнка"),
    };

    private static readonly (string Key, string Title)[] HueOptions =
    {
        ("Auto", "Авто-гармония"),
        ("NeutralDark", "⚫ Тёмные (Dark)"),
        ("Blue", "🔵 Синие (Blue)"),
        ("Red", "🔴 Красные (Red)"),
        ("Green", "🟢 Зелёные (Green)"),
        ("Purple", "🟣 Пурпурные (Purple)"),
        ("Orange", "🟠 Оранжевые (Orange)"),
        ("Cyan", "🩵 Бирюзовые (Cyan)"),
        ("Yellow", "🟡 Жёлтые (Yellow)"),
        ("NeutralLight", "⚪ Светлые (Light)"),
        ("NeutralMuted", "🩶 Приглушённые (Muted)"),
    };

    private static readonly (string Key, string Title)[] ContextModes =
    {
        ("auto", "Авто (по приложениям и времени)"),
        ("work", "Рабочий"),
        ("game", "Игровой"),
        ("night", "Ночной"),
    };

    private static string GetCategoryDisplayName(string category, int count)
    {
        var c = category.ToLowerInvariant();
        string icon = "📁 ";
        if (c.Contains("тачки") || c.Contains("car") || c.Contains("supra")) icon = "🏎️ ";
        else if (c.Contains("аниме") || c.Contains("anime") || c.Contains("мику")) icon = "🌸 ";
        else if (c.Contains("эстетика") || c.Contains("aesthetic") || c.Contains("неон")) icon = "🌌 ";
        else if (c.Contains("япония") || c.Contains("japan") || c.Contains("токио")) icon = "🇯🇵 ";
        else if (c.Contains("фоны") || c.Contains("обои") || c.Contains("wallpaper")) icon = "🖼️ ";
        else if (c.Contains("мем")) icon = "😄 ";
        else if (c.Contains("персонаж") || c.Contains("рисунк")) icon = "🎨 ";

        return $"{icon}{category} ({count})";
    }

    public TrayManager(
        IConfigManager configManager,
        Services.Pins.PinsIndexerService indexer,
        Services.Context.IContextService contextService,
        ILogger<TrayManager> logger,
        Action changeWallpaperCallback,
        Action togglePauseCallback,
        Action openSettingsCallback,
        Action exitCallback)
    {
        _configManager = configManager;
        _indexer = indexer;
        _contextService = contextService;
        _logger = logger;
        _changeWallpaperCallback = changeWallpaperCallback;
        _togglePauseCallback = togglePauseCallback;
        _openSettingsCallback = openSettingsCallback;
        _exitCallback = exitCallback;

        _notifyIcon = new NotifyIcon
        {
            Text = "LiveWallpaper Engine",
            Icon = AppBrand.Icon,
            Visible = true,
            ContextMenuStrip = CreateContextMenu()
        };

        _notifyIcon.DoubleClick += (s, e) => _openSettingsCallback();
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();

        var changeItem = new ToolStripMenuItem("Сменить коллаж сейчас", null, (s, e) =>
        {
            _logger.LogInformation("Tray requested immediate collage change.");
            _changeWallpaperCallback();
        });

        var pauseItem = new ToolStripMenuItem("Приостановить", null, (s, e) =>
        {
            _isPaused = !_isPaused;
            ((ToolStripMenuItem)s!).Text = _isPaused ? "Возобновить" : "Приостановить";
            _logger.LogInformation("Tray requested pause toggle. New state: {IsPaused}", _isPaused);
            _togglePauseCallback();
        });

        var openPinsItem = new ToolStripMenuItem("Открыть папку с пинами", null, (s, e) =>
        {
            try
            {
                var dir = _configManager.CurrentConfig.Collage.PinsDirectory;
                if (Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open pins folder.");
            }
        });

        var hideIconsItem = new ToolStripMenuItem("Скрывать значки рабочего стола", null, (s, e) =>
        {
            var cfg = _configManager.CurrentConfig;
            cfg.Launcher.HideDesktopIcons = !cfg.Launcher.HideDesktopIcons;
            _configManager.Save(cfg);
            DesktopIconManager.SetIconsVisible(!cfg.Launcher.HideDesktopIcons);
            ((ToolStripMenuItem)s!).Checked = cfg.Launcher.HideDesktopIcons;
            _logger.LogInformation("Tray toggled desktop icons visibility. Hidden: {Hidden}", cfg.Launcher.HideDesktopIcons);
        })
        {
            Checked = _configManager.CurrentConfig.Launcher.HideDesktopIcons
        };

        _configManager.ConfigChanged += (s, cfg) =>
        {
            try
            {
                hideIconsItem.Checked = cfg.Launcher.HideDesktopIcons;
            }
            catch { }
        };

        // Layout submenu: applies instantly (the web side re-lays out the current collage).
        var layoutMenu = new ToolStripMenuItem("Раскладка");
        var layoutItems = new List<ToolStripMenuItem>();
        foreach (var (key, title) in LayoutNames)
        {
            var item = new ToolStripMenuItem(title) { Tag = key };
            item.Click += (s, e) =>
            {
                var cfg = _configManager.CurrentConfig;
                cfg.Collage.Style = key;
                cfg.Collage.ShuffleStyle = false;
                _configManager.Save(cfg);
                _logger.LogInformation("Tray switched collage layout to {Style}.", key);
            };
            layoutItems.Add(item);
            layoutMenu.DropDownItems.Add(item);
        }
        layoutMenu.DropDownItems.Add(new ToolStripSeparator());
        var shuffleItem = new ToolStripMenuItem("Менять вместе с картинками");
        shuffleItem.Click += (s, e) =>
        {
            var cfg = _configManager.CurrentConfig;
            cfg.Collage.ShuffleStyle = !cfg.Collage.ShuffleStyle;
            _configManager.Save(cfg);
            if (cfg.Collage.ShuffleStyle) _changeWallpaperCallback();
        };
        layoutMenu.DropDownItems.Add(shuffleItem);

        void SyncLayoutChecks(AppConfig cfg)
        {
            foreach (var it in layoutItems)
            {
                it.Checked = !cfg.Collage.ShuffleStyle && string.Equals((string)it.Tag!, cfg.Collage.Style, StringComparison.OrdinalIgnoreCase);
            }
            shuffleItem.Checked = cfg.Collage.ShuffleStyle;
        }
        SyncLayoutChecks(_configManager.CurrentConfig);
        _configManager.ConfigChanged += (s, cfg) =>
        {
            try { SyncLayoutChecks(cfg); } catch { }
        };

        // Category submenu
        var categoryMenu = new ToolStripMenuItem("Категория");

        void RebuildCategoryMenu()
        {
            categoryMenu.DropDownItems.Clear();
            var currentCat = _configManager.CurrentConfig.Collage.SelectedCategory;
            var categories = _indexer.GetCategoriesWithCounts();
            int totalPins = categories.Sum(c => c.Count);

            var allItem = new ToolStripMenuItem($"Все темы ({totalPins})")
            {
                Checked = string.IsNullOrWhiteSpace(currentCat) ||
                          string.Equals(currentCat, "All", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(currentCat, "Все", StringComparison.OrdinalIgnoreCase)
            };
            allItem.Click += (s, e) =>
            {
                var cfg = _configManager.CurrentConfig;
                cfg.Collage.SelectedCategory = "All";
                _configManager.Save(cfg);
                _logger.LogInformation("Tray switched collage category to All.");
                _changeWallpaperCallback();
            };
            categoryMenu.DropDownItems.Add(allItem);

            if (categories.Count > 0)
            {
                categoryMenu.DropDownItems.Add(new ToolStripSeparator());
            }

            foreach (var (cat, count) in categories)
            {
                var isSelected = string.Equals(cat, currentCat, StringComparison.OrdinalIgnoreCase);
                var item = new ToolStripMenuItem(GetCategoryDisplayName(cat, count))
                {
                    Checked = isSelected
                };
                var capturedCat = cat;
                item.Click += (s, e) =>
                {
                    var cfg = _configManager.CurrentConfig;
                    cfg.Collage.SelectedCategory = capturedCat;
                    _configManager.Save(cfg);
                    _logger.LogInformation("Tray switched collage category to {Category}.", capturedCat);
                    _changeWallpaperCallback();
                };
                categoryMenu.DropDownItems.Add(item);
            }
        }

        RebuildCategoryMenu();
        _indexer.IndexUpdated += (s, e) =>
        {
            try
            {
                if (menu.IsHandleCreated)
                {
                    menu.BeginInvoke((Action)RebuildCategoryMenu);
                }
            }
            catch { }
        };
        menu.Opening += (s, e) =>
        {
            try { RebuildCategoryMenu(); } catch { }
        };

        // Hue Palette submenu
        var hueMenu = new ToolStripMenuItem("Палитра");
        var hueItems = new List<ToolStripMenuItem>();
        foreach (var (key, title) in HueOptions)
        {
            var item = new ToolStripMenuItem(title) { Tag = key };
            item.Click += (s, e) =>
            {
                var cfg = _configManager.CurrentConfig;
                cfg.Collage.SelectedHueGroup = key;
                _configManager.Save(cfg);
                _logger.LogInformation("Tray switched collage hue group to {Hue}.", key);
                _changeWallpaperCallback();
            };
            hueItems.Add(item);
            hueMenu.DropDownItems.Add(item);
        }

        void SyncHueChecks(AppConfig cfg)
        {
            var currentHue = string.IsNullOrWhiteSpace(cfg.Collage.SelectedHueGroup) ? "Auto" : cfg.Collage.SelectedHueGroup;
            foreach (var it in hueItems)
            {
                it.Checked = string.Equals((string)it.Tag!, currentHue, StringComparison.OrdinalIgnoreCase);
            }
        }
        SyncHueChecks(_configManager.CurrentConfig);
        _configManager.ConfigChanged += (s, cfg) =>
        {
            try { SyncHueChecks(cfg); } catch { }
        };

        // Context Mode submenu
        var modeMenu = new ToolStripMenuItem("Режим");
        var modeItems = new List<ToolStripMenuItem>();
        foreach (var (key, title) in ContextModes)
        {
            var item = new ToolStripMenuItem(title) { Tag = key };
            item.Click += (s, e) =>
            {
                _contextService.SetMode(key);
                _logger.LogInformation("Tray switched context mode to {Mode}.", key);
            };
            modeItems.Add(item);
            modeMenu.DropDownItems.Add(item);
        }

        void SyncModeChecks(string currentMode, string effectiveMode)
        {
            foreach (var it in modeItems)
            {
                var k = (string)it.Tag!;
                bool isSelected = string.Equals(k, currentMode, StringComparison.OrdinalIgnoreCase);
                it.Checked = isSelected;
                if (k == "auto" && isSelected)
                {
                    it.Text = $"Авто (сейчас: {Core.Context.ContextMode.GetDisplayName(effectiveMode)})";
                }
                else
                {
                    var baseTitle = ContextModes.FirstOrDefault(m => m.Key == k).Title ?? k;
                    it.Text = baseTitle;
                }
            }
        }

        SyncModeChecks(_contextService.SelectedMode, _contextService.EffectiveMode);
        _contextService.ModeChanged += (s, e) =>
        {
            try { SyncModeChecks(e.SelectedMode, e.EffectiveMode); } catch { }
        };

        var updateItem = new ToolStripMenuItem("Обновить обои", null, (s, e) =>
        {
            if (!WallpaperUpdate.TryStart(out var error))
            {
                MessageBox.Show(error, "LiveWallpaper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        });

        var settingsItem = new ToolStripMenuItem("Настройки", null, (s, e) =>
        {
            _openSettingsCallback();
        });

        var exitItem = new ToolStripMenuItem("Выход", null, (s, e) =>
        {
            _logger.LogInformation("Tray exit requested by user.");
            _exitCallback();
        });

        void SyncCollageEnabled(AppConfig cfg)
        {
            var on = cfg.Collage.Enabled;
            changeItem.Enabled = on;
            layoutMenu.Enabled = on;
            categoryMenu.Enabled = on;
            hueMenu.Enabled = on;
            openPinsItem.Enabled = on;
            changeItem.Visible = on;
            layoutMenu.Visible = on;
            categoryMenu.Visible = on;
            hueMenu.Visible = on;
            openPinsItem.Visible = on;
        }
        SyncCollageEnabled(_configManager.CurrentConfig);
        _configManager.ConfigChanged += (s, cfg) =>
        {
            try { SyncCollageEnabled(cfg); } catch { }
        };

        menu.Items.Add(changeItem);
        menu.Items.Add(layoutMenu);
        menu.Items.Add(categoryMenu);
        menu.Items.Add(hueMenu);
        menu.Items.Add(modeMenu);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(hideIconsItem);
        menu.Items.Add(openPinsItem);
        menu.Items.Add(updateItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
