namespace LiveWallpaper.Core.SetupPalette;

public sealed class SetupPaletteConfig
{
    public bool Enabled { get; set; } = true;

    public bool SyncWindowsAccent { get; set; } = true;

    public bool SyncWindowsTerminal { get; set; } = true;

    /// <summary>Direct HID control for Redragon/Sinowealth 258A:0049 (Garen Pro and siblings).</summary>
    public bool SyncRedragonHid { get; set; } = true;

    public bool SyncOpenRgb { get; set; }

    /// <summary>Requires Razer Synapse Chroma SDK. DeathAdder Essential is single-color green — leave off.</summary>
    public bool SyncRazerChroma { get; set; }

    public string OpenRgbHost { get; set; } = "127.0.0.1";

    public int OpenRgbPort { get; set; } = 6742;

    public string? CustomAccentHex { get; set; }

    /// <summary>
    /// Windows accent + Terminal are expensive (every app repaints), so they follow the collage
    /// at most this often. The keyboard still follows every collage change (cheap, faded).
    /// </summary>
    public int DesktopMinIntervalMinutes { get; set; } = 20;

    /// <summary>Skip Windows/Terminal updates unless the hue moved at least this many degrees.</summary>
    public int DesktopHueThresholdDegrees { get; set; } = 30;

    /// <summary>Cross-fade the keyboard to the new colour instead of switching instantly.</summary>
    public bool FadeKeyboard { get; set; } = true;

    /// <summary>HID scale for the keyboard outside night mode. 1 = full channel.</summary>
    public float KeyboardIntensity { get; set; } = 0.85f;

    /// <summary>HID scale while the context mode is night. Applied on top of the dim warm accent.</summary>
    public float NightKeyboardIntensity { get; set; } = 0.40f;
}
