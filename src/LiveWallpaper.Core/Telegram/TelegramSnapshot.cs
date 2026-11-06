using System.Collections.Generic;

namespace LiveWallpaper.Core.Telegram;

public sealed class TelegramSnapshot
{
    public bool Enabled { get; set; }
    public bool Available { get; set; }
    public List<TelegramChat> Chats { get; set; } = new();

    public static TelegramSnapshot Hidden { get; } = new() { Enabled = false, Available = false };
}

public sealed class TelegramChat
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Telegram's own title, when the primary sent one. May match <see cref="Name"/>.</summary>
    public string? Title { get; set; }

    public int Unread { get; set; }
    public string? Username { get; set; }
    public string ChatType { get; set; } = "unknown";
}
