using System;
using System.Text.RegularExpressions;

namespace LiveWallpaper.Core.Wallpaper;

public static class YouTubeUrl
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    public static bool TryGetVideoId(string? input, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var raw = input.Trim();
        if (IdPattern.IsMatch(raw))
        {
            videoId = raw;
            return true;
        }

        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;

        if (!host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return Accept(id.Length > 0 ? id[0] : null, out videoId);
        }

        var v = GetQueryValue(uri, "v");
        if (Accept(v, out videoId))
            return true;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] is "embed" or "shorts" or "live" or "v")
                return Accept(parts[i + 1], out videoId);
        }

        return false;
    }

    private static bool Accept(string? candidate, out string videoId)
    {
        videoId = string.Empty;
        if (candidate == null)
            return false;
        var id = candidate.Trim();
        var cut = id.IndexOfAny(['?', '&']);
        if (cut >= 0)
            id = id[..cut];
        if (!IdPattern.IsMatch(id))
            return false;
        videoId = id;
        return true;
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query.Length < 2)
            return null;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq >= 0 ? pair[..eq] : pair;
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            return eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..]) : string.Empty;
        }

        return null;
    }
}
