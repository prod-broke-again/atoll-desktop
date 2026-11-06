using LiveWallpaper.Core.Today;

namespace LiveWallpaper.Core.Tests;

public sealed class IcsCalendarParserTests
{
    [Fact]
    public void ParseTodaysEvents_FindsTimedAndAllDay()
    {
        var day = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Local);
        var ymd = day.ToString("yyyyMMdd");
        var ics = $"""
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:allday-1
            SUMMARY:Отпуск
            DTSTART;VALUE=DATE:{ymd}
            DTEND;VALUE=DATE:{day.AddDays(1):yyyyMMdd}
            END:VEVENT
            BEGIN:VEVENT
            UID:meet-1
            SUMMARY:Стендап
            DTSTART:{ymd}T100000
            DTEND:{ymd}T103000
            END:VEVENT
            BEGIN:VEVENT
            UID:old-1
            SUMMARY:Вчера
            DTSTART:{day.AddDays(-1):yyyyMMdd}T090000
            DTEND:{day.AddDays(-1):yyyyMMdd}T100000
            END:VEVENT
            END:VCALENDAR
            """;

        var events = IcsCalendarParser.ParseTodaysEvents(ics, day, maxEvents: 10);

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Title == "Отпуск" && e.AllDay);
        Assert.Contains(events, e => e.Title == "Стендап" && e.TimeLabel.Contains("10:00", StringComparison.Ordinal));
    }

    private static string Cal(params string[] events) =>
        "BEGIN:VCALENDAR\nVERSION:2.0\n" + string.Join("\n", events) + "\nEND:VCALENDAR\n";

    [Fact]
    public void WeeklyRecurringEvent_ShowsUpToday()
    {
        var today = DateTime.Today.AddHours(8);
        var first = today.Date.AddDays(-21); // same weekday, three weeks ago
        var ics = Cal($"BEGIN:VEVENT\nUID:weekly-1\nSUMMARY:Созвон\nDTSTART;TZID=Europe/Moscow:{first:yyyyMMdd}T150000\nDTEND;TZID=Europe/Moscow:{first:yyyyMMdd}T153000\nRRULE:FREQ=WEEKLY;BYDAY={Code(first.DayOfWeek)}\nEND:VEVENT");

        var events = IcsCalendarParser.ParseTodaysEvents(ics, today, 10);

        var ev = Assert.Single(events);
        Assert.Equal("Созвон", ev.Title);
        var expectedLocal = TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(today.Date.AddHours(15), DateTimeKind.Unspecified), TimeSpan.FromHours(3)), TimeZoneInfo.Local).DateTime;
        Assert.Equal(expectedLocal, ev.StartLocal);
    }

    [Fact]
    public void RecurringEvent_RespectsExdateUntilAndOverride()
    {
        var today = DateTime.Today.AddHours(8);
        var first = today.Date.AddDays(-14);
        var day = Code(first.DayOfWeek);
        var ics = Cal(
            // excluded today via EXDATE
            $"BEGIN:VEVENT\nUID:ex\nSUMMARY:Исключён\nDTSTART:{first:yyyyMMdd}T090000Z\nDTEND:{first:yyyyMMdd}T100000Z\nRRULE:FREQ=WEEKLY;BYDAY={day}\nEXDATE:{today:yyyyMMdd}T090000Z\nEND:VEVENT",
            // series ended last week
            $"BEGIN:VEVENT\nUID:until\nSUMMARY:Закончился\nDTSTART:{first:yyyyMMdd}T110000Z\nRRULE:FREQ=WEEKLY;UNTIL={today.AddDays(-3):yyyyMMdd}T000000Z\nEND:VEVENT",
            // today's occurrence moved to 18:00 UTC
            $"BEGIN:VEVENT\nUID:mv\nSUMMARY:Перенесён\nDTSTART:{first:yyyyMMdd}T120000Z\nDTEND:{first:yyyyMMdd}T130000Z\nRRULE:FREQ=WEEKLY\nEND:VEVENT",
            $"BEGIN:VEVENT\nUID:mv\nRECURRENCE-ID:{today:yyyyMMdd}T120000Z\nSUMMARY:Перенесён\nDTSTART:{today:yyyyMMdd}T180000Z\nDTEND:{today:yyyyMMdd}T190000Z\nEND:VEVENT");

        var events = IcsCalendarParser.ParseEvents(ics, today.Date.AddDays(-1), today.Date.AddDays(2), today)
            .Where(e => e.StartLocal.ToUniversalTime().Date == today.Date)
            .ToList();

        Assert.DoesNotContain(events, e => e.Title == "Исключён");
        Assert.DoesNotContain(events, e => e.Title == "Закончился");
        var moved = Assert.Single(events, e => e.Title == "Перенесён");
        Assert.Equal(18, moved.StartLocal.ToUniversalTime().Hour);
    }

    [Fact]
    public void Upcoming_IncludesNextDaysWhenTodayIsEmpty()
    {
        var now = DateTime.Today.AddHours(12);
        var tomorrow = now.Date.AddDays(1);
        var ics = Cal($"BEGIN:VEVENT\nUID:t\nSUMMARY:Завтра\nDTSTART:{tomorrow:yyyyMMdd}T100000\nDTEND:{tomorrow:yyyyMMdd}T110000\nEND:VEVENT");

        var events = IcsCalendarParser.ParseTodayAndUpcoming(ics, now, 5);

        var ev = Assert.Single(events);
        Assert.False(ev.IsToday);
        Assert.StartsWith("завтра", ev.TimeLabel, StringComparison.Ordinal);
    }

    private static string Code(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "MO", DayOfWeek.Tuesday => "TU", DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH", DayOfWeek.Friday => "FR", DayOfWeek.Saturday => "SA", _ => "SU",
    };
}
