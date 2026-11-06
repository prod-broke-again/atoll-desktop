using System.Collections.Generic;

namespace LiveWallpaper.Core.Context;

public sealed class ContextConfig
{
    public string Mode { get; set; } = ContextMode.Auto;

    // Schedule for Auto mode
    public int WorkStartHour { get; set; } = 9;
    public int WorkEndHour { get; set; } = 19;
    public int NightStartHour { get; set; } = 21;
    public int NightEndHour { get; set; } = 7;

    public List<string> WorkProcesses { get; set; } = new()
    {
        "code", "devenv", "rider", "cursor", "idea64", "pycharm64",
        "wt", "windowsterminal", "cmd", "powershell", "slack", "telegram",
        "chrome", "msedge", "firefox", "browser"
    };

    public List<string> GameProcesses { get; set; } = new()
    {
        "steam", "steamwebhelper", "epicgameslauncher", "cs2", "dota2",
        "cyberpunk2077", "valorant", "overwatch", "gta5", "genshinimpact",
        "games"
    };

    public ModeWidgetsConfig WorkWidgets { get; set; } = new()
    {
        ShowClock = true,
        ShowWeather = true,
        ShowSystem = true,
        ShowServer = true,
        ShowLauncher = true,
        ShowMedia = true,
        ShowToday = true,
        ShowTelegram = true,
        PalettePreset = "work"
    };

    public ModeWidgetsConfig GameWidgets { get; set; } = new()
    {
        ShowClock = false,
        ShowWeather = false,
        ShowSystem = true,
        ShowServer = false,
        ShowLauncher = false,
        ShowMedia = true,
        ShowToday = false,
        ShowTelegram = false,
        PalettePreset = "game"
    };

    public ModeWidgetsConfig NightWidgets { get; set; } = new()
    {
        ShowClock = true,
        ShowWeather = true,
        ShowSystem = true,
        ShowServer = true,
        ShowLauncher = true,
        ShowMedia = true,
        ShowToday = true,
        ShowTelegram = true,
        PalettePreset = "night"
    };
}

public sealed class ModeWidgetsConfig
{
    public bool ShowClock { get; set; } = true;
    public bool ShowWeather { get; set; } = true;
    public bool ShowSystem { get; set; } = true;
    public bool ShowServer { get; set; } = true;
    public bool ShowLauncher { get; set; } = true;
    public bool ShowMedia { get; set; } = true;
    public bool ShowToday { get; set; } = true;
    public bool ShowTelegram { get; set; } = true;
    public string PalettePreset { get; set; } = "pin"; // "pin", "work", "game", "night"
}
