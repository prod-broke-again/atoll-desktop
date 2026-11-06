using System;
using System.Collections.Generic;
using System.IO;
using LiveWallpaper.Core.Atmosphere;
using LiveWallpaper.Core.Context;
using LiveWallpaper.Core.SetupPalette;
using LiveWallpaper.Core.Telegram;
using LiveWallpaper.Core.Today;

namespace LiveWallpaper.Core.Configuration;

public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 15;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public CollageConfig Collage { get; set; } = new();

    public WeatherConfig Weather { get; set; } = new();

    public PerformanceConfig Performance { get; set; } = new();

    public WidgetsConfig Widgets { get; set; } = new();

    public LauncherConfig Launcher { get; set; } = new();

    public ServerConfig Server { get; set; } = new();

    public ContextConfig Context { get; set; } = new();

    public AtmosphereConfig Atmosphere { get; set; } = new();

    public SetupPaletteConfig SetupPalette { get; set; } = new();

    public TodayConfig Today { get; set; } = new();

    public TelegramConfig Telegram { get; set; } = new();

    public TaskbarConfig Taskbar { get; set; } = new();

    public WallpaperBackgroundConfig Wallpaper { get; set; } = new();

    public static AppConfig CreateDefault()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directPins = Path.Combine(userProfile, "Pictures", "Pins");
        var specialPins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pins");
        var defaultPins = Directory.Exists(directPins) ? directPins : (Directory.Exists(specialPins) ? specialPins : directPins);

        return new AppConfig
        {
            SchemaVersion = CurrentSchemaVersion,
            Collage = new CollageConfig
            {
                Enabled = false,
                PinsDirectory = defaultPins,
                IntervalMinutes = 5,
                Style = "fan",
                MaxHistoryCount = 20,
                EnableColorHarmony = true
            },
            Weather = new WeatherConfig
            {
                Enabled = false,
                City = string.Empty,
                Latitude = 0.0,
                Longitude = 0.0,
                Units = "metric",
                RefreshIntervalMinutes = 30
            },
            Performance = new PerformanceConfig
            {
                FpsIdle = 30,
                FpsTransition = 60,
                AutoPauseFullscreen = true,
                AutoPauseBattery = true,
                BlockedProcesses = new List<string> { "games.exe" }
            },
            Widgets = new WidgetsConfig
            {
                ShowClock = true,
                ShowDate = true,
                ShowWeather = true,
                ShowSystemMetrics = true,
                ClockPosition = "top-center",
                ClockFont = "default",
                SystemPosition = "bottom-right",
                WeatherPosition = "top-left"
            },
            Launcher = new LauncherConfig
            {
                Enabled = true,
                HideDesktopIcons = false,
                ExcludedFolders = new List<string> { "Документы", "Проекты" }
            },
            Server = new ServerConfig
            {
                Enabled = true,
                SshAlias = "myserver",
                MetricsIntervalSec = 2,
                Sites = new List<ServerSiteConfig>
                {
                    new() { Name = "api.example.com", Url = "https://api.example.com" }
                },
                TimewebEnabled = true,
                PrivacyMode = false
            },
            Context = new ContextConfig(),
            Atmosphere = new AtmosphereConfig(),
            SetupPalette = new SetupPaletteConfig(),
            Today = new TodayConfig(),
            Telegram = new TelegramConfig(),
            Taskbar = new TaskbarConfig(),
            Wallpaper = new WallpaperBackgroundConfig()
        };
    }
}

public sealed class CollageConfig
{
    /// <summary>Opt-in pin collage. Off by default — wallpapers are the primary background.</summary>
    public bool Enabled { get; set; }
    public string PinsDirectory { get; set; } = string.Empty;
    public int IntervalMinutes { get; set; } = 5;
    public string Style { get; set; } = "fan"; // fan, bento, hex, diagonal, polaroid, masonry, film
    /// <summary>Pick a random layout on every collage rotation instead of the fixed <see cref="Style"/>.</summary>
    public bool ShuffleStyle { get; set; }
    public int MaxHistoryCount { get; set; } = 20;
    public bool EnableColorHarmony { get; set; } = true;
    public string SelectedCategory { get; set; } = "All"; // "All" or specific category name
    public string SelectedHueGroup { get; set; } = "Auto"; // "Auto" or specific HueGroup name
}

public sealed class WeatherConfig
{
    public bool Enabled { get; set; }
    public string City { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double LongLongitude { get; set; }
    public double Longitude { get; set; }
    public string Units { get; set; } = "metric";
    public int RefreshIntervalMinutes { get; set; } = 30;
}

public sealed class PerformanceConfig
{
    public int FpsIdle { get; set; } = 30;
    public int FpsTransition { get; set; } = 60;
    public bool AutoPauseFullscreen { get; set; } = true;
    public bool AutoPauseBattery { get; set; } = true;
    public List<string> BlockedProcesses { get; set; } = new();
}

public sealed class WidgetsConfig
{
    public bool ShowClock { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowWeather { get; set; } = true;
    public bool ShowSystemMetrics { get; set; } = true;
    /// <summary>Clock preset: "top-left" | "top-center" | "top-right" | "bottom-left" | "bottom-center" | "bottom-right".</summary>
    public string ClockPosition { get; set; } = "top-center";
    /// <summary>Display font for the plain-text clock: "default" | "inter" | "manrope" | "mono" | "serif".</summary>
    public string ClockFont { get; set; } = "default";
    public string SystemPosition { get; set; } = "bottom-right";
    public string WeatherPosition { get; set; } = "top-left";
}

public sealed class LauncherConfig
{
    public bool Enabled { get; set; } = true;
    public bool HideDesktopIcons { get; set; }
    /// <summary>Folder names on the desktop to skip (empty = show all desktop folders).</summary>
    public List<string> ExcludedFolders { get; set; } = new();
}

public sealed class ServerConfig
{
    public bool Enabled { get; set; } = true;
    public string SshAlias { get; set; } = "myserver";
    public int MetricsIntervalSec { get; set; } = 2;
    public List<ServerSiteConfig> Sites { get; set; } = new()
    {
        new() { Name = "api.example.com", Url = "https://api.example.com" }
    };
    public bool TimewebEnabled { get; set; } = true;
    public bool PrivacyMode { get; set; }
}

public sealed class TaskbarConfig
{
    /// <summary>When on, the wallpaper starts its own taskbar and hides the Windows one.</summary>
    public bool Enabled { get; set; }
}

public sealed class WallpaperBackgroundConfig
{
    /// <summary>
    /// When on, play the custom playlist (<see cref="Sources"/>).
    /// When off (or playlist empty), the web layer shows the current Windows desktop wallpaper.
    /// </summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Legacy single YouTube URL (migrated into <see cref="Sources"/> when non-empty).</summary>
    public string YoutubeUrl { get; set; } = string.Empty;
    /// <summary>Off by default so a live wallpaper does not blast audio after login.</summary>
    public bool SoundEnabled { get; set; }
    /// <summary>Seconds each playlist item stays on screen before advancing (Opera-like cycle).</summary>
    public int RotationSeconds { get; set; } = 300;
    public bool Shuffle { get; set; }
    /// <summary>Ordered wallpaper playlist: youtube | video | image.</summary>
    public List<WallpaperSourceConfig> Sources { get; set; } = new();
}

public sealed class WallpaperSourceConfig
{
    /// <summary>"youtube" | "video" | "image"</summary>
    public string Type { get; set; } = "youtube";
    /// <summary>YouTube URL/id, or absolute local file path for video/image.</summary>
    public string Value { get; set; } = string.Empty;
    /// <summary>Optional per-item dwell time in seconds; 0 = use playlist <c>RotationSeconds</c>.</summary>
    public int DurationSeconds { get; set; }
}

public sealed class ServerSiteConfig
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
