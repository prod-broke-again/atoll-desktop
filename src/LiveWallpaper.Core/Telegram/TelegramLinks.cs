using System.Globalization;

namespace LiveWallpaper.Core.Telegram;

/// <summary>
/// Builds a chat link from fields the primary already returned.
/// A public username becomes https://t.me/…. A user without one becomes tg://openmessage.
/// A private channel or supergroup (Bot API id -100…) becomes tg://privatepost?channel=&amp;post=1
/// with the MTProto id. Desktop opens it when the account already has the chat; on a forum, post 1 is General.
/// A basic group has no such link, and a public username is never invented.
/// </summary>
public static class TelegramLinks
{
    public const string DefaultBaseUrl = "http://127.0.0.1:18765";

    /// <summary>Bot API marks channels and supergroups as <c>-(10^12 + channel id)</c>.</summary>
    private const long ChannelIdPrefix = 1_000_000_000_000L;

    public static bool TryNormalizeBaseUrl(string? raw, out string normalized)
    {
        normalized = DefaultBaseUrl;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (!IsLoopback(uri.IdnHost)) return false;

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    public static Uri? TryBuildChatUri(long id, string? username, string? chatType)
    {
        if (TryPublicUsername(username, out var user))
            return new Uri($"https://t.me/{user}");

        if (id > 0 && IsDirectUser(chatType))
            return new Uri($"tg://openmessage?user_id={id.ToString(CultureInfo.InvariantCulture)}");

        // post=1 is required: this Desktop build ignores privatepost with no message id.
        // On a forum, message 1 is the General topic.
        if (TryPrivateChannelId(id, out var channelId))
            return new Uri($"tg://privatepost?channel={channelId.ToString(CultureInfo.InvariantCulture)}&post=1");

        return null;
    }

    /// <summary>
    /// <see cref="Uri.AbsoluteUri"/> inserts a slash before the query of a tg:// link.
    /// Telegram expects <c>tg://openmessage?user_id=</c> without that slash.
    /// </summary>
    public static string ToShellTarget(Uri uri)
    {
        if (uri.Scheme.Equals("tg", StringComparison.OrdinalIgnoreCase))
            return $"tg://{uri.Host}{uri.Query}";
        return uri.AbsoluteUri;
    }

    public static bool TryPublicUsername(string? raw, out string username)
    {
        username = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim();
        if (value[0] == '@') value = value[1..];

        // Telegram public usernames: 5–32 chars, start with a letter, then [A-Za-z0-9_].
        if (value.Length is < 5 or > 32) return false;
        if (!char.IsAsciiLetter(value[0])) return false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        }

        username = value;
        return true;
    }

    private static bool TryPrivateChannelId(long botApiId, out ulong channelId)
    {
        channelId = 0;
        if (botApiId >= -ChannelIdPrefix) return false;

        var raw = -botApiId - ChannelIdPrefix;
        if (raw <= 0) return false;

        channelId = (ulong)raw;
        return true;
    }

    private static bool IsDirectUser(string? chatType)
    {
        return chatType != null &&
               (chatType.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                chatType.Equals("bot", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoopback(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal)
            || host.Equals("::1", StringComparison.Ordinal);
    }
}
