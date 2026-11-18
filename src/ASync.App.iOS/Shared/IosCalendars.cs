using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ASync.Client.Core.Collections;
using ASync.Contracts;

namespace ASync.App.iOS;

// The iPhone's calendars (task 24): EventKit behind a thin interface (IEventKit, implemented by EventKitBridge in the head), and
// everything that decides something in plain C# here, linked into ASync.App.Tests and tested on Linux. AGENTS.md "Extra data sources"
// → "iOS contacts and calendars (task 24)".

/// <summary>EKAuthorizationStatus (3 is iOS 17's "Full Access" as well as the older "Authorized"; 4 "Add Events Only").</summary>
public enum EKAuth { NotDetermined = 0, Restricted = 1, Denied = 2, FullAccess = 3, WriteOnly = 4 }

/// <summary>EKSourceType.</summary>
public enum EKSourceKind { Local = 0, Exchange = 1, CalDav = 2, MobileMe = 3, Subscribed = 4, Birthdays = 5 }

/// <summary>EKCalendarType.</summary>
public enum EKCalendarKind { Local = 0, CalDav = 1, Exchange = 2, Subscription = 3, Birthday = 4 }

public sealed record EKCalendarData(string Id, string Title, string SourceId, string SourceTitle, EKSourceKind SourceKind, EKCalendarKind Kind,
    string? Color, bool AllowsModifications);

/// <summary>An EKParticipant: its URL (<c>mailto:…</c>), name, and EKParticipantRole / Status / Type as their raw values.</summary>
public sealed record EKParticipantData(string? Url, string? Name, int Role = 1, int Status = 1, int Type = 1);

/// <summary>An EKAlarm: an offset from the start in seconds (negative: before), or an absolute time (Unix ms).</summary>
public sealed record EKAlarmData(double? RelativeSeconds, long? Absolute = null);

/// <summary>
/// An EKEvent as EventKit lists it: one occurrence of a series (with the series' rules, its <see cref="OccurrenceDate"/> the original
/// start; a detached one changed on its own) or a single event. Times are Unix ms; <see cref="TimeZone"/> the IANA zone (null: floating,
/// which all-day events are). <see cref="Status"/> EKEventStatus, <see cref="Availability"/> EKEventAvailability (-1: not supported).
/// </summary>
public sealed record EKEventData
{
    public string EventId { get; init; } = "";
    public string? ExternalId { get; init; }
    public string CalendarId { get; init; } = "";
    public string? Title { get; init; }
    public string? Location { get; init; }
    public string? Notes { get; init; }
    public long Start { get; init; }
    public long End { get; init; }
    public bool AllDay { get; init; }
    public string? TimeZone { get; init; }
    public long? OccurrenceDate { get; init; }
    public bool IsDetached { get; init; }
    public IReadOnlyList<EKRule> Rules { get; init; } = [];
    public int Status { get; init; }
    public int Availability { get; init; } = -1;
    public EKParticipantData? Organizer { get; init; }
    public IReadOnlyList<EKParticipantData> Attendees { get; init; } = [];
    public IReadOnlyList<EKAlarmData> Alarms { get; init; } = [];
    public long? Created { get; init; }
    public long? LastModified { get; init; }
}

/// <summary>
/// EventKit, as thin as it can be: listings and saves, no decisions. <c>EventKitBridge</c> in the head implements it; the tests use a
/// fake. Calls block and are made off the main thread.
/// </summary>
public interface IEventKit
{
    EKAuth Authorization { get; }

    /// <summary>The device's zone (IANA): where all-day and floating events are.</summary>
    string DeviceTimeZone { get; }

    /// <summary>The event calendars.</summary>
    IReadOnlyList<EKCalendarData> Calendars();

    /// <summary>
    /// eventsMatchingPredicate: every occurrence in [<paramref name="from"/>, <paramref name="to"/>) of these calendars (EventKit
    /// limits a predicate to four years: callers ask for less).
    /// </summary>
    IReadOnlyList<EKEventData> Occurrences(IReadOnlyCollection<string> calendarIds, long from, long to);

    /// <summary>eventWithIdentifier: a series' first occurrence (or the single event); null when it is gone.</summary>
    EKEventData? Event(string eventId);

    /// <summary>Saves a new event (with its rules and alarms) into the calendar; answers its eventIdentifier.</summary>
    string Add(EKEventData e, string calendarId);

    /// <summary>
    /// One occurrence of a series (its original start <paramref name="occurrenceDate"/>; all-day: any time that day): changed on its own
    /// (<paramref name="changes"/>: title, location, notes, times, alarms; saved with span thisEvent) or, with null, removed (span thisEvent).
    /// False when there is no such occurrence.
    /// </summary>
    bool ChangeOccurrence(string eventId, string calendarId, long occurrenceDate, bool allDay, EKEventData? changes);
}

/// <summary>
/// The mapping of EventKit onto <see cref="CalendarInfo"/>s and <see cref="CalendarEvent"/>s (AGENTS.md "iOS contacts and calendars"):
/// calendars and their sources, occurrences back into series with their changed and deleted occurrences, rules, alarms, attendees,
/// and back for a restore.
/// </summary>
public static class IosEventMapping
{
    public const string Source = "ios-eventkit";

    /// <summary>How far ahead a backup looks: EventKit lists occurrences, so a never-ending series needs an end; five years ahead.</summary>
    public const int FutureYears = 5;

    /// <summary>EventKit answers at most four years per predicate: the listing goes in pieces of three.</summary>
    public const long Piece = 3L * 365 * 86400000;

    public static MediaAccess Access(EKAuth status) => status == EKAuth.FullAccess ? MediaAccess.Full : MediaAccess.None;

    // ── Calendars ───────────────────────────────────────────────────────────

    /// <summary>What people call a calendar's account: iCloud (a CalDAV source named iCloud), Exchange, CalDAV, the iPhone's own, subscribed.</summary>
    public static string SourceLabel(EKCalendarData c) => c.SourceKind switch
    {
        EKSourceKind.Local => "iPhone",
        EKSourceKind.Exchange => "Exchange",
        EKSourceKind.CalDav when c.SourceTitle.Equals("iCloud", StringComparison.OrdinalIgnoreCase) => "iCloud",
        EKSourceKind.CalDav => "CalDAV",
        EKSourceKind.MobileMe => "iCloud",
        EKSourceKind.Subscribed => "Subscribed",
        EKSourceKind.Birthdays => "Birthdays",
        _ => "Calendar",
    };

    /// <summary>
    /// The calendars offered: every event calendar but Birthdays, which iOS makes from the contacts' birthdays (the Contacts folder keeps
    /// them, and a restore could not write it). Subscribed and other read-only ones are backed up and marked read-only.
    /// </summary>
    public static bool Offered(EKCalendarData c) => c.Kind != EKCalendarKind.Birthday && c.SourceKind != EKSourceKind.Birthdays;

    public static CalendarInfo Calendar(EKCalendarData c, int events) =>
        new(c.Id, CollectionLayout.HashId(c.Id), c.Title is { Length: > 0 } t ? t : "Calendar",
            c.SourceKind == EKSourceKind.Local ? "iPhone" : c.SourceTitle is { Length: > 0 } s ? s : SourceLabel(c), SourceLabel(c), c.Color, events,
            null, !c.AllowsModifications);

    // ── Zones and days ──────────────────────────────────────────────────────

    public static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static long Midnight(DateTime date) => new DateTimeOffset(DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>An all-day time from EventKit (local midnight of the device) as the model keeps it: midnight UTC of that date.</summary>
    public static long DayStart(long ms, TimeZoneInfo device) => Midnight(IosRecurrence.Local(ms, device));

    /// <summary>An all-day event's end (EventKit: the last second of its last day, or the next midnight) as midnight UTC after its last day.</summary>
    public static long DayEnd(long ms, TimeZoneInfo device) => Midnight(IosRecurrence.Local(ms - 1000, device).Date.AddDays(1));

    /// <summary>Midnight UTC of a date as EventKit's all-day time: local midnight of the device.</summary>
    public static long DeviceMidnight(long utcMidnight, TimeZoneInfo device) =>
        IosRecurrence.Utc(DateTimeOffset.FromUnixTimeMilliseconds(utcMidnight).UtcDateTime.Date, device);

    // ── Occurrences → series ────────────────────────────────────────────────

    /// <summary>The file name's short id: 16 hex digits of SHA-256 over the calendar and the event's UID (stable across EventKit's id changes).</summary>
    public static string ShortId(string calendarId, string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(calendarId + "\n" + key)))[..16];

    private static string? Status(int s) => s switch { 1 => "CONFIRMED", 2 => "TENTATIVE", 3 => "CANCELLED", _ => null };

    private static string? Transparency(int a) => a switch { 1 => "TRANSPARENT", 0 or 2 or 3 => "OPAQUE", _ => null };

    private static string? Address(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        return u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? u[7..] : u;
    }

    public static Attendee Attendee(EKParticipantData p) => new(
        Address(p.Url), p.Name is { Length: > 0 } n ? n : null,
        p.Role switch { 2 => "OPT-PARTICIPANT", 3 => "CHAIR", 4 => "NON-PARTICIPANT", _ => "REQ-PARTICIPANT" },
        p.Status switch { 2 => "ACCEPTED", 3 => "DECLINED", 4 => "TENTATIVE", 5 => "DELEGATED", 6 => "COMPLETED", 7 => "IN-PROCESS", _ => "NEEDS-ACTION" },
        p.Type switch { 2 => "ROOM", 3 => "RESOURCE", 4 => "GROUP", _ => "INDIVIDUAL" });

    public static Alarm Alarm(EKAlarmData a, long start) => a.RelativeSeconds is { } rel
        ? new Alarm((int)Math.Round(-rel / 60))
        : new Alarm(a.Absolute is { } at ? (int)((start - at) / 60000) : 0);

    private static string Key(EKEventData o) => o.ExternalId is { Length: > 0 } x ? x : o.EventId;

    /// <summary>One occurrence's own fields as an event (a single event, a series' base, or a changed occurrence).</summary>
    private static CalendarEvent Plain(EKEventData o, TimeZoneInfo device, string id, string shortId)
    {
        var zone = o.TimeZone ?? device.Id;
        return new CalendarEvent
        {
            Id = id, ShortId = shortId, CalendarId = o.CalendarId, Uid = o.ExternalId is { Length: > 0 } x ? x : null,
            Title = o.Title, Location = o.Location is { Length: > 0 } l ? l : null, Description = o.Notes is { Length: > 0 } d ? d : null,
            Start = o.AllDay ? DayStart(o.Start, device) : o.Start, End = o.AllDay ? DayEnd(o.End, device) : o.End, AllDay = o.AllDay,
            TimeZone = o.AllDay ? null : zone, Status = Status(o.Status), Transparency = Transparency(o.Availability),
            Organizer = Address(o.Organizer?.Url), Attendees = [.. o.Attendees.Select(Attendee)], Alarms = [.. o.Alarms.Select(a => Alarm(a, o.Start))],
            Created = o.Created, LastModified = o.LastModified,
        };
    }

    /// <summary>
    /// EventKit's occurrences of [<paramref name="from"/>, <paramref name="to"/>) back into events: a single event as it is; a series
    /// as its master (the first occurrence's start from <paramref name="first"/>, the rule as RRULE, the length as DURATION) with its
    /// detached occurrences as exceptions (RECURRENCE-ID = their original start; a cancelled one keeps STATUS:CANCELLED) and the
    /// occurrences the rule has but EventKit no longer lists as EXDATE (deleted ones; inferred only well inside the window, where a
    /// moved occurrence would still be listed, and only when every listed occurrence is one the rule has). Grouped by
    /// eventIdentifier; detached occurrences listed under another identifier join their series by calendarItemExternalIdentifier.
    /// </summary>
    public static List<CalendarEvent> Events(IReadOnlyList<EKEventData> occurrences, Func<string, EKEventData?> first, TimeZoneInfo device,
        long from, long to, Action<string>? log = null)
    {
        var groups = occurrences
            .GroupBy(o => o.EventId, StringComparer.Ordinal)
            .Select(g => g.DistinctBy(o => o.OccurrenceDate ?? o.Start).ToList())
            .ToList();
        static bool Recurring(List<EKEventData> g) => g.Any(o => o.Rules.Count > 0 && !o.IsDetached);
        // Detached occurrences listed under an identifier (and, on the iPhone's own calendars, a UID) of their own — seen on the
        // simulator: into the series of the same calendar with their UID, else the one series whose rule has their original date
        // and lists no occurrence then.
        var lo = from + Margin;
        var hi = to - Margin;
        var expected = new Dictionary<List<EKEventData>, HashSet<long>?>();
        HashSet<long>? Expected(List<EKEventData> series)
        {
            if (expected.TryGetValue(series, out var set)) return set;
            var head = Head(series, first, device);
            var list = head.Basis.Rules.Count == 1 && hi > lo ? Occurrences(head, device, lo, hi) : null;
            return expected[series] = list?.ToHashSet();
        }
        foreach (var orphan in groups.Where(g => !Recurring(g) && g.Count == 1 && LooksDetached(g[0])).ToList())
        {
            var o = orphan[0] with { IsDetached = true };
            var same = groups.Where(g => g != orphan && Recurring(g) && g[0].CalendarId == o.CalendarId).ToList();
            var series = same.FirstOrDefault(g => Key(g[0]) == Key(o));
            if (series is null)
            {
                var candidates = same.Where(g =>
                {
                    var original = Original(o, g[0].AllDay, device);
                    return Expected(g) is { } e && e.Contains(original) && g.All(x => Original(x, g[0].AllDay, device) != original);
                }).Take(2).ToList();
                series = candidates.Count == 1 ? candidates[0] : null;
            }
            if (series is null) continue;
            series.Add(o);
            groups.Remove(orphan);
        }
        var result = new List<CalendarEvent>();
        foreach (var g in groups)
        {
            if (!Recurring(g))
            {
                foreach (var o in g.OrderBy(o => o.Start))
                {
                    var id = g.Count == 1 ? o.EventId : $"{o.EventId}@{(o.OccurrenceDate ?? o.Start).ToString(CultureInfo.InvariantCulture)}";
                    result.Add(Plain(o, device, id, ShortId(o.CalendarId, g.Count == 1 ? Key(o) : id)));
                }
                continue;
            }
            result.Add(Series(g, first, device, from, to, log));
        }
        return [.. result.OrderBy(e => e.Id, StringComparer.Ordinal)];
    }

    /// <summary>Deleted occurrences are inferred only this far inside the listed window (a moved one near its edge could look deleted).</summary>
    private const long Margin = 7L * 86400000;

    /// <summary>A detached occurrence: EventKit says so, or it starts at another time than it originally did.</summary>
    private static bool LooksDetached(EKEventData o) => o.IsDetached || o.OccurrenceDate is { } od && od != o.Start;

    /// <summary>An occurrence's original start as the model keeps it (all-day: midnight UTC of its date).</summary>
    private static long Original(EKEventData o, bool allDay, TimeZoneInfo device) =>
        allDay ? DayStart(o.OccurrenceDate ?? o.Start, device) : o.OccurrenceDate ?? o.Start;

    private sealed record SeriesHead(EKEventData Basis, long SeriesStart, bool AllDay, TimeZoneInfo Zone);

    /// <summary>What a series is: its basis (a regular occurrence with the rule) and its first start (<c>eventWithIdentifier</c>).</summary>
    private static SeriesHead Head(List<EKEventData> g, Func<string, EKEventData?> first, TimeZoneInfo device)
    {
        var regular = g.Where(o => !o.IsDetached).OrderBy(o => o.OccurrenceDate ?? o.Start).ToList();
        var head = first(regular[0].EventId);
        var basis = head is { IsDetached: false, Rules.Count: > 0 } ? head : regular.FirstOrDefault(o => o.Rules.Count > 0) ?? regular[0];
        var seriesStart = head is not null ? head.OccurrenceDate ?? head.Start : basis.OccurrenceDate ?? basis.Start;
        return new SeriesHead(basis, seriesStart, basis.AllDay, basis.AllDay ? TimeZoneInfo.Utc : Zone(basis.TimeZone ?? device.Id));
    }

    /// <summary>The original starts the series' (single) rule has in [lo, hi), as the model keeps them; null when not computed.</summary>
    private static List<long>? Occurrences(SeriesHead h, TimeZoneInfo device, long lo, long hi)
    {
        var rule = h.Basis.Rules[0];
        var startLocal = h.AllDay ? DateTimeOffset.FromUnixTimeMilliseconds(DayStart(h.SeriesStart, device)).UtcDateTime : IosRecurrence.Local(h.SeriesStart, h.Zone);
        return IosRecurrence.Occurrences(rule with { Until = rule.Until is { } u && h.AllDay ? DayEnd(u + 1000, device) - 1 : rule.Until },
            startLocal, h.Zone, lo, hi);
    }

    private static CalendarEvent Series(List<EKEventData> g, Func<string, EKEventData?> first, TimeZoneInfo device, long from, long to, Action<string>? log)
    {
        var listed = g.OrderBy(o => o.OccurrenceDate ?? o.Start).ToList();
        var h = Head(g, first, device);
        var (basis, seriesStart, allDay, zone) = (h.Basis, h.SeriesStart, h.AllDay, h.Zone);
        var seconds = allDay
            ? (DayEnd(basis.End, device) - DayStart(basis.Start, device)) / 1000
            : Math.Max(0, (basis.End - basis.Start) / 1000);
        var master = Plain(basis, device, basis.EventId, ShortId(basis.CalendarId, Key(basis))) with
        {
            Start = allDay ? DayStart(seriesStart, device) : seriesStart,
            End = null,
            Duration = ICalendar.FormatDuration(seconds),
            RRule = string.Join('\n', basis.Rules.Select(r => IosRecurrence.ToRRule(r, allDay, device))),
        };
        if (basis.Rules.Count > 1) log?.Invoke($"\"{basis.Title}\" repeats by {basis.Rules.Count} rules: deleted occurrences are not inferred.");

        long Original(EKEventData o) => IosEventMapping.Original(o, allDay, device);
        var exceptions = listed.Where(o => o.IsDetached)
            .Select(o => Plain(o, device, $"{basis.EventId}@{Original(o).ToString(CultureInfo.InvariantCulture)}", "") with { OriginalStart = Original(o) })
            .ToList();

        // Deleted occurrences: what the rule has, well inside the listed window, that EventKit does not list.
        var lo = from + Margin;
        var hi = to - Margin;
        string? exDate = null;
        if (basis.Rules.Count == 1 && hi > lo)
        {
            var expected = Occurrences(h, device, lo, hi);
            var seen = listed.Select(Original).Where(t => t >= lo && t < hi).ToHashSet();
            if (expected is null) log?.Invoke($"\"{basis.Title}\": its rule ({master.RRule}) is not computed here: deleted occurrences are not inferred.");
            else if (seen.Any(t => !expected.Contains(t)))
                log?.Invoke($"\"{basis.Title}\": EventKit lists occurrences its rule ({master.RRule}) does not have: deleted occurrences are not inferred.");
            else
            {
                var missing = expected.Where(t => !seen.Contains(t)).ToList();
                if (missing.Count > 0)
                    exDate = allDay
                        ? string.Join(',', missing.Select(t => DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))
                        : zone == TimeZoneInfo.Utc
                            ? string.Join(',', missing.Select(t => DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)))
                            : zone.Id + ";" + string.Join(',', missing.Select(t => IosRecurrence.Local(t, zone).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)));
            }
        }
        var modified = listed.Max(o => o.LastModified ?? 0);
        return master with
        {
            ExDate = exDate,
            Exceptions = exceptions,
            LastModified = modified > 0 ? modified : master.LastModified,
        };
    }

    // ── A restore: events → EventKit ────────────────────────────────────────

    /// <summary>The length of an event in seconds: its end, else its duration, else a day (all-day) or nothing.</summary>
    private static long Seconds(CalendarEvent e) => e.End is { } end ? (end - e.Start) / 1000 : ICalendar.ParseDuration(e.Duration) ?? (e.AllDay ? 86400 : 0);

    /// <summary>
    /// An event as EventKit takes it: all-day events from local midnight of their first day to their last day; timed ones in their zone
    /// (UTC when they have none); the rule (one RRULE: iOS stores no more, nor RDATE or EXRULE); alarms by their offset. Attendees and the
    /// organizer cannot be written by an app (EventKit has no API for them): they stay in the file.
    /// </summary>
    public static EKEventData ToEventKit(CalendarEvent e, TimeZoneInfo device)
    {
        var seconds = Math.Max(0, Seconds(e));
        long start, end;
        if (e.AllDay)
        {
            // EventKit's own form: from local midnight of the first day to the last second of the last day.
            start = DeviceMidnight(e.Start, device);
            var days = Math.Max(1, (seconds + 86399) / 86400);
            end = Math.Max(start, DeviceMidnight(e.Start + days * 86400000L, device) - 1000);
        }
        else
        {
            start = e.Start;
            end = e.Start + seconds * 1000;
        }
        var zone = e.AllDay ? device : Zone(e.TimeZone);
        var rules = e.RRule is { Length: > 0 } rrule ? [IosRecurrence.Parse(rrule, zone)] : Array.Empty<EKRule>();
        return new EKEventData
        {
            CalendarId = e.CalendarId, Title = e.Title, Location = e.Location, Notes = e.Description, Start = start, End = end, AllDay = e.AllDay,
            TimeZone = e.AllDay ? null : e.TimeZone is { Length: > 0 } tz && Zone(tz) != TimeZoneInfo.Utc ? tz : "UTC",
            Rules = rules, Availability = e.Transparency switch { "TRANSPARENT" => 1, "OPAQUE" => 0, _ => -1 },
            Alarms = [.. e.Alarms.Select(a => new EKAlarmData(-a.MinutesBefore * 60.0))],
        };
    }

    /// <summary>
    /// The dates of an EXDATE (or RDATE) as the model keeps them (<c>[tzid;]v1,v2</c>, maybe several lines): Unix ms, all-day ones
    /// as midnight UTC; local times in their zone, else in <paramref name="zone"/>.
    /// </summary>
    public static List<long> Dates(string? text, TimeZoneInfo zone)
    {
        var result = new List<long>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var semi = line.IndexOf(';');
            if (semi < 0 && line.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase)) semi = line.IndexOf(':'); // TZID=zone:values
            var tz = zone;
            var list = line;
            if (semi > 0)
            {
                var prefix = line[..semi];
                if (prefix.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase)) prefix = prefix[5..];
                tz = Zone(prefix);
                list = line[(semi + 1)..].TrimStart(':');
            }
            foreach (var v in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (v.Length == 8 && DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    result.Add(Midnight(date));
                else if (DateTime.TryParseExact(v.TrimEnd('Z', 'z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                    result.Add(v.EndsWith('Z') || v.EndsWith('z') ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : IosRecurrence.Utc(local, tz));
            }
        }
        return result;
    }
}

/// <summary>
/// The iPhone's calendars as an <see cref="ICalendarSource"/> (task 24): EKCalendars (not Birthdays) with their EKSource as the
/// account, events from EventKit's occurrences turned back into series (<see cref="IosEventMapping.Events"/>), from "events since"
/// (everything: from 1970) to <see cref="IosEventMapping.FutureYears"/> ahead. A restore adds a series, then changes and removes its
/// occurrences as the snapshot's exceptions and EXDATEs say.
/// </summary>
public sealed class IosCalendarSource(IEventKit kit, Func<long>? now = null, Action<string>? log = null) : ICalendarSource
{
    private readonly Func<long> _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public MediaAccess Access => IosEventMapping.Access(kit.Authorization);

    /// <summary>Full access only: "Add Events Only" could add, but a restore must see what is there to skip it.</summary>
    public bool CanWrite => kit.Authorization == EKAuth.FullAccess;

    private TimeZoneInfo Device => IosEventMapping.Zone(kit.DeviceTimeZone);

    private void Need()
    {
        if (Access is MediaAccess.None or MediaAccess.Unsupported) throw new UnauthorizedAccessException("ASync may not read the calendars on this iPhone.");
    }

    private long Until => DateTimeOffset.FromUnixTimeMilliseconds(_now()).AddYears(IosEventMapping.FutureYears).ToUnixTimeMilliseconds();

    /// <summary>Every occurrence of the window, in pieces EventKit answers whole, each once.</summary>
    private List<EKEventData> Occurrences(IReadOnlyCollection<string> calendarIds, long from, long to)
    {
        var all = new List<EKEventData>();
        var seen = new HashSet<(string, long)>();
        for (var a = from; a < to; a += IosEventMapping.Piece)
            foreach (var o in kit.Occurrences(calendarIds, a, Math.Min(to, a + IosEventMapping.Piece)))
                if (seen.Add((o.EventId, o.OccurrenceDate ?? o.Start))) all.Add(o);
        return all;
    }

    private List<EKCalendarData> Offered() => [.. kit.Calendars().Where(IosEventMapping.Offered)];

    /// <summary>The calendars with the events of the default window (two years back, five ahead): what a new folder would back up.</summary>
    public IReadOnlyList<CalendarInfo> Calendars()
    {
        Need();
        var calendars = Offered();
        var from = _now() - CollectionLimits.DefaultPastDays * 86400000L;
        var counts = Occurrences([.. calendars.Select(c => c.Id)], from, Until)
            .GroupBy(o => (o.CalendarId, o.ExternalId is { Length: > 0 } x ? x : o.EventId))
            .GroupBy(g => g.Key.CalendarId).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return [.. calendars.Select(c => IosEventMapping.Calendar(c, counts.GetValueOrDefault(c.Id))).OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    public IReadOnlyList<CalendarEvent> Events(IReadOnlySet<string>? calendarIds = null, long? since = null)
    {
        Need();
        var ids = Offered().Select(c => c.Id).Where(id => calendarIds is null || calendarIds.Contains(id)).ToList();
        if (ids.Count == 0) return [];
        var from = since ?? 0;
        var to = Until;
        var cache = new Dictionary<string, EKEventData?>(StringComparer.Ordinal);
        return IosEventMapping.Events(Occurrences(ids, from, to), id => cache.TryGetValue(id, out var e) ? e : cache[id] = kit.Event(id), Device, from, to, log);
    }

    /// <summary>
    /// Adds the series (or event) with its rule and alarms, then each exception: a cancelled one, and each EXDATE, removes that
    /// occurrence; a changed one changes it (title, place, notes, times, alarms). Attendees cannot be written by EventKit. Never changes
    /// or deletes another event; what of the series could not be restored is reported after it is in.
    /// </summary>
    public Task<string> InsertAsync(CalendarInfo calendar, CalendarEvent e, CancellationToken ct) => Task.Run(() =>
    {
        if (!CanWrite) throw new UnauthorizedAccessException("ASync may not add events.");
        var device = Device;
        var data = IosEventMapping.ToEventKit(e, device);
        var id = kit.Add(data, calendar.Id);
        var problems = new List<string>();
        if (e.RDate is { Length: > 0 }) problems.Add("its extra dates (RDATE), which iOS cannot store");
        if (e.ExRule is { Length: > 0 }) problems.Add("its exclusion rule (EXRULE), which iOS cannot store");
        if (e.Recurring && id.Length > 0)
        {
            var zone = e.AllDay ? TimeZoneInfo.Utc : IosEventMapping.Zone(e.TimeZone);
            long Occurrence(long original) => e.AllDay ? IosEventMapping.DeviceMidnight(original, device) : original;
            foreach (var date in IosEventMapping.Dates(e.ExDate, zone))
            {
                ct.ThrowIfCancellationRequested();
                if (!kit.ChangeOccurrence(id, calendar.Id, Occurrence(date), e.AllDay, null)) problems.Add($"no occurrence at {Show(date, e.AllDay)} to remove");
            }
            foreach (var x in e.Exceptions.Where(x => x.OriginalStart is not null).OrderBy(x => x.OriginalStart))
            {
                ct.ThrowIfCancellationRequested();
                var changes = x.Status == "CANCELLED" ? null : IosEventMapping.ToEventKit(x with { RRule = null, CalendarId = calendar.Id }, device);
                if (!kit.ChangeOccurrence(id, calendar.Id, Occurrence(x.OriginalStart!.Value), e.AllDay, changes))
                    problems.Add($"the occurrence of {Show(x.OriginalStart.Value, e.AllDay)} is not in the series");
            }
        }
        if (problems.Count > 0)
        {
            log?.Invoke($"\"{e.Title}\" restored without {string.Join("; ", problems)}");
            throw new IOException($"\"{e.Title}\" was added, but not {problems[0]}{(problems.Count > 1 ? $" (and {problems.Count - 1} more)" : "")}.");
        }
        return id;
    }, ct);

    private static string Show(long ms, bool allDay) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString(allDay ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture);
}
