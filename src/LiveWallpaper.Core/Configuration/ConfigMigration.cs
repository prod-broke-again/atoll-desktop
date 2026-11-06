using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace LiveWallpaper.Core.Configuration;

public interface IConfigMigrator
{
    int FromVersion { get; }
    int ToVersion { get; }
    JsonObject Migrate(JsonObject node);
}

public sealed class ConfigMigrationPipeline
{
    private readonly List<IConfigMigrator> _migrators = new();

    public ConfigMigrationPipeline Register(IConfigMigrator migrator)
    {
        _migrators.Add(migrator);
        return this;
    }

    public JsonObject ApplyMigrations(JsonObject root, int currentVersion)
    {
        int detectedVersion = 0;
        if (root.TryGetPropertyValue("SchemaVersion", out var versionNode) && versionNode != null)
        {
            detectedVersion = versionNode.GetValue<int>();
        }

        while (detectedVersion < currentVersion)
        {
            var nextMigrator = _migrators.Find(m => m.FromVersion == detectedVersion);
            if (nextMigrator == null)
            {
                // No migration path found; default to current
                root["SchemaVersion"] = currentVersion;
                break;
            }

            root = nextMigrator.Migrate(root);
            detectedVersion = nextMigrator.ToVersion;
            root["SchemaVersion"] = detectedVersion;
        }

        return root;
    }

    public static ConfigMigrationPipeline CreateDefault()
    {
        var pipeline = new ConfigMigrationPipeline();
        pipeline.Register(new V0ToV1Migrator());
        pipeline.Register(new V1ToV2LauncherMigrator());
        pipeline.Register(new V2ToV3ServerMigrator());
        pipeline.Register(new V3ToV4ContextAndAtmosphereMigrator());
        pipeline.Register(new V4ToV5SetupPaletteMigrator());
        pipeline.Register(new V5ToV6RedragonHidMigrator());
        pipeline.Register(new V6ToV7TodayMigrator());
        pipeline.Register(new V7ToV8TelegramMigrator());
        pipeline.Register(new V8ToV9KeyboardIntensityMigrator());
        pipeline.Register(new V9ToV10NightScheduleMigrator());
        pipeline.Register(new V10ToV11ClockWidgetMigrator());
        pipeline.Register(new V11ToV12TaskbarMigrator());
        pipeline.Register(new V12ToV13WallpaperMigrator());
        pipeline.Register(new V13ToV14CollageAndWallpaperPlaylistMigrator());
        pipeline.Register(new V14ToV15WallpaperToggleAndLauncherFoldersMigrator());
        return pipeline;
    }
}

public sealed class V0ToV1Migrator : IConfigMigrator
{
    public int FromVersion => 0;
    public int ToVersion => 1;

    public JsonObject Migrate(JsonObject node)
    {
        return node;
    }
}

public sealed class V1ToV2LauncherMigrator : IConfigMigrator
{
    public int FromVersion => 1;
    public int ToVersion => 2;

    public JsonObject Migrate(JsonObject node)
    {
        if (!node.ContainsKey("Launcher"))
        {
            var launcher = new JsonObject
            {
                ["Enabled"] = true,
                ["HideDesktopIcons"] = false,
                ["ExcludedFolders"] = new JsonArray("Документы", "Проекты")
            };
            node["Launcher"] = launcher;
        }
        return node;
    }
}

public sealed class V2ToV3ServerMigrator : IConfigMigrator
{
    public int FromVersion => 2;
    public int ToVersion => 3;

    public JsonObject Migrate(JsonObject node)
    {
        if (!node.ContainsKey("Server"))
        {
            var sites = new JsonArray
            {
                new JsonObject
                {
                    ["Name"] = "api.example.com",
                    ["Url"] = "https://api.example.com"
                }
            };

            var server = new JsonObject
            {
                ["Enabled"] = true,
                ["SshAlias"] = "myserver",
                ["MetricsIntervalSec"] = 2,
                ["Sites"] = sites,
                ["TimewebEnabled"] = true,
                ["PrivacyMode"] = false
            };
            node["Server"] = server;
        }
        return node;
    }
}

public sealed class V3ToV4ContextAndAtmosphereMigrator : IConfigMigrator
{
    public int FromVersion => 3;
    public int ToVersion => 4;

    public JsonObject Migrate(JsonObject node)
    {
        if (!node.ContainsKey("Context"))
        {
            var context = new JsonObject
            {
                ["Mode"] = "auto",
                ["WorkStartHour"] = 9,
                ["WorkEndHour"] = 19,
                ["NightStartHour"] = 22,
                ["NightEndHour"] = 7,
                ["WorkProcesses"] = new JsonArray(
                    "code", "devenv", "rider", "cursor", "idea64", "pycharm64",
                    "wt", "windowsterminal", "cmd", "powershell", "slack", "telegram",
                    "chrome", "msedge", "firefox", "browser"
                ),
                ["GameProcesses"] = new JsonArray(
                    "steam", "steamwebhelper", "epicgameslauncher", "cs2", "dota2",
                    "cyberpunk2077", "valorant", "overwatch", "gta5", "genshinimpact", "games"
                ),
                ["WorkWidgets"] = new JsonObject
                {
                    ["ShowClock"] = true,
                    ["ShowWeather"] = true,
                    ["ShowSystem"] = true,
                    ["ShowServer"] = true,
                    ["ShowLauncher"] = true,
                    ["ShowMedia"] = true,
                    ["PalettePreset"] = "work"
                },
                ["GameWidgets"] = new JsonObject
                {
                    ["ShowClock"] = false,
                    ["ShowWeather"] = false,
                    ["ShowSystem"] = true,
                    ["ShowServer"] = false,
                    ["ShowLauncher"] = false,
                    ["ShowMedia"] = true,
                    ["PalettePreset"] = "game"
                },
                ["NightWidgets"] = new JsonObject
                {
                    ["ShowClock"] = true,
                    ["ShowWeather"] = true,
                    ["ShowSystem"] = false,
                    ["ShowServer"] = false,
                    ["ShowLauncher"] = false,
                    ["ShowMedia"] = true,
                    ["PalettePreset"] = "night"
                }
            };
            node["Context"] = context;
        }

        if (!node.ContainsKey("Atmosphere"))
        {
            var atmosphere = new JsonObject
            {
                ["Enabled"] = true,
                ["EnableProdAlert"] = true,
                ["EnableUpcomingCall"] = true,
                ["EnableLowBalanceBeacon"] = true,
                ["UpcomingCallMinutesBefore"] = 15,
                ["TestProdAlert"] = false,
                ["TestUpcomingCall"] = false,
                ["TestLowBalance"] = false
            };
            node["Atmosphere"] = atmosphere;
        }

        return node;
    }
}

public sealed class V4ToV5SetupPaletteMigrator : IConfigMigrator
{
    public int FromVersion => 4;
    public int ToVersion => 5;

    public JsonObject Migrate(JsonObject node)
    {
        if (!node.ContainsKey("SetupPalette"))
        {
            var palette = new JsonObject
            {
                ["Enabled"] = true,
                ["SyncWindowsAccent"] = true,
                ["SyncWindowsTerminal"] = true,
                ["SyncRedragonHid"] = true,
                ["SyncOpenRgb"] = false,
                ["SyncRazerChroma"] = false,
                ["OpenRgbHost"] = "127.0.0.1",
                ["OpenRgbPort"] = 6742,
                ["CustomAccentHex"] = null
            };
            node["SetupPalette"] = palette;
        }

        return node;
    }
}

public sealed class V5ToV6RedragonHidMigrator : IConfigMigrator
{
    public int FromVersion => 5;
    public int ToVersion => 6;

    public JsonObject Migrate(JsonObject node)
    {
        if (node.TryGetPropertyValue("SetupPalette", out var paletteNode) &&
            paletteNode is JsonObject palette)
        {
            if (!palette.ContainsKey("SyncRedragonHid"))
                palette["SyncRedragonHid"] = true;

            // DeathAdder Essential is single-color green; Chroma REST usually offline.
            if (!palette.ContainsKey("SyncRazerChroma"))
                palette["SyncRazerChroma"] = false;
        }
        else if (!node.ContainsKey("SetupPalette"))
        {
            node["SetupPalette"] = new JsonObject
            {
                ["Enabled"] = true,
                ["SyncWindowsAccent"] = true,
                ["SyncWindowsTerminal"] = true,
                ["SyncRedragonHid"] = true,
                ["SyncOpenRgb"] = false,
                ["SyncRazerChroma"] = false,
                ["OpenRgbHost"] = "127.0.0.1",
                ["OpenRgbPort"] = 6742
            };
        }

        return node;
    }
}

public sealed class V6ToV7TodayMigrator : IConfigMigrator
{
    public int FromVersion => 6;
    public int ToVersion => 7;

    public JsonObject Migrate(JsonObject node)
    {
        if (!node.ContainsKey("Today"))
        {
            node["Today"] = new JsonObject
            {
                ["Enabled"] = true,
                ["RefreshIntervalMinutes"] = 5,
                ["MaxEvents"] = 6,
                ["MaxTasks"] = 8,
                ["GoogleCalendarIcsUrl"] = null
            };
        }

        if (node.TryGetPropertyValue("Context", out var ctxNode) && ctxNode is JsonObject ctx)
        {
            EnsureShowToday(ctx, "WorkWidgets", defaultValue: true);
            EnsureShowToday(ctx, "GameWidgets", defaultValue: false);
            EnsureShowToday(ctx, "NightWidgets", defaultValue: true);
        }

        return node;
    }

    private static void EnsureShowToday(JsonObject context, string widgetsKey, bool defaultValue)
    {
        if (context.TryGetPropertyValue(widgetsKey, out var wNode) && wNode is JsonObject widgets)
        {
            if (!widgets.ContainsKey("ShowToday"))
                widgets["ShowToday"] = defaultValue;
        }
    }
}

public sealed class V7ToV8TelegramMigrator : IConfigMigrator
{
    public int FromVersion => 7;
    public int ToVersion => 8;

    public JsonObject Migrate(JsonObject node)
    {
        if (!node.ContainsKey("Telegram"))
        {
            node["Telegram"] = new JsonObject
            {
                ["Enabled"] = true,
                ["BaseUrl"] = "http://127.0.0.1:18765",
                ["PollIntervalSeconds"] = 45,
                ["BackfillIntervalMinutes"] = 15
            };
        }

        if (node.TryGetPropertyValue("Context", out var ctxNode) && ctxNode is JsonObject ctx)
        {
            EnsureShowTelegram(ctx, "WorkWidgets", defaultValue: true);
            EnsureShowTelegram(ctx, "GameWidgets", defaultValue: false);
            EnsureShowTelegram(ctx, "NightWidgets", defaultValue: true);
        }

        return node;
    }

    private static void EnsureShowTelegram(JsonObject context, string widgetsKey, bool defaultValue)
    {
        if (context.TryGetPropertyValue(widgetsKey, out var wNode) && wNode is JsonObject widgets)
        {
            if (!widgets.ContainsKey("ShowTelegram"))
                widgets["ShowTelegram"] = defaultValue;
        }
    }
}

public sealed class V8ToV9KeyboardIntensityMigrator : IConfigMigrator
{
    public int FromVersion => 8;
    public int ToVersion => 9;

    public JsonObject Migrate(JsonObject node)
    {
        if (node.TryGetPropertyValue("SetupPalette", out var paletteNode) &&
            paletteNode is JsonObject palette)
        {
            if (!palette.ContainsKey("KeyboardIntensity"))
                palette["KeyboardIntensity"] = 0.85;
            if (!palette.ContainsKey("NightKeyboardIntensity"))
                palette["NightKeyboardIntensity"] = 0.40;
        }

        return node;
    }
}

public sealed class V9ToV10NightScheduleMigrator : IConfigMigrator
{
    public int FromVersion => 9;
    public int ToVersion => 10;

    public JsonObject Migrate(JsonObject node)
    {
        if (node.TryGetPropertyValue("Context", out var ctxNode) && ctxNode is JsonObject ctx)
        {
            if (ctx.TryGetPropertyValue("NightStartHour", out var start) && start?.GetValue<int>() == 22)
                ctx["NightStartHour"] = 21;

            // Manual night was only turned on to recolor the keyboard; the clock owns that now.
            if (ctx.TryGetPropertyValue("Mode", out var mode) &&
                string.Equals(mode?.GetValue<string>(), "night", StringComparison.OrdinalIgnoreCase))
                ctx["Mode"] = "auto";

            if (ctx.TryGetPropertyValue("NightWidgets", out var widgetsNode) && widgetsNode is JsonObject widgets)
            {
                widgets["ShowSystem"] = true;
                widgets["ShowServer"] = true;
                widgets["ShowLauncher"] = true;
            }
        }

        return node;
    }
}

public sealed class V10ToV11ClockWidgetMigrator : IConfigMigrator
{
    public int FromVersion => 10;
    public int ToVersion => 11;

    public JsonObject Migrate(JsonObject node)
    {
        // The clock is a plain-text element placed by preset now. ClockPosition existed in the
        // schema but had no UI, so every stored value is the old default — reset it, and add
        // the font choice introduced together with the split of the agenda widget.
        if (node.TryGetPropertyValue("Widgets", out var wNode) && wNode is JsonObject widgets)
        {
            widgets["ClockPosition"] = "top-center";
            if (!widgets.ContainsKey("ClockFont"))
                widgets["ClockFont"] = "default";
        }

        return node;
    }
}

public sealed class V11ToV12TaskbarMigrator : IConfigMigrator
{
    public int FromVersion => 11;
    public int ToVersion => 12;

    public JsonObject Migrate(JsonObject node)
    {
        if (!node.ContainsKey("Taskbar"))
        {
            node["Taskbar"] = new JsonObject { ["Enabled"] = false };
        }

        return node;
    }
}

public sealed class V12ToV13WallpaperMigrator : IConfigMigrator
{
    public int FromVersion => 12;
    public int ToVersion => 13;

    public JsonObject Migrate(JsonObject node)
    {
        if (!node.ContainsKey("Wallpaper"))
        {
            node["Wallpaper"] = new JsonObject
            {
                ["YoutubeUrl"] = "",
                ["SoundEnabled"] = false
            };
        }

        return node;
    }
}

public sealed class V13ToV14CollageAndWallpaperPlaylistMigrator : IConfigMigrator
{
    public int FromVersion => 13;
    public int ToVersion => 14;

    public JsonObject Migrate(JsonObject node)
    {
        if (node["Collage"] is JsonObject collage && !collage.ContainsKey("Enabled"))
        {
            // Existing installs that already used collage keep it on; brand-new defaults are off via CreateDefault.
            collage["Enabled"] = true;
        }

        if (node["Wallpaper"] is JsonObject wallpaper)
        {
            if (!wallpaper.ContainsKey("RotationSeconds"))
                wallpaper["RotationSeconds"] = 300;
            if (!wallpaper.ContainsKey("Shuffle"))
                wallpaper["Shuffle"] = false;
            if (!wallpaper.ContainsKey("Sources"))
            {
                var sources = new JsonArray();
                var yt = wallpaper["YoutubeUrl"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(yt))
                {
                    sources.Add(new JsonObject
                    {
                        ["Type"] = "youtube",
                        ["Value"] = yt,
                        ["DurationSeconds"] = 0
                    });
                }
                wallpaper["Sources"] = sources;
            }
        }

        return node;
    }
}

/// <summary>
/// Wallpaper.Enabled toggle (default on) + stop excluding «Документы»/«Проекты» by default
/// so desktop folders and their files show up in the custom launcher.
/// </summary>
public sealed class V14ToV15WallpaperToggleAndLauncherFoldersMigrator : IConfigMigrator
{
    public int FromVersion => 14;
    public int ToVersion => 15;

    public JsonObject Migrate(JsonObject node)
    {
        if (node["Wallpaper"] is JsonObject wallpaper && !wallpaper.ContainsKey("Enabled"))
        {
            wallpaper["Enabled"] = true;
        }

        if (node["Launcher"] is JsonObject launcher && launcher["ExcludedFolders"] is JsonArray excluded)
        {
            // Old default hid common user folders from the launcher; clear that pair.
            var names = excluded
                .Select(x => x?.GetValue<string>()?.Trim() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
            if (names.Count == 2 &&
                names.Any(n => n.Equals("Документы", StringComparison.OrdinalIgnoreCase)) &&
                names.Any(n => n.Equals("Проекты", StringComparison.OrdinalIgnoreCase)))
            {
                launcher["ExcludedFolders"] = new JsonArray();
            }
        }

        return node;
    }
}
