using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveWallpaper.Core.Context;

public sealed record ContextEvaluationResult
{
    public string SelectedMode { get; init; } = ContextMode.Auto;
    public string EffectiveMode { get; init; } = ContextMode.Work;
    public string Reason { get; init; } = string.Empty;
    public ModeWidgetsConfig Widgets { get; init; } = new();
}

public static class ContextEvaluator
{
    public static ContextEvaluationResult Evaluate(
        ContextConfig config,
        DateTime now,
        string? activeProcess,
        IEnumerable<string>? runningBlockedProcesses = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var selected = ContextMode.Normalize(config.Mode);

        if (selected != ContextMode.Auto)
        {
            var widgets = GetWidgetsForMode(config, selected);
            return new ContextEvaluationResult
            {
                SelectedMode = selected,
                EffectiveMode = selected,
                Reason = "manual",
                Widgets = widgets
            };
        }

        // Night light follows the clock, including while a work app is in front.
        if (IsNightHour(config, now))
        {
            return new ContextEvaluationResult
            {
                SelectedMode = ContextMode.Auto,
                EffectiveMode = ContextMode.Night,
                Reason = $"schedule:night_{now.Hour}h",
                Widgets = config.NightWidgets
            };
        }

        // Auto mode: evaluate active process, then blocked/game running apps, then time of day
        var proc = activeProcess?.Trim();
        if (!string.IsNullOrEmpty(proc))
        {
            // Remove .exe suffix if present for comparison
            if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                proc = proc[..^4];
            }

            // 1. Is active process a game?
            if (config.GameProcesses.Any(g => MatchProcess(g, proc)))
            {
                return new ContextEvaluationResult
                {
                    SelectedMode = ContextMode.Auto,
                    EffectiveMode = ContextMode.Game,
                    Reason = $"active_app:{proc}",
                    Widgets = config.GameWidgets
                };
            }

            // 2. Is active process a work app?
            if (config.WorkProcesses.Any(w => MatchProcess(w, proc)))
            {
                return new ContextEvaluationResult
                {
                    SelectedMode = ContextMode.Auto,
                    EffectiveMode = ContextMode.Work,
                    Reason = $"active_app:{proc}",
                    Widgets = config.WorkWidgets
                };
            }
        }

        // 3. Are any blocked/game processes currently running?
        if (runningBlockedProcesses != null)
        {
            foreach (var bp in runningBlockedProcesses)
            {
                var clean = bp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? bp[..^4] : bp;
                if (config.GameProcesses.Any(g => MatchProcess(g, clean)))
                {
                    return new ContextEvaluationResult
                    {
                        SelectedMode = ContextMode.Auto,
                        EffectiveMode = ContextMode.Game,
                        Reason = $"running_app:{clean}",
                        Widgets = config.GameWidgets
                    };
                }
            }
        }

        // 4. Time of day schedule (night is handled above, before the foreground app)
        int hour = now.Hour;
        bool isWorkHours;
        if (config.WorkStartHour <= config.WorkEndHour)
        {
            isWorkHours = hour >= config.WorkStartHour && hour < config.WorkEndHour;
        }
        else
        {
            isWorkHours = hour >= config.WorkStartHour || hour < config.WorkEndHour;
        }

        if (isWorkHours)
        {
            return new ContextEvaluationResult
            {
                SelectedMode = ContextMode.Auto,
                EffectiveMode = ContextMode.Work,
                Reason = $"schedule:work_{hour}h",
                Widgets = config.WorkWidgets
            };
        }

        // Outside work and night: default to work set during the day
        return new ContextEvaluationResult
        {
            SelectedMode = ContextMode.Auto,
            EffectiveMode = ContextMode.Work,
            Reason = $"schedule:day_{hour}h",
            Widgets = config.WorkWidgets
        };
    }

    /// <summary>True from <see cref="ContextConfig.NightStartHour"/> until <see cref="ContextConfig.NightEndHour"/>.</summary>
    public static bool IsNightHour(ContextConfig config, DateTime now)
    {
        int hour = now.Hour;
        if (config.NightStartHour > config.NightEndHour)
            return hour >= config.NightStartHour || hour < config.NightEndHour;
        return hour >= config.NightStartHour && hour < config.NightEndHour;
    }

    private static bool MatchProcess(string pattern, string procName)
    {
        var cleanPattern = pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pattern[..^4] : pattern;
        return string.Equals(cleanPattern, procName, StringComparison.OrdinalIgnoreCase);
    }

    private static ModeWidgetsConfig GetWidgetsForMode(ContextConfig config, string mode)
    {
        return mode switch
        {
            ContextMode.Game => config.GameWidgets,
            ContextMode.Night => config.NightWidgets,
            _ => config.WorkWidgets
        };
    }
}
