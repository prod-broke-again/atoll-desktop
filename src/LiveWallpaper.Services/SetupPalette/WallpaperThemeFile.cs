using System.Globalization;
using System.Text.Json;
using LiveWallpaper.Core.Collage;
using LiveWallpaper.Core.SetupPalette;

namespace LiveWallpaper.Services.SetupPalette;

/// <summary>
/// Writes the collage accent where TaskbarSpike can read it.
/// Failures propagate so the caller can log them; the wallpaper must keep running.
/// </summary>
internal static class WallpaperThemeFile
{
    public static void Write(RgbColor accent)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveWallpaper");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "theme.json");
        var tmp = Path.Combine(dir, "theme.json.tmp");
        var json = JsonSerializer.Serialize(new ThemeDto(
            PaletteColorUtils.ToHex(accent),
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private sealed record ThemeDto(string accent, string updatedAt);
}
