using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Collage;
using LiveWallpaper.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace LiveWallpaper.Services.Pins;

public sealed class PinsIndexerService : BackgroundService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private readonly IConfigManager _configManager;
    private readonly ILogger<PinsIndexerService> _logger;
    private readonly ConcurrentDictionary<string, PinItem> _indexedPins = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;

    public IReadOnlyList<PinItem> IndexedPins => _indexedPins.Values.ToList();

    public event EventHandler? IndexUpdated;

    public PinsIndexerService(IConfigManager configManager, ILogger<PinsIndexerService> logger)
    {
        _configManager = configManager;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshIndex();
        SetupWatcher();

        _configManager.ConfigChanged += (s, cfg) =>
        {
            SetupWatcher();
            RefreshIndex();
        };

        return Task.CompletedTask;
    }

    public void RefreshIndex()
    {
        var dir = _configManager.CurrentConfig.Collage.PinsDirectory;
        if (!Directory.Exists(dir))
        {
            _logger.LogWarning("Pins directory '{Directory}' does not exist.", dir);
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var dbPath = Path.Combine(dir, "pins_catalog.sqlite");
                int loadedFromDb = 0;

                if (File.Exists(dbPath))
                {
                    try
                    {
                        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT id, filename, category, dominant_color, hue_group, width, height, is_favorite, is_hidden FROM pins;";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var fn = reader.GetString(1);
                            var fullPath = Path.Combine(dir, fn);
                            if (!File.Exists(fullPath)) continue;

                            var cat = reader.IsDBNull(2) ? "Общее" : reader.GetString(2);
                            var hex = reader.IsDBNull(3) ? "#28282d" : reader.GetString(3);
                            var hueStr = reader.IsDBNull(4) ? "NeutralDark" : reader.GetString(4);
                            var w = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                            var h = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
                            var isFav = !reader.IsDBNull(7) && reader.GetInt32(7) == 1;
                            var isHidden = !reader.IsDBNull(8) && reader.GetInt32(8) == 1;

                            var dominant = ParseHexColor(hex);
                            var (hue, sat, bri) = dominant.ToHsv();
                            if (!Enum.TryParse<HueGroup>(hueStr, true, out var hg))
                            {
                                hg = HueClassifier.Classify(dominant);
                            }

                            var pin = new PinItem
                            {
                                FilePath = fullPath,
                                FileName = fn,
                                DominantColor = dominant,
                                HueGroup = hg,
                                Hue = hue,
                                Saturation = sat,
                                Brightness = bri,
                                Width = w,
                                Height = h,
                                Category = cat,
                                IsFavorite = isFav,
                                IsHidden = isHidden
                            };

                            _indexedPins[fullPath] = pin;
                            loadedFromDb++;
                        }
                        _logger.LogInformation("Loaded {Count} pins from SQLite catalog.", loadedFromDb);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load pins from SQLite. Falling back to disk scan.");
                    }
                }

                var files = Directory.GetFiles(dir)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                    .Where(f => !Path.GetFileName(f).StartsWith(".trashed", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                int added = 0;
                foreach (var file in files)
                {
                    if (_indexedPins.ContainsKey(file)) continue;

                    try
                    {
                        using var codec = SKCodec.Create(file);
                        if (codec == null) continue;

                        var info = codec.Info;
                        using var bmp = SKBitmap.Decode(codec);
                        if (bmp == null) continue;

                        var colors = ColorQuantizer.ExtractDominantColors(bmp, k: 3);
                        var dominant = colors.Count > 0 ? colors[0] : new RgbColor(40, 40, 45);
                        var (h, s, v) = dominant.ToHsv();
                        var hueGroup = HueClassifier.Classify(dominant);

                        var pin = new PinItem
                        {
                            FilePath = file,
                            FileName = Path.GetFileName(file),
                            DominantColor = dominant,
                            HueGroup = hueGroup,
                            Hue = h,
                            Saturation = s,
                            Brightness = v,
                            Width = info.Width,
                            Height = info.Height,
                            Category = "Общее"
                        };

                        _indexedPins[file] = pin;
                        added++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to analyze pin '{File}'. Skipping.", file);
                    }
                }

                _logger.LogInformation("Pins indexing complete. Total indexed: {Total} (new: {New}, sqlite: {FromDb})",
                    _indexedPins.Count, added, loadedFromDb);
                IndexUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while scanning pins directory.");
            }
        });
    }

    public IReadOnlyList<(string Category, int Count)> GetCategoriesWithCounts()
    {
        return _indexedPins.Values
            .Where(p => !p.IsHidden)
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Category) ? "Общее" : p.Category)
            .Select(g => (Category: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();
    }

    private static RgbColor ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return new RgbColor(40, 40, 45);
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        }
        if (hex.Length != 6) return new RgbColor(40, 40, 45);
        if (byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return new RgbColor(r, g, b);
        }
        return new RgbColor(40, 40, 45);
    }

    private void SetupWatcher()
    {
        var dir = _configManager.CurrentConfig.Collage.PinsDirectory;
        if (!Directory.Exists(dir)) return;

        try
        {
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnDirectoryChanged;
            _watcher.Deleted += OnDirectoryChanged;
            _watcher.Renamed += OnDirectoryChanged;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start FileSystemWatcher for pins directory.");
        }
    }

    private void OnDirectoryChanged(object sender, FileSystemEventArgs e)
    {
        // 500ms debounce
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Threading.Timer(_ =>
        {
            RefreshIndex();
        }, null, 500, Timeout.Infinite);
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        base.Dispose();
    }
}
