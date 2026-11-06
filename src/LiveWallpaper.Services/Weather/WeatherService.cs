using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Weather;

public interface IWeatherService
{
    WeatherData? CurrentWeather { get; }
    event EventHandler<WeatherData>? WeatherUpdated;
    Task RefreshNowAsync();
    Task<(double Latitude, double Longitude, string ResolvedName)?> GeocodeCityAsync(string cityName, CancellationToken ct = default);
}

public sealed class WeatherService : BackgroundService, IWeatherService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigManager _configManager;
    private readonly ILogger<WeatherService> _logger;
    private readonly string _cacheFilePath;

    private System.Threading.Timer? _refreshTimer;
    private int _consecutiveFailures;
    private WeatherData? _currentWeather;

    public WeatherData? CurrentWeather => _currentWeather;
    public event EventHandler<WeatherData>? WeatherUpdated;

    public WeatherService(
        IHttpClientFactory httpClientFactory,
        IConfigManager configManager,
        ILogger<WeatherService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configManager = configManager;
        _logger = logger;

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveWallpaper",
            "Cache");

        if (!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        _cacheFilePath = Path.Combine(cacheDir, "weather.json");
        LoadCachedWeather();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _configManager.ConfigChanged += async (s, cfg) =>
        {
            await RefreshNowAsync().ConfigureAwait(false);
        };

        // Initial fetch
        await RefreshNowAsync().ConfigureAwait(false);

        // Keep service alive while monitoring cancellation
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

    public async Task RefreshNowAsync()
    {
        var cfg = _configManager.CurrentConfig.Weather;
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.City))
        {
            _logger.LogDebug("Weather service is disabled or city is empty.");
            if (_currentWeather != null && _currentWeather.Enabled)
            {
                _currentWeather.Enabled = false;
                WeatherUpdated?.Invoke(this, _currentWeather);
            }
            ScheduleNextRefresh(TimeSpan.FromMinutes(cfg.RefreshIntervalMinutes));
            return;
        }

        try
        {
            // If lat/lon not set, geocode the city first
            var lat = cfg.Latitude;
            var lon = cfg.Longitude;

            if (Math.Abs(lat) < 0.0001 && Math.Abs(lon) < 0.0001)
            {
                var geo = await GeocodeCityAsync(cfg.City).ConfigureAwait(false);
                if (geo != null)
                {
                    lat = geo.Value.Latitude;
                    lon = geo.Value.Longitude;
                }
                else
                {
                    _logger.LogWarning("Failed to geocode city '{City}'.", cfg.City);
                    ScheduleNextRefresh(GetBackoffDelay());
                    return;
                }
            }

            var client = _httpClientFactory.CreateClient("OpenMeteo");
            client.Timeout = TimeSpan.FromSeconds(10);

            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,wind_speed_10m&timezone=auto";

            using var response = await client.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement.GetProperty("current");

            var temp = current.GetProperty("temperature_2m").GetDouble();
            var appTemp = current.GetProperty("apparent_temperature").GetDouble();
            var humidity = current.GetProperty("relative_humidity_2m").GetInt32();
            var isDay = current.GetProperty("is_day").GetInt32() == 1;
            var weatherCode = current.GetProperty("weather_code").GetInt32();
            var windSpeed = current.GetProperty("wind_speed_10m").GetDouble();

            var (desc, icon) = WmoWeatherCodeHelper.Describe(weatherCode, isDay);

            _currentWeather = new WeatherData
            {
                Enabled = true,
                City = cfg.City,
                Temperature = Math.Round(temp, 1),
                ApparentTemperature = Math.Round(appTemp, 1),
                RelativeHumidity = humidity,
                WeatherCode = weatherCode,
                WeatherDescription = desc,
                Icon = icon,
                WindSpeed = Math.Round(windSpeed, 1),
                IsDay = isDay,
                Units = cfg.Units,
                UpdatedAt = DateTime.UtcNow
            };

            _consecutiveFailures = 0;
            SaveCachedWeather(_currentWeather);
            WeatherUpdated?.Invoke(this, _currentWeather);
            _logger.LogInformation("Weather updated successfully for {City}: {Temp}°C, {Desc}", cfg.City, temp, desc);

            ScheduleNextRefresh(TimeSpan.FromMinutes(Math.Max(5, cfg.RefreshIntervalMinutes)));
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex, "Failed to refresh weather from Open-Meteo (Attempt {Attempt}). Using cached data if available.", _consecutiveFailures);

            if (_currentWeather != null)
            {
                WeatherUpdated?.Invoke(this, _currentWeather);
            }

            ScheduleNextRefresh(GetBackoffDelay());
        }
    }

    public async Task<(double Latitude, double Longitude, string ResolvedName)?> GeocodeCityAsync(string cityName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cityName)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient("OpenMeteo");
            client.Timeout = TimeSpan.FromSeconds(8);
            var encoded = Uri.EscapeDataString(cityName);
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={encoded}&count=1&language=ru&format=json";

            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                var lat = first.GetProperty("latitude").GetDouble();
                var lon = first.GetProperty("longitude").GetDouble();
                var name = first.GetProperty("name").GetString() ?? cityName;
                return (lat, lon, name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to geocode city '{City}'.", cityName);
        }

        return null;
    }

    private TimeSpan GetBackoffDelay()
    {
        // Exponential backoff: 1 min, 2 min, 4 min, 8 min, max 15 min
        var minutes = Math.Min(15, Math.Pow(2, Math.Min(_consecutiveFailures, 4)));
        return TimeSpan.FromMinutes(minutes);
    }

    private void ScheduleNextRefresh(TimeSpan delay)
    {
        _refreshTimer?.Dispose();
        _refreshTimer = new System.Threading.Timer(
            async _ => await RefreshNowAsync().ConfigureAwait(false),
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private void LoadCachedWeather()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                var json = File.ReadAllText(_cacheFilePath);
                _currentWeather = JsonSerializer.Deserialize<WeatherData>(json);
                if (_currentWeather != null)
                {
                    _logger.LogInformation("Loaded cached weather for {City}: {Temp}°C", _currentWeather.City, _currentWeather.Temperature);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cached weather from {Path}", _cacheFilePath);
        }
    }

    private void SaveCachedWeather(WeatherData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(_cacheFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist weather cache to {Path}", _cacheFilePath);
        }
    }

    public override void Dispose()
    {
        _refreshTimer?.Dispose();
        base.Dispose();
    }
}
