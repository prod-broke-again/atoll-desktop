namespace LiveWallpaper.Core.Today;

public sealed class TodayConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Poll interval for calendar + tasks.</summary>
    public int RefreshIntervalMinutes { get; set; } = 5;

    /// <summary>Max calendar events shown for today.</summary>
    public int MaxEvents { get; set; } = 6;

    /// <summary>Max WEEEK tasks shown for today.</summary>
    public int MaxTasks { get; set; } = 8;

    /// <summary>
    /// Optional Google Calendar secret iCal URL
    /// (Calendar settings → Integrate calendar → Secret address in iCal format).
    /// Prefer CredentialManager target LiveWallpaper/GoogleCalendarIcs when set there.
    /// </summary>
    public string? GoogleCalendarIcsUrl { get; set; }
}
