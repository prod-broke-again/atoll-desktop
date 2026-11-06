using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LiveWallpaper.Services.Media;

public interface IMediaService
{
    MediaTrackInfo? CurrentTrack { get; }
    event EventHandler<MediaTrackInfo>? MediaUpdated;
    Task TogglePlayPauseAsync();
    Task NextTrackAsync();
    Task PreviousTrackAsync();
    Task SeekToAsync(double seconds);
}

public sealed class MediaService : BackgroundService, IMediaService
{
    private readonly ILogger<MediaService> _logger;
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private MediaTrackInfo? _currentTrack;
    private System.Threading.Timer? _pollTimer;

    public MediaTrackInfo? CurrentTrack => _currentTrack;
    public event EventHandler<MediaTrackInfo>? MediaUpdated;

    public MediaService(ILogger<MediaService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;

            UpdateSession(_sessionManager.GetCurrentSession());
            await RefreshMediaPropertiesAsync().ConfigureAwait(false);

            // Periodic check every 1s for accurate timeline & playback updates
            _pollTimer = new System.Threading.Timer(
                async _ => await RefreshMediaPropertiesAsync().ConfigureAwait(false),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Windows Media Transport Controls session.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        UpdateSession(sender.GetCurrentSession());
        _ = RefreshMediaPropertiesAsync();
    }

    private void UpdateSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _currentSession = session;

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshMediaPropertiesAsync();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        _ = RefreshMediaPropertiesAsync();
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        _ = RefreshMediaPropertiesAsync();
    }

    private async Task RefreshMediaPropertiesAsync()
    {
        try
        {
            if (_currentSession == null)
            {
                if (_currentTrack != null && _currentTrack.HasMedia)
                {
                    _currentTrack = new MediaTrackInfo { HasMedia = false };
                    MediaUpdated?.Invoke(this, _currentTrack);
                }
                return;
            }

            var playback = _currentSession.GetPlaybackInfo();
            var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var props = await _currentSession.TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrWhiteSpace(props.Title))
            {
                if (_currentTrack != null && _currentTrack.HasMedia)
                {
                    _currentTrack = new MediaTrackInfo { HasMedia = false };
                    MediaUpdated?.Invoke(this, _currentTrack);
                }
                return;
            }

            string? thumbBase64 = null;
            // Only re-encode thumbnail if track changed or wasn't loaded
            if (_currentTrack?.Title == props.Title && _currentTrack?.Artist == props.Artist && _currentTrack.ThumbnailBase64 != null)
            {
                thumbBase64 = _currentTrack.ThumbnailBase64;
            }
            else if (props.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var memStream = new MemoryStream();
                    using var netStream = stream.AsStreamForRead();
                    await netStream.CopyToAsync(memStream).ConfigureAwait(false);
                    thumbBase64 = Convert.ToBase64String(memStream.ToArray());
                }
                catch { }
            }

            // Timeline calculation
            double positionSeconds = 0;
            double durationSeconds = 0;
            bool canSeek = false;

            try
            {
                var timeline = _currentSession.GetTimelineProperties();
                if (timeline != null)
                {
                    durationSeconds = timeline.EndTime.TotalSeconds;
                    
                    // Extrapolate position if playing since last updated time
                    if (isPlaying && timeline.LastUpdatedTime != default)
                    {
                        var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                        positionSeconds = timeline.Position.TotalSeconds + elapsed.TotalSeconds;
                        if (durationSeconds > 0 && positionSeconds > durationSeconds)
                        {
                            positionSeconds = durationSeconds;
                        }
                    }
                    else
                    {
                        positionSeconds = timeline.Position.TotalSeconds;
                    }

                    canSeek = durationSeconds > 0 && (playback?.Controls.IsPlaybackPositionEnabled ?? true);
                }
            }
            catch { }

            var previous = _currentTrack;
            _currentTrack = new MediaTrackInfo
            {
                HasMedia = true,
                Title = props.Title,
                Artist = props.Artist,
                Album = props.AlbumTitle,
                IsPlaying = isPlaying,
                ThumbnailBase64 = thumbBase64,
                PositionSeconds = Math.Max(0, Math.Round(positionSeconds, 1)),
                DurationSeconds = Math.Max(0, Math.Round(durationSeconds, 1)),
                CanSeek = canSeek,
                SourceAppId = _currentSession.SourceAppUserModelId ?? string.Empty,
                UpdatedAt = DateTime.UtcNow
            };

            // The page extrapolates the position itself, so only push real changes:
            // new track/state/cover, a seek (position jumped), or a periodic drift correction.
            if (ShouldNotify(previous, _currentTrack))
            {
                _lastNotifiedAt = DateTime.UtcNow;
                MediaUpdated?.Invoke(this, _currentTrack);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to refresh media properties.");
        }
    }

    private DateTime _lastNotifiedAt = DateTime.MinValue;

    private bool ShouldNotify(MediaTrackInfo? prev, MediaTrackInfo cur)
    {
        if (prev == null || !prev.HasMedia) return true;
        if (prev.Title != cur.Title || prev.Artist != cur.Artist || prev.IsPlaying != cur.IsPlaying ||
            prev.ThumbnailBase64 != cur.ThumbnailBase64 || prev.CanSeek != cur.CanSeek ||
            prev.SourceAppId != cur.SourceAppId || Math.Abs(prev.DurationSeconds - cur.DurationSeconds) > 1)
        {
            return true;
        }

        // Where the page thinks the track is now vs. where it really is.
        var elapsed = (DateTime.UtcNow - _lastNotifiedAt).TotalSeconds;
        var expected = prev.PositionSeconds + (prev.IsPlaying ? (DateTime.UtcNow - prev.UpdatedAt).TotalSeconds : 0);
        if (Math.Abs(expected - cur.PositionSeconds) > 2.5) return true; // seek / skip within track
        return elapsed >= 15; // gentle drift correction
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession == null) return;
        try { await _currentSession.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextTrackAsync()
    {
        if (_currentSession == null) return;
        try { await _currentSession.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousTrackAsync()
    {
        if (_currentSession == null) return;
        try { await _currentSession.TrySkipPreviousAsync(); } catch { }
    }

    public async Task SeekToAsync(double seconds)
    {
        if (_currentSession == null || seconds < 0) return;
        try
        {
            var targetSpan = TimeSpan.FromSeconds(seconds);
            await _currentSession.TryChangePlaybackPositionAsync(targetSpan.Ticks);
            await RefreshMediaPropertiesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to change playback position to {Seconds}s", seconds);
        }
    }

    public override void Dispose()
    {
        _pollTimer?.Dispose();
        UpdateSession(null);
        base.Dispose();
    }
}
