using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Server;

public sealed class SiteHealthService : ISiteHealthService, IDisposable
{
    private readonly IConfigManager _configManager;
    private readonly ILogger<SiteHealthService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, SiteStatusRecord> _statusMap = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _lastTlsCheck = DateTimeOffset.MinValue;
    private bool _isDisposed;

    public SiteHealthService(
        IConfigManager configManager,
        ILogger<SiteHealthService> logger)
    {
        _configManager = configManager;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // Do not auto-follow redirects to external hosts
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LiveWallpaper-Monitor/1.0");
    }

    public IReadOnlyList<SiteStatusRecord> GetCurrentStatus()
    {
        var configuredSites = _configManager.CurrentConfig.Server.Sites;
        var list = new List<SiteStatusRecord>(configuredSites.Count);

        foreach (var cfg in configuredSites)
        {
            if (_statusMap.TryGetValue(cfg.Name, out var record))
            {
                list.Add(record);
            }
            else
            {
                list.Add(new SiteStatusRecord
                {
                    Name = cfg.Name,
                    Url = cfg.Url,
                    State = "unknown"
                });
            }
        }

        return list;
    }

    public async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        var sites = _configManager.CurrentConfig.Server.Sites.ToList();
        if (sites.Count == 0) return;

        bool checkTls = DateTimeOffset.UtcNow - _lastTlsCheck >= TimeSpan.FromHours(6);
        if (checkTls)
        {
            _lastTlsCheck = DateTimeOffset.UtcNow;
        }

        foreach (var site in sites)
        {
            if (cancellationToken.IsCancellationRequested) break;

            int? tlsDays = null;
            if (_statusMap.TryGetValue(site.Name, out var existing))
            {
                tlsDays = existing.TlsDays;
            }

            if (checkTls || !tlsDays.HasValue)
            {
                tlsDays = await CheckTlsExpirationDaysAsync(site.Url, cancellationToken).ConfigureAwait(false) ?? tlsDays;
            }

            var (state, ms, downCount) = await CheckHttpStateAsync(site, cancellationToken).ConfigureAwait(false);

            var record = new SiteStatusRecord
            {
                Name = site.Name,
                Url = site.Url,
                State = state,
                ResponseMs = ms,
                TlsDays = tlsDays,
                ConsecutiveDownCount = downCount
            };

            _statusMap[site.Name] = record;
        }
    }

    public async Task<string> CheckSiteDiagnosticAsync(string name, string url, CancellationToken cancellationToken)
    {
        var dummy = new ServerSiteConfig { Name = name, Url = url };
        var (state, ms, _) = await CheckHttpStateAsync(dummy, cancellationToken).ConfigureAwait(false);
        int? tls = await CheckTlsExpirationDaysAsync(url, cancellationToken).ConfigureAwait(false);

        string tlsStr = tls.HasValue ? $"{tls.Value} дн." : "N/A";
        return $"{name} ({url}): {state.ToUpperInvariant()} ({ms:F0} мс), SSL: {tlsStr}";
    }

    private async Task<(string state, double ms, int consecutiveDown)> CheckHttpStateAsync(ServerSiteConfig site, CancellationToken cancellationToken)
    {
        int prevDown = 0;
        if (_statusMap.TryGetValue(site.Name, out var prevRecord))
        {
            prevDown = prevRecord.ConsecutiveDownCount;
        }

        if (!Uri.TryCreate(site.Url, UriKind.Absolute, out var targetUri))
        {
            return ("down", 0, prevDown + 1);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, targetUri);
            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            double ms = sw.Elapsed.TotalMilliseconds;

            // Handle same-host redirect check
            int statusCode = (int)res.StatusCode;
            if (statusCode is >= 300 and <= 399)
            {
                var loc = res.Headers.Location;
                if (loc != null)
                {
                    var redirectUri = loc.IsAbsoluteUri ? loc : new Uri(targetUri, loc);
                    if (!string.Equals(redirectUri.Host, targetUri.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        // Redirected to different host: do not follow, treat as up if 3xx received
                    }
                }
            }

            if (statusCode >= 500)
            {
                _logger.LogWarning("Site {Name} returned HTTP {Status}", site.Name, statusCode);
                return ("down", ms, prevDown + 1);
            }

            string state = ms > 1500.0 ? "slow" : "up";
            return (state, ms, 0); // Reset consecutive down count on success
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ("down", sw.Elapsed.TotalMilliseconds, prevDown + 1);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning("HTTP check failed for {Name}: {Message}", site.Name, ex.Message);
            return ("down", sw.Elapsed.TotalMilliseconds, prevDown + 1);
        }
    }

    private async Task<int?> CheckTlsExpirationDaysAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var tcp = new TcpClient();
            int port = uri.Port > 0 ? uri.Port : 443;

            using var reg = cancellationToken.Register(() => tcp.Close());
            await tcp.ConnectAsync(uri.Host, port, cancellationToken).ConfigureAwait(false);

            using var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(uri.Host).ConfigureAwait(false);

            if (ssl.RemoteCertificate != null)
            {
                using var cert = new X509Certificate2(ssl.RemoteCertificate);
                var expiry = cert.NotAfter.ToUniversalTime();
                double days = (expiry - DateTime.UtcNow).TotalDays;
                return (int)Math.Floor(days);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TLS check error for {Host}: {Message}", uri.Host, ex.Message);
        }

        return null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _httpClient.Dispose();
    }
}
