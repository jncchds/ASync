using ASync.Contracts;

namespace ASync.Client.Core.Collections;

// ── Calendars, platform-neutral (task 20) ─────────────────────────────────────
//
// What a phone's calendar provider gives (Android's CalendarContract; iOS's EventKit since task 24) in a shape close to iCalendar (RFC 5545):
// times as instants plus the zone they were made in, recurrence rules as iCalendar text, exceptions with the master. ICalendar.Write
// is a plain mapping. See AGENTS.md "Extra data sources".

/// <summary>
/// A calendar. <paramref name="Id"/> is stable on the device (Android: <c>_id</c>); <paramref name="ShortId"/> goes into the snapshot's
/// directory name. <paramref name="ReadOnly"/>: one ASync cannot add events to (holidays, contacts' birthdays).
/// </summary>
public sealed record CalendarInfo(string Id, string ShortId, string Name, string AccountName, string AccountType, string? Color, int Events,
    string? TimeZone = null, bool ReadOnly = false);

public sealed record Attendee(string? Email, string? Name = null, string Role = "REQ-PARTICIPANT", string Status = "NEEDS-ACTION",
    string Type = "INDIVIDUAL");

/// <summary>A reminder: <paramref name="MinutesBefore"/> the start (negative: after it). <paramref name="Method"/>: <c>display</c>, <c>email</c>, <c>sms</c>, <c>alarm</c>.</summary>
public sealed record Alarm(int MinutesBefore, string Method = "display");

/// <summary>
/// An event (Android: a row of Events). Times are Unix milliseconds, UTC; an all-day event's are midnight UTC of its dates (how
/// Android stores them, and how iCalendar DATE values are read here). <see cref="TimeZone"/> is the IANA zone the times are shown in
/// (a recurring event repeats at the same local time in it). A recurring master carries its <see cref="Exceptions"/>: changed or
/// cancelled occurrences, each with <see cref="OriginalStart"/>.
/// </summary>
public sealed record CalendarEvent
{
    public required string Id { get; init; }
    public required string ShortId { get; init; }
    public required string CalendarId { get; init; }
    /// <summary>The iCalendar UID where the event has one (Android: UID_2445, set by invitations and by ASync's restores).</summary>
    public string? Uid { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Location { get; init; }
    public long Start { get; init; }
    /// <summary>Non-recurring events: the end. Recurring ones use <see cref="Duration"/> (Android keeps no end for them).</summary>
    public long? End { get; init; }
    /// <summary>RFC 5545 duration (<c>PT1H</c>, <c>P1D</c>): recurring events.</summary>
    public string? Duration { get; init; }
    public bool AllDay { get; init; }
    public string? TimeZone { get; init; }
    public string? EndTimeZone { get; init; }
    /// <summary>RFC 5545 RRULE value (<c>FREQ=WEEKLY;BYDAY=MO</c>), as the provider stores it.</summary>
    public string? RRule { get; init; }
    public string? ExRule { get; init; }
    /// <summary>Extra and excluded dates as the provider stores them: comma-separated iCalendar date(-time)s, optionally <c>&lt;tzid&gt;;</c> first.</summary>
    public string? RDate { get; init; }
    public string? ExDate { get; init; }
    /// <summary><c>CONFIRMED</c>, <c>TENTATIVE</c>, <c>CANCELLED</c> or null.</summary>
    public string? Status { get; init; }
    /// <summary><c>OPAQUE</c> (busy) or <c>TRANSPARENT</c> (free).</summary>
    public string? Transparency { get; init; }
    /// <summary><c>PUBLIC</c>, <c>PRIVATE</c>, <c>CONFIDENTIAL</c> or null (the calendar's default).</summary>
    public string? Class { get; init; }
    public string? Organizer { get; init; }
    public IReadOnlyList<Attendee> Attendees { get; init; } = [];
    public IReadOnlyList<Alarm> Alarms { get; init; } = [];
    /// <summary>The event's own colour, <c>#rrggbb</c> (null: the calendar's).</summary>
    public string? Color { get; init; }
    public long? Created { get; init; }
    public long? LastModified { get; init; }
    /// <summary>Exceptions only: the start of the occurrence this one replaces.</summary>
    public long? OriginalStart { get; init; }
    /// <summary>Recurring masters: when the last occurrence ends (Android: LAST_DATE); null = it never ends.</summary>
    public long? LastEnd { get; init; }
    public IReadOnlyList<CalendarEvent> Exceptions { get; init; } = [];

    public bool Recurring => RRule is { Length: > 0 } || RDate is { Length: > 0 };
}

/// <summary>
/// A device's calendars: Android's CalendarContract (<c>AndroidCalendarSource</c>), iOS's EventKit (<c>IosCalendarSource</c>), a fake in the tests. Same
/// threading rules as <see cref="IContactsSource"/>.
/// </summary>
public interface ICalendarSource
{
    /// <summary>Whether ASync may read calendars now (READ_CALENDAR).</summary>
    MediaAccess Access { get; }

    /// <summary>Whether ASync may also write them (WRITE_CALENDAR: asked only for a restore).</summary>
    bool CanWrite { get; }

    IReadOnlyList<CalendarInfo> Calendars();

    /// <summary>
    /// The events of the calendars (all when null), masters with their exceptions, that end on or after <paramref name="since"/>
    /// (Unix ms; null = all): a recurring one while it still has occurrences then. Deleted ones are not listed.
    /// </summary>
    IReadOnlyList<CalendarEvent> Events(IReadOnlySet<string>? calendarIds = null, long? since = null);

    /// <summary>Adds an event (with its exceptions) to a calendar; returns its new id. Never changes or deletes others.</summary>
    Task<string> InsertAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken ct);
}
