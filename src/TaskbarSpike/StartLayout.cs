using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace TaskbarSpike;

/// <summary>
/// The signed-in user's Start layout: pinned order from start2.bin, recently added
/// shortcuts, and recent documents. The Windows Start window is never opened.
/// start2.bin is only read. Nothing is written back.
/// </summary>
internal static class StartLayout
{
    private static readonly byte[] Magic =
    {
        0xE2, 0x7A, 0xE1, 0x4B, 0x01, 0xFC, 0x4D, 0x1B,
        0x9C, 0x00, 0x81, 0x0B, 0xDE, 0x6E, 0x51, 0x85,
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic",
        ".txt", ".rtf", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".csv", ".odt", ".ods", ".odp",
    };

    private static IReadOnlyList<SearchHit>? _pins;
    private static DateTime _pinsAt = DateTime.MinValue;
    private static string? _error;

    public static string Describe()
    {
        var pins = Pins();
        var added = Added(6);
        var files = Files(6);
        var icon = "missing";
        if (pins.Count > 0)
        {
            var image = SearchCatalog.LoadIcon(pins[0].IconPath);
            if (image != null)
            {
                icon = "ok";
                image.Dispose();
            }
        }

        var report = new StringBuilder();
        report.Append("start pins=").Append(pins.Count);
        report.Append(" apps=").Append(SearchCatalog.Applications().Count);
        report.Append(" added=").Append(added.Count);
        report.Append(" files=").Append(files.Count);
        report.Append(" icon=").Append(icon);
        if (!string.IsNullOrWhiteSpace(_error))
        {
            report.Append(" error=").Append(_error);
        }

        report.AppendLine();
        report.Append(string.Join(" | ", pins.Select(pin => pin.Title)));
        if (added.Count > 0)
        {
            report.AppendLine();
            report.Append("added: ").Append(string.Join(" | ", added.Select(app => app.Title)));
        }

        if (files.Count > 0)
        {
            report.AppendLine();
            report.Append("files: ").Append(string.Join(" | ", files.Select(file => file.Title)));
        }

        return report.ToString();
    }

    public static IReadOnlyList<SearchHit> Pins()
    {
        if (_pins != null && DateTime.UtcNow - _pinsAt < TimeSpan.FromSeconds(20))
        {
            return _pins;
        }

        var loaded = LoadPins();
        _pins = loaded.Count > 0 ? loaded : SearchCatalog.Home();
        _pinsAt = DateTime.UtcNow;
        return _pins;
    }

    public static IReadOnlyList<SearchHit> Added(int take)
    {
        var pinned = new HashSet<string>(Pins().Select(pin => pin.Title), StringComparer.CurrentCultureIgnoreCase);
        var found = new Dictionary<string, (DateTime Created, string Link)>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var root in StartMenuRoots())
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (file.Contains("\\Startup\\", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains("\\Автозагрузка\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name) || IsNoise(name) || pinned.Contains(name))
                {
                    continue;
                }

                DateTime created;
                try
                {
                    created = File.GetCreationTime(file);
                }
                catch
                {
                    continue;
                }

                if (DateTime.Now - created > TimeSpan.FromDays(7))
                {
                    continue;
                }

                if (!found.TryGetValue(name, out var previous) || created > previous.Created)
                {
                    found[name] = (created, file);
                }
            }
        }

        return found
            .OrderByDescending(item => item.Value.Created)
            .Take(take)
            .Select(item => Hit(item.Key, "Недавно добавленные", item.Value.Link, null, item.Value.Link))
            .ToList();
    }

    public static IReadOnlyList<SearchHit> Files(int take)
    {
        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrWhiteSpace(recent) || !Directory.Exists(recent))
        {
            return Array.Empty<SearchHit>();
        }

        IEnumerable<string> links;
        try
        {
            links = Directory.EnumerateFiles(recent, "*.lnk").OrderByDescending(File.GetLastWriteTime);
        }
        catch
        {
            return Array.Empty<SearchHit>();
        }

        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (hits.Count >= take)
            {
                break;
            }

            var target = ShortcutTarget(link);
            if (string.IsNullOrWhiteSpace(target) || !seen.Add(target) || !File.Exists(target))
            {
                continue;
            }

            if (!DocumentExtensions.Contains(Path.GetExtension(target)))
            {
                continue;
            }

            DateTime when;
            try
            {
                when = File.GetLastWriteTime(link);
            }
            catch
            {
                when = DateTime.Now;
            }

            var title = Path.GetFileNameWithoutExtension(target);
            hits.Add(new SearchHit("file", title, Age(when), link, null, target));
        }

        return hits;
    }

    public static Image? AccountImage()
    {
        foreach (var path in AccountImagePaths())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var image = Image.FromStream(stream);
                return new Bitmap(image);
            }
            catch
            {
                // The next candidate is tried.
            }
        }

        return null;
    }

    private static List<SearchHit> LoadPins()
    {
        _error = null;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState\start2.bin");
            if (!File.Exists(path))
            {
                _error = "no start2.bin";
                return new List<SearchHit>();
            }

            var plain = Decrypt(File.ReadAllBytes(path));
            var json = Encoding.UTF8.GetString(plain).Trim('\uFEFF', '\0', ' ', '\r', '\n');
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("pinnedList", out var list))
            {
                _error = "no pinnedList";
                return new List<SearchHit>();
            }

            var hits = new List<SearchHit>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.TryGetProperty("pinType", out var pinType) && pinType.ValueKind == JsonValueKind.Number && pinType.GetInt32() == 2)
                {
                    if (entry.TryGetProperty("items", out var items))
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            AddPin(hits, item);
                        }
                    }

                    continue;
                }

                AddPin(hits, entry);
            }

            return hits;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return new List<SearchHit>();
        }
    }

    private static void AddPin(List<SearchHit> hits, JsonElement entry)
    {
        if (!entry.TryGetProperty("id", out var idElement))
        {
            return;
        }

        var raw = idElement.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var hit = Resolve(raw);
        if (hit != null)
        {
            hits.Add(hit);
        }
    }

    private static SearchHit? Resolve(string raw)
    {
        var key = raw.StartsWith("P~", StringComparison.Ordinal) || raw.StartsWith("W~", StringComparison.Ordinal)
            ? raw[2..]
            : raw;
        var file = ToFile(key);
        var parsing = file != null && (File.Exists(file) || Directory.Exists(file))
            ? file
            : "shell:AppsFolder\\" + key;
        var name = SearchCatalog.ShellName(parsing) ?? SearchCatalog.ShellName("shell:AppsFolder\\" + key);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = file != null ? Path.GetFileNameWithoutExtension(file) : key;
        }

        if (name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            name = "Windows PowerShell";
        }

        foreach (var app in SearchCatalog.Applications())
        {
            if (string.Equals(app.Title, name, StringComparison.OrdinalIgnoreCase))
            {
                name = app.Title;
                break;
            }
        }

        string launch;
        string icon;
        if (file != null && (File.Exists(file) || Directory.Exists(file)))
        {
            launch = file;
            icon = file;
        }
        else if (key.Equals("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase))
        {
            launch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            icon = launch;
            name = string.IsNullOrWhiteSpace(name) ? "Проводник" : name;
        }
        else
        {
            launch = "shell:AppsFolder\\" + key;
            icon = launch;
        }

        return Hit(name, "Приложение", launch, RunningId(name), icon);
    }

    private static string? ToFile(string key)
    {
        if (key.Length >= 3 && char.IsLetter(key[0]) && key[1] == ':' && (key[2] == '\\' || key[2] == '/'))
        {
            return key;
        }

        var slash = key.IndexOf('\\');
        if (slash <= 0 || key[0] != '{')
        {
            return null;
        }

        if (!Guid.TryParse(key[..slash], out var folderId))
        {
            return null;
        }

        var root = KnownFolder(folderId);
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        return Path.Combine(root, key[(slash + 1)..]);
    }

    private static SearchHit Hit(string title, string detail, string launch, string? appId, string icon)
    {
        return new SearchHit("app", title, detail, launch, appId, icon);
    }

    private static string? RunningId(string title)
    {
        foreach (var app in WindowCatalog.Current)
        {
            if (app.Running && string.Equals(app.Name, title, StringComparison.CurrentCultureIgnoreCase))
            {
                return app.Id;
            }
        }

        return null;
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        var common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        if (Directory.Exists(user))
        {
            yield return user;
        }

        if (Directory.Exists(common))
        {
            yield return common;
        }
    }

    private static bool IsNoise(string name)
    {
        var text = name.ToLowerInvariant();
        return text.Contains("uninstall", StringComparison.Ordinal) ||
               text.Contains("uninstaller", StringComparison.Ordinal) ||
               text.Contains("удалить", StringComparison.Ordinal) ||
               text.Contains("удаление", StringComparison.Ordinal);
    }

    private static string Age(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "Только что";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return Math.Max(1, (int)span.TotalMinutes) + " мин назад";
        }

        if (span < TimeSpan.FromHours(24))
        {
            return Math.Max(1, (int)span.TotalHours) + " ч назад";
        }

        return Math.Max(1, (int)span.TotalDays) + " дн. назад";
    }

    private static IEnumerable<string> AccountImagePaths()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        var publics = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrWhiteSpace(sid) && !string.IsNullOrWhiteSpace(publics))
        {
            var folder = Path.Combine(publics, "AccountPictures", sid);
            if (Directory.Exists(folder))
            {
                // Prefer mid sizes used by the Start rail; fall back to any AccountPictures file.
                foreach (var size in new[] { "96", "64", "192", "240", "48", "448", "424", "208", "32", "40", "1080" })
                {
                    string[] matches;
                    try
                    {
                        matches = Directory.GetFiles(folder, "*-Image" + size + ".jpg");
                    }
                    catch
                    {
                        matches = Array.Empty<string>();
                    }

                    foreach (var match in matches)
                    {
                        yield return match;
                    }
                }

                string[] leftovers;
                try
                {
                    leftovers = Directory.GetFiles(folder, "*-Image*.jpg");
                }
                catch
                {
                    leftovers = Array.Empty<string>();
                }

                foreach (var match in leftovers)
                {
                    yield return match;
                }
            }
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\AccountPicture\UserImage.jpg");
    }

    private static string? ShortcutTarget(string linkPath)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(linkPath, 0);
            var buffer = new StringBuilder(520);
            var find = Marshal.AllocCoTaskMem(592);
            try
            {
                link.GetPath(buffer, buffer.Capacity, find, 0);
            }
            finally
            {
                Marshal.FreeCoTaskMem(find);
            }

            var target = buffer.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link != null)
            {
                Marshal.ReleaseComObject(link);
            }
        }
    }

    private static string? KnownFolder(Guid folderId)
    {
        var hr = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pointer);
        if (hr != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            CoTaskMemFree(pointer);
        }
    }

    private static byte[] Decrypt(byte[] bytes)
    {
        if (bytes.Length < 0x2C)
        {
            throw new InvalidDataException("start2.bin is too short");
        }

        for (var i = 0; i < Magic.Length; i++)
        {
            if (bytes[i] != Magic[i])
            {
                throw new InvalidDataException("start2.bin magic does not match");
            }
        }

        var low = BitConverter.ToUInt32(bytes, 0x20);
        var high = BitConverter.ToUInt32(bytes, 0x24);
        var total = BitConverter.ToUInt32(bytes, 0x28);
        if (total < 0x200)
        {
            throw new InvalidDataException("start2.bin length is invalid");
        }

        var cipherLength = (int)(total - 0x200);
        if (cipherLength <= 0 || cipherLength % 16 != 0)
        {
            throw new InvalidDataException("start2.bin ciphertext is invalid");
        }

        var pad = new Mt19937(0x00AF4D97u ^ low);
        var prePad = (int)(pad.Next() & 0x1FFu);
        var offset = 0x2C + prePad;
        if (bytes.Length < offset + cipherLength)
        {
            throw new InvalidDataException("start2.bin is truncated");
        }

        var keys = SymmetricKeys(high ^ low ^ 0x3B21D91Eu);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = keys.Key;
        aes.IV = keys.Iv;
        using var transform = aes.CreateDecryptor();
        return transform.TransformFinalBlock(bytes, offset, cipherLength);
    }

    private static (byte[] Key, byte[] Iv) SymmetricKeys(uint seed)
    {
        var mt = new Mt19937(seed);
        var keySeed = mt.Next();
        var offset = Uniform(mt, 0x80u - 0x40u);
        var ivSeed = mt.Next();
        var key = Alpha(keySeed, (int)offset + 0x40);
        var iv = Alpha(ivSeed, 16);
        return (key[..32], iv);
    }

    private static uint Uniform(Mt19937 mt, uint range)
    {
        if (range == 0)
        {
            return 0;
        }

        if (range == 0xFFFFFFFFu)
        {
            return mt.Next();
        }

        var bound = (ulong)range + 1UL;
        while (true)
        {
            var value = (ulong)mt.Next();
            var remainder = value % bound;
            var condA = (0xFFFFFFFFUL / bound) <= (value / bound);
            var condB = (0xFFFFFFFFUL % bound) != range;
            if (!(condA && condB))
            {
                return (uint)remainder;
            }
        }
    }

    private static byte[] Alpha(uint seed, int length)
    {
        var mt = new Mt19937(seed);
        ulong value;
        while (true)
        {
            value = mt.Next();
            if (value / 0x3E9UL <= 0x417873UL)
            {
                break;
            }
        }

        mt.Discard((int)(value % 0x3E9UL));
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            ulong next;
            while (true)
            {
                next = mt.Next();
                if (next / 0x60UL <= 0x2AAAAA9UL)
                {
                    break;
                }
            }

            bytes[i] = (byte)((next % 0x60UL) + 0x20UL);
        }

        return bytes;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, IntPtr findData, uint flags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    private sealed class Mt19937
    {
        private readonly uint[] _state = new uint[624];
        private int _index = 624;

        public Mt19937(uint seed)
        {
            _state[0] = seed;
            for (var i = 1; i < 624; i++)
            {
                var previous = _state[i - 1];
                var mixed = previous ^ (previous >> 30);
                _state[i] = unchecked(mixed * 1812433253u + (uint)i);
            }
        }

        public uint Next()
        {
            if (_index >= 624)
            {
                Twist();
            }

            var value = _state[_index++];
            value ^= value >> 11;
            value ^= (value << 7) & 0x9D2C5680u;
            value ^= (value << 15) & 0xEFC60000u;
            value ^= value >> 18;
            return value;
        }

        public void Discard(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Next();
            }
        }

        private void Twist()
        {
            for (var i = 0; i < 624; i++)
            {
                var mixed = (_state[i] & 0x80000000u) | (_state[(i + 1) % 624] & 0x7FFFFFFFu);
                var value = _state[(i + 397) % 624] ^ (mixed >> 1);
                if ((mixed & 1u) != 0)
                {
                    value ^= 0x9908B0DFu;
                }

                _state[i] = value;
            }

            _index = 0;
        }
    }
}
