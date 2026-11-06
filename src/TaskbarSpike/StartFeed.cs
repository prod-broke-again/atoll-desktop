using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;

namespace TaskbarSpike;

/// <summary>
/// Start menu and Phone Link payloads for the popup page (HANDOFF 5.2, 8.2).
/// The catalog is built off the UI thread and kept warm, so opening Start does not
/// scan the Start menu or read the phone card. Search results are filled when the
/// page (or the Win10 search box) sends a query. The phone-column choice is remembered.
/// </summary>
internal static class StartFeed
{
    private static readonly object Gate = new();
    private static readonly string PrefPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarSpike",
        "start.json");

    private static Control? _ui;
    private static Dictionary<string, SearchHit> _hits = new(StringComparer.Ordinal);
    private static object _user = new { name = "", initial = "?" };
    private static object _pinned = Array.Empty<object>();
    private static object _recommended = Array.Empty<object>();
    private static object _groups = Array.Empty<object>();
    private static object _tiles = Array.Empty<object>();
    private static object _results = Array.Empty<object>();
    private static bool _hasDevice;
    private static object? _device;
    private static object? _features;
    private static object? _recent;
    private static string _recentHeading = "Недавние";
    private static bool _phoneOn = true;
    /// <summary>Local Start tile order (stable keys = Launch). Null means follow start2.bin pins as-is.</summary>
    private static List<string>? _tileKeys;
    private static string _query = "";
    private static string _payload = "";
    private static int _building;
    private static int _pending;
    private static DateTime _builtAt = DateTime.MinValue;
    private static DateTime _phoneAt = DateTime.MinValue;
    private static Pack? _phoneCache;

    public static event Action? Changed;

    public static void Prime(Control ui)
    {
        _ui = ui;
        LoadPref();
        lock (Gate)
        {
            _user = UserNow();
            RebuildPayload();
        }

        Kick();
    }

    public static void Touch()
    {
        if (DateTime.UtcNow - _builtAt > TimeSpan.FromSeconds(25))
        {
            Kick();
        }
    }

    public static string PayloadJson()
    {
        lock (Gate)
        {
            return _payload;
        }
    }

    public static void SetQuery(string? query)
    {
        lock (Gate)
        {
            _query = query ?? "";
            DropQueryHits();
            if (_query.Trim().Length == 0)
            {
                _results = Array.Empty<object>();
            }
            else
            {
                FillResults();
            }

            RebuildPayload();
        }
    }

    public static void SetPhone(bool visible)
    {
        lock (Gate)
        {
            if (_phoneOn == visible)
            {
                return;
            }

            _phoneOn = visible;
            RebuildPayload();
        }

        SavePref();
    }

    public static void Activate(string id)
    {
        if (id == "web")
        {
            string query;
            lock (Gate)
            {
                query = _query;
            }

            if (query.Trim().Length > 0)
            {
                SearchCatalog.Launch(SearchCatalog.Web(query));
            }

            return;
        }

        if (id.StartsWith("shell:", StringComparison.Ordinal))
        {
            OpenShell(id["shell:".Length..]);
            return;
        }

        SearchHit? hit;
        lock (Gate)
        {
            _hits.TryGetValue(id, out hit);
        }

        if (hit != null)
        {
            SearchCatalog.Launch(hit);
        }
    }

    public static void OpenPhone(string launch)
    {
        if (launch.Length == 0 || launch.Length > 512 || !launch.StartsWith("ms-phone:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] phone launch: " + ex.Message);
        }
    }

    public static void Power(string action) => StartMenu.Power(action);

    /// <summary>Unpin a Start tile (id like p:0 / a:3). Persists a local override of the tile strip.</summary>
    public static void UnpinTile(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (Gate)
        {
            if (!_hits.TryGetValue(id, out var hit) || string.IsNullOrWhiteSpace(hit.Launch))
            {
                return;
            }

            EnsureTileKeysLocked();
            if (_tileKeys == null)
            {
                return;
            }

            var key = TileKey(hit);
            if (_tileKeys.RemoveAll(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return;
            }

            SavePref();
        }

        Kick();
    }

    /// <summary>Pin an app from the all-apps list onto the Start tile strip.</summary>
    public static void PinTile(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (Gate)
        {
            if (!_hits.TryGetValue(id, out var hit) || string.IsNullOrWhiteSpace(hit.Launch))
            {
                return;
            }

            EnsureTileKeysLocked();
            if (_tileKeys == null)
            {
                return;
            }

            var key = TileKey(hit);
            if (_tileKeys.Any(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _tileKeys.Add(key);
            SavePref();
        }

        Kick();
    }

    private static void OpenShell(string key)
    {
        var target = key switch
        {
            "docs" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "pics" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "settings" => "ms-settings:",
            "account" => "ms-settings:yourinfo",
            _ => "",
        };
        if (target.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] start shell: " + ex.Message);
        }
    }

    private static void Kick()
    {
        Interlocked.Exchange(ref _pending, 1);
        if (Interlocked.Exchange(ref _building, 1) == 1)
        {
            return;
        }

        Task.Run(Work);
    }

    private static void Work()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _pending, 0);
                Apply(Build(false));
                Apply(Build(true));
            }
            while (Interlocked.CompareExchange(ref _pending, 0, 1) == 1);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] start: " + ex.Message);
        }
        finally
        {
            _builtAt = DateTime.UtcNow;
            Interlocked.Exchange(ref _building, 0);
            if (Interlocked.Exchange(ref _pending, 0) == 1)
            {
                Kick();
            }
        }
    }

    private static void Apply(Pack pack)
    {
        lock (Gate)
        {
            _hits = pack.Hits;
            _user = pack.User;
            _pinned = pack.Pinned;
            _recommended = pack.Recommended;
            _groups = pack.Groups;
            _tiles = pack.Tiles;
            _hasDevice = pack.HasDevice;
            _device = pack.Device;
            _features = pack.Features;
            _recent = pack.Recent;
            _recentHeading = pack.RecentHeading;
            DropQueryHits();
            if (_query.Trim().Length == 0)
            {
                _results = Array.Empty<object>();
            }
            else
            {
                FillResults();
            }

            RebuildPayload();
        }

        Raise();
    }

    private static Pack Build(bool icons)
    {
        var pack = new Pack { User = UserNow() };
        var hits = pack.Hits;
        var pinned = new List<object>();
        var pinIndex = 0;
        foreach (var pin in StartLayout.Pins())
        {
            var id = "p:" + pinIndex++;
            hits[id] = pin;
            pinned.Add(new { id, label = pin.Title, icon = icons ? SearchCatalog.IconData(pin.IconPath) : null });
        }

        pack.Pinned = pinned;

        var recommended = new List<object>();
        var recIndex = 0;
        var files = StartLayout.Files(8);
        IReadOnlyList<SearchHit> recSource = files.Count > 0 ? files : StartLayout.Added(4);
        foreach (var hit in recSource)
        {
            var id = "r:" + recIndex++;
            hits[id] = hit;
            recommended.Add(new
            {
                id,
                name = hit.Title,
                sub = hit.Detail,
                icon = icons ? SearchCatalog.IconData(hit.IconPath) : null,
                file = hit.Kind == "file",
            });
        }

        pack.Recommended = recommended;

        var groups = new List<object>();
        List<object>? bucket = null;
        var letter = "";
        var appIndex = 0;
        var tileItems = new List<object>();
        foreach (var app in SearchCatalog.Applications())
        {
            var id = "a:" + appIndex++;
            hits[id] = app;
            var item = new { id, label = app.Title, icon = icons ? SearchCatalog.IconData(app.IconPath) : null };
            var next = Letter(app.Title);
            if (!string.Equals(letter, next, StringComparison.Ordinal))
            {
                bucket = new List<object>();
                letter = next;
                groups.Add(new { letter = next, items = bucket });
            }

            bucket!.Add(item);
            if (tileItems.Count < 12 && pinned.Count == 0)
            {
                tileItems.Add(item);
            }
        }

        pack.Groups = groups;
        pack.Tiles = TilesOf(ResolveTileItems(pinned, tileItems, hits, icons));
        ReadPhone(pack);
        return pack;
    }

    private static List<object> ResolveTileItems(
        List<object> pinned,
        List<object> fallback,
        Dictionary<string, SearchHit> hits,
        bool icons)
    {
        List<string>? keys;
        lock (Gate)
        {
            keys = _tileKeys?.ToList();
        }

        if (keys == null)
        {
            return pinned.Count > 0 ? pinned : fallback;
        }

        var byKey = new Dictionary<string, (SearchHit Hit, string Id)>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in hits)
        {
            if (pair.Key.StartsWith("q:", StringComparison.Ordinal))
            {
                continue;
            }

            var key = TileKey(pair.Value);
            if (key.Length == 0 || byKey.ContainsKey(key))
            {
                continue;
            }

            byKey[key] = (pair.Value, pair.Key);
        }

        var items = new List<object>();
        foreach (var key in keys)
        {
            if (!byKey.TryGetValue(key, out var entry))
            {
                continue;
            }

            items.Add(new
            {
                id = entry.Id,
                label = entry.Hit.Title,
                icon = icons ? SearchCatalog.IconData(entry.Hit.IconPath) : null,
            });
        }

        return items;
    }

    private static object TilesOf(List<object> source)
    {
        var tiles = new List<object>();
        if (source.Count == 0)
        {
            return tiles;
        }

        var first = source.Take(6).ToList();
        tiles.Add(new { title = "Работа", items = first });
        if (source.Count > 6)
        {
            tiles.Add(new { title = "Общение и медиа", items = source.Skip(6).Take(6).ToList() });
        }

        return tiles;
    }

    /// <summary>Caller holds <see cref="Gate"/>. Seeds <see cref="_tileKeys"/> from the current pin strip.</summary>
    private static void EnsureTileKeysLocked()
    {
        if (_tileKeys != null)
        {
            return;
        }

        var keys = new List<string>();
        foreach (var pair in _hits.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!pair.Key.StartsWith("p:", StringComparison.Ordinal))
            {
                continue;
            }

            var key = TileKey(pair.Value);
            if (key.Length > 0)
            {
                keys.Add(key);
            }
        }

        if (keys.Count == 0)
        {
            foreach (var pair in _hits.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!pair.Key.StartsWith("a:", StringComparison.Ordinal))
                {
                    continue;
                }

                var key = TileKey(pair.Value);
                if (key.Length > 0 && keys.Count < 12)
                {
                    keys.Add(key);
                }
            }
        }

        _tileKeys = keys;
    }

    private static string TileKey(SearchHit hit)
    {
        if (!string.IsNullOrWhiteSpace(hit.Launch))
        {
            return hit.Launch;
        }

        return hit.IconPath ?? hit.Title;
    }

    private static void ReadPhone(Pack pack)
    {
        if (_phoneCache != null && DateTime.UtcNow - _phoneAt < TimeSpan.FromSeconds(30))
        {
            pack.HasDevice = _phoneCache.HasDevice;
            pack.Device = _phoneCache.Device;
            pack.Features = _phoneCache.Features;
            pack.Recent = _phoneCache.Recent;
            pack.RecentHeading = _phoneCache.RecentHeading;
            return;
        }

        var snapshot = PhoneLink.TryRead(out var problem);
        _phoneAt = DateTime.UtcNow;
        if (snapshot == null)
        {
            if (!string.IsNullOrEmpty(problem))
            {
                Console.WriteLine("[spike] phone: " + problem);
            }

            _phoneCache = pack;
            return;
        }

        try
        {
            var glyphs = new List<string>();
            foreach (var glyph in snapshot.Status)
            {
                var url = Png(glyph.Image);
                if (url != null)
                {
                    glyphs.Add(url);
                }
            }

            pack.HasDevice = true;
            pack.Device = new
            {
                name = snapshot.DeviceName,
                status = string.IsNullOrWhiteSpace(snapshot.StatusLabel) ? "Подключено" : snapshot.StatusLabel,
                launch = snapshot.DeviceLaunch,
                wallpaper = Png(snapshot.Wallpaper),
                glyphs,
            };
            pack.Features = snapshot.Features.Select(entry => new
            {
                title = entry.Title,
                icon = Png(entry.Icon),
                count = entry.Count,
                launch = entry.Launch,
            }).ToList();
            pack.Recent = snapshot.Recent.Select(entry => new
            {
                title = entry.Title,
                subtitle = entry.Subtitle,
                icon = Png(entry.Icon),
                missed = IsMissed(entry.Subtitle),
                launch = entry.Launch,
                initial = PersonInitial(entry.Title),
            }).ToList();
            pack.RecentHeading = snapshot.RecentHeading;
            _phoneCache = new Pack
            {
                HasDevice = true,
                Device = pack.Device,
                Features = pack.Features,
                Recent = pack.Recent,
                RecentHeading = pack.RecentHeading,
            };
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    /// <summary>Caller holds <see cref="Gate"/>. Replaces q: hits from <see cref="SearchCatalog.Find"/>.</summary>
    private static void FillResults()
    {
        var results = new List<object>();
        var index = 0;
        foreach (var hit in SearchCatalog.Find(_query))
        {
            var id = "q:" + index++;
            _hits[id] = hit;
            results.Add(new
            {
                id,
                label = hit.Title,
                kind = KindLabel(hit),
                icon = SearchCatalog.IconData(hit.IconPath),
                file = hit.Kind == "file",
            });
        }

        _results = results;
    }

    private static void DropQueryHits()
    {
        foreach (var key in _hits.Keys.Where(key => key.StartsWith("q:", StringComparison.Ordinal)).ToList())
        {
            _hits.Remove(key);
        }
    }

    private static void RebuildPayload()
    {
        _payload = JsonSerializer.Serialize(new
        {
            type = "start-data",
            q = _query,
            start = new
            {
                user = _user,
                pinned = _pinned,
                recommended = _recommended,
                groups = _groups,
                tiles = _tiles,
                results = _results,
            },
            phone = PhoneObject(),
        });
    }

    private static object? PhoneObject()
    {
        if (!_hasDevice)
        {
            return null;
        }

        return new
        {
            visible = _phoneOn,
            device = _device,
            features = _features,
            recent = _recent,
            recentHeading = _recentHeading,
        };
    }

    private static object UserNow()
    {
        string name;
        try
        {
            name = StartMenu.AccountName();
        }
        catch
        {
            name = Environment.UserName;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.UserName;
        }

        string? avatar = null;
        try
        {
            using var image = StartLayout.AccountImage();
            avatar = Png(image);
        }
        catch
        {
            avatar = null;
        }

        return new { name, initial = PersonInitial(name), avatar };
    }

    private static string KindLabel(SearchHit hit) => hit.Kind switch
    {
        "setting" => "Параметры",
        "file" => string.IsNullOrWhiteSpace(hit.Detail) ? "Файл" : hit.Detail,
        "web" => "Интернет",
        _ => "Приложение",
    };

    private static string Letter(string name)
    {
        var text = name.Trim();
        if (text.Length == 0)
        {
            return "#";
        }

        var ch = char.ToUpper(text[0], CultureInfo.CurrentCulture);
        return char.IsLetter(ch) ? ch.ToString() : "#";
    }

    private static string PersonInitial(string name)
    {
        var text = name.Trim();
        if (text.Length == 0)
        {
            return "?";
        }

        var ch = text[0];
        if (char.IsDigit(ch) || ch is '+' or '#')
        {
            return "#";
        }

        return char.ToUpper(ch, CultureInfo.CurrentCulture).ToString();
    }

    private static bool IsMissed(string subtitle)
    {
        return subtitle.Contains("пропущ", StringComparison.OrdinalIgnoreCase)
            || subtitle.Contains("missed", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Png(Image? image)
    {
        if (image == null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream();
            using var copy = new Bitmap(image);
            copy.Save(stream, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static void LoadPref()
    {
        try
        {
            if (!File.Exists(PrefPath))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(PrefPath));
            if (document.RootElement.TryGetProperty("phone", out var phone)
                && phone.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _phoneOn = phone.GetBoolean();
            }

            if (document.RootElement.TryGetProperty("tiles", out var tiles)
                && tiles.ValueKind == JsonValueKind.Array)
            {
                var keys = new List<string>();
                foreach (var entry in tiles.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String)
                    {
                        var key = entry.GetString();
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            keys.Add(key);
                        }
                    }
                }

                _tileKeys = keys;
            }
        }
        catch
        {
            // A broken preference file just means the column starts visible.
        }
    }

    private static void SavePref()
    {
        try
        {
            var dir = Path.GetDirectoryName(PrefPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            object payload = _tileKeys == null
                ? new { phone = _phoneOn }
                : new { phone = _phoneOn, tiles = _tileKeys };
            File.WriteAllText(PrefPath, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] start pref: " + ex.Message);
        }
    }

    private static void Raise()
    {
        var ui = _ui;
        if (ui == null || ui.IsDisposed || !ui.IsHandleCreated)
        {
            return;
        }

        if (ui.InvokeRequired)
        {
            try
            {
                ui.BeginInvoke(new Action(Raise));
            }
            catch (InvalidOperationException)
            {
                // The window is closing.
            }

            return;
        }

        Changed?.Invoke();
    }

    private sealed class Pack
    {
        public Dictionary<string, SearchHit> Hits { get; } = new(StringComparer.Ordinal);

        public object User { get; set; } = new { name = "", initial = "?" };

        public object Pinned { get; set; } = Array.Empty<object>();

        public object Recommended { get; set; } = Array.Empty<object>();

        public object Groups { get; set; } = Array.Empty<object>();

        public object Tiles { get; set; } = Array.Empty<object>();

        public bool HasDevice { get; set; }

        public object? Device { get; set; }

        public object? Features { get; set; }

        public object? Recent { get; set; }

        public string RecentHeading { get; set; } = "Недавние";
    }
}
