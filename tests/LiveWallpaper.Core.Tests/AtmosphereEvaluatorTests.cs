using System;
using System.Collections.Generic;
using FluentAssertions;
using LiveWallpaper.Core.Atmosphere;
using LiveWallpaper.Core.Server;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public sealed class AtmosphereEvaluatorTests
{
    private readonly AtmosphereConfig _config = new()
    {
        Enabled = true,
        EnableProdAlert = true,
        EnableUpcomingCall = true,
        EnableLowBalanceBeacon = true,
        UpcomingCallMinutesBefore = 15
    };

    [Fact]
    public void Evaluate_WhenDisabled_ReturnsEmptySignals()
    {
        _config.Enabled = false;
        var snapshot = new ServerStateSnapshot
        {
            Sites = new List<ServerSiteInfo>
            {
                new() { Name = "portal", State = "down" }
            }
        };

        var result = AtmosphereEvaluator.Evaluate(_config, snapshot);
        result.ProdAlert.Should().BeFalse();
        result.LowBalance.Should().BeFalse();
        result.UpcomingCall.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenSiteIsDown_TriggersProdAlert()
    {
        var snapshot = new ServerStateSnapshot
        {
            Sites = new List<ServerSiteInfo>
            {
                new() { Name = "api.example.com", State = "down" }
            }
        };

        var result = AtmosphereEvaluator.Evaluate(_config, snapshot);
        result.ProdAlert.Should().BeTrue();
        result.ProdAlertText.Should().Contain("api.example.com недоступен");
    }

    [Fact]
    public void Evaluate_WhenCriticalAlertPresent_TriggersProdAlert()
    {
        var snapshot = new ServerStateSnapshot
        {
            Alerts = new List<ServerAlert>
            {
                new(ServerAlertLevel.Critical, "SSH офлайн более 2 мин")
            }
        };

        var result = AtmosphereEvaluator.Evaluate(_config, snapshot);
        result.ProdAlert.Should().BeTrue();
        result.ProdAlertText.Should().Contain("SSH офлайн");
    }

    [Fact]
    public void Evaluate_WhenAccountBalanceLowWithoutAutopay_TriggersLowBalance()
    {
        var snapshot = new ServerStateSnapshot
        {
            Account = new AccountFinanceInfo
            {
                Balance = 320,
                DaysLeft = 2,
                Autopay = false
            }
        };

        var result = AtmosphereEvaluator.Evaluate(_config, snapshot);
        result.LowBalance.Should().BeTrue();
        result.LowBalanceText.Should().Contain("320 ₽");
    }

    [Fact]
    public void Evaluate_WhenAccountHasAutopay_DoesNotTriggerLowBalance()
    {
        var snapshot = new ServerStateSnapshot
        {
            Account = new AccountFinanceInfo
            {
                Balance = 150,
                DaysLeft = 1,
                Autopay = true // autopay handles it!
            }
        };

        var result = AtmosphereEvaluator.Evaluate(_config, snapshot);
        result.LowBalance.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenCallIsWithinThreshold_TriggersUpcomingCall()
    {
        var callTime = DateTime.Now.AddMinutes(10);
        var result = AtmosphereEvaluator.Evaluate(_config, null, nextCallTime: callTime, nextCallTitle: "Daily Standup");

        result.UpcomingCall.Should().BeTrue();
        result.UpcomingCallMinutes.Should().BeInRange(9, 11);
        result.UpcomingCallTitle.Should().Contain("Daily Standup");
    }

    [Fact]
    public void Evaluate_WhenCallIsFarInFuture_DoesNotTriggerUpcomingCall()
    {
        var callTime = DateTime.Now.AddMinutes(45);
        var result = AtmosphereEvaluator.Evaluate(_config, null, nextCallTime: callTime);

        result.UpcomingCall.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenTestFlagsActive_TriggersRespectiveSignals()
    {
        _config.TestProdAlert = true;
        _config.TestUpcomingCall = true;
        _config.TestLowBalance = true;

        var result = AtmosphereEvaluator.Evaluate(_config, null);
        result.ProdAlert.Should().BeTrue();
        result.UpcomingCall.Should().BeTrue();
        result.LowBalance.Should().BeTrue();
    }
}
