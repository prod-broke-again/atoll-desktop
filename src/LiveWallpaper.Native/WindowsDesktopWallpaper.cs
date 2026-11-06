using System;
using System.Runtime.InteropServices;

namespace LiveWallpaper.Native;

/// <summary>Reads the current Windows desktop wallpaper path (SPI_GETDESKWALLPAPER).</summary>
public static class WindowsDesktopWallpaper
{
    private const int SpiGetDeskWallpaper = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(int action, int param, char[] value, int winIni);

    public static string? TryGetPath()
    {
        try
        {
            var buffer = new char[512];
            if (!SystemParametersInfo(SpiGetDeskWallpaper, buffer.Length, buffer, 0))
                return null;
            var end = Array.IndexOf(buffer, '\0');
            var path = new string(buffer, 0, end >= 0 ? end : buffer.Length).Trim();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }
}
