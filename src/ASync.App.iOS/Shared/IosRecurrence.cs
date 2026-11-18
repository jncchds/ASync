using System.Globalization;
using System.Text;

namespace ASync.App.iOS;

/// <summary>EKRecurrenceDayOfWeek: <paramref name="Day"/> 1 = Sunday … 7 = Saturday (EKWeekday), <paramref name="Week"/> 0 = every, ±n = the nth (from the end).</summary>
public sealed record EKDayOfWeek(int Day, int Week = 0);

/// <summary>
/// An EKRecurrenceRule as plain data: <paramref name="Frequency"/> 0 daily, 1 weekly, 2 monthly, 3 yearly (EKRecurrenceFrequency);
/// <paramref name="FirstDayOfWeek"/> 0 = not set, else 1 = Sunday … 7; <paramref name="Until"/> the recurrence end's date (Unix ms) or
/// <paramref name="Count"/> its occurrence count.
/// </summary>
public sealed record EKRule(
    int Frequency, int Interval = 1, int FirstDayOfWeek = 0, IReadOnlyList<EKDayOfWeek>? DaysOfWeek = null, IReadOnlyList<int>? DaysOfMonth = null,
    IReadOnlyList<int>? MonthsOfYear = null, IReadOnlyList<int>? WeeksOfYear = null, IReadOnlyList<int>? DaysOfYear = null,
    IReadOnlyList<int>? SetPositions = null, int? Count = null, long? Until = null)
{
    public IReadOnlyList<EKDayOfWeek> Days => DaysOfWeek ?? [];
    public IReadOnlyList<int> MonthDays => DaysOfMonth ?? [];
    public IReadOnlyList<int> Months => MonthsOfYear ?? [];
    public IReadOnlyList<int> Weeks => WeeksOfYear ?? [];
    public IReadOnlyList<int> YearDays => DaysOfYear ?? [];
    public IReadOnlyList<int> Positions => SetPositions ?? [];
}

/// <summary>
/// EventKit's recurrence rules as RFC 5545 RRULE text and back, and their occurrences (to tell which occurrences of a series were
/// deleted: EventKit lists the occurrences that are there, never the excluded dates). Covers what EKRecurrenceRule can hold: FREQ
/// DAILY / WEEKLY / MONTHLY / YEARLY with INTERVAL, COUNT or UNTIL, BYDAY (with ordinals), BYMONTHDAY, BYMONTH, BYWEEKNO, BYYEARDAY,
/// BYSETPOS and WKST. Occurrences are computed for everything but BYWEEKNO and BYYEARDAY (rare; no deleted dates are inferred then).
/// </summary>
public static class IosRecurrence
{
    private static readonly string[] DayNames = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"]; // EKWeekday 1…7

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The rule as an RRULE value (<c>FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE</c>). UNTIL: a DATE in <paramref name="zone"/> for all-day
    /// series (the day the recurrence ends), else UTC.
    /// </summary>
    public static string ToRRule(EKRule r, bool allDay, TimeZoneInfo zone)
    {
        var sb = new StringBuilder("FREQ=").Append(r.Frequency switch { 0 => "DAILY", 1 => "WEEKLY", 2 => "MONTHLY", _ => "YEARLY" });
        if (r.Interval > 1) sb.Append(";INTERVAL=").Append(I(r.Interval));
        if (r.Count is { } count and > 0) sb.Append(";COUNT=").Append(I(count));
        else if (r.Until is { } until)
        {
            var utc = DateTimeOffset.FromUnixTimeMilliseconds(until).UtcDateTime;
            sb.Append(";UNTIL=").Append(allDay
                ? TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        }
        if (r.Months.Count > 0) sb.Append(";BYMONTH=").Append(string.Join(',', r.Months.Select(I)));
        if (r.Weeks.Count > 0) sb.Append(";BYWEEKNO=").Append(string.Join(',', r.Weeks.Select(I)));
        if (r.YearDays.Count > 0) sb.Append(";BYYEARDAY=").Append(string.Join(',', r.YearDays.Select(I)));
        if (r.MonthDays.Count > 0) sb.Append(";BYMONTHDAY=").Append(string.Join(',', r.MonthDays.Select(I)));
        if (r.Days.Count > 0) sb.Append(";BYDAY=").Append(string.Join(',', r.Days.Select(d => (d.Week != 0 ? I(d.Week) : "") + DayNames[Math.Clamp(d.Day, 1, 7) - 1])));
        if (r.Positions.Count > 0) sb.Append(";BYSETPOS=").Append(string.Join(',', r.Positions.Select(I)));
        if (r.FirstDayOfWeek is >= 1 and <= 7) sb.Append(";WKST=").Append(DayNames[r.FirstDayOfWeek - 1]);
        return sb.ToString();
    }

    /// <summary>
    /// An RRULE value as an EventKit rule. UNTIL as a DATE is the end of that day's occurrence: its local midnight in
    /// <paramref name="zone"/>; a floating date-time is local in <paramref name="zone"/>. Throws <see cref="NotSupportedException"/>
    /// for what EventKit cannot hold (several rules, hourly ones, BYHOUR…).
    /// </summary>
    public static EKRule Parse(string rrule, TimeZoneInfo zone)
    {
        var lines = rrule.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1) throw new NotSupportedException("it repeats by several rules, which iOS cannot store");
        var text = lines[0].StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? lines[0][6..] : lines[0];
        int? frequency = null, count = null;
        int interval = 1, wkst = 0;
        long? until = null;
        List<EKDayOfWeek> days = [];
        List<int> monthDays = [], months = [], weeks = [], yearDays = [], positions = [];
        static List<int> Ints(string v) => [.. v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : throw new NotSupportedException($"\"{x}\" in its rule"))];
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].ToUpperInvariant();
            var value = part[(eq + 1)..].Trim();
            switch (key)
            {
                case "FREQ":
                    frequency = value.ToUpperInvariant() switch
                    {
                        "DAILY" => 0, "WEEKLY" => 1, "MONTHLY" => 2, "YEARLY" => 3,
                        var f => throw new NotSupportedException($"it repeats {f.ToLowerInvariant()}, which iOS cannot store"),
                    };
                    break;
                case "INTERVAL": interval = Math.Max(1, Ints(value).FirstOrDefault(1)); break;
                case "COUNT": count = Ints(value).FirstOrDefault(); break;
                case "UNTIL": until = ParseUntil(value, zone); break;
                case "BYMONTH": months = Ints(value); break;
                case "BYWEEKNO": weeks = Ints(value); break;
                case "BYYEARDAY": yearDays = Ints(value); break;
                case "BYMONTHDAY": monthDays = Ints(value); break;
                case "BYSETPOS": positions = Ints(value); break;
                case "WKST": wkst = Day(value) ?? 0; break;
                case "BYDAY":
                    foreach (var d in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var name = d.Length >= 2 ? d[^2..] : d;
                        var week = d.Length > 2 && int.TryParse(d[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var w) ? w : 0;
                        days.Add(new EKDayOfWeek(Day(name) ?? throw new NotSupportedException($"\"{d}\" in its rule"), week));
                    }
                    break;
                default:
                    throw new NotSupportedException($"its rule has {key}, which iOS cannot store");
            }
        }
        if (frequency is null) throw new NotSupportedException("its rule has no FREQ");
        return new EKRule(frequency.Value, interval, wkst, days, monthDays, months, weeks, yearDays, positions, count is > 0 ? count : null, until);
    }

    private static int? Day(string name)
    {
        var i = Array.IndexOf(DayNames, name.Trim().ToUpperInvariant());
        return i < 0 ? null : i + 1;
    }

    private static long ParseUntil(string value, TimeZoneInfo zone)
    {
        var v = value.Trim();
        if (v.Length == 8 && DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return Utc(date, zone);
        if (DateTime.TryParseExact(v.TrimEnd('Z', 'z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return v.EndsWith('Z') || v.EndsWith('z')
                ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
                : Utc(local, zone);
        throw new NotSupportedException($"UNTIL={value} in its rule");
    }

    /// <summary>A wall time in a zone as Unix ms (a time in a daylight-saving gap moves forward an hour, as calendars do).</summary>
    public static long Utc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)).ToUnixTimeMilliseconds();
    }

    public static DateTime Local(long ms, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, zone);

    /// <summary>
    /// The starts (Unix ms) of a series' occurrences that begin in [<paramref name="from"/>, <paramref name="to"/>), counted from
    /// <paramref name="start"/> (a wall time in <paramref name="zone"/>; all-day series: midnight in UTC), with COUNT and UNTIL as
    /// EventKit applies them. Null when the rule has parts this does not compute (BYWEEKNO, BYYEARDAY).
    /// </summary>
    public static List<long>? Occurrences(EKRule r, DateTime start, TimeZoneInfo zone, long from, long to, int max = 20000)
    {
        if (r.Weeks.Count > 0 || r.YearDays.Count > 0) return null;
        var result = new List<long>();
        var interval = Math.Max(1, r.Interval);
        var time = start.TimeOfDay;
        var startDate = start.Date;
        var count = 0;
        var wkst = r.FirstDayOfWeek is >= 1 and <= 7 ? (DayOfWeek)(r.FirstDayOfWeek - 1) : DayOfWeek.Monday;
        for (var period = 0; period < 100000; period++)
        {
            List<DateTime> candidates;
            switch (r.Frequency)
            {
                case 0:
                    candidates = [startDate.AddDays((double)period * interval)];
                    break;
                case 1:
                {
                    var weekStart = startDate.AddDays(-(((int)startDate.DayOfWeek - (int)wkst + 7) % 7)).AddDays(7.0 * period * interval);
                    var wanted = r.Days.Count > 0 ? r.Days.Select(d => (DayOfWeek)(d.Day - 1)).ToHashSet() : [startDate.DayOfWeek];
                    candidates = [.. Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).Where(d => wanted.Contains(d.DayOfWeek))];
                    break;
                }
                case 2:
                {
                    var month = new DateTime(startDate.Year, startDate.Month, 1).AddMonths(period * interval);
                    if (month.Year > 9998) return result;
                    candidates = InMonth(r, month, startDate.Day);
                    break;
                }
                default:
                {
                    var year = startDate.Year + period * interval;
                    if (year > 9998) return result;
                    candidates = [];
                    var months = r.Months.Count > 0 ? r.Months.Where(m => m is >= 1 and <= 12).Order().ToList() : [startDate.Month];
                    if (r.Days.Count > 0 && r.Months.Count == 0 && r.MonthDays.Count == 0)
                    {
                        // BYDAY in a yearly rule without BYMONTH: the nth weekday of the year.
                        var all = Enumerable.Range(0, DateTime.IsLeapYear(year) ? 366 : 365).Select(i => new DateTime(year, 1, 1).AddDays(i)).ToList();
                        candidates = Pick(all, r.Days);
                    }
                    else
                        foreach (var m in months) candidates.AddRange(InMonth(r with { SetPositions = [] }, new DateTime(year, m, 1), startDate.Day));
                    candidates = Positions(candidates.Distinct().Order().ToList(), r.Positions);
                    break;
                }
            }
            if (r.Frequency is 0 or 1 && r.Months.Count > 0) candidates = [.. candidates.Where(d => r.Months.Contains(d.Month))];
            if (r.Frequency == 0 && r.Days.Count > 0) candidates = [.. candidates.Where(d => r.Days.Any(x => (DayOfWeek)(x.Day - 1) == d.DayOfWeek))];
            if (r.Frequency == 0 && r.MonthDays.Count > 0) candidates = [.. candidates.Where(d => r.MonthDays.Any(x => DayOf(d, x) == d.Day))];
            if (r.Frequency == 1) candidates = Positions(candidates, r.Positions);
            foreach (var date in candidates.Order())
            {
                if (date < startDate) continue;
                var ms = Utc(date + time, zone);
                if (r.Until is { } until && ms > until) return result;
                count++;
                if (r.Count is { } c && count > c) return result;
                if (ms >= to) return result;
                if (ms >= from) result.Add(ms);
                if (result.Count >= max) return result;
            }
        }
        return result;
    }

    private static int DayOf(DateTime d, int monthDay) => monthDay > 0 ? monthDay : DateTime.DaysInMonth(d.Year, d.Month) + monthDay + 1;

    private static List<DateTime> InMonth(EKRule r, DateTime month, int startDay)
    {
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        List<DateTime> list;
        if (r.MonthDays.Count > 0)
            list = [.. r.MonthDays.Select(d => d > 0 ? d : days + d + 1).Where(d => d >= 1 && d <= days).Distinct().Select(d => month.AddDays(d - 1))];
        else if (r.Days.Count > 0)
            list = Pick([.. Enumerable.Range(0, days).Select(i => month.AddDays(i))], r.Days);
        else
            list = startDay <= days ? [month.AddDays(startDay - 1)] : []; // a month without that day has no occurrence
        if (r.MonthDays.Count > 0 && r.Days.Count > 0) list = [.. list.Where(d => r.Days.Any(x => (DayOfWeek)(x.Day - 1) == d.DayOfWeek))];
        return Positions([.. list.Order()], r.Positions);
    }

    /// <summary>The days of <paramref name="span"/> (a month or a year) that are the given weekdays, the nth of them where a week number is given.</summary>
    private static List<DateTime> Pick(List<DateTime> span, IReadOnlyList<EKDayOfWeek> days)
    {
        var result = new List<DateTime>();
        foreach (var d in days)
        {
            var matching = span.Where(x => x.DayOfWeek == (DayOfWeek)(d.Day - 1)).ToList();
            if (d.Week == 0) result.AddRange(matching);
            else
            {
                var i = d.Week > 0 ? d.Week - 1 : matching.Count + d.Week;
                if (i >= 0 && i < matching.Count) result.Add(matching[i]);
            }
        }
        return [.. result.Distinct().Order()];
    }

    private static List<DateTime> Positions(List<DateTime> sorted, IReadOnlyList<int> positions)
    {
        if (positions.Count == 0) return sorted;
        var result = new List<DateTime>();
        foreach (var p in positions)
        {
            var i = p > 0 ? p - 1 : sorted.Count + p;
            if (i >= 0 && i < sorted.Count) result.Add(sorted[i]);
        }
        return [.. result.Distinct().Order()];
    }
}
