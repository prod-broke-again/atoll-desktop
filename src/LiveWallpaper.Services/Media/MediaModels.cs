using System;

namespace LiveWallpaper.Services.Media;

public sealed class MediaTrackInfo
{
    public bool HasMedia { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public bool IsPlaying { get; set; }
    public string? ThumbnailBase64 { get; set; }
    
    // Timeline & Seek
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public bool CanSeek { get; set; }

    // Source & Yandex specific
    public string SourceAppId { get; set; } = string.Empty;
    /// <summary>
    /// Yandex Music desktop app only (AUMID like "ru.yandex.desktop.music").
    /// Must not match Spotify ("SpotifyAB.SpotifyMusic_…"), Windows Media Player ("ZuneMusic") or Yandex Browser.
    /// </summary>
    public bool IsYandexMusic => SourceAppId.Contains("yandex", StringComparison.OrdinalIgnoreCase) &&
                                 SourceAppId.Contains("music", StringComparison.OrdinalIgnoreCase);

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
