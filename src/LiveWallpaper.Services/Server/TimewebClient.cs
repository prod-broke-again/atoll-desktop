using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Native;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Server;

public sealed class TimewebClient : ITimewebClient
{
    private const string BaseUrl = "https://api.timeweb.cloud";
    private readonly HttpClient _httpClient;
    private readonly ILogger<TimewebClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private DateTimeOffset _lastServerFetch = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFinancesFetch = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStatusFetch = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBackupFetch = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBucketFetch = DateTimeOffset.MinValue;

    private DateTimeOffset _lastSuccessTime = DateTimeOffset.UtcNow;

    private ServerDto? _cachedServer;
    private FinancesDto? _cachedFinances;
    private bool _cachedBlocked;
    private BackupDto? _cachedBackup;
    private BucketDto? _cachedBucket;
    private bool _tokenValid = true;

    public TimewebClient(HttpClient httpClient, ILogger<TimewebClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<TimewebSnapshot> PollAsync(CancellationToken cancellationToken)
    {
        string? token = CredentialManager.ReadSecret();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new TimewebSnapshot
            {
                TokenConfigured = false,
                TokenValid = false,
                StaleMinutes = 0
            };
        }

        var now = DateTimeOffset.UtcNow;

        // 1. Servers (60s)
        if (now - _lastServerFetch >= TimeSpan.FromSeconds(60))
        {
            var serversRes = await GetJsonAsync<ServersResponse>("/api/v1/servers", token, cancellationToken).ConfigureAwait(false);
            if (serversRes != null)
            {
                _cachedServer = serversRes.Servers.FirstOrDefault();
                _lastServerFetch = now;
                _lastSuccessTime = now;
            }
        }

        // 2. Finances (10m)
        if (now - _lastFinancesFetch >= TimeSpan.FromMinutes(10))
        {
            var finRes = await GetJsonAsync<FinancesResponse>("/api/v1/account/finances", token, cancellationToken).ConfigureAwait(false);
            if (finRes != null)
            {
                _cachedFinances = finRes.Finances;
                _lastFinancesFetch = now;
                _lastSuccessTime = now;
            }
        }

        // 3. Status (10m)
        if (now - _lastStatusFetch >= TimeSpan.FromMinutes(10))
        {
            var statRes = await GetJsonAsync<AccountStatusResponse>("/api/v1/account/status", token, cancellationToken).ConfigureAwait(false);
            if (statRes != null)
            {
                _cachedBlocked = statRes.Status.IsAnyBlocked();
                _lastStatusFetch = now;
                _lastSuccessTime = now;
            }
        }

        // 4. Backups (30m)
        if (_cachedServer != null && _cachedServer.Disks.Count > 0 && now - _lastBackupFetch >= TimeSpan.FromMinutes(30))
        {
            int serverId = _cachedServer.Id;
            int diskId = _cachedServer.Disks[0].Id;
            var backupRes = await GetJsonAsync<BackupsResponse>($"/api/v1/servers/{serverId}/disks/{diskId}/backups", token, cancellationToken).ConfigureAwait(false);
            if (backupRes != null)
            {
                _cachedBackup = backupRes.Backups.OrderByDescending(b => b.CreatedAt ?? DateTimeOffset.MinValue).FirstOrDefault();
                _lastBackupFetch = now;
                _lastSuccessTime = now;
            }
        }

        // 5. Buckets (15m)
        if (now - _lastBucketFetch >= TimeSpan.FromMinutes(15))
        {
            var bucketsRes = await GetJsonAsync<BucketsResponse>("/api/v1/storages/buckets", token, cancellationToken).ConfigureAwait(false);
            if (bucketsRes != null)
            {
                _cachedBucket = bucketsRes.Buckets.FirstOrDefault();
                _lastBucketFetch = now;
                _lastSuccessTime = now;
            }
        }

        double staleMin = Math.Max(0.0, (now - _lastSuccessTime).TotalMinutes);

        return new TimewebSnapshot
        {
            TokenConfigured = true,
            TokenValid = _tokenValid,
            Server = _cachedServer,
            Finances = _cachedFinances,
            IsAccountBlocked = _cachedBlocked,
            LatestBackup = _cachedBackup,
            Bucket = _cachedBucket,
            StaleMinutes = staleMin
        };
    }

    public async Task<string> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        string? token = CredentialManager.ReadSecret();
        if (string.IsNullOrWhiteSpace(token))
        {
            return "Токен Timeweb не найден в Диспетчере учётных данных (цель: LiveWallpaper/Timeweb).";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v1/account/finances");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("LiveWallpaper/1.0");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return $"Ошибка авторизации: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). Проверьте правильность токена.";
            }

            if (!response.IsSuccessStatusCode)
            {
                return $"Ошибка Timeweb API: HTTP {(int)response.StatusCode} ({response.ReasonPhrase})";
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var fin = await JsonSerializer.DeserializeAsync<FinancesResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (fin?.Finances != null)
            {
                return $"Успешно: Баланс {fin.Finances.Balance:F2} {fin.Finances.Currency}, запас ~{fin.Finances.ComputeDaysLeft():F1} дн.";
            }

            return "Успешно: Подключение к Timeweb Cloud установлено.";
        }
        catch (Exception ex)
        {
            return $"Ошибка подключения: {ex.Message}";
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, string token, CancellationToken cancellationToken) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("LiveWallpaper/1.0");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                _tokenValid = false;
                _logger.LogWarning("Timeweb API returned {StatusCode} for {Path}: token invalid or unauthorized.", response.StatusCode, path);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Timeweb API returned {StatusCode} for {Path}.", response.StatusCode, path);
                return null;
            }

            _tokenValid = true;
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query Timeweb API at {Path}", path);
            return null;
        }
    }
}
