using System;
using System.IO;
using System.Text.Json.Nodes;
using FluentAssertions;
using LiveWallpaper.Core.Configuration;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public sealed class ConfigManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFile;

    public ConfigManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LiveWallpaperTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFile = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_CreatesDefaultConfigAndSavesIt()
    {
        // Arrange & Act
        var manager = new ConfigManager(_configFile);

        // Assert
        File.Exists(_configFile).Should().BeTrue();
        manager.CurrentConfig.Should().NotBeNull();
        manager.CurrentConfig.SchemaVersion.Should().Be(AppConfig.CurrentSchemaVersion);
        manager.CurrentConfig.Collage.Style.Should().Be("fan");
        manager.CurrentConfig.Collage.IntervalMinutes.Should().Be(5);
    }

    [Fact]
    public void Save_PersistsChangesAndTriggersConfigChangedEvent()
    {
        // Arrange
        var manager = new ConfigManager(_configFile);
        AppConfig? notifiedConfig = null;
        manager.ConfigChanged += (s, cfg) => notifiedConfig = cfg;

        // Act
        var newConfig = manager.CurrentConfig;
        newConfig.Collage.Style = "bento";
        newConfig.Collage.IntervalMinutes = 10;
        manager.Save(newConfig);

        // Assert
        notifiedConfig.Should().NotBeNull();
        notifiedConfig!.Collage.Style.Should().Be("bento");

        var reloaded = new ConfigManager(_configFile);
        reloaded.CurrentConfig.Collage.Style.Should().Be("bento");
        reloaded.CurrentConfig.Collage.IntervalMinutes.Should().Be(10);
    }

    [Fact]
    public void ValidateAndSanitize_ClampsInvalidValues()
    {
        // Arrange
        var config = AppConfig.CreateDefault();
        config.Collage.Style = "unsupported_unknown_style";
        config.Collage.IntervalMinutes = -999;
        config.Performance.FpsIdle = 500;
        config.Performance.FpsTransition = 5;

        // Act
        ConfigManager.ValidateAndSanitize(config);

        // Assert
        config.Collage.Style.Should().Be("fan");
        config.Collage.IntervalMinutes.Should().Be(1);
        config.Performance.FpsIdle.Should().Be(60);
        config.Performance.FpsTransition.Should().Be(30);
    }

    [Fact]
    public void MigrationPipeline_UpgradesLegacySchema()
    {
        // Arrange: Legacy config with SchemaVersion 0 and obsolete field
        var legacyJson = new JsonObject
        {
            ["SchemaVersion"] = 0,
            ["Collage"] = new JsonObject
            {
                ["IntervalMinutes"] = 15,
                ["Style"] = "hex"
            }
        };

        var pipeline = new ConfigMigrationPipeline();
        pipeline.Register(new TestLegacyMigrator());

        // Act
        var migrated = pipeline.ApplyMigrations(legacyJson, currentVersion: 1);

        // Assert
        migrated["SchemaVersion"]?.GetValue<int>().Should().Be(1);
        migrated["MigratedField"]?.GetValue<string>().Should().Be("Applied");
    }

    [Fact]
    public void MigrationPipeline_UpgradesV1ToV2WithLauncher()
    {
        // Arrange
        var v1Json = new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["Collage"] = new JsonObject
            {
                ["IntervalMinutes"] = 5,
                ["Style"] = "fan"
            }
        };

        var pipeline = ConfigMigrationPipeline.CreateDefault();

        // Act
        var migrated = pipeline.ApplyMigrations(v1Json, currentVersion: AppConfig.CurrentSchemaVersion);

        // Assert
        migrated["SchemaVersion"]?.GetValue<int>().Should().Be(AppConfig.CurrentSchemaVersion);
        migrated["Launcher"]?.Should().NotBeNull();
        migrated["Launcher"]?["Enabled"]?.GetValue<bool>().Should().BeTrue();
        migrated["Launcher"]?["HideDesktopIcons"]?.GetValue<bool>().Should().BeFalse();
        var excluded = migrated["Launcher"]?["ExcludedFolders"]?.AsArray();
        excluded.Should().NotBeNull();
        excluded.Should().BeEmpty();
        migrated["Server"]?.Should().NotBeNull();
        migrated["Context"]?.Should().NotBeNull();
        migrated["Context"]?["Mode"]?.GetValue<string>().Should().Be("auto");
        migrated["Atmosphere"]?.Should().NotBeNull();
        migrated["Atmosphere"]?["EnableProdAlert"]?.GetValue<bool>().Should().BeTrue();
    }

    private sealed class TestLegacyMigrator : IConfigMigrator
    {
        public int FromVersion => 0;
        public int ToVersion => 1;

        public JsonObject Migrate(JsonObject node)
        {
            node["MigratedField"] = "Applied";
            return node;
        }
    }
}
