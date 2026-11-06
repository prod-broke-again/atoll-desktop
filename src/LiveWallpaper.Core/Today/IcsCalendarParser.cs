using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LiveWallpaper.Core.Today;

/// <summary>
/// VEVENT parser for Google Calendar (and similar) ICS feeds.
/// Supports: TZID / UTC / floating / all-day times, DTEND or DURATION, RRULE expansion
/// (DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, COUNT, UNTIL, BYDAY, BYMONTHDAY), EXDATE,
/// RECURRENCE-ID overrides and STATUS:CANCELLED.
/// </summary>
public static class IcsCalendarParser
{
    private static readonly CultureInfo Ru = new("ru-RU");
    private static readonly string[] DateTimeFormats = { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" };

    /// <summary>Events overlapping today (local), sorted: all-day first, then by start.</summary>
    public static IReadOnlyList<TodayEvent> ParseTodaysEvents(string ics, DateTime localNow, int maxEvents)
    {
        var day = localNow.Date;
        return ParseEvents(ics, day, day.AddDays(1), localNow)
            .OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.StartLocal)
            .Take(Math.Clamp(maxEvents, 1, 50))
            .ToList();
    }

    /// <summary>
    /// Today's events plus, if there is room, the next ones within <paramref name="aheadDays"/>.
    /// Finished timed events of today are dropped so the list stays useful.
    /// </summary>
    public static IReadOnlyList<TodayEvent> ParseTodayAndUpcoming(string ics, DateTime localNow, int maxEvents, int aheadDays = 7)
    {
        maxEvents = Math.Clamp(maxEvents, 1, 50);
        var all = ParseEvents(ics, localNow.Date, localNow.Date.AddDays(1 + aheadDays), localNow);
        var today = all.Where(e => e.IsToday && (e.AllDay || (e.EndLocal ?? e.StartLocal) > localNow))
            .OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.StartLocal);
        var later = all.Where(e => !e.IsToday).OrderBy(e => e.StartLocal);
        return today.Concat(later).Take(maxEvents).ToList();
    }

    /// <summary>All occurrences overlapping [fromLocal, toLocal).</summary>
    public static List<TodayEvent> ParseEvents(string ics, DateTime fromLocal, DateTime toLocal, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(ics);
        var raw = SplitEvents(Unfold(ics)).Select(ParseVEvent).Where(e => e != null).Cast<VEvent>().ToList();

        // RECURRENCE-ID overrides: uid -> set of original starts (UTC) that were replaced
        var overridden = raw.Where(e => e.RecurrenceIdUtc.HasValue)
            .GroupBy(e => e.Uid)
            .ToDictionary(g => g.Key, g => g.Select(e => e.RecurrenceIdUtc!.Value).ToHashSet());

        var result = new List<TodayEvent>();
        var todayStart = localNow.Date;
        var todayEnd = todayStart.AddDays(1);

        foreach (var ev in raw)
        {
            if (ev.Cancelled) continue;
            var duration = ev.Duration;
            IEnumerable<DateTime> startsUtc;

            if (ev.Rule != null && !ev.RecurrenceIdUtc.HasValue)
            {
                overridden.TryGetValue(ev.Uid, out var skip);
                startsUtc = Expand(ev, fromLocal.ToUniversalTime().AddDays(-1) - duration, toLocal.ToUniversalTime().AddDays(1))
                    .Where(s => !ev.ExDatesUtc.Contains(s) && (skip == null || !skip.Contains(s)));
            }
            else
            {
                startsUtc = new[] { ev.StartUtc };
            }

            foreach (var sUtc in startsUtc)
            {
                DateTime startLocal, endLocal;
                if (ev.AllDay)
                {
                    // All-day dates are calendar dates, not instants.
                    var date = sUtc.Date; // all-day keys are calendar dates
                    startLocal = DateTime.SpecifyKind(date, DateTimeKind.Local);
                    endLocal = startLocal + (duration <= TimeSpan.Zero ? TimeSpan.FromDays(1) : duration);
                }
                else
                {
                    startLocal = sUtc.ToLocalTime();
                    endLocal = (sUtc + duration).ToLocalTime();
                }

                var overlaps = ev.AllDay || duration > TimeSpan.Zero
                    ? startLocal < toLocal && endLocal > fromLocal
                    : startLocal >= fromLocal && startLocal < toLocal;
                if (!overlaps) continue;

                var isToday = startLocal < todayEnd && (ev.AllDay ? endLocal > todayStart : endLocal >= todayStart);
                result.Add(new TodayEvent
                {
                    Id = $"{(string.IsNullOrEmpty(ev.Uid) ? Guid.NewGuid().ToString("N") : ev.Uid)}@{sUtc:yyyyMMddTHHmm}",
                    Title = string.IsNullOrWhiteSpace(ev.Summary) ? "Без названия" : ev.Summary.Trim(),
                    StartLocal = startLocal,
                    EndLocal = endLocal,
                    AllDay = ev.AllDay,
                    IsToday = isToday,
                    TimeLabel = FormatTimeLabel(startLocal, endLocal, ev.AllDay, localNow, isToday),
                });
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ model

    private sealed class Rule
    {
        public string Freq = "WEEKLY";
        public int Interval = 1;
        public int? Count;
        public DateTime? UntilUtc;
        public List<(int Nth, DayOfWeek Day)> ByDay = new();
        public List<int> ByMonthDay = new();
    }

    private sealed class VEvent
    {
        public string Uid = string.Empty;
        public string Summary = string.Empty;
        public DateTime StartUtc;
        public TimeSpan Duration;
        public bool AllDay;
        public TimeZoneInfo Zone = TimeZoneInfo.Local; // zone of the original wall clock
        public Rule? Rule;
        public HashSet<DateTime> ExDatesUtc = new();
        public DateTime? RecurrenceIdUtc;
        public bool Cancelled;

        /// <summary>Wall-clock start in the event's own zone (all-day: the date itself).</summary>
        public DateTime WallStart => AllDay ? StartUtc : TimeZoneInfo.ConvertTimeFromUtc(StartUtc, Zone);
    }

    // ------------------------------------------------------------------ expansion

    private static IEnumerable<DateTime> Expand(VEvent ev, DateTime windowFromUtc, DateTime windowToUtc)
    {
        var r = ev.Rule!;
        var wall = ev.WallStart;
        var emitted = 0;
        var guard = 0;

        DateTime ToUtc(DateTime w) => ev.AllDay
            ? DateTime.SpecifyKind(w, DateTimeKind.Utc)
            : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(w, DateTimeKind.Unspecified), ev.Zone);

        // Iterate period by period; generate candidates inside each period.
        for (var period = 0; guard++ < 5000; period++)
        {
            var candidates = new List<DateTime>();
            switch (r.Freq)
            {
                case "DAILY":
                    candidates.Add(wall.AddDays(period * r.Interval));
                    break;
                case "WEEKLY":
                {
                    var weekStart = wall.Date.AddDays(-(((int)wall.DayOfWeek + 6) % 7)).AddDays(7 * period * r.Interval);
                    var days = r.ByDay.Count > 0 ? r.ByDay.Select(b => b.Day).Distinct() : new[] { wall.DayOfWeek };
                    foreach (var d in days.OrderBy(d => ((int)d + 6) % 7))
                        candidates.Add(weekStart.AddDays(((int)d + 6) % 7) + wall.TimeOfDay);
                    break;
                }
                case "MONTHLY":
                {
                    var m = new DateTime(wall.Year, wall.Month, 1).AddMonths(period * r.Interval);
                    if (r.ByDay.Count > 0)
                    {
                        foreach (var (nth, day) in r.ByDay)
                        {
                            var dt = NthWeekday(m.Year, m.Month, day, nth == 0 ? 1 : nth);
                            if (dt.HasValue) candidates.Add(dt.Value + wall.TimeOfDay);
                        }
                    }
                    else
                    {
                        var mdays = r.ByMonthDay.Count > 0 ? r.ByMonthDay : new List<int> { wall.Day };
                        foreach (var md in mdays)
                        {
                            var dim = DateTime.DaysInMonth(m.Year, m.Month);
                            var dayNum = md < 0 ? dim + md + 1 : md;
                            if (dayNum >= 1 && dayNum <= dim) candidates.Add(new DateTime(m.Year, m.Month, dayNum) + wall.TimeOfDay);
                        }
                    }
                    break;
                }
                case "YEARLY":
                {
                    var y = wall.Year + period * r.Interval;
                    if (wall.Month == 2 && wall.Day == 29 && !DateTime.IsLeapYear(y)) break;
                    candidates.Add(new DateTime(y, wall.Month, wall.Day) + wall.TimeOfDay);
                    break;
                }
                default:
                    yield break;
            }

            foreach (var c in candidates.OrderBy(c => c))
            {
                if (c < wall) continue;
                var utc = ToUtc(c);
                if (r.UntilUtc.HasValue && utc > r.UntilUtc.Value) yield break;
                if (r.Count.HasValue && emitted >= r.Count.Value) yield break;
                emitted++;
                if (utc >= windowToUtc) yield break;
                if (utc >= windowFromUtc) yield return utc;
            }
        }
    }

    private static DateTime? NthWeekday(int year, int month, DayOfWeek day, int nth)
    {
        var dim = DateTime.DaysInMonth(year, month);
        if (nth > 0)
        {
            var first = new DateTime(year, month, 1);
            var offset = ((int)day - (int)first.DayOfWeek + 7) % 7;
            var d = 1 + offset + (nth - 1) * 7;
            return d <= dim ? new DateTime(year, month, d) : null;
        }
        var last = new DateTime(year, month, dim);
        var back = ((int)last.DayOfWeek - (int)day + 7) % 7;
        var dd = dim - back + (nth + 1) * 7;
        return dd >= 1 ? new DateTime(year, month, dd) : null;
    }

    // ------------------------------------------------------------------ parsing

    private static VEvent? ParseVEvent(List<(string Name, string Params, string Value)> props)
    {
        var ev = new VEvent();
        DateTime? endUtc = null;
        TimeSpan? duration = null;
        var hasStart = false;

        foreach (var (name, parms, value) in props)
        {
            switch (name)
            {
                case "UID": ev.Uid = value.Trim(); break;
                case "SUMMARY": ev.Summary = UnescapeText(value); break;
                case "STATUS": ev.Cancelled = value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "DTSTART":
                    if (TryParseDate(value, parms, out var s, out var allDay, out var zone))
                    {
                        ev.StartUtc = s;
                        ev.AllDay = allDay;
                        ev.Zone = zone;
                        hasStart = true;
                    }
                    break;
                case "DTEND":
                    if (TryParseDate(value, parms, out var e, out _, out _)) endUtc = e;
                    break;
                case "DURATION":
                    duration = ParseDuration(value);
                    break;
                case "RRULE":
                    ev.Rule = ParseRule(value);
                    break;
                case "EXDATE":
                    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (TryParseDate(part, parms, out var x, out _, out _)) ev.ExDatesUtc.Add(x);
                    break;
                case "RECURRENCE-ID":
                    if (TryParseDate(value, parms, out var rid, out _, out _)) ev.RecurrenceIdUtc = rid;
                    break;
            }
        }

        if (!hasStart) return null;
        ev.Duration = endUtc.HasValue ? endUtc.Value - ev.StartUtc : duration ?? (ev.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero);
        if (ev.Duration < TimeSpan.Zero) ev.Duration = TimeSpan.Zero;
        return ev;
    }

    private static Rule ParseRule(string value)
    {
        var r = new Rule();
        foreach (var kv in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            if (i <= 0) continue;
            var k = kv[..i].Trim().ToUpperInvariant();
            var v = kv[(i + 1)..].Trim();
            switch (k)
            {
                case "FREQ": r.Freq = v.ToUpperInvariant(); break;
                case "INTERVAL": if (int.TryParse(v, out var iv) && iv > 0) r.Interval = iv; break;
                case "COUNT": if (int.TryParse(v, out var c) && c > 0) r.Count = c; break;
                case "UNTIL":
                    if (TryParseDate(v, string.Empty, out var u, out var ad, out _))
                        r.UntilUtc = ad ? u.AddDays(1).AddTicks(-1) : u;
                    break;
                case "BYDAY":
                    foreach (var d in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var code = d.Length >= 2 ? d[^2..].ToUpperInvariant() : d;
                        var nthStr = d.Length > 2 ? d[..^2] : "0";
                        int.TryParse(nthStr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var nth);
                        var day = code switch
                        {
                            "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
                            "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday,
                            _ => DayOfWeek.Sunday,
                        };
                        r.ByDay.Add((nth, day));
                    }
                    break;
                case "BYMONTHDAY":
                    foreach (var d in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(d, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var md)) r.ByMonthDay.Add(md);
                    break;
            }
        }
        return r;
    }

    private static bool TryParseDate(string value, string parms, out DateTime utc, out bool allDay, out TimeZoneInfo zone)
    {
        value = value.Trim();
        zone = TimeZoneInfo.Local;
        allDay = parms.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && !parms.Contains("VALUE=DATE-TIME", StringComparison.OrdinalIgnoreCase)
                 || (value.Length == 8 && !value.Contains('T'));
        utc = default;

        if (allDay)
        {
            if (!DateTime.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return false;
            utc = DateTime.SpecifyKind(d, DateTimeKind.Utc); // date key, not an instant
            return true;
        }

        var isUtc = value.EndsWith('Z');
        var core = isUtc ? value[..^1] : value;
        if (!DateTime.TryParseExact(core, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            return false;

        if (isUtc)
        {
            utc = DateTime.SpecifyKind(wall, DateTimeKind.Utc);
            zone = TimeZoneInfo.Utc;
            return true;
        }

        var tzid = GetParam(parms, "TZID");
        if (!string.IsNullOrEmpty(tzid) && TryFindZone(tzid, out var tz)) zone = tz;
        utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone);
        return true;
    }

    private static bool TryFindZone(string tzid, out TimeZoneInfo zone)
    {
        tzid = tzid.Trim().Trim('"');
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(tzid); return true; }
        catch
        {
            // Windows without ICU: map IANA → Windows id
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(tzid, out var winId))
            {
                try { zone = TimeZoneInfo.FindSystemTimeZoneById(winId); return true; } catch { }
            }
        }
        zone = TimeZoneInfo.Local;
        return false;
    }

    private static string? GetParam(string parms, string key)
    {
        foreach (var p in parms.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = p.IndexOf('=');
            if (i > 0 && p[..i].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return p[(i + 1)..];
        }
        return null;
    }

    private static TimeSpan? ParseDuration(string v)
    {
        // P1D, PT1H30M, -PT15M, P1W
        var m = System.Text.RegularExpressions.Regex.Match(v.Trim(), @"^(-)?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return null;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        var ts = new TimeSpan(G(2) * 7 + G(3), G(4), G(5), G(6));
        return m.Groups[1].Success ? -ts : ts;
    }

    private static string Unfold(string ics)
    {
        var sb = new StringBuilder(ics.Length);
        var lines = ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && sb.Length > 0)
                sb.Append(line, 1, line.Length - 1);
            else
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<List<(string Name, string Params, string Value)>> SplitEvents(string unfolded)
    {
        List<(string, string, string)>? cur = null;
        foreach (var line in unfolded.Split('\n'))
        {
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new(); continue; }
            if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (cur != null) yield return cur;
                cur = null;
                continue;
            }
            if (cur == null) continue;
            var colon = IndexOfUnquotedColon(line);
            if (colon <= 0) continue;
            var head = line[..colon];
            var semi = head.IndexOf(';');
            var name = (semi >= 0 ? head[..semi] : head).Trim().ToUpperInvariant();
            var parms = semi >= 0 ? head[(semi + 1)..] : string.Empty;
            cur.Add((name, parms, line[(colon + 1)..]));
        }
    }

    private static int IndexOfUnquotedColon(string line)
    {
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') q = !q;
            else if (line[i] == ':' && !q) return i;
        }
        return -1;
    }

    private static string UnescapeText(string v) =>
        v.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static string FormatTimeLabel(DateTime start, DateTime? end, bool allDay, DateTime now, bool isToday)
    {
        var dayPrefix = isToday ? string.Empty
            : start.Date == now.Date.AddDays(1) ? "завтра · "
            : start.ToString("ddd d MMM", Ru).Replace(".", string.Empty) + " · ";
        if (allDay) return dayPrefix + "весь день";
        var t = start.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (end.HasValue && end.Value > start && end.Value.Date == start.Date)
            t += "–" + end.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        return dayPrefix + t;
    }
}
