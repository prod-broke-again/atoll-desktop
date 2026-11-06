using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TaskbarSpike;

/// <summary>
/// Read-only Phone Link databases. Nothing is written back, and nothing is sent to the phone.
/// Message and notification text stays in the returned objects for the Start column.
/// </summary>
internal static class PhoneCache
{
    public static string Summary()
    {
        var folder = Folder();
        if (folder == null)
        {
            return "cache missing Phone Link database";
        }

        try
        {
            var talks = Count(folder, "phone.db", "conversation");
            var calls = Count(folder, "calling.db", "call_history");
            var photos = Count(folder, "photos.db", "media");
            var thumbs = Scalar(folder, "photos.db", "SELECT COUNT(*) FROM media WHERE thumbnail IS NOT NULL AND length(thumbnail) > 32");
            var notices = Count(folder, "notifications.db", "notifications");
            return "cache talks=" + talks + " calls=" + calls + " photos=" + photos + " thumbs=" + thumbs + " notices=" + notices;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            return "cache error=" + ex.GetType().Name;
        }
    }

    public static IReadOnlyList<PhoneTalk> Conversations()
    {
        var folder = Folder();
        if (folder == null)
        {
            return Array.Empty<PhoneTalk>();
        }

        var names = ContactNames(folder);
        using var connection = Open(Path.Combine(folder, "phone.db"));
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.thread_id, c.timestamp, c.msg_count, c.unread_count,
                   COALESCE((SELECT send_address FROM participant WHERE thread_id = c.thread_id LIMIT 1), c.recipient_list, '')
            FROM conversation c
            ORDER BY c.timestamp DESC
            """;
        var list = new List<PhoneTalk>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var raw = reader.IsDBNull(4) ? "" : reader.GetString(4);
            list.Add(new PhoneTalk(
                reader.GetInt64(0),
                Display(raw, names),
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1)));
        }

        return list;
    }

    public static long? FindThread(string title)
    {
        var wanted = title.Trim();
        if (wanted.Length == 0)
        {
            return null;
        }

        var digits = Digits(wanted);
        foreach (var talk in Conversations())
        {
            if (string.Equals(talk.Title, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return talk.Id;
            }

            if (digits.Length >= 3 && Digits(talk.Title) == digits)
            {
                return talk.Id;
            }
        }

        return null;
    }

    public static PhoneThread Thread(long id)
    {
        var folder = Folder();
        var title = "";
        foreach (var talk in Conversations())
        {
            if (talk.Id == id)
            {
                title = talk.Title;
                break;
            }
        }

        var lines = new List<PhoneLine>();
        if (folder == null)
        {
            return new PhoneThread(title, lines);
        }

        using var connection = Open(Path.Combine(folder, "phone.db"));
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type, body, timestamp
            FROM message
            WHERE thread_id = $id AND body IS NOT NULL AND length(trim(body)) > 0
            ORDER BY timestamp
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(new PhoneLine(reader.GetInt32(0) == 2, reader.GetString(1), reader.IsDBNull(2) ? 0 : reader.GetInt64(2)));
        }

        return new PhoneThread(title, lines);
    }

    public static IReadOnlyList<PhoneCall> Calls()
    {
        var folder = Folder();
        if (folder == null || !File.Exists(Path.Combine(folder, "calling.db")))
        {
            return Array.Empty<PhoneCall>();
        }

        var names = ContactNames(folder);
        using var connection = Open(Path.Combine(folder, "calling.db"));
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT phone_number, call_type, duration, start_time
            FROM call_history
            ORDER BY start_time DESC
            """;
        var list = new List<PhoneCall>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var number = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var kind = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            list.Add(new PhoneCall(
                Display(number, names),
                kind switch
                {
                    1 => "Входящий",
                    2 => "Исходящий",
                    3 => "Пропущенный",
                    _ => "Вызов",
                },
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
        }

        return list;
    }

    public static IReadOnlyList<PhoneShot> Shots(int limit)
    {
        var folder = Folder();
        if (folder == null)
        {
            return Array.Empty<PhoneShot>();
        }

        using var connection = Open(Path.Combine(folder, "photos.db"));
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, thumbnail
            FROM media
            WHERE thumbnail IS NOT NULL AND length(thumbnail) > 32
            ORDER BY taken_time DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var list = new List<PhoneShot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bytes = (byte[])reader.GetValue(1);
            list.Add(new PhoneShot(reader.GetInt64(0), bytes));
        }

        return list;
    }

    public static byte[]? Picture(long id)
    {
        var folder = Folder();
        if (folder == null)
        {
            return null;
        }

        using var connection = Open(Path.Combine(folder, "photos.db"));
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                CASE WHEN media IS NOT NULL AND length(media) BETWEEN 64 AND 2500000 THEN media ELSE thumbnail END
            FROM media
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is byte[] bytes && bytes.Length > 32 ? bytes : null;
    }

    public static PhoneNotice? NoticeFor(string app)
    {
        var wanted = app.Trim();
        PhoneNotice? fuzzy = null;
        foreach (var notice in Notices())
        {
            if (string.Equals(notice.App, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return notice;
            }

            if (fuzzy == null &&
                notice.App.Length > 0 &&
                (notice.App.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                 wanted.Contains(notice.App, StringComparison.OrdinalIgnoreCase)))
            {
                fuzzy = notice;
            }
        }

        return fuzzy;
    }

    public static string When(long fileTime)
    {
        try
        {
            if (fileTime < 1)
            {
                return "";
            }

            var local = DateTime.FromFileTime(fileTime);
            var today = DateTime.Now.Date;
            if (local.Date == today)
            {
                return local.ToString("HH:mm", CultureInfo.CurrentCulture);
            }

            if (local.Date == today.AddDays(-1))
            {
                return "вчера " + local.ToString("HH:mm", CultureInfo.CurrentCulture);
            }

            return local.ToString("d MMM", CultureInfo.GetCultureInfo("ru-RU"));
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    private static IReadOnlyList<PhoneNotice> Notices()
    {
        var folder = Folder();
        if (folder == null)
        {
            return Array.Empty<PhoneNotice>();
        }

        using var connection = Open(Path.Combine(folder, "notifications.db"));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json, post_time FROM notifications ORDER BY post_time DESC";
        var list = new List<PhoneNotice>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var json = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var when = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            if (TryNotice(json, when, out var notice))
            {
                list.Add(notice);
            }
        }

        return list;
    }

    private static bool TryNotice(string json, long when, out PhoneNotice notice)
    {
        notice = new PhoneNotice("", "", "", when);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var app = Text(root, "appName");
            var title = Text(root, "title");
            var body = Messages(root);
            if (body.Length == 0)
            {
                body = Text(root, "text");
            }

            if (body.Length == 0)
            {
                body = Text(root, "bigText");
            }

            notice = new PhoneNotice(app, title, body, when);
            return app.Length > 0 || title.Length > 0 || body.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Messages(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var builder = new StringBuilder();
        foreach (var item in messages.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = item.GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    private static string Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return value.GetString()?.Trim() ?? "";
    }

    private static Dictionary<string, string> ContactNames(string folder)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(folder, "contacts.db");
        if (!File.Exists(path))
        {
            return map;
        }

        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.phone_number, c.display_name
            FROM phonenumber p
            JOIN contact c ON c.contact_id = p.contact_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var number = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
            var key = Tail(Digits(number));
            if (key.Length >= 7 && name.Length > 0 && !map.ContainsKey(key))
            {
                map[key] = name;
            }
        }

        return map;
    }

    private static string Display(string raw, Dictionary<string, string> names)
    {
        var text = raw.Trim();
        var key = Tail(Digits(text));
        if (key.Length >= 7 && names.TryGetValue(key, out var name))
        {
            return name;
        }

        return text.Length == 0 ? "Без имени" : text;
    }

    private static int Count(string folder, string file, string table)
    {
        return Scalar(folder, file, "SELECT COUNT(*) FROM " + table);
    }

    private static int Scalar(string folder, string file, string sql)
    {
        var path = Path.Combine(folder, file);
        if (!File.Exists(path))
        {
            return 0;
        }

        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string path)
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

    private static string? Folder()
    {
        var indexed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Packages\Microsoft.YourPhone_8wekyb3d8bbwe\LocalCache\Indexed");
        if (!Directory.Exists(indexed))
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(indexed))
        {
            var database = Path.Combine(dir, @"System\Database");
            if (File.Exists(Path.Combine(database, "phone.db")))
            {
                return database;
            }
        }

        return null;
    }

    private static string Digits(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string Tail(string digits)
    {
        return digits.Length <= 10 ? digits : digits[^10..];
    }
}

internal sealed class PhoneTalk
{
    public PhoneTalk(long id, string title, int unread, int count, long timestamp)
    {
        Id = id;
        Title = title;
        Unread = unread;
        Count = count;
        Timestamp = timestamp;
    }

    public long Id { get; }

    public string Title { get; }

    public int Unread { get; }

    public int Count { get; }

    public long Timestamp { get; }
}

internal sealed class PhoneThread
{
    public PhoneThread(string title, IReadOnlyList<PhoneLine> lines)
    {
        Title = title;
        Lines = lines;
    }

    public string Title { get; }

    public IReadOnlyList<PhoneLine> Lines { get; }
}

internal sealed class PhoneLine
{
    public PhoneLine(bool mine, string text, long timestamp)
    {
        Mine = mine;
        Text = text;
        Timestamp = timestamp;
    }

    public bool Mine { get; }

    public string Text { get; }

    public long Timestamp { get; }
}

internal sealed class PhoneCall
{
    public PhoneCall(string title, string kind, int seconds, long timestamp)
    {
        Title = title;
        Kind = kind;
        Seconds = seconds;
        Timestamp = timestamp;
    }

    public string Title { get; }

    public string Kind { get; }

    public int Seconds { get; }

    public long Timestamp { get; }
}

internal sealed class PhoneShot
{
    public PhoneShot(long id, byte[] bytes)
    {
        Id = id;
        Bytes = bytes;
    }

    public long Id { get; }

    public byte[] Bytes { get; }
}

internal sealed class PhoneNotice
{
    public PhoneNotice(string app, string title, string body, long timestamp)
    {
        App = app;
        Title = title;
        Body = body;
        Timestamp = timestamp;
    }

    public string App { get; }

    public string Title { get; }

    public string Body { get; }

    public long Timestamp { get; }
}
