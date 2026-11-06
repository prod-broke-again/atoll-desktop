using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace TaskbarSpike;

internal static class NotificationCounts
{
    private static readonly Regex Unread = new(
        @"^(\d+)\s+Unread\s+Notifications?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static AutomationElement[] _buttons = Array.Empty<AutomationElement>();
    private static Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private static long _readAt;

    public static void Warm()
    {
        try
        {
            var found = AutomationElement.RootElement.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer"));
            var buttons = new AutomationElement[found.Count];
            for (var i = 0; i < found.Count; i++)
            {
                buttons[i] = found[i];
            }

            _buttons = buttons;
            _readAt = Environment.TickCount64;
            var counts = Parse(buttons);
            if (counts != null)
            {
                _counts = counts;
            }

            Console.WriteLine("[spike] taskbar badges: " + Format(_counts));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] notification counts: {ex.Message}");
        }
    }

    public static int For(IEnumerable<string?> aumids, IEnumerable<string?> processNames)
    {
        Refresh();
        var counts = _counts;
        var best = 0;
        foreach (var key in aumids.Concat(processNames))
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (counts.TryGetValue(key, out var count))
            {
                best = Math.Max(best, count);
            }
        }

        return best;
    }

    private static void Refresh()
    {
        if (_buttons.Length == 0 || Environment.TickCount64 - _readAt < 1000)
        {
            return;
        }

        _readAt = Environment.TickCount64;
        Dictionary<string, int>? counts;
        try
        {
            counts = Parse(_buttons);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] notification counts: {ex.Message}");
            return;
        }

        if (counts == null || Same(counts, _counts))
        {
            return;
        }

        _counts = counts;
        Console.WriteLine("[spike] taskbar badges: " + Format(counts));
    }

    private static Dictionary<string, int>? Parse(IReadOnlyList<AutomationElement> buttons)
    {
        var next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var readable = 0;
        foreach (var button in buttons)
        {
            string help;
            string automationId;
            string name;
            try
            {
                help = button.Current.HelpText ?? string.Empty;
                automationId = button.Current.AutomationId ?? string.Empty;
                name = button.Current.Name ?? string.Empty;
                readable++;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            var match = Unread.Match(help.Trim());
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var count) || count <= 0)
            {
                continue;
            }

            const string prefix = "Appid: ";
            if (automationId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                next[automationId[prefix.Length..]] = count;
            }

            var appName = AppName(name);
            if (appName.Length > 0)
            {
                next[appName] = count;
            }
        }

        return readable == 0 ? null : next;
    }

    private static bool Same(Dictionary<string, int> next, Dictionary<string, int> previous)
    {
        if (next.Count != previous.Count)
        {
            return false;
        }

        foreach (var pair in next)
        {
            if (!previous.TryGetValue(pair.Key, out var count) || count != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static string Format(Dictionary<string, int> counts)
    {
        return counts.Count == 0
            ? "none"
            : string.Join(", ", counts.Select(pair => pair.Key + "=" + pair.Value));
    }

    private static string AppName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var dash = name.IndexOf('—');
        if (dash < 0)
        {
            dash = name.IndexOf(" - ", StringComparison.Ordinal);
        }

        var head = dash > 0 ? name[..dash] : name;
        return head.Trim();
    }
}
