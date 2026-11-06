using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace TaskbarSpike;

/// <summary>
/// Toasts from the Windows notification database (the same store the shell badge counts come from).
/// Dismiss and clear hide rows in this panel only — the database is opened read-only.
/// </summary>
internal static class NotificationLog
{
    internal readonly record struct Item(long Id, string AppId, string App, string Title, string Text, string Time);

    private static readonly HashSet<long> Hidden = new();
    private static List<Item> _items = new();
    private static long _readAt;
    private static bool _failed;

    public static bool Unavailable => _failed;

    public static IReadOnlyList<Item> Current()
    {
        if (Environment.TickCount64 - _readAt > 2000)
        {
            Refresh();
        }

        return _items;
    }

    public static void Dismiss(long id)
    {
        Hidden.Add(id);
        _items = _items.Where(item => item.Id != id).ToList();
    }

    public static void Clear()
    {
        foreach (var item in _items)
        {
            Hidden.Add(item.Id);
        }

        _items = new List<Item>();
    }

    public static void Launch(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:AppsFolder\\" + appId,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] notification launch: " + ex.Message);
        }
    }

    private static void Refresh()
    {
        _readAt = Environment.TickCount64;
        try
        {
            var next = Read();
            _failed = false;
            if (!Same(next, _items))
            {
                _items = next;
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            _items = new List<Item>();
            Console.WriteLine("[spike] notifications: " + ex.Message);
        }
    }

    private static bool Same(List<Item> next, List<Item> previous)
    {
        if (next.Count != previous.Count)
        {
            return false;
        }

        for (var i = 0; i < next.Count; i++)
        {
            if (next[i].Id != previous[i].Id || next[i].Title != previous[i].Title)
            {
                return false;
            }
        }

        return true;
    }

    private static List<Item> Read()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Notifications\wpndatabase.db");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("wpndatabase.db");
        }

        var list = new List<Item>();
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.Id, n.Payload, n.ArrivalTime, n.ExpiryTime, h.PrimaryId
            FROM Notification n
            LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId
            WHERE n.Type = 'toast'
            ORDER BY n.ArrivalTime DESC
            LIMIT 40
            """;
        using var reader = command.ExecuteReader();
        var now = DateTime.Now;
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (Hidden.Contains(id))
            {
                continue;
            }

            if (!reader.IsDBNull(3))
            {
                var expiry = reader.GetInt64(3);
                if (expiry > 0 && FromFileTime(expiry) < now)
                {
                    continue;
                }
            }

            var payload = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
            var (title, text) = Texts(payload);
            if (title.Length == 0 && text.Length == 0)
            {
                continue;
            }

            var arrived = reader.IsDBNull(2) ? now : FromFileTime(reader.GetInt64(2));
            var appId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            list.Add(new Item(id, appId, AppName(appId), title, text, When(arrived, now)));
            if (list.Count >= 12)
            {
                break;
            }
        }

        return list;
    }

    private static SqliteConnection Open(string path)
    {
        try
        {
            return Connect(path);
        }
        catch (SqliteException)
        {
            var copy = Path.Combine(Path.GetTempPath(), "TaskbarSpike_notifications.db");
            File.Copy(path, copy, true);
            return Connect(copy);
        }
    }

    private static SqliteConnection Connect(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static (string Title, string Text) Texts(byte[]? payload)
    {
        if (payload == null || payload.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            var xml = payload.Length >= 2 && payload[0] == 0xFF && payload[1] == 0xFE
                ? Encoding.Unicode.GetString(payload)
                : Encoding.UTF8.GetString(payload);
            var document = XDocument.Parse(xml);
            var lines = document.Descendants()
                .Where(element => element.Name.LocalName == "text")
                .Select(element => (element.Value ?? string.Empty).Trim())
                .Where(line => line.Length > 0)
                .Take(2)
                .ToList();
            return lines.Count switch
            {
                0 => (string.Empty, string.Empty),
                1 => (lines[0], string.Empty),
                _ => (lines[0], lines[1]),
            };
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private static string AppName(string primary)
    {
        if (string.IsNullOrWhiteSpace(primary))
        {
            return "Уведомление";
        }

        var id = primary;
        var bang = id.IndexOf('!');
        if (bang > 0)
        {
            id = id[..bang];
        }

        var under = id.LastIndexOf('_');
        if (under > 0)
        {
            id = id[..under];
        }

        var dot = id.LastIndexOf('.');
        if (dot >= 0 && dot < id.Length - 1)
        {
            id = id[(dot + 1)..];
        }

        return id.Length == 0 ? primary : id;
    }

    private static DateTime FromFileTime(long value)
    {
        try
        {
            return DateTime.FromFileTime(value);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    private static string When(DateTime arrived, DateTime now)
    {
        if (arrived.Date == now.Date)
        {
            return arrived.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        return arrived.ToString("d MMM", CultureInfo.GetCultureInfo("ru-RU"));
    }
}
