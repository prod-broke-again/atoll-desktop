using System.Text.Json.Nodes;
using LiveWallpaper.Core.Configuration;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public sealed class ConfigMigrationTests
{
    [Fact]
    public void V10ToV11_ResetsClockPositionAndAddsFont()
    {
        var node = new JsonObject
        {
            ["SchemaVersion"] = 10,
            ["Widgets"] = new JsonObject
            {
                ["ShowClock"] = true,
                ["ClockPosition"] = "top-right"
            }
        };

        var result = new V10ToV11ClockWidgetMigrator().Migrate(node);
        var widgets = result["Widgets"]!.AsObject();

        // ClockPosition was never user-editable before (no UI), so the old default is reset.
        Assert.Equal("top-center", widgets["ClockPosition"]!.GetValue<string>());
        Assert.Equal("default", widgets["ClockFont"]!.GetValue<string>());
        Assert.True(widgets["ShowClock"]!.GetValue<bool>());
    }

    [Fact]
    public void V10ToV11_WithoutWidgetsNode_LeavesNodeUntouched()
    {
        var node = new JsonObject { ["SchemaVersion"] = 10 };

        var result = new V10ToV11ClockWidgetMigrator().Migrate(node);

        Assert.False(result.ContainsKey("Widgets"));
    }

    [Fact]
    public void Pipeline_MigratesV10ConfigToCurrentVersion()
    {
        var node = new JsonObject
        {
            ["SchemaVersion"] = 10,
            ["Widgets"] = new JsonObject { ["ClockPosition"] = "top-right" }
        };

        var result = ConfigMigrationPipeline.CreateDefault().ApplyMigrations(node, AppConfig.CurrentSchemaVersion);

        Assert.Equal(AppConfig.CurrentSchemaVersion, result["SchemaVersion"]!.GetValue<int>());
        Assert.Equal("top-center", result["Widgets"]!["ClockPosition"]!.GetValue<string>());
        Assert.False(result["Taskbar"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal("", result["Wallpaper"]!["YoutubeUrl"]!.GetValue<string>());
        Assert.False(result["Wallpaper"]!["SoundEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void V11ToV12_AddsTaskbarOff()
    {
        var node = new JsonObject { ["SchemaVersion"] = 11 };

        var result = new V11ToV12TaskbarMigrator().Migrate(node);

        Assert.False(result["Taskbar"]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void V12ToV13_AddsMutedYoutubeWallpaper()
    {
        var node = new JsonObject { ["SchemaVersion"] = 12 };

        var result = new V12ToV13WallpaperMigrator().Migrate(node);

        Assert.Equal("", result["Wallpaper"]!["YoutubeUrl"]!.GetValue<string>());
        Assert.False(result["Wallpaper"]!["SoundEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void V13ToV14_EnablesCollageForExistingInstallsAndPlaylistFields()
    {
        var node = new JsonObject
        {
            ["SchemaVersion"] = 13,
            ["Collage"] = new JsonObject(),
            ["Wallpaper"] = new JsonObject { ["YoutubeUrl"] = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" },
        };

        var result = new V13ToV14CollageAndWallpaperPlaylistMigrator().Migrate(node);

        Assert.True(result["Collage"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(300, result["Wallpaper"]!["RotationSeconds"]!.GetValue<int>());
        Assert.NotNull(result["Wallpaper"]!["Sources"]!.AsArray());
        Assert.Single(result["Wallpaper"]!["Sources"]!.AsArray());
    }
}
