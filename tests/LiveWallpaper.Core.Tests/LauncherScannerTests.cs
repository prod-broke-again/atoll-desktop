using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiveWallpaper.Core.Launcher;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public sealed class LauncherScannerTests : IDisposable
{
    private static readonly string[] ExcludedDocAndProjects = ["Документы", "проекты"];
    private static readonly string[] ExpectedPinned = ["Chrome", "Yandex"];

    private readonly string _tempDesktop;
    private readonly string _tempCommonDesktop;

    public LauncherScannerTests()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        _tempDesktop = Path.Combine(Path.GetTempPath(), $"LwTestDesk_{testId}");
        _tempCommonDesktop = Path.Combine(Path.GetTempPath(), $"LwTestCommonDesk_{testId}");

        Directory.CreateDirectory(_tempDesktop);
        Directory.CreateDirectory(_tempCommonDesktop);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDesktop)) Directory.Delete(_tempDesktop, true); } catch { }
        try { if (Directory.Exists(_tempCommonDesktop)) Directory.Delete(_tempCommonDesktop, true); } catch { }
    }

    [Fact]
    public void Scan_ExcludesSpecifiedFolders()
    {
        // Arrange
        var gamesDir = Directory.CreateDirectory(Path.Combine(_tempDesktop, "Игры")).FullName;
        File.WriteAllText(Path.Combine(gamesDir, "Game.lnk"), "dummy");

        var docDir = Directory.CreateDirectory(Path.Combine(_tempDesktop, "Документы")).FullName;
        File.WriteAllText(Path.Combine(docDir, "Report.lnk"), "dummy");

        var projectsDir = Directory.CreateDirectory(Path.Combine(_tempDesktop, "Проекты")).FullName;
        File.WriteAllText(Path.Combine(projectsDir, "Course.lnk"), "dummy");

        var scanner = new LauncherScanner(excludedFolderNames: ExcludedDocAndProjects);

        // Act
        var result = scanner.Scan(_tempDesktop);

        // Assert
        result.Snapshot.Groups.Should().HaveCount(1);
        result.Snapshot.Groups[0].Name.Should().Be("Игры");
        result.Snapshot.Groups[0].Items.Should().HaveCount(1);
        result.Snapshot.Groups[0].Items[0].Name.Should().Be("Game");
    }

    [Fact]
    public void Scan_KeepsEmptyFolders_AndFoldersWithDocuments()
    {
        // Arrange — empty folders and folders with docs all appear as tiles
        Directory.CreateDirectory(Path.Combine(_tempDesktop, "ПустаяПапка"));
        var onlyTxtDir = Directory.CreateDirectory(Path.Combine(_tempDesktop, "ТолькоТекст")).FullName;
        File.WriteAllText(Path.Combine(onlyTxtDir, "notes.txt"), "dummy text");

        var validDir = Directory.CreateDirectory(Path.Combine(_tempDesktop, "Разработка")).FullName;
        File.WriteAllText(Path.Combine(validDir, "App.exe"), "dummy exe");

        var scanner = new LauncherScanner();

        // Act
        var result = scanner.Scan(_tempDesktop);

        // Assert
        result.Snapshot.Groups.Should().HaveCount(3);
        result.Snapshot.Groups.Select(g => g.Name).Should().BeEquivalentTo("ПустаяПапка", "ТолькоТекст", "Разработка");
        result.Snapshot.Groups.Single(g => g.Name == "Разработка").Items.Should().HaveCount(1);
        result.Snapshot.Groups.Single(g => g.Name == "ТолькоТекст").Items.Should().HaveCount(1);
        result.Snapshot.Groups.Single(g => g.Name == "ТолькоТекст").Items[0].Name.Should().Be("notes");
        result.Snapshot.Groups.Single(g => g.Name == "ПустаяПапка").Items.Should().BeEmpty();
    }

    [Fact]
    public void Scan_IncludesShortcutsAndDocuments_SkipsDesktopIni()
    {
        // Arrange
        var groupDir = Directory.CreateDirectory(Path.Combine(_tempDesktop, "Медиа")).FullName;
        File.WriteAllText(Path.Combine(groupDir, "Player.exe"), "exe");
        File.WriteAllText(Path.Combine(groupDir, "SteamGame.url"), "url");
        File.WriteAllText(Path.Combine(groupDir, "Editor.lnk"), "lnk");
        File.WriteAllText(Path.Combine(groupDir, "Readme.txt"), "txt");
        File.WriteAllText(Path.Combine(groupDir, "Music.mp3"), "mp3");
        File.WriteAllText(Path.Combine(groupDir, "desktop.ini"), "[.ShellClassInfo]");

        var scanner = new LauncherScanner();

        // Act
        var result = scanner.Scan(_tempDesktop);

        // Assert
        result.Snapshot.Groups.Should().HaveCount(1);
        var items = result.Snapshot.Groups[0].Items;
        items.Should().HaveCount(5);
        items.Select(i => i.Name).Should().BeEquivalentTo("Player", "SteamGame", "Editor", "Readme", "Music");
    }

    [Fact]
    public void Scan_PinsRootDocumentsOnDock()
    {
        File.WriteAllText(Path.Combine(_tempDesktop, "Invoice.pdf"), "pdf");
        File.WriteAllText(Path.Combine(_tempDesktop, "Chrome.lnk"), "lnk");
        File.WriteAllText(Path.Combine(_tempDesktop, "desktop.ini"), "junk");

        var scanner = new LauncherScanner();
        var result = scanner.Scan(_tempDesktop);

        result.Snapshot.Pinned.Select(p => p.Name).Should().BeEquivalentTo("Invoice", "Chrome");
    }

    [Fact]
    public void Scan_GeneratesStableIdsAcrossMultipleRuns()
    {
        // Arrange
        var groupDir = Directory.CreateDirectory(Path.Combine(_tempDesktop, "Сеть")).FullName;
        File.WriteAllText(Path.Combine(groupDir, "Browser.lnk"), "lnk");
        File.WriteAllText(Path.Combine(_tempDesktop, "RootApp.lnk"), "lnk");

        var scanner = new LauncherScanner();

        // Act
        var result1 = scanner.Scan(_tempDesktop);
        var result2 = scanner.Scan(_tempDesktop);

        // Assert
        result1.Snapshot.Pinned.Should().HaveCount(1);
        result2.Snapshot.Pinned.Should().HaveCount(1);
        result1.Snapshot.Pinned[0].Id.Should().Be(result2.Snapshot.Pinned[0].Id);

        result1.Snapshot.Groups.Should().HaveCount(1);
        result2.Snapshot.Groups.Should().HaveCount(1);
        result1.Snapshot.Groups[0].Id.Should().Be(result2.Snapshot.Groups[0].Id);
        result1.Snapshot.Groups[0].Items[0].Id.Should().Be(result2.Snapshot.Groups[0].Items[0].Id);
    }

    [Fact]
    public void Scan_IncludesPinnedFromRootOfUserAndCommonDesktops()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDesktop, "Chrome.lnk"), "lnk");
        File.WriteAllText(Path.Combine(_tempCommonDesktop, "Yandex.lnk"), "lnk");

        var scanner = new LauncherScanner();

        // Act
        var result = scanner.Scan(_tempDesktop, _tempCommonDesktop);

        // Assert
        result.Snapshot.Pinned.Should().HaveCount(2);
        result.Snapshot.Pinned.Select(p => p.Name).Should().BeEquivalentTo(ExpectedPinned);
    }

    [Fact]
    public void SecurityManager_RejectsUnknownId()
    {
        // Arrange
        var security = new LauncherSecurityManager();
        var itemPaths = new System.Collections.Generic.Dictionary<string, string>
        {
            ["item_valid"] = @"C:\Apps\App.exe"
        };
        security.UpdateMappings(itemPaths, new System.Collections.Generic.Dictionary<string, string>());

        // Act
        var ok = security.ValidateLaunch("item_nonexistent", out var targetPath, out var reason);

        // Assert
        ok.Should().BeFalse();
        targetPath.Should().BeNull();
        reason.Should().Contain("Unknown");
    }

    [Fact]
    public void SecurityManager_EnforcesRateLimitForSameId()
    {
        // Arrange
        var security = new LauncherSecurityManager();
        var itemPaths = new System.Collections.Generic.Dictionary<string, string>
        {
            ["item_game"] = @"C:\Games\Game.exe"
        };
        security.UpdateMappings(itemPaths, new System.Collections.Generic.Dictionary<string, string>());

        // Act 1: First launch
        var ok1 = security.ValidateLaunch("item_game", out var target1, out var reason1);
        ok1.Should().BeTrue();
        target1.Should().Be(@"C:\Games\Game.exe");
        reason1.Should().BeNull();

        // Act 2: Immediate second launch within 1.5s
        var ok2 = security.ValidateLaunch("item_game", out var target2, out var reason2);
        ok2.Should().BeFalse();
        reason2.Should().Contain("rate-limited");
    }
}
