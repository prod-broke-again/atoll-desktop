using System;
using System.Collections.Generic;
using FluentAssertions;
using LiveWallpaper.Core.Context;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public sealed class ContextEvaluatorTests
{
    private static readonly string[] RunningBlockedSteam = { "steam.exe" };
    private readonly ContextConfig _config = new();

    [Fact]
    public void Evaluate_WhenManualModeSpecified_ReturnsSelectedModeDirectly()
    {
        _config.Mode = ContextMode.Game;
        var result = ContextEvaluator.Evaluate(_config, new DateTime(2026, 10, 28, 14, 0, 0), "code");

        result.SelectedMode.Should().Be(ContextMode.Game);
        result.EffectiveMode.Should().Be(ContextMode.Game);
        result.Reason.Should().Be("manual");
        result.Widgets.ShowServer.Should().BeFalse();
        result.Widgets.ShowSystem.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_WhenAutoModeAndGameProcessActive_ReturnsGameMode()
    {
        _config.Mode = ContextMode.Auto;
        var result = ContextEvaluator.Evaluate(_config, new DateTime(2026, 10, 28, 14, 0, 0), "cs2.exe");

        result.SelectedMode.Should().Be(ContextMode.Auto);
        result.EffectiveMode.Should().Be(ContextMode.Game);
        result.Reason.Should().Be("active_app:cs2");
        result.Widgets.PalettePreset.Should().Be("game");
    }

    [Fact]
    public void Evaluate_WhenAutoModeAndWorkProcessActive_ReturnsWorkMode()
    {
        _config.Mode = ContextMode.Auto;
        var result = ContextEvaluator.Evaluate(_config, new DateTime(2026, 10, 28, 14, 30, 0), "code");

        result.SelectedMode.Should().Be(ContextMode.Auto);
        result.EffectiveMode.Should().Be(ContextMode.Work);
        result.Reason.Should().Be("active_app:code");
        result.Widgets.ShowServer.Should().BeTrue();
        result.Widgets.PalettePreset.Should().Be("work");
    }

    [Fact]
    public void Evaluate_WhenAutoModeAtNightWithWorkApp_ReturnsNightMode()
    {
        _config.Mode = ContextMode.Auto;
        var result = ContextEvaluator.Evaluate(_config, new DateTime(2026, 10, 28, 21, 5, 0), "cursor");

        result.EffectiveMode.Should().Be(ContextMode.Night);
        result.Reason.Should().StartWith("schedule:night");
        result.Widgets.ShowSystem.Should().BeTrue();
        result.Widgets.ShowServer.Should().BeTrue();
        result.Widgets.ShowLauncher.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_WhenAutoModeAndBlockedGameProcessRunning_ReturnsGameMode()
    {
        _config.Mode = ContextMode.Auto;
        var result = ContextEvaluator.Evaluate(
            _config,
            new DateTime(2026, 10, 28, 14, 0, 0),
            activeProcess: null,
            runningBlockedProcesses: RunningBlockedSteam);

        result.EffectiveMode.Should().Be(ContextMode.Game);
        result.Reason.Should().Be("running_app:steam");
    }

    [Fact]
    public void Evaluate_WhenAutoModeAtNightWithNoActiveApp_ReturnsNightMode()
    {
        _config.Mode = ContextMode.Auto;
        var result = ContextEvaluator.Evaluate(_config, new DateTime(2026, 10, 28, 23, 15, 0), activeProcess: null);

        result.SelectedMode.Should().Be(ContextMode.Auto);
        result.EffectiveMode.Should().Be(ContextMode.Night);
        result.Reason.Should().StartWith("schedule:night");
        result.Widgets.ShowSystem.Should().BeTrue();
        result.Widgets.ShowClock.Should().BeTrue();
        result.Widgets.PalettePreset.Should().Be("night");
    }

    [Fact]
    public void Evaluate_WhenAutoModeDuringWorkHoursWithNoActiveApp_ReturnsWorkMode()
    {
        _config.Mode = ContextMode.Auto;
        var result = ContextEvaluator.Evaluate(_config, new DateTime(2026, 10, 28, 12, 0, 0), activeProcess: null);

        result.SelectedMode.Should().Be(ContextMode.Auto);
        result.EffectiveMode.Should().Be(ContextMode.Work);
        result.Reason.Should().StartWith("schedule:work");
        result.Widgets.ShowServer.Should().BeTrue();
        result.Widgets.ShowWeather.Should().BeTrue();
    }
}
