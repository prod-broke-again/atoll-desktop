using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveWallpaper.Core.Collage;
using LiveWallpaper.Core.SetupPalette;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.SetupPalette;

public static class WindowsTerminalSync
{
    private const string SchemeName = "LiveWallpaper";

    public static string? FindSettingsFilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 1. Packaged Store edition
        var packagedPath = Path.Combine(
            localAppData,
            "Packages",
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            "LocalState",
            "settings.json");
        if (File.Exists(packagedPath)) return packagedPath;

        // 2. Unpackaged / Preview edition
        var unpackagedPath = Path.Combine(localAppData, "Microsoft", "Windows Terminal", "settings.json");
        if (File.Exists(unpackagedPath)) return unpackagedPath;

        return null;
    }

    public static bool ApplyAccent(RgbColor accent, ILogger? logger = null)
    {
        var path = FindSettingsFilePath();
        if (path == null)
        {
            logger?.LogDebug("Windows Terminal settings.json not found on this system.");
            return false;
        }

        try
        {
            var jsonText = File.ReadAllText(path);
            var root = JsonNode.Parse(jsonText) as JsonObject;
            if (root == null) return false;

            var hex = PaletteColorUtils.ToHex(accent);
            var lightAccentHex = PaletteColorUtils.ToHex(PaletteColorUtils.Blend(accent, new RgbColor(255, 255, 255), 0.25));

            // Ensure schemes array
            if (!root.TryGetPropertyValue("schemes", out var schemesNode) || schemesNode is not JsonArray schemesArray)
            {
                schemesArray = new JsonArray();
                root["schemes"] = schemesArray;
            }

            // Find existing "LiveWallpaper" scheme or create new
            JsonObject? targetScheme = null;
            foreach (var item in schemesArray)
            {
                if (item is JsonObject obj &&
                    obj.TryGetPropertyValue("name", out var nameVal) &&
                    string.Equals(nameVal?.ToString(), SchemeName, StringComparison.OrdinalIgnoreCase))
                {
                    targetScheme = obj;
                    break;
                }
            }

            if (targetScheme == null)
            {
                targetScheme = new JsonObject
                {
                    ["name"] = SchemeName,
                    ["background"] = "#0C0E14",
                    ["foreground"] = "#E2E8F0",
                    ["cursorColor"] = hex,
                    ["selectionBackground"] = hex,
                    ["black"] = "#1E222D",
                    ["red"] = "#EF4444",
                    ["green"] = "#10B981",
                    ["yellow"] = "#F59E0B",
                    ["blue"] = hex,
                    ["purple"] = "#8B5CF6",
                    ["cyan"] = "#06B6D4",
                    ["white"] = "#F1F5F9",
                    ["brightBlack"] = "#475569",
                    ["brightRed"] = "#F87171",
                    ["brightGreen"] = "#34D399",
                    ["brightYellow"] = "#FBBF24",
                    ["brightBlue"] = lightAccentHex,
                    ["brightPurple"] = "#A78BFA",
                    ["brightCyan"] = "#22D3EE",
                    ["brightWhite"] = "#FFFFFF"
                };
                schemesArray.Add(targetScheme);
            }
            else
            {
                targetScheme["cursorColor"] = hex;
                targetScheme["selectionBackground"] = hex;
                targetScheme["blue"] = hex;
                targetScheme["brightBlue"] = lightAccentHex;
            }

            // Ensure profiles.defaults has colorScheme = "LiveWallpaper"
            if (!root.TryGetPropertyValue("profiles", out var profilesNode) || profilesNode is not JsonObject profilesObj)
            {
                profilesObj = new JsonObject();
                root["profiles"] = profilesObj;
            }

            if (!profilesObj.TryGetPropertyValue("defaults", out var defaultsNode) || defaultsNode is not JsonObject defaultsObj)
            {
                defaultsObj = new JsonObject();
                profilesObj["defaults"] = defaultsObj;
            }

            defaultsObj["colorScheme"] = SchemeName;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var updatedText = root.ToJsonString(options);

            // Write atomically via temporary file
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, updatedText);
            File.Move(tempPath, path, overwrite: true);

            logger?.LogInformation("Windows Terminal scheme updated with accent {Accent}.", hex);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to update Windows Terminal settings.json");
            return false;
        }
    }
}
