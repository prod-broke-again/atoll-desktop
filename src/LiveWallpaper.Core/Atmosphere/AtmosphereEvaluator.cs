using System;
using System.Linq;
using LiveWallpaper.Core.Server;

namespace LiveWallpaper.Core.Atmosphere;

public static class AtmosphereEvaluator
{
    public static AtmosphereSignalsState Evaluate(
        AtmosphereConfig config,
        ServerStateSnapshot? serverSnapshot,
        DateTime? nextCallTime = null,
        string? nextCallTitle = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!config.Enabled && !config.TestProdAlert && !config.TestUpcomingCall && !config.TestLowBalance)
        {
            return AtmosphereSignalsState.Empty;
        }

        // 1. Prod Down Alert (красноватые края экрана при падении прода)
        bool prodAlert = false;
        string? prodAlertText = null;

        if (config.TestProdAlert)
        {
            prodAlert = true;
            prodAlertText = "Тест: Сбой прода (сайт недоступен)";
        }
        else if (config.EnableProdAlert && serverSnapshot != null)
        {
            // Check down sites
            var downSite = serverSnapshot.Sites.FirstOrDefault(s => string.Equals(s.State, "down", StringComparison.OrdinalIgnoreCase));
            if (downSite != null)
            {
                prodAlert = true;
                prodAlertText = $"Сайт {downSite.Name} недоступен!";
            }
            else
            {
                // Check critical alerts (SSH offline, server down, etc.)
                var critAlert = serverSnapshot.Alerts.FirstOrDefault(a =>
                    string.Equals(a.Level, "critical", StringComparison.OrdinalIgnoreCase));

                if (critAlert != null)
                {
                    prodAlert = true;
                    prodAlertText = critAlert.Text;
                }
            }
        }

        // 2. Upcoming Call (тёплый фон перед созвоном)
        bool upcomingCall = false;
        int? callMinutes = null;
        string? callTitle = null;

        if (config.TestUpcomingCall)
        {
            upcomingCall = true;
            callMinutes = 10;
            callTitle = "Тест: Созвон команды через 10 мин";
        }
        else if (config.EnableUpcomingCall && nextCallTime.HasValue)
        {
            var diff = nextCallTime.Value - DateTime.Now;
            if (diff > TimeSpan.Zero && diff.TotalMinutes <= config.UpcomingCallMinutesBefore)
            {
                upcomingCall = true;
                callMinutes = (int)Math.Max(1, Math.Ceiling(diff.TotalMinutes));
                callTitle = string.IsNullOrWhiteSpace(nextCallTitle)
                    ? $"Созвон через {callMinutes} мин"
                    : $"{nextCallTitle} через {callMinutes} мин";
            }
        }

        // 3. Low Balance Beacon (янтарная точка при низком балансе)
        bool lowBalance = false;
        string? lowBalanceText = null;

        if (config.TestLowBalance)
        {
            lowBalance = true;
            lowBalanceText = "Тест: Низкий баланс Timeweb: 350 ₽ (~2 дня)";
        }
        else if (config.EnableLowBalanceBeacon && serverSnapshot != null)
        {
            var acc = serverSnapshot.Account;
            if (acc != null)
            {
                if (!acc.Autopay)
                {
                    if (acc.DaysLeft.HasValue && acc.DaysLeft.Value <= 3)
                    {
                        lowBalance = true;
                        lowBalanceText = $"Баланс Timeweb: {acc.Balance:F0} ₽ (осталось {acc.DaysLeft.Value} д)";
                    }
                    else if (acc.Balance <= 500)
                    {
                        lowBalance = true;
                        lowBalanceText = $"Низкий баланс Timeweb: {acc.Balance:F0} ₽";
                    }
                }
            }

            if (!lowBalance)
            {
                var balanceAlert = serverSnapshot.Alerts.FirstOrDefault(a =>
                    a.Text.Contains("Баланс", StringComparison.OrdinalIgnoreCase) ||
                    a.Text.Contains("заблокирован", StringComparison.OrdinalIgnoreCase));

                if (balanceAlert != null)
                {
                    lowBalance = true;
                    lowBalanceText = balanceAlert.Text;
                }
            }
        }

        return new AtmosphereSignalsState
        {
            ProdAlert = prodAlert,
            ProdAlertText = prodAlertText,
            UpcomingCall = upcomingCall,
            UpcomingCallMinutes = callMinutes,
            UpcomingCallTitle = callTitle,
            LowBalance = lowBalance,
            LowBalanceText = lowBalanceText
        };
    }
}
