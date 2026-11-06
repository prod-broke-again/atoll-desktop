using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Today;
using LiveWallpaper.Native;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Today;

public sealed class GoogleCalendarIcsClient
{
    private readonly HttpClient _http;
    private readonly ILogger? _logger;

    public GoogleCalendarIcsClient(HttpClient http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    public static string? ResolveIcsUrl(string? configUrl)
    {
        var fromCred = CredentialManager.ReadSecret(CredentialManager.GoogleCalendarIcsTarget);
        if (!string.IsNullOrWhiteSpace(fromCred))
            return fromCred.Trim();
        return string.IsNullOrWhiteSpace(configUrl) ? null : configUrl.Trim();
    }

    public static bool HasCalendar(string? configUrl) =>
        !string.IsNullOrWhiteSpace(ResolveIcsUrl(configUrl));

    public async Task<IReadOnlyList<TodayEvent>> GetTodaysEventsAsync(string? configUrl, int maxEvents, CancellationToken ct)
    {
        var url = ResolveIcsUrl(configUrl);
        if (string.IsNullOrWhiteSpace(url))
            return Array.Empty<TodayEvent>();

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "LiveWallpaper/1.0");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger?.LogWarning("Google ICS HTTP {Status}", (int)resp.StatusCode);
            throw new InvalidOperationException($"Calendar ICS HTTP {(int)resp.StatusCode}");
        }

        if (string.IsNullOrWhiteSpace(body) || !body.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Calendar ICS response is empty or invalid");

        // Today first; if there is room, the next events of the coming week (recurring ones expanded).
        return IcsCalendarParser.ParseTodayAndUpcoming(body, DateTime.Now, maxEvents, aheadDays: 7);
    }
}
