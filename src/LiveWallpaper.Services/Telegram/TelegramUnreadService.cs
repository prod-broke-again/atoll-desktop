using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Telegram;

public interface ITelegramUnreadService
{
    TelegramSnapshot? Current { get; }
    event EventHandler<TelegramSnapshot>? Updated;

    /// <summary>
    /// Turns an id from the latest snapshot into a t.me or tg:// link.
    /// Unknown ids and repeats inside 1.5 s are rejected.
    /// </summary>
    Uri? ResolveChatUri(long id, out string? reason);
}

/// <summary>
/// HTTP client of the telegram-mcp primary (127.0.0.1:18765). Does not open a Telegram session.
/// </summary>
public sealed class TelegramUnreadService : BackgroundService, ITelegramUnreadService
{
    public const string HttpClientName = "telegram-primary";

    private static readonly TimeSpan OpenCooldown = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ChatsTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(130);
    private static readonly TimeSpan BackfillTimeout = TimeSpan.FromMinutes(31);

    private readonly IConfigManager _configManager;
    private readonly HttpClient _http;
    private readonly ILogger<TelegramUnreadService> _logger;

    private readonly object _openGate = new();
    private long _lastOpenedId;
    private DateTime _lastOpenedAt = DateTime.MinValue;
    private int _syncBusy;
    private int _backfillBusy;
    private DateTime _lastBackfillStartedUtc = DateTime.MinValue;
    private bool? _reportedAvailable;
    private CancellationToken _stopping;

    public TelegramUnreadService(
        IConfigManager configManager,
        IHttpClientFactory httpFactory,
        ILogger<TelegramUnreadService> logger)
    {
        _configManager = configManager;
        _logger = logger;
        _http = httpFactory.CreateClient(HttpClientName);
        _configManager.ConfigChanged += OnConfigChanged;
    }

    public TelegramSnapshot? Current { get; private set; }
    public event EventHandler<TelegramSnapshot>? Updated;

    public Uri? ResolveChatUri(long id, out string? reason)
    {
        reason = null;
        var chat = Current?.Chats.Find(c => c.Id == id);
        if (chat == null)
        {
            reason = "Chat id is not in the latest snapshot.";
            return null;
        }

        var uri = TelegramLinks.TryBuildChatUri(chat.Id, chat.Username, chat.ChatType);
        if (uri == null)
        {
            reason = "No public username, and this chat is not a channel or supergroup.";
            return null;
        }

        lock (_openGate)
        {
            var now = DateTime.UtcNow;
            if (_lastOpenedId == id && now - _lastOpenedAt < OpenCooldown)
            {
                reason = "Rate-limited (cooldown 1.5s).";
                return null;
            }

            _lastOpenedId = id;
            _lastOpenedAt = now;
        }

        return uri;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        // Hold the backfill interval so the poll loop does not start one before warmup's sync.
        _lastBackfillStartedUtc = DateTime.UtcNow;

        await FetchAndPublishAsync(stoppingToken);
        _ = WarmupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var seconds = Math.Clamp(_configManager.CurrentConfig.Telegram.PollIntervalSeconds, 15, 300);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (!_configManager.CurrentConfig.Telegram.Enabled)
            {
                Publish(TelegramSnapshot.Hidden);
                continue;
            }

            await FetchAndPublishAsync(stoppingToken);
            KickSync(stoppingToken);
            MaybeKickBackfill(stoppingToken);
        }
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        var ct = _stopping;
        if (ct.IsCancellationRequested) return;
        if (!config.Telegram.Enabled)
        {
            Publish(TelegramSnapshot.Hidden);
            return;
        }

        _ = FetchAndPublishAsync(ct);
    }

    private async Task WarmupAsync(CancellationToken ct)
    {
        try
        {
            if (!_configManager.CurrentConfig.Telegram.Enabled) return;
            await SyncUnderGateAsync(ct);
            KickBackfill(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telegram warmup failed.");
        }
    }

    private void KickSync(CancellationToken ct) => _ = SyncUnderGateAsync(ct);

    private async Task SyncUnderGateAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _syncBusy, 1, 0) != 0) return;
        try
        {
            if (!_configManager.CurrentConfig.Telegram.Enabled || ct.IsCancellationRequested) return;
            if (await PostAsync("/api/sync", SyncTimeout, ct))
                await FetchAndPublishAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Telegram sync timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telegram sync failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _syncBusy, 0);
        }
    }

    private void MaybeKickBackfill(CancellationToken ct)
    {
        var minutes = Math.Clamp(_configManager.CurrentConfig.Telegram.BackfillIntervalMinutes, 5, 720);
        if (DateTime.UtcNow - _lastBackfillStartedUtc < TimeSpan.FromMinutes(minutes)) return;
        KickBackfill(ct);
    }

    private void KickBackfill(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _backfillBusy, 1, 0) != 0) return;
        _lastBackfillStartedUtc = DateTime.UtcNow;
        _ = BackfillAsync(ct);
    }

    private async Task BackfillAsync(CancellationToken ct)
    {
        try
        {
            if (!_configManager.CurrentConfig.Telegram.Enabled || ct.IsCancellationRequested) return;
            // No chat_id: the primary backfills every tracked chat. Not awaited by the UI.
            await PostAsync("/api/backfill", BackfillTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Telegram backfill timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telegram backfill failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _backfillBusy, 0);
        }
    }

    private async Task FetchAndPublishAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await FetchAsync(ct);
            if (!ct.IsCancellationRequested) Publish(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<TelegramSnapshot> FetchAsync(CancellationToken ct)
    {
        var cfg = _configManager.CurrentConfig.Telegram;
        if (!cfg.Enabled) return TelegramSnapshot.Hidden;
        if (!TelegramLinks.TryNormalizeBaseUrl(cfg.BaseUrl, out var baseUrl))
            return Unreachable();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ChatsTimeout);
            using var response = await _http.GetAsync(new Uri(baseUrl + "/api/chats"), cts.Token);
            if (!response.IsSuccessStatusCode)
                return Unreachable();

            var body = await response.Content.ReadFromJsonAsync<ChatsEnvelope>(cts.Token);
            return new TelegramSnapshot
            {
                Enabled = true,
                Available = true,
                Chats = Map(body)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_reportedAvailable != false)
                _logger.LogDebug("Telegram chats request failed: {Reason}", ex.Message);
            return Unreachable();
        }
    }

    private async Task<bool> PostAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        if (!TelegramLinks.TryNormalizeBaseUrl(_configManager.CurrentConfig.Telegram.BaseUrl, out var baseUrl))
            return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(new Uri(baseUrl + path), content, cts.Token);
        if (response.IsSuccessStatusCode) return true;

        _logger.LogDebug("Telegram {Path} returned HTTP {Status}.", path, (int)response.StatusCode);
        return false;
    }

    private void Publish(TelegramSnapshot snapshot)
    {
        if (Same(Current, snapshot)) return;
        Current = snapshot;

        if (_reportedAvailable != snapshot.Available)
        {
            _reportedAvailable = snapshot.Available;
            if (snapshot.Available)
                _logger.LogInformation("Telegram unread widget is live ({Count} tracked).", snapshot.Chats.Count);
            else if (snapshot.Enabled)
                _logger.LogDebug("Telegram unread widget hidden: primary is not reachable.");
            else
                _logger.LogDebug("Telegram unread widget hidden: disabled in settings.");
        }

        Updated?.Invoke(this, snapshot);
    }

    private static TelegramSnapshot Unreachable() => new() { Enabled = true, Available = false };

    private static bool Same(TelegramSnapshot? a, TelegramSnapshot b)
    {
        if (a == null) return false;
        if (a.Enabled != b.Enabled || a.Available != b.Available) return false;
        if (a.Chats.Count != b.Chats.Count) return false;
        for (var i = 0; i < a.Chats.Count; i++)
        {
            var left = a.Chats[i];
            var right = b.Chats[i];
            if (left.Id != right.Id || left.Unread != right.Unread || left.ChatType != right.ChatType) return false;
            if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal)) return false;
            if (!string.Equals(left.Title, right.Title, StringComparison.Ordinal)) return false;
            if (!string.Equals(left.Username, right.Username, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static List<TelegramChat> Map(ChatsEnvelope? envelope)
    {
        var list = new List<TelegramChat>();
        if (envelope?.Chats == null) return list;

        foreach (var row in envelope.Chats)
        {
            if (!row.IsTracked) continue;
            var name = First(row.ResolvedName, row.Title, row.Id.ToString(CultureInfo.InvariantCulture));
            var title = string.IsNullOrWhiteSpace(row.Title) ? null : row.Title.Trim();
            list.Add(new TelegramChat
            {
                Id = row.Id,
                Name = name,
                Title = title,
                Unread = Math.Max(0, row.UnreadCount),
                Username = string.IsNullOrWhiteSpace(row.Username) ? null : row.Username.Trim(),
                ChatType = string.IsNullOrWhiteSpace(row.ChatType) ? "unknown" : row.ChatType.Trim()
            });
        }

        list.Sort(static (a, b) =>
        {
            var hot = (b.Unread > 0).CompareTo(a.Unread > 0);
            if (hot != 0) return hot;
            var count = b.Unread.CompareTo(a.Unread);
            if (count != 0) return count;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    private static string First(string? a, string? b, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(a)) return a.Trim();
        if (!string.IsNullOrWhiteSpace(b)) return b.Trim();
        return fallback;
    }

    private sealed class ChatsEnvelope
    {
        [JsonPropertyName("chats")]
        public List<ApiChat>? Chats { get; set; }
    }

    private sealed class ApiChat
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("resolved_name")]
        public string? ResolvedName { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("is_tracked")]
        public bool IsTracked { get; set; }

        [JsonPropertyName("unread_count")]
        public int UnreadCount { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("chat_type")]
        public string? ChatType { get; set; }
    }
}
