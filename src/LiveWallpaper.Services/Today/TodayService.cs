using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Today;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Today;

public interface ITodayService
{
    TodaySnapshot? CurrentSnapshot { get; }
    event EventHandler<TodaySnapshot>? TodayUpdated;
    DateTime? NextUpcomingEventLocal { get; }
    string? NextUpcomingEventTitle { get; }

    /// <summary>
    /// Resolves a task id coming from the web side into a WEEEK URL. Only ids present in the
    /// latest snapshot are accepted, and the same id is rate-limited (1.5 s) against double clicks.
    /// Returns null (with a reason) when nothing should be opened.
    /// </summary>
    Uri? ResolveTaskUri(string? taskId, out string? reason);
}

public sealed class TodayService : BackgroundService, ITodayService
{
    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    private readonly IConfigManager _configManager;
    private readonly WeeekClient _weeek;
    private readonly GoogleCalendarIcsClient _calendar;
    private readonly ILogger<TodayService> _logger;
    private readonly string _cacheFilePath;

    private TodaySnapshot? _current;
    private int _consecutiveFailures;

    public TodaySnapshot? CurrentSnapshot => _current;
    public event EventHandler<TodaySnapshot>? TodayUpdated;

    public DateTime? NextUpcomingEventLocal { get; private set; }
    public string? NextUpcomingEventTitle { get; private set; }

    private static readonly TimeSpan OpenTaskCooldown = TimeSpan.FromMilliseconds(1500);
    private readonly object _openGate = new();
    private string? _lastOpenedTaskId;
    private DateTime _lastOpenedAt = DateTime.MinValue;
    private long? _weeekWorkspaceId;

    public Uri? ResolveTaskUri(string? taskId, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            reason = "Empty task id.";
            return null;
        }

        var snapshot = _current;
        if (snapshot == null || !snapshot.Tasks.Any(t => string.Equals(t.Id, taskId, StringComparison.Ordinal)))
        {
            reason = "Task id is not in the latest snapshot.";
            return null;
        }

        var ws = _weeekWorkspaceId;
        if (!ws.HasValue)
        {
            reason = "WEEEK workspace id is not known yet.";
            return null;
        }

        lock (_openGate)
        {
            var now = DateTime.UtcNow;
            if (_lastOpenedTaskId == taskId && now - _lastOpenedAt < OpenTaskCooldown)
            {
                reason = "Rate-limited (cooldown 1.5s).";
                return null;
            }
            _lastOpenedTaskId = taskId;
            _lastOpenedAt = now;
        }

        var uri = WeeekClient.BuildTaskUri(ws.Value, taskId);
        if (uri == null) reason = "Task id is not numeric.";
        return uri;
    }

    public TodayService(
        IConfigManager configManager,
        IHttpClientFactory httpFactory,
        ILogger<TodayService> logger)
    {
        _configManager = configManager;
        _logger = logger;
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        _weeek = new WeeekClient(http, logger);
        _calendar = new GoogleCalendarIcsClient(http, logger);

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveWallpaper", "Cache");
        Directory.CreateDirectory(cacheDir);
        _cacheFilePath = Path.Combine(cacheDir, "today.json");

        _configManager.ConfigChanged += OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, AppConfig e) =>
        _ = RefreshAsync(CancellationToken.None);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TryLoadCache();
        await RefreshAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = Math.Clamp(_configManager.CurrentConfig.Today.RefreshIntervalMinutes, 1, 120);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var cfg = _configManager.CurrentConfig.Today;
        if (!cfg.Enabled)
        {
            Publish(TodaySnapshot.Disabled);
            return;
        }

        var snapshot = new TodaySnapshot
        {
            Enabled = true,
            DateLabel = DateTime.Now.ToString("d MMMM, dddd", new CultureInfo("ru-RU")),
            HasWeeekToken = WeeekClient.HasToken(),
            HasCalendar = GoogleCalendarIcsClient.HasCalendar(cfg.GoogleCalendarIcsUrl),
            UpdatedAt = DateTime.UtcNow
        };

        try
        {
            if (snapshot.HasCalendar)
            {
                try
                {
                    snapshot.Events = (await _calendar.GetTodaysEventsAsync(cfg.GoogleCalendarIcsUrl, cfg.MaxEvents, ct)).ToList();
                }
                catch (Exception ex)
                {
                    snapshot.CalendarError = ex.Message.Contains("HTTP 404") || ex.Message.Contains("HTTP 403") ? "Ссылка календаря недействительна" : "Календарь недоступен";
                    _logger.LogWarning(ex, "Failed to load Google Calendar ICS.");
                }
            }

            if (snapshot.HasWeeekToken)
            {
                try
                {
                    snapshot.Tasks = (await _weeek.GetTodaysTasksAsync(cfg.MaxTasks, ct)).ToList();
                    try
                    {
                        _weeekWorkspaceId ??= await _weeek.GetWorkspaceIdAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not resolve WEEEK workspace id.");
                    }
                }
                catch (Exception ex)
                {
                    snapshot.TasksError = ex.Message.Contains("HTTP 401") ? "WEEEK: неверный токен" : "WEEEK недоступен";
                    _logger.LogWarning(ex, "Failed to load WEEEK tasks.");
                }
            }

            _consecutiveFailures = 0;
            Publish(snapshot);
            TrySaveCache(snapshot);
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex, "Today refresh failed (attempt {N}).", _consecutiveFailures);
            if (_current != null)
                TodayUpdated?.Invoke(this, _current);
        }
    }

    private void Publish(TodaySnapshot snapshot)
    {
        _current = snapshot;

        var now = DateTime.Now;
        var next = snapshot.Events
            .Where(e => !e.AllDay && e.StartLocal >= now.AddMinutes(-1))
            .OrderBy(e => e.StartLocal)
            .FirstOrDefault();

        if (next != null)
        {
            NextUpcomingEventLocal = next.StartLocal;
            NextUpcomingEventTitle = next.Title;
        }
        else
        {
            NextUpcomingEventLocal = null;
            NextUpcomingEventTitle = null;
        }

        TodayUpdated?.Invoke(this, snapshot);
        _logger.LogInformation(
            "Today snapshot: {Events} events, {Tasks} tasks (calendar={HasCal}, weeek={HasWeeek}).",
            snapshot.Events.Count, snapshot.Tasks.Count, snapshot.HasCalendar, snapshot.HasWeeekToken);
    }

    private void TryLoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath)) return;
            var json = File.ReadAllText(_cacheFilePath);
            var snap = JsonSerializer.Deserialize<TodaySnapshot>(json, CacheJson);
            if (snap == null) return;
            _current = snap;
            TodayUpdated?.Invoke(this, snap);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load today cache.");
        }
    }

    private void TrySaveCache(TodaySnapshot snapshot)
    {
        try
        {
            File.WriteAllText(_cacheFilePath, JsonSerializer.Serialize(snapshot, CacheJson));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to save today cache.");
        }
    }

    public override void Dispose()
    {
        _configManager.ConfigChanged -= OnConfigChanged;
        base.Dispose();
    }
}
