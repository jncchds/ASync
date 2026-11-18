using System.Globalization;
using System.Text;

namespace ASync.Client.Core.Collections;

/// <summary>
/// Events as iCalendar (RFC 5545), one VCALENDAR per event: the event, then its exceptions (same UID, each with RECURRENCE-ID) in the
/// order of the occurrences they replace, with a VTIMEZONE for every zone they use. <b>Part of the permanent format</b> (AGENTS.md
/// "Extra data sources"). Deterministic: fixed property order, no "generated at" stamp. DTSTAMP, which RFC 5545 requires, is the
/// event's last modification where the provider knows it, else its creation, else its start (Android keeps neither), so an unchanged
/// event always gives the same bytes. Timed events in a zone are written in local time with TZID (a weekly 10:00 stays 10:00 across
/// daylight saving); all-day events as DATE values; times without a usable zone in UTC.
/// </summary>
public static class ICalendar
{
    public const string ProdId = "-//ASync//Calendars 1//EN";

    // ── Writing ──────────────────────────────────────────────────────────────

    public static byte[] Write(CalendarEvent master)
    {
        var w = new ContentWriter();
        w.Line("BEGIN", "VCALENDAR");
        w.Line("VERSION", "2.0");
        w.Line("PRODID", ProdId);
        w.Line("CALSCALE", "GREGORIAN");
        var all = new[] { master }.Concat(master.Exceptions).ToList();
        var zones = new SortedDictionary<string, long>(StringComparer.Ordinal); // zone → earliest start using it
        foreach (var e in all.Where(e => !e.AllDay))
        {
            foreach (var tz in new[] { e.TimeZone, e.EndTimeZone ?? e.TimeZone })
                if (Zone(tz) is { } zone) zones[zone.Id] = Math.Min(zones.GetValueOrDefault(zone.Id, long.MaxValue), e.Start);
        }
        if (master.ExDate is { } exd && TzPrefix(exd).Tz is { } exTz && Zone(exTz) is { } exZone) zones.TryAdd(exZone.Id, master.Start);
        if (master.RDate is { } rd && TzPrefix(rd).Tz is { } rTz && Zone(rTz) is { } rZone) zones.TryAdd(rZone.Id, master.Start);
        foreach (var (id, start) in zones) WriteTimeZone(w, Zone(id)!, start);

        var uid = master.Uid is { Length: > 0 } u ? u : master.Id;
        WriteEvent(w, master, uid, null);
        foreach (var ex in master.Exceptions.OrderBy(e => e.OriginalStart ?? e.Start).ThenBy(e => e.Id, StringComparer.Ordinal))
            WriteEvent(w, ex, uid, master);
        w.Line("END", "VCALENDAR");
        return w.ToArray();
    }

    private static void WriteEvent(ContentWriter w, CalendarEvent e, string uid, CalendarEvent? master)
    {
        w.Line("BEGIN", "VEVENT");
        w.Line("UID", VCard.Escape(uid));
        w.Line("DTSTAMP", Utc(e.LastModified ?? e.Created ?? e.Start));
        if (e.Created is { } created) w.Line("CREATED", Utc(created));
        if (e.LastModified is { } modified) w.Line("LAST-MODIFIED", Utc(modified));
        if (master is not null)
        {
            var original = e.OriginalStart ?? e.Start;
            w.Line(master.AllDay ? "RECURRENCE-ID;VALUE=DATE" : TimeName("RECURRENCE-ID", master.TimeZone), master.AllDay ? Date(original) : Time(original, master.TimeZone));
        }
        w.Line(e.AllDay ? "DTSTART;VALUE=DATE" : TimeName("DTSTART", e.TimeZone), e.AllDay ? Date(e.Start) : Time(e.Start, e.TimeZone));
        if (e.End is { } end && !(e.Recurring && e.Duration is { Length: > 0 }))
        {
            var endTz = e.EndTimeZone ?? e.TimeZone;
            w.Line(e.AllDay ? "DTEND;VALUE=DATE" : TimeName("DTEND", endTz), e.AllDay ? Date(end) : Time(end, endTz));
        }
        else if (Duration(e.Duration) is { } duration) w.Line("DURATION", duration);
        if (e.Title is { Length: > 0 } title) w.Line("SUMMARY", VCard.Escape(title));
        if (e.Location is { Length: > 0 } location) w.Line("LOCATION", VCard.Escape(location));
        if (e.Description is { Length: > 0 } description) w.Line("DESCRIPTION", VCard.Escape(description));
        if (master is null)
        {
            foreach (var rule in Lines(e.RRule)) w.Line("RRULE", rule);
            foreach (var rule in Lines(e.ExRule)) w.Line("EXRULE", rule);
            WriteDates(w, "RDATE", e.RDate);
            WriteDates(w, "EXDATE", e.ExDate);
        }
        if (e.Status is { Length: > 0 } status) w.Line("STATUS", status.ToUpperInvariant());
        if (e.Transparency is { Length: > 0 } transp) w.Line("TRANSP", transp.ToUpperInvariant());
        if (e.Class is { Length: > 0 } cls) w.Line("CLASS", cls.ToUpperInvariant());
        if (e.Organizer is { Length: > 0 } organizer) w.Line("ORGANIZER", Mailto(organizer));
        foreach (var a in e.Attendees)
        {
            var head = new StringBuilder("ATTENDEE");
            if (a.Name is { Length: > 0 } cn) head.Append(";CN=").Append(VCard.Param(cn));
            head.Append(";CUTYPE=").Append(Token(a.Type, "INDIVIDUAL"));
            head.Append(";ROLE=").Append(Token(a.Role, "REQ-PARTICIPANT"));
            head.Append(";PARTSTAT=").Append(Token(a.Status, "NEEDS-ACTION"));
            w.Line(head.ToString(), a.Email is { Length: > 0 } email ? Mailto(email) : "mailto:");
        }
        if (e.Color is { Length: > 0 } color) w.Line("X-ASYNC-COLOR", color.ToLowerInvariant());
        foreach (var alarm in e.Alarms)
        {
            w.Line("BEGIN", "VALARM");
            w.Line("ACTION", "DISPLAY");
            w.Line("DESCRIPTION", VCard.Escape(e.Title is { Length: > 0 } t ? t : "Reminder"));
            w.Line("TRIGGER", Trigger(alarm.MinutesBefore));
            if (alarm.Method is { Length: > 0 } m && m != "display") w.Line("X-ASYNC-METHOD", m.ToLowerInvariant());
            w.Line("END", "VALARM");
        }
        w.Line("END", "VEVENT");
    }

    private static string Token(string? value, string fallback)
    {
        var t = new string((value ?? "").ToUpperInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').ToArray());
        return t.Length == 0 ? fallback : t;
    }

    private static string Mailto(string address) =>
        address.Contains(':') ? address : "mailto:" + address;

    private static IEnumerable<string> Lines(string? value) =>
        (value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("EXRULE:", StringComparison.OrdinalIgnoreCase) ? v[(v.IndexOf(':') + 1)..] : v);

    /// <summary>Android's RDATE/EXDATE text (<c>[tzid;]v1,v2</c>, maybe several lines) as properties.</summary>
    private static void WriteDates(ContentWriter w, string name, string? value)
    {
        foreach (var line in Lines(value))
        {
            var (tz, list) = TzPrefix(line);
            var values = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 0) continue;
            var isDate = values.All(v => v.Length == 8 && v.All(char.IsAsciiDigit));
            var head = isDate ? $"{name};VALUE=DATE" : tz is { } z && Zone(z) is { } zone ? $"{name};TZID={zone.Id}" : name;
            w.Line(head, string.Join(',', values));
        }
    }

    internal static (string? Tz, string List) TzPrefix(string value)
    {
        var semi = value.IndexOf(';');
        if (semi <= 0) return (null, value);
        var tz = value[..semi];
        return tz.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase) ? (tz[5..], value[(semi + 1)..].TrimStart(':')) : (tz, value[(semi + 1)..]);
    }

    private static string Trigger(int minutesBefore)
    {
        if (minutesBefore == 0) return "PT0S";
        var sign = minutesBefore > 0 ? "-" : "";
        var m = Math.Abs(minutesBefore);
        if (m % 10080 == 0) return $"{sign}P{m / 10080}W";
        if (m % 1440 == 0) return $"{sign}P{m / 1440}D";
        if (m % 60 == 0) return $"{sign}PT{m / 60}H";
        return $"{sign}PT{m}M";
    }

    /// <summary>A provider's duration (Android writes <c>P3600S</c>, <c>P1D</c>) as RFC 5545 (<c>PT1H</c>, <c>P1D</c>); null when it is none.</summary>
    internal static string? Duration(string? value) => ParseDuration(value) is { } seconds ? FormatDuration(seconds) : null;

    public static string FormatDuration(long seconds)
    {
        var sign = seconds < 0 ? "-" : "";
        var s = Math.Abs(seconds);
        if (s == 0) return "PT0S";
        if (s % 604800 == 0) return $"{sign}P{s / 604800}W";
        var sb = new StringBuilder(sign).Append('P');
        if (s >= 86400) { sb.Append(s / 86400).Append('D'); s %= 86400; }
        if (s > 0)
        {
            sb.Append('T');
            if (s >= 3600) { sb.Append(s / 3600).Append('H'); s %= 3600; }
            if (s >= 60) { sb.Append(s / 60).Append('M'); s %= 60; }
            if (s > 0) sb.Append(s).Append('S');
        }
        return sb.ToString();
    }

    /// <summary>Seconds of an RFC 5545 duration (also Android's lax <c>P3600S</c>); null when it is not one.</summary>
    public static long? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToUpperInvariant();
        var sign = 1;
        if (v[0] is '+' or '-') { if (v[0] == '-') sign = -1; v = v[1..]; }
        if (v.Length < 2 || v[0] != 'P') return null;
        long total = 0, number = 0;
        var any = false;
        foreach (var ch in v[1..])
        {
            if (char.IsAsciiDigit(ch)) { number = number * 10 + (ch - '0'); any = true; continue; }
            switch (ch)
            {
                case 'T': continue;
                case 'W': total += number * 604800; break;
                case 'D': total += number * 86400; break;
                case 'H': total += number * 3600; break;
                case 'M': total += number * 60; break;
                case 'S': total += number; break;
                default: return null;
            }
            number = 0;
        }
        return any ? sign * total : null;
    }

    // ── Times and zones ──────────────────────────────────────────────────────

    /// <summary>The zone by IANA (or Windows) id; null for none, UTC, or one this system does not know.</summary>
    internal static TimeZoneInfo? Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "UTC" or "Etc/UTC" or "GMT" or "Etc/GMT" or "Z") return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    private static string TimeName(string name, string? tz) => Zone(tz) is { } zone ? $"{name};TZID={zone.Id}" : name;

    private static string Time(long ms, string? tz)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return Zone(tz) is { } zone
            ? TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)
            : utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }

    private static string Utc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Date(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// A VTIMEZONE for <paramref name="zone"/> as it was in the year of <paramref name="fromMs"/>: its transitions that year (found by
    /// scanning the offsets), each with a yearly rule when the next year has the same one (the nth or last weekday of the month).
    /// </summary>
    private static void WriteTimeZone(ContentWriter w, TimeZoneInfo zone, long fromMs)
    {
        var year = Math.Clamp(DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(fromMs, -62135596800000, 253402300799999)).UtcDateTime.Year, 1971, 9998);
        w.Line("BEGIN", "VTIMEZONE");
        w.Line("TZID", zone.Id);
        var transitions = Transitions(zone, year);
        if (transitions.Count == 0)
        {
            var offset = zone.GetUtcOffset(new DateTime(year, 7, 1, 0, 0, 0, DateTimeKind.Utc));
            w.Line("BEGIN", "STANDARD");
            w.Line("DTSTART", "19700101T000000");
            w.Line("TZOFFSETFROM", Offset(offset));
            w.Line("TZOFFSETTO", Offset(offset));
            w.Line("END", "STANDARD");
        }
        else
        {
            var next = Transitions(zone, year + 1);
            foreach (var t in transitions)
            {
                var kind = zone.IsDaylightSavingTime(t.Utc.AddMinutes(1)) ? "DAYLIGHT" : "STANDARD";
                var local = t.Utc + t.From;
                w.Line("BEGIN", kind);
                w.Line("DTSTART", local.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
                var rule = Rule(local);
                if (next.Any(n => n.From == t.From && n.To == t.To && Rule(n.Utc + n.From) == rule && (n.Utc + n.From).TimeOfDay == local.TimeOfDay))
                    w.Line("RRULE", rule);
                w.Line("TZOFFSETFROM", Offset(t.From));
                w.Line("TZOFFSETTO", Offset(t.To));
                w.Line("END", kind);
            }
        }
        w.Line("END", "VTIMEZONE");
    }

    private sealed record Transition(DateTime Utc, TimeSpan From, TimeSpan To);

    private static List<Transition> Transitions(TimeZoneInfo zone, int year)
    {
        var list = new List<Transition>();
        var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddYears(1);
        var previous = zone.GetUtcOffset(start);
        for (var t = start.AddHours(1); t <= end; t = t.AddHours(1))
        {
            var offset = zone.GetUtcOffset(t);
            if (offset == previous) continue;
            // the exact minute inside this hour
            var lo = t.AddHours(-1);
            var hi = t;
            while (hi - lo > TimeSpan.FromMinutes(1))
            {
                var mid = lo + (hi - lo) / 2;
                mid = new DateTime(mid.Ticks - mid.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
                if (mid <= lo) break;
                if (zone.GetUtcOffset(mid) == previous) lo = mid; else hi = mid;
            }
            list.Add(new Transition(hi, previous, offset));
            previous = offset;
        }
        return list;
    }

    private static string Rule(DateTime local)
    {
        var days = DateTime.DaysInMonth(local.Year, local.Month);
        var nth = local.Day + 7 > days ? -1 : (local.Day - 1) / 7 + 1;
        var day = local.DayOfWeek switch
        {
            DayOfWeek.Monday => "MO", DayOfWeek.Tuesday => "TU", DayOfWeek.Wednesday => "WE", DayOfWeek.Thursday => "TH",
            DayOfWeek.Friday => "FR", DayOfWeek.Saturday => "SA", _ => "SU",
        };
        return $"FREQ=YEARLY;BYMONTH={local.Month.ToString(CultureInfo.InvariantCulture)};BYDAY={nth.ToString(CultureInfo.InvariantCulture)}{day}";
    }

    private static string Offset(TimeSpan o)
    {
        var sign = o < TimeSpan.Zero ? "-" : "+";
        var a = o.Duration();
        return a.Seconds != 0
            ? $"{sign}{a.Hours:00}{a.Minutes:00}{a.Seconds:00}"
            : $"{sign}{a.Hours:00}{a.Minutes:00}";
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every event of a .ics (one or many VCALENDARs): masters with their exceptions (VEVENTs with a RECURRENCE-ID and the same UID).
    /// Throws <see cref="InvalidDataException"/> when there is none.
    /// </summary>
    public static List<CalendarEvent> Parse(ReadOnlySpan<byte> data)
    {
        var events = new List<(CalendarEvent Event, bool IsException)>();
        var zones = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase); // a VTIMEZONE's standard offset, for unknown zones
        List<ContentLine>? ev = null, alarm = null;
        var alarms = new List<List<ContentLine>>();
        string? tzid = null;
        var inTz = false;
        foreach (var line in ContentLine.ReadAll(data))
        {
            if (line.Is("BEGIN"))
            {
                switch (line.Value.ToUpperInvariant())
                {
                    case "VEVENT": ev = []; alarms = []; break;
                    case "VALARM" when ev is not null: alarm = []; break;
                    case "VTIMEZONE": inTz = true; tzid = null; break;
                }
                continue;
            }
            if (line.Is("END"))
            {
                switch (line.Value.ToUpperInvariant())
                {
                    case "VALARM" when alarm is not null: alarms.Add(alarm); alarm = null; break;
                    case "VEVENT" when ev is not null: events.Add(FromLines(ev, alarms, zones, events.Count)); ev = null; break;
                    case "VTIMEZONE": inTz = false; break;
                }
                continue;
            }
            if (alarm is not null) alarm.Add(line);
            else if (ev is not null) ev.Add(line);
            else if (inTz)
            {
                if (line.Is("TZID")) tzid = line.Value;
                else if (line.Is("TZOFFSETTO") && tzid is not null && !zones.ContainsKey(tzid) && ParseOffset(line.Value) is { } off) zones[tzid] = off;
            }
        }
        if (events.Count == 0) throw new InvalidDataException("No event in the file.");
        var masters = events.Where(e => !e.IsException).Select(e => e.Event).ToList();
        var byUid = masters.Where(m => m.Uid is not null).GroupBy(m => m.Uid!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var result = new List<CalendarEvent>(masters);
        foreach (var (exception, _) in events.Where(e => e.IsException))
        {
            if (exception.Uid is { } uid && byUid.TryGetValue(uid, out var master))
            {
                var updated = master with { Exceptions = [.. master.Exceptions, exception] };
                result[result.IndexOf(master)] = updated;
                byUid[uid] = updated;
            }
            else result.Add(exception with { OriginalStart = null }); // its series is not in the file: on its own
        }
        return result;
    }

    private static (CalendarEvent, bool) FromLines(List<ContentLine> lines, List<List<ContentLine>> alarmLines, Dictionary<string, TimeSpan> zones, int index)
    {
        ContentLine? Get(string name) => lines.FirstOrDefault(l => l.Is(name));
        string? Text(string name) => Get(name) is { } l ? VCard.Unescape(l.Value) : null;
        var start = Get("DTSTART") ?? throw new InvalidDataException("An event without DTSTART.");
        var (startMs, allDay, tz) = ParseTime(start, zones);
        long? end = null;
        string? endTz = null;
        if (Get("DTEND") is { } dtend) { (var e, _, endTz) = ParseTime(dtend, zones); end = e; }
        var duration = Get("DURATION")?.Value;
        long? recurrenceId = Get("RECURRENCE-ID") is { } rid ? ParseTime(rid, zones).Ms : null;
        var rrule = string.Join('\n', lines.Where(l => l.Is("RRULE")).Select(l => l.Value));
        var exrule = string.Join('\n', lines.Where(l => l.Is("EXRULE")).Select(l => l.Value));
        string? Dates(string name)
        {
            var parts = lines.Where(l => l.Is(name)).Select(l => l.Param("TZID") is { } z ? $"{z};{l.Value}" : l.Value).ToList();
            return parts.Count == 0 ? null : string.Join('\n', parts);
        }
        var attendees = lines.Where(l => l.Is("ATTENDEE")).Select(l => new Attendee(
            StripMailto(l.Value), l.Param("CN"), l.Param("ROLE") ?? "REQ-PARTICIPANT", l.Param("PARTSTAT") ?? "NEEDS-ACTION", l.Param("CUTYPE") ?? "INDIVIDUAL")).ToList();
        var alarms = new List<Alarm>();
        foreach (var a in alarmLines)
        {
            if (a.FirstOrDefault(l => l.Is("TRIGGER")) is not { } trigger) continue;
            int minutes;
            if (ParseDuration(trigger.Value) is { } seconds) minutes = (int)(-seconds / 60);
            else if (DateTime.TryParseExact(trigger.Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                minutes = (int)((startMs - new DateTimeOffset(at).ToUnixTimeMilliseconds()) / 60000);
            else continue;
            var method = a.FirstOrDefault(l => l.Is("X-ASYNC-METHOD"))?.Value.ToLowerInvariant()
                         ?? (a.FirstOrDefault(l => l.Is("ACTION"))?.Value.Equals("EMAIL", StringComparison.OrdinalIgnoreCase) == true ? "email" : "display");
            alarms.Add(new Alarm(minutes, method));
        }
        long? Utc(string name) => Get(name) is { } l ? ParseTime(l, zones).Ms : null;
        var uid = Text("UID");
        var ev = new CalendarEvent
        {
            Id = uid ?? $"ics:{index}", ShortId = "", CalendarId = "", Uid = uid,
            Title = Text("SUMMARY"), Description = Text("DESCRIPTION"), Location = Text("LOCATION"),
            Start = startMs, End = end, AllDay = allDay, TimeZone = tz, EndTimeZone = endTz is not null && endTz != tz ? endTz : null,
            Duration = duration is { Length: > 0 } && ParseDuration(duration) is { } ds ? FormatDuration(ds) : null,
            RRule = rrule.Length > 0 ? rrule : null, ExRule = exrule.Length > 0 ? exrule : null, RDate = Dates("RDATE"), ExDate = Dates("EXDATE"),
            Status = Get("STATUS")?.Value.ToUpperInvariant(), Transparency = Get("TRANSP")?.Value.ToUpperInvariant(), Class = Get("CLASS")?.Value.ToUpperInvariant(),
            Organizer = Get("ORGANIZER") is { } org ? StripMailto(org.Value) : null, Attendees = attendees, Alarms = alarms,
            Color = Get("X-ASYNC-COLOR")?.Value, Created = Utc("CREATED"), LastModified = Utc("LAST-MODIFIED"), OriginalStart = recurrenceId,
        };
        if (ev.End is null && ev.Duration is null && !ev.Recurring)
            ev = ev with { End = allDay ? startMs + 86400000 : startMs }; // RFC 5545: a day / an instant
        return (ev, recurrenceId is not null);
    }

    private static string? StripMailto(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) v = v[7..];
        return v.Length == 0 ? null : v;
    }

    private static TimeSpan? ParseOffset(string value)
    {
        var v = value.Trim();
        if (v.Length < 5 || v[0] is not ('+' or '-')) return null;
        if (!int.TryParse(v[1..3], CultureInfo.InvariantCulture, out var h) || !int.TryParse(v[3..5], CultureInfo.InvariantCulture, out var m)) return null;
        var s = v.Length >= 7 && int.TryParse(v[5..7], CultureInfo.InvariantCulture, out var sec) ? sec : 0;
        var span = new TimeSpan(h, m, s);
        return v[0] == '-' ? -span : span;
    }

    /// <summary>A DATE or DATE-TIME property as Unix ms (UTC), whether it is a date, and its zone (null for UTC and floating times).</summary>
    private static (long Ms, bool AllDay, string? Tz) ParseTime(ContentLine line, Dictionary<string, TimeSpan> zones)
    {
        var v = line.Value.Trim();
        if (v.Contains(',')) v = v[..v.IndexOf(',')];
        if (line.Param("VALUE")?.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true || (v.Length == 8 && v.All(char.IsAsciiDigit)))
        {
            var d = DateTime.ParseExact(v[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return (new DateTimeOffset(d, TimeSpan.Zero).ToUnixTimeMilliseconds(), true, null);
        }
        if (!DateTime.TryParseExact(v.TrimEnd('Z', 'z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new InvalidDataException($"Not a date-time: {v}");
        if (v.EndsWith('Z') || v.EndsWith('z'))
            return (new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), false, null);
        var tzid = line.Param("TZID");
        if (Zone(tzid) is { } zone)
        {
            var utc = zone.IsInvalidTime(local) ? TimeZoneInfo.ConvertTimeToUtc(local.AddHours(1), zone) : TimeZoneInfo.ConvertTimeToUtc(local, zone);
            return (new DateTimeOffset(utc).ToUnixTimeMilliseconds(), false, zone.Id);
        }
        var offset = tzid is not null && zones.TryGetValue(tzid, out var o) ? o : TimeSpan.Zero; // unknown zone: its VTIMEZONE's offset; floating: UTC
        return (new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUnixTimeMilliseconds(), false, null);
    }

    /// <summary>
    /// What makes two events "the same" for a restore that finds one already there under another UID: title, start, length, rule. The
    /// rule compares in a normal form (<see cref="NormalizeRule"/>): a calendar gives a series back in its own words (iOS's EventKit
    /// writes no INTERVAL=1 or WKST, and UNTIL as it likes), and a second restore must still find it.
    /// </summary>
    public static string ContentKey(CalendarEvent e)
    {
        var length = e.End is { } end ? (end - e.Start) / 1000 : ParseDuration(e.Duration) ?? 0;
        return string.Join('\u001f', (e.Title ?? "").Trim().ToLowerInvariant(), Invariant.S(e.Start), Invariant.S(length), e.AllDay ? "1" : "0",
            NormalizeRule(e.RRule));
    }

    /// <summary>An RRULE in a form that compares equal however it was written: parts sorted, INTERVAL=1 and WKST left out, UNTIL to its date.</summary>
    public static string NormalizeRule(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return "";
        var lines = Lines(rrule).Select(line => string.Join(';', line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToUpperInvariant())
            .Where(p => p != "INTERVAL=1" && !p.StartsWith("WKST=", StringComparison.Ordinal))
            .Select(p => p.StartsWith("UNTIL=", StringComparison.Ordinal) && p.Length > 14 ? p[..14] : p)
            .Order(StringComparer.Ordinal)));
        return string.Join('\n', lines.Order(StringComparer.Ordinal));
    }
}
