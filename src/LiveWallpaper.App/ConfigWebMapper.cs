using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Native;

namespace LiveWallpaper.App;

internal static class ConfigWebMapper
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    public static JsonObject ToWebConfig(AppConfig config, WallpaperMediaHost mediaHost)
    {
        mediaHost.SyncFromConfig(config.Wallpaper);
        var node = JsonSerializer.SerializeToNode(config, Options)!.AsObject();

        if (node["wallpaper"] is JsonObject wallpaper)
        {
            if (wallpaper["sources"] is JsonArray sources)
            {
                foreach (var entry in sources.OfType<JsonObject>())
                {
                    var type = entry["type"]?.GetValue<string>();
                    var value = entry["value"]?.GetValue<string>();
                    if (type is "video" or "image" && !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value))
                    {
                        var url = mediaHost.TryGetMediaUrl(value);
                        if (!string.IsNullOrEmpty(url)) entry["value"] = url;
                    }
                }
            }

            // Windows desktop wallpaper as fallback when custom playlist is off / empty.
            var systemPath = WindowsDesktopWallpaper.TryGetPath();
            if (!string.IsNullOrWhiteSpace(systemPath) && File.Exists(systemPath))
            {
                var sysUrl = mediaHost.TryGetMediaUrl(systemPath);
                if (!string.IsNullOrEmpty(sysUrl))
                    wallpaper["systemWallpaperUrl"] = sysUrl;
            }
        }

        return node;
    }
}
