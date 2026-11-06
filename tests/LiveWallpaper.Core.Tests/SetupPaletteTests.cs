using System.Collections.Generic;
using System.Text.Json.Nodes;
using LiveWallpaper.Core.Collage;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.SetupPalette;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public class SetupPaletteTests
{
    [Fact]
    public void ExtractVividAccent_WithEmptyList_ReturnsDefaultAccent()
    {
        var accent = PaletteColorUtils.ExtractVividAccent(new List<PinItem>());
        Assert.True(accent.R > 0 || accent.G > 0 || accent.B > 0);
    }

    [Fact]
    public void ExtractVividAccent_WithPins_PicksVividDominantColor()
    {
        var pins = new List<PinItem>
        {
            new()
            {
                FilePath = "c:\\test\\gray.jpg",
                FileName = "gray.jpg",
                DominantColor = new RgbColor(50, 50, 50),
                Hue = 0,
                Saturation = 0,
                Brightness = 50,
                HueGroup = HueGroup.NeutralDark
            },
            new()
            {
                FilePath = "c:\\test\\vibrant_blue.jpg",
                FileName = "vibrant_blue.jpg",
                DominantColor = new RgbColor(30, 100, 240),
                Hue = 220,
                Saturation = 0.87f,
                Brightness = 94,
                HueGroup = HueGroup.Blue
            }
        };

        var accent = PaletteColorUtils.ExtractVividAccent(pins, HueGroup.Blue);
        var (h, s, v) = accent.ToHsv();

        // Should pick the vibrant blue and ensure high saturation
        Assert.InRange(h, 210, 230);
        Assert.True(s >= 0.65);
        Assert.True(v >= 0.70);
    }

    [Fact]
    public void GenerateWindowsAccentPalette_Returns32BytesWith8Shades()
    {
        var baseColor = new RgbColor(226, 36, 26); // Crimson
        var bytes = PaletteColorUtils.GenerateWindowsAccentPalette(baseColor);

        Assert.NotNull(bytes);
        Assert.Equal(32, bytes.Length);

        // Byte 12..15 should correspond to baseColor (Index 3)
        Assert.Equal(baseColor.R, bytes[12]);
        Assert.Equal(baseColor.G, bytes[13]);
        Assert.Equal(baseColor.B, bytes[14]);
        Assert.Equal(0, bytes[15]);

        // Index 0 (Lightest) should have higher RGB than baseColor
        Assert.True(bytes[0] >= baseColor.R);
        Assert.True(bytes[1] >= baseColor.G);
        Assert.True(bytes[2] >= baseColor.B);

        // Index 6 (Darkest) should have lower RGB than baseColor
        Assert.True(bytes[24] <= baseColor.R);
        Assert.True(bytes[25] <= baseColor.G);
        Assert.True(bytes[26] <= baseColor.B);
    }

    [Fact]
    public void ColorConversions_WorkCorrectly()
    {
        var color = new RgbColor(0x12, 0x34, 0x56);

        // ABGR: 0xFF563412
        var abgr = PaletteColorUtils.ToAbgr(color);
        Assert.Equal(0xFF563412u, abgr);

        // ARGB: 0xC4123456
        var argb = PaletteColorUtils.ToArgb(color, 0xC4);
        Assert.Equal(0xC4123456u, argb);

        // Hex
        var hex = PaletteColorUtils.ToHex(color);
        Assert.Equal("#123456", hex);

        var parsed = PaletteColorUtils.ParseHex("#123456");
        Assert.NotNull(parsed);
        Assert.Equal(color, parsed.Value);
    }

    [Fact]
    public void MigrationPipeline_V4ToV5_AddsSetupPaletteConfig()
    {
        var pipeline = ConfigMigrationPipeline.CreateDefault();
        var v4Json = new JsonObject
        {
            ["SchemaVersion"] = 4,
            ["Collage"] = new JsonObject { ["PinsDirectory"] = "C:\\Pins" }
        };

        var migrated = pipeline.ApplyMigrations(v4Json, currentVersion: 5);

        Assert.Equal(5, migrated["SchemaVersion"]?.GetValue<int>());
        Assert.True(migrated.ContainsKey("SetupPalette"));

        var palette = migrated["SetupPalette"] as JsonObject;
        Assert.NotNull(palette);
        Assert.True(palette["Enabled"]?.GetValue<bool>());
        Assert.True(palette["SyncWindowsAccent"]?.GetValue<bool>());
        Assert.True(palette["SyncWindowsTerminal"]?.GetValue<bool>());
        Assert.True(palette["SyncRedragonHid"]?.GetValue<bool>());
        Assert.False(palette["SyncOpenRgb"]?.GetValue<bool>());
        Assert.False(palette["SyncRazerChroma"]?.GetValue<bool>());
        Assert.Equal(6742, palette["OpenRgbPort"]?.GetValue<int>());
    }

    [Fact]
    public void ExtractNightAccent_PrefersWarmPin_AndCapsBrightness()
    {
        var pins = new List<PinItem>
        {
            Pin("blue", 30, 100, 240, HueGroup.Blue),
            Pin("orange", 255, 140, 0, HueGroup.Orange)
        };

        var accent = PaletteColorUtils.ExtractNightAccent(pins);
        var (h, s, v) = accent.ToHsv();

        Assert.True(PaletteColorUtils.IsWarmHue(h));
        Assert.InRange(h, 15f, 50f);
        Assert.True(v <= PaletteColorUtils.NightMaxValue + 0.01f);
        Assert.True(s >= 0.45f);
        Assert.True(accent.R > accent.B);
    }

    [Fact]
    public void ExtractNightAccent_WithOnlyCoolPins_ReturnsDimAmber()
    {
        var pins = new List<PinItem> { Pin("blue", 0, 40, 255, HueGroup.Blue) };

        var accent = PaletteColorUtils.ExtractNightAccent(pins);
        var (h, _, v) = accent.ToHsv();

        Assert.Equal(PaletteColorUtils.NightAmber, accent);
        Assert.Equal(PaletteColorUtils.NightAmber, PaletteColorUtils.ToneForNight(new RgbColor(0, 40, 255)));
        Assert.True(PaletteColorUtils.IsWarmHue(h));
        Assert.True(v <= 0.51f);
        Assert.True(accent.R > accent.G && accent.G > accent.B);
    }

    [Fact]
    public void ExtractNightAccent_Empty_ReturnsDimAmber()
    {
        Assert.Equal(PaletteColorUtils.NightAmber, PaletteColorUtils.ExtractNightAccent(new List<PinItem>()));
        Assert.Equal(PaletteColorUtils.NightAmber, PaletteColorUtils.ExtractNightAccent(null));
    }

    [Fact]
    public void Migration_V8ToV9_AddsKeyboardIntensity()
    {
        var pipeline = ConfigMigrationPipeline.CreateDefault();
        var v8 = new JsonObject
        {
            ["SchemaVersion"] = 8,
            ["SetupPalette"] = new JsonObject { ["Enabled"] = true }
        };

        var migrated = pipeline.ApplyMigrations(v8, currentVersion: 9);
        Assert.Equal(9, migrated["SchemaVersion"]?.GetValue<int>());

        var palette = migrated["SetupPalette"] as JsonObject;
        Assert.NotNull(palette);
        Assert.Equal(0.85, palette["KeyboardIntensity"]?.GetValue<double>() ?? 0, 2);
        Assert.Equal(0.40, palette["NightKeyboardIntensity"]?.GetValue<double>() ?? 0, 2);
    }

    [Fact]
    public void Migration_V9ToV10_StartsNightAt21_AndKeepsWidgets()
    {
        var pipeline = ConfigMigrationPipeline.CreateDefault();
        var v9 = new JsonObject
        {
            ["SchemaVersion"] = 9,
            ["Context"] = new JsonObject
            {
                ["Mode"] = "night",
                ["NightStartHour"] = 22,
                ["NightWidgets"] = new JsonObject
                {
                    ["ShowSystem"] = false,
                    ["ShowServer"] = false,
                    ["ShowLauncher"] = false
                }
            }
        };

        var migrated = pipeline.ApplyMigrations(v9, currentVersion: 10);
        var ctx = migrated["Context"] as JsonObject;
        Assert.NotNull(ctx);
        Assert.Equal("auto", ctx["Mode"]?.GetValue<string>());
        Assert.Equal(21, ctx["NightStartHour"]?.GetValue<int>());
        var widgets = ctx["NightWidgets"] as JsonObject;
        Assert.NotNull(widgets);
        Assert.True(widgets["ShowSystem"]?.GetValue<bool>());
        Assert.True(widgets["ShowServer"]?.GetValue<bool>());
        Assert.True(widgets["ShowLauncher"]?.GetValue<bool>());
    }

    private static PinItem Pin(string name, byte r, byte g, byte b, HueGroup group)
    {
        var color = new RgbColor(r, g, b);
        var (h, s, v) = color.ToHsv();
        return new PinItem
        {
            FilePath = name,
            FileName = name,
            DominantColor = color,
            Hue = h,
            Saturation = s,
            Brightness = v,
            HueGroup = group
        };
    }
}
