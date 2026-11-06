using System.Text;
using System.Text.Json;

namespace TaskbarSpike;

/// <summary>
/// Phone Link companion for Start, read from the local Your Phone cache.
/// The file is the adaptive card Windows writes for the Start pane.
/// A click uses the card's own ms-phone link, the same handoff Windows Start uses.
/// Message bodies live under preview cards and are not read.
/// </summary>
internal static class PhoneLink
{
    public static string Describe()
    {
        var snapshot = TryRead(out var problem);
        if (snapshot == null)
        {
            return string.IsNullOrEmpty(problem) ? "phone: none" : "phone: none (" + problem + ")";
        }

        try
        {
            var report = new StringBuilder();
            report.Append("phone device=").Append(Shown(snapshot.DeviceName));
            report.Append(" icons=").Append(snapshot.Status.Count);
            report.Append(" status=").Append(string.Join(" | ", snapshot.Status.Select(item => Shown(item.Tooltip))));
            if (snapshot.StatusLabel.Length > 0)
            {
                report.Append(" label=").Append(Shown(snapshot.StatusLabel));
            }
            report.AppendLine();
            report.Append("features: ").Append(string.Join(" | ", snapshot.Features.Select(item => Shown(item.Title))));
            report.AppendLine();
            report.Append("recent: ");
            report.Append(snapshot.Recent.Count == 0
                ? "(none)"
                : string.Join(" | ", snapshot.Recent.Select(item => Shown(item.Title) + " — " + Shown(item.Subtitle))));
            report.AppendLine();
            report.Append(PhoneCache.Summary());
            return report.ToString();
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    public static PhoneSnapshot? TryRead(out string? problem)
    {
        problem = null;
        var path = CompanionPath();
        if (!File.Exists(path))
        {
            problem = "missing " + path;
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            return Parse(document.RootElement, out problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = "unreadable StartMenuCompanion.json (" + ex.GetType().Name + ")";
            return null;
        }
    }

    private static string CompanionPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Packages\Microsoft.YourPhone_8wekyb3d8bbwe\LocalState\StartMenu\StartMenuCompanion.json");
    }

    private static PhoneSnapshot? Parse(JsonElement root, out string? problem)
    {
        problem = null;
        if (!root.TryGetProperty("body", out var body))
        {
            problem = "StartMenuCompanion.json has no body";
            return null;
        }

        Image? wallpaper = null;
        Image? bezel = null;
        var status = new List<PhoneGlyph>();
        List<PhoneEntry>? features = null;
        List<PhoneEntry>? recent = null;
        string? name = null;
        string? recentHeading = null;
        string? statusLabel = null;
        string? deviceLaunch = null;
        var imageSetSeen = false;

        void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item);
                }

                return;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var type = Str(element, "type");
            if (type == "TextBlock")
            {
                var text = Str(element, "text")?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    var weight = Str(element, "weight");
                    var align = Str(element, "horizontalAlignment");
                    var style = Str(element, "style");
                    var size = Str(element, "size");
                    if (weight != null && weight.Equals("Bolder", StringComparison.OrdinalIgnoreCase) &&
                        align != null && align.Equals("Center", StringComparison.OrdinalIgnoreCase))
                    {
                        name ??= text;
                    }
                    else if (style != null && style.Equals("heading", StringComparison.OrdinalIgnoreCase))
                    {
                        recentHeading ??= text;
                    }
                    else if (statusLabel == null &&
                             size != null &&
                             size.Equals("small", StringComparison.OrdinalIgnoreCase) &&
                             text.Length <= 16)
                    {
                        statusLabel = text;
                    }
                }
            }
            else if (type == "ImageSet" && !imageSetSeen)
            {
                imageSetSeen = true;
                if (element.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var image in images.EnumerateArray())
                    {
                        var decoded = Decode(Str(image, "url"));
                        if (index == 0)
                        {
                            wallpaper = decoded;
                        }
                        else if (index == 1)
                        {
                            bezel = decoded;
                        }
                        else
                        {
                            decoded?.Dispose();
                        }

                        index++;
                    }
                }

                return;
            }
            else if (type == "Container")
            {
                var title = Str(element, "title");
                if (title == "Функции")
                {
                    features = ReadRows(element);
                    return;
                }

                if (title == "Недавние")
                {
                    recent = ReadRows(element);
                    return;
                }
            }
            else if (type == "Column")
            {
                var launch = LaunchUrl(element);
                if (launch.Length > 0)
                {
                    deviceLaunch ??= launch;
                }
            }
            else if (type == "Image")
            {
                var tip = Str(element, "tooltip")?.Trim();
                var size = Pixel(element, 16);
                if (!string.IsNullOrEmpty(tip) && size <= 24)
                {
                    var image = Decode(Str(element, "url"));
                    if (image != null)
                    {
                        status.Add(new PhoneGlyph(image, tip.Length <= 80 ? tip : "", size));
                    }
                }

                return;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("preview") || property.NameEquals("selectAction"))
                {
                    continue;
                }

                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    Walk(property.Value);
                }
            }
        }

        Walk(body);
        if (string.IsNullOrWhiteSpace(name) || features == null || recent == null || status.Count == 0)
        {
            wallpaper?.Dispose();
            bezel?.Dispose();
            foreach (var glyph in status)
            {
                glyph.Image.Dispose();
            }

            DisposeEntries(features);
            DisposeEntries(recent);
            problem = string.IsNullOrWhiteSpace(name)
                ? "StartMenuCompanion.json has no device name"
                : features == null
                    ? "StartMenuCompanion.json has no feature list"
                    : recent == null
                        ? "StartMenuCompanion.json has no recent list"
                        : "StartMenuCompanion.json has no connection icons";
            return null;
        }

        return new PhoneSnapshot
        {
            DeviceName = name,
            DeviceLaunch = deviceLaunch ?? "",
            StatusLabel = statusLabel ?? "",
            RecentHeading = string.IsNullOrWhiteSpace(recentHeading) ? "Недавние" : recentHeading,
            Wallpaper = wallpaper,
            Bezel = bezel,
            Status = status,
            Features = features,
            Recent = recent,
        };
    }

    private static List<PhoneEntry> ReadRows(JsonElement container)
    {
        var rows = new List<PhoneEntry>();
        if (!container.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!string.Equals(Str(item, "type"), "ColumnSet", StringComparison.Ordinal))
            {
                continue;
            }

            var texts = new List<string>();
            Image? icon = null;
            var person = false;
            Collect(item, texts, ref icon, ref person);
            if (texts.Count == 0)
            {
                icon?.Dispose();
                continue;
            }

            var subtitle = "";
            var count = 0;
            for (var i = 1; i < texts.Count; i++)
            {
                var extra = texts[i];
                if (count == 0 && extra.Length <= 3 && extra.All(char.IsDigit) && int.TryParse(extra, out var number))
                {
                    count = number;
                    continue;
                }

                if (subtitle.Length == 0)
                {
                    subtitle = extra;
                }
            }

            var tip = Str(item, "tooltip")?.Trim() ?? "";
            if (tip.Length > 120)
            {
                tip = "";
            }

            var entry = new PhoneEntry(texts[0], subtitle, icon, person, tip, LaunchUrl(item))
            {
                Count = count,
            };
            rows.Add(entry);
        }

        return rows;
    }

    private static void Collect(JsonElement element, List<string> texts, ref Image? icon, ref bool person)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Collect(item, texts, ref icon, ref person);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = Str(element, "type");
        if (type == "TextBlock")
        {
            var text = Str(element, "text")?.Trim();
            if (!string.IsNullOrEmpty(text) && text.Length <= 80)
            {
                texts.Add(text);
            }

            return;
        }

        if (type == "Image" && icon == null)
        {
            icon = Decode(Str(element, "url"));
            person = string.Equals(Str(element, "style"), "person", StringComparison.OrdinalIgnoreCase);
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("preview") || property.NameEquals("selectAction"))
            {
                continue;
            }

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                Collect(property.Value, texts, ref icon, ref person);
            }
        }
    }

    private static void DisposeEntries(List<PhoneEntry>? entries)
    {
        if (entries == null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            entry.Icon?.Dispose();
        }
    }

    private static Image? Decode(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var comma = url.IndexOf(',');
        if (comma < 0 || !url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(url[(comma + 1)..]);
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static int Pixel(JsonElement element, int fallback)
    {
        var text = Str(element, "width");
        if (text != null &&
            text.EndsWith("px", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text[..^2], out var value) &&
            value is > 0 and < 128)
        {
            return value;
        }

        return fallback;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static string LaunchUrl(JsonElement element)
    {
        if (!element.TryGetProperty("selectAction", out var action))
        {
            return "";
        }

        var url = Str(action, "url")?.Trim() ?? "";
        if (url.Length == 0 || url.Length > 512 || !url.StartsWith("ms-phone:", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return url;
    }

    private static string Shown(string value)
    {
        var text = value.Trim();
        return text.Length <= 80 ? text : "len=" + text.Length;
    }
}

internal sealed class PhoneSnapshot : IDisposable
{
    public string DeviceName { get; init; } = "";

    public string DeviceLaunch { get; init; } = "";

    public string StatusLabel { get; init; } = "";

    public string RecentHeading { get; init; } = "Недавние";

    public Image? Wallpaper { get; init; }

    public Image? Bezel { get; init; }

    public IReadOnlyList<PhoneGlyph> Status { get; init; } = Array.Empty<PhoneGlyph>();

    public IReadOnlyList<PhoneEntry> Features { get; init; } = Array.Empty<PhoneEntry>();

    public IReadOnlyList<PhoneEntry> Recent { get; init; } = Array.Empty<PhoneEntry>();

    public void Dispose()
    {
        Wallpaper?.Dispose();
        Bezel?.Dispose();
        foreach (var glyph in Status)
        {
            glyph.Image.Dispose();
        }

        foreach (var entry in Features)
        {
            entry.Icon?.Dispose();
        }

        foreach (var entry in Recent)
        {
            entry.Icon?.Dispose();
        }
    }
}

internal sealed class PhoneGlyph
{
    public PhoneGlyph(Image image, string tooltip, int size)
    {
        Image = image;
        Tooltip = tooltip;
        Size = size;
    }

    public Image Image { get; }

    public string Tooltip { get; }

    public int Size { get; }
}

internal sealed class PhoneEntry
{
    public PhoneEntry(string title, string subtitle, Image? icon, bool person, string tooltip, string launch)
    {
        Title = title;
        Subtitle = subtitle;
        Icon = icon;
        Person = person;
        Tooltip = tooltip;
        Launch = launch;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public Image? Icon { get; }

    public bool Person { get; }

    public string Tooltip { get; }

    public string Launch { get; }

    /// <summary>Unread count drawn as a badge. Zero means the card did not show one.</summary>
    public int Count { get; init; }
}
