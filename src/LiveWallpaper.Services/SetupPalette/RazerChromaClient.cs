using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Collage;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.SetupPalette;

public sealed class RazerChromaClient
{
    private static readonly string[] SupportedDevices = { "mouse", "keyboard", "mousepad", "headset", "chromalink" };

    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private string? _sessionUri;
    private DateTime _sessionExpiresAt;

    public RazerChromaClient(HttpClient http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<bool> ApplyAccentAsync(RgbColor accent, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));

            var uri = await EnsureSessionUriAsync(cts.Token);
            if (string.IsNullOrEmpty(uri)) return false;

            // Razer Chroma colors are packed as BGR integer: (B << 16) | (G << 8) | R
            int bgr = (accent.B << 16) | (accent.G << 8) | accent.R;

            var payload = JsonSerializer.Serialize(new
            {
                effect = "CHROMA_STATIC",
                param = new { color = bgr }
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            // Apply to mouse (user's Razer mouse)
            var mouseResp = await _http.PutAsync($"{uri}/mouse", content, cts.Token);
            if (!mouseResp.IsSuccessStatusCode)
            {
                _sessionUri = null; // Session may have expired
                return false;
            }

            _logger?.LogInformation("Razer Chroma mouse updated to accent #{R:X2}{G:X2}{B:X2}.",
                accent.R, accent.G, accent.B);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Razer Chroma REST endpoint unavailable.");
            _sessionUri = null;
            return false;
        }
    }

    private async Task<string?> EnsureSessionUriAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_sessionUri) && DateTime.UtcNow < _sessionExpiresAt)
        {
            return _sessionUri;
        }

        var regPayload = JsonSerializer.Serialize(new
        {
            title = "LiveWallpaper Setup Palette",
            description = "Collage palette synchronizer for Windows and peripherals",
            author = new { name = "LiveWallpaper", contact = "local" },
            device_supported = SupportedDevices,
            category = "application"
        });

        using var regContent = new StringContent(regPayload, Encoding.UTF8, "application/json");
        var regResp = await _http.PostAsync("http://localhost:12076/chromasdk", regContent, ct);
        if (!regResp.IsSuccessStatusCode) return null;

        var jsonStr = await regResp.Content.ReadAsStringAsync(ct);
        var doc = JsonNode.Parse(jsonStr);
        var sessionUri = doc?["uri"]?.ToString();
        if (!string.IsNullOrEmpty(sessionUri))
        {
            _sessionUri = sessionUri;
            _sessionExpiresAt = DateTime.UtcNow.AddMinutes(5);
            return _sessionUri;
        }

        return null;
    }
}
