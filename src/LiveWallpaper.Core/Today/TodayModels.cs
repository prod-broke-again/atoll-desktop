using System;
using System.Collections.Generic;

namespace LiveWallpaper.Core.Today;

public sealed class TodaySnapshot
{
    public bool Enabled { get; set; }
    public string DateLabel { get; set; } = string.Empty;
    public List<TodayEvent> Events { get; set; } = new();
    public List<TodayTask> Tasks { get; set; } = new();
    public string? CalendarError { get; set; }
    public string? TasksError { get; set; }
    public bool HasWeeekToken { get; set; }
    public bool HasCalendar { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static TodaySnapshot Disabled { get; } = new() { Enabled = false };
}

public sealed class TodayEvent
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime StartLocal { get; set; }
    public DateTime? EndLocal { get; set; }
    public bool AllDay { get; set; }
    public string TimeLabel { get; set; } = string.Empty;
    /// <summary>False for "upcoming" events shown when today has few events.</summary>
    public bool IsToday { get; set; } = true;
}

public sealed class TodayTask
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int? Priority { get; set; }
    public bool IsCompleted { get; set; }
    public string? DueLabel { get; set; }
    public string? Board { get; set; }
    public string? Column { get; set; }
}
