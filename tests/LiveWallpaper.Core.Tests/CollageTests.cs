using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiveWallpaper.Core.Collage;
using SkiaSharp;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public sealed class CollageTests
{
    [Fact]
    public void ColorQuantizer_ExtractsDominantColor()
    {
        // Arrange: 80x80 red bitmap
        using var bitmap = new SKBitmap(80, 80);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(255, 0, 0));

        // Act
        var colors = ColorQuantizer.ExtractDominantColors(bitmap, k: 1);

        // Assert
        colors.Should().NotBeEmpty();
        colors[0].R.Should().Be(255);
        colors[0].G.Should().Be(0);
        colors[0].B.Should().Be(0);
    }

    [Theory]
    [InlineData(255, 0, 0, HueGroup.Red)]
    [InlineData(255, 140, 0, HueGroup.Orange)]
    [InlineData(255, 255, 0, HueGroup.Yellow)]
    [InlineData(0, 200, 0, HueGroup.Green)]
    [InlineData(0, 220, 220, HueGroup.Cyan)]
    [InlineData(0, 50, 255, HueGroup.Blue)]
    [InlineData(160, 0, 255, HueGroup.Purple)]
    [InlineData(255, 0, 180, HueGroup.Magenta)]
    [InlineData(15, 15, 18, HueGroup.NeutralDark)]
    [InlineData(240, 240, 245, HueGroup.NeutralLight)]
    public void HueClassifier_ClassifiesColorAccurately(byte r, byte g, byte b, HueGroup expected)
    {
        // Arrange
        var color = new RgbColor(r, g, b);

        // Act
        var group = HueClassifier.Classify(color);

        // Assert
        group.Should().Be(expected);
    }

    [Fact]
    public void CollageSelector_AvoidsRecentPinsAcrossConsecutiveSelections()
    {
        // Arrange
        var mockPins = Enumerable.Range(1, 30).Select(i => new PinItem
        {
            FilePath = $"C:\\Pictures\\Pins\\pin_{i}.jpg",
            FileName = $"pin_{i}.jpg",
            DominantColor = new RgbColor((byte)(i * 8), 100, 150),
            HueGroup = (HueGroup)(i % 11),
            Hue = (i * 12) % 360,
            Saturation = 0.8f,
            Brightness = 0.7f
        }).ToList();

        var selector = new CollageSelector(maxHistory: 15, random: new Random(42));

        // Act
        var selection1 = selector.SelectHarmoniousCollage(mockPins, count: 5);
        var selection2 = selector.SelectHarmoniousCollage(mockPins, count: 5);

        // Assert
        selection1.Should().NotBeNull();
        selection2.Should().NotBeNull();

        var set1 = selection1!.SelectedPins.Select(p => p.FilePath).ToHashSet();
        var set2 = selection2!.SelectedPins.Select(p => p.FilePath).ToHashSet();

        // No overlap between consecutive 5-pin selections
        set1.Intersect(set2).Should().BeEmpty();
    }

    [Fact]
    public void CollageSelector_FiltersByCategory()
    {
        // Arrange: 10 cars, 10 anime
        var mockPins = Enumerable.Range(1, 20).Select(i => new PinItem
        {
            FilePath = $"C:\\Pictures\\Pins\\pin_{i}.jpg",
            FileName = $"pin_{i}.jpg",
            Category = i <= 10 ? "Крутые тачки" : "Аниме",
            DominantColor = new RgbColor(50, 50, 50),
            HueGroup = HueGroup.NeutralDark,
            Hue = 0,
            Saturation = 0.1f,
            Brightness = 0.2f
        }).ToList();

        var selector = new CollageSelector(maxHistory: 15, random: new Random(42));

        // Act
        var result = selector.SelectHarmoniousCollage(mockPins, count: 5, targetCategory: "Крутые тачки");

        // Assert
        result.Should().NotBeNull();
        result!.SelectedPins.Should().HaveCount(5);
        result.SelectedPins.Should().OnlyContain(p => p.Category == "Крутые тачки");
    }

    [Fact]
    public void CollageSelector_FiltersByHueGroup()
    {
        // Arrange: red pins and blue pins
        var mockPins = Enumerable.Range(1, 20).Select(i => new PinItem
        {
            FilePath = $"C:\\Pictures\\Pins\\pin_{i}.jpg",
            FileName = $"pin_{i}.jpg",
            Category = "Общее",
            DominantColor = i <= 10 ? new RgbColor(255, 0, 0) : new RgbColor(0, 50, 255),
            HueGroup = i <= 10 ? HueGroup.Red : HueGroup.Blue,
            Hue = i <= 10 ? 0 : 220,
            Saturation = 0.9f,
            Brightness = 0.8f
        }).ToList();

        var selector = new CollageSelector(maxHistory: 15, random: new Random(42));

        // Act
        var result = selector.SelectHarmoniousCollage(mockPins, count: 5, targetHue: "Blue");

        // Assert
        result.Should().NotBeNull();
        result!.SelectedPins.Should().HaveCount(5);
        result.SelectedPins.Should().OnlyContain(p => p.HueGroup == HueGroup.Blue);
    }
}
