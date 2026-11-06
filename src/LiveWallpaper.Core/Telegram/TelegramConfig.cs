namespace LiveWallpaper.Core.Telegram;

public sealed class TelegramConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Origin of the telegram-mcp primary UI. Loopback only.</summary>
    public string BaseUrl { get; set; } = TelegramLinks.DefaultBaseUrl;

    /// <summary>How often to re-read GET /api/chats.</summary>
    public int PollIntervalSeconds { get; set; } = 45;

    /// <summary>How often to POST /api/backfill. Sync is not on this timer.</summary>
    public int BackfillIntervalMinutes { get; set; } = 15;
}
