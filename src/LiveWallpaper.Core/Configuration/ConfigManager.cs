using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveWallpaper.Core.SetupPalette;
using LiveWallpaper.Core.Telegram;
using LiveWallpaper.Core.Today;
using LiveWallpaper.Core.Wallpaper;

namespace LiveWallpaper.Core.Configuration;

public sealed class ConfigManager : IConfigManager
{
    private readonly string _configFilePath;
    private readonly ConfigMigrationPipeline _migrationPipeline;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppConfig CurrentConfig { get; private set; }
    public string ConfigFilePath => _configFilePath;

    public event EventHandler<AppConfig>? ConfigChanged;

    public ConfigManager(string? customPath = null, ConfigMigrationPipeline? pipeline = null)
    {
        _configFilePath = customPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LiveWallpaper",
            "config.json");

        _migrationPipeline = pipeline ?? ConfigMigrationPipeline.CreateDefault();
        CurrentConfig = Load();
    }

    public AppConfig Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_configFilePath))
            {
                var defaultConfig = AppConfig.CreateDefault();
                ValidateAndSanitize(defaultConfig);
                Save(defaultConfig);
                CurrentConfig = defaultConfig;
                return defaultConfig;
            }

            try
            {
                var jsonContent = File.ReadAllText(_configFilePath);
                var jsonNode = JsonNode.Parse(jsonContent) as JsonObject;
                if (jsonNode == null)
                {
                    return FallbackToDefault();
                }

                var migratedNode = _migrationPipeline.ApplyMigrations(jsonNode, AppConfig.CurrentSchemaVersion);
                var config = migratedNode.Deserialize<AppConfig>(JsonOptions);
                if (config == null)
                {
                    return FallbackToDefault();
                }

                ValidateAndSanitize(config);
                CurrentConfig = config;
                return config;
            }
            catch
            {
                return FallbackToDefault();
            }
        }
    }

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidateAndSanitize(config);

        lock (_lock)
        {
            var directory = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _configFilePath + ".tmp";
            var jsonString = JsonSerializer.Serialize(config, JsonOptions);

            // Atomic file write using temporary file + replace
            File.WriteAllText(tempPath, jsonString);
            if (File.Exists(_configFilePath))
            {
                File.Replace(tempPath, _configFilePath, null);
            }
            else
            {
                File.Move(tempPath, _configFilePath);
            }

            CurrentConfig = config;
        }

        ConfigChanged?.Invoke(this, config);
    }

    public static void ValidateAndSanitize(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.Collage.PinsDirectory))
        {
            config.Collage.PinsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "Pins");
        }

        config.Collage.IntervalMinutes = Math.Clamp(config.Collage.IntervalMinutes, 1, 1440);
        config.Collage.MaxHistoryCount = Math.Clamp(config.Collage.MaxHistoryCount, 5, 200);

        var validStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fan", "bento", "hex", "diagonal", "polaroid", "masonry", "film" };
        if (!validStyles.Contains(config.Collage.Style))
        {
            config.Collage.Style = "fan";
        }

        config.Performance.FpsIdle = Math.Clamp(config.Performance.FpsIdle, 10, 60);
        config.Performance.FpsTransition = Math.Clamp(config.Performance.FpsTransition, 30, 144);
        config.Weather.RefreshIntervalMinutes = Math.Clamp(config.Weather.RefreshIntervalMinutes, 10, 1440);

        config.Launcher ??= new LauncherConfig();
        config.Launcher.ExcludedFolders ??= new List<string> { "Документы", "Проекты" };
        config.Today ??= new TodayConfig();
        config.Today.RefreshIntervalMinutes = Math.Clamp(config.Today.RefreshIntervalMinutes, 1, 120);
        config.Today.MaxEvents = Math.Clamp(config.Today.MaxEvents, 1, 50);
        config.Today.MaxTasks = Math.Clamp(config.Today.MaxTasks, 1, 50);

        config.Telegram ??= new TelegramConfig();
        config.Telegram.BaseUrl = TelegramLinks.TryNormalizeBaseUrl(config.Telegram.BaseUrl, out var telegramBase)
            ? telegramBase
            : TelegramLinks.DefaultBaseUrl;
        config.Telegram.PollIntervalSeconds = Math.Clamp(config.Telegram.PollIntervalSeconds, 15, 300);
        config.Telegram.BackfillIntervalMinutes = Math.Clamp(config.Telegram.BackfillIntervalMinutes, 5, 720);

        config.Taskbar ??= new TaskbarConfig();
        config.Wallpaper ??= new WallpaperBackgroundConfig();
        config.Wallpaper.YoutubeUrl = (config.Wallpaper.YoutubeUrl ?? string.Empty).Trim();
        if (config.Wallpaper.YoutubeUrl.Length > 2048)
            config.Wallpaper.YoutubeUrl = config.Wallpaper.YoutubeUrl[..2048];
        if (YouTubeUrl.TryGetVideoId(config.Wallpaper.YoutubeUrl, out var ytId))
            config.Wallpaper.YoutubeUrl = "https://www.youtube.com/watch?v=" + ytId;
        config.SetupPalette ??= new SetupPaletteConfig();
        config.SetupPalette.KeyboardIntensity = ClampUnit(config.SetupPalette.KeyboardIntensity, 0.85f);
        config.SetupPalette.NightKeyboardIntensity = ClampUnit(config.SetupPalette.NightKeyboardIntensity, 0.40f);

        config.Widgets ??= new WidgetsConfig();
        var validClockPositions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "top-left", "top-center", "top-right", "bottom-left", "bottom-center", "bottom-right"
        };
        if (!validClockPositions.Contains(config.Widgets.ClockPosition))
        {
            config.Widgets.ClockPosition = "top-center";
        }
        var validClockFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "default", "inter", "manrope", "mono", "serif"
        };
        if (!validClockFonts.Contains(config.Widgets.ClockFont))
        {
            config.Widgets.ClockFont = "default";
        }
    }

    private static float ClampUnit(float value, float fallback)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
            return fallback;
        return Math.Clamp(value, 0.05f, 1f);
    }

    private AppConfig FallbackToDefault()
    {
        var defaultConfig = AppConfig.CreateDefault();
        ValidateAndSanitize(defaultConfig);
        try
        {
            Save(defaultConfig);
        }
        catch { }
        CurrentConfig = defaultConfig;
        return defaultConfig;
    }
}
