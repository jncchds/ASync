using System.Collections.Concurrent;
using ASync.Client.Core.Collections;
using ASync.Contracts;

namespace ASync.Tests.Client;

/// <summary>An address book in memory (task 20): accounts, contacts with versions, and counters for what was read and inserted.</summary>
public sealed class FakeContactsSource : IContactsSource
{
    private readonly ConcurrentDictionary<string, (Contact Contact, int Version, long Modified)> _contacts = new();
    private readonly List<ContactAccount> _accounts = [];
    private int _next = 100;

    public MediaAccess Access { get; set; } = MediaAccess.Full;
    public bool CanWrite { get; set; } = true;
    /// <summary>Full reads (with the photo) per contact id: what a backup generated.</summary>
    public ConcurrentDictionary<string, int> Reads { get; } = new();
    public List<(string AccountId, Contact Contact)> Inserted { get; } = [];

    public ContactAccount Account(string name, string type = "com.google", string label = "Google", bool readOnly = false)
    {
        var id = $"{type}:{name}";
        var a = new ContactAccount(id, CollectionLayout.HashId(id), name, type, label, 0, readOnly);
        _accounts.Add(a);
        return a;
    }

    public Contact Add(ContactAccount account, Contact contact)
    {
        var n = Interlocked.Increment(ref _next);
        var c = contact with { Id = $"fake:raw:{n}", ShortId = n.ToString(System.Globalization.CultureInfo.InvariantCulture), AccountId = account.Id };
        _contacts[c.Id] = (c, 1, 1_700_000_000_000 + n);
        return c;
    }

    public static Contact New(string name, string? phone = null, string? email = null) => new()
    {
        Id = "", ShortId = "", AccountId = "", DisplayName = name,
        Name = new StructuredName(Family: name.Split(' ').Last(), Given: name.Split(' ').First()),
        Phones = phone is null ? [] : [new LabeledValue(phone, "cell")],
        Emails = email is null ? [] : [new LabeledValue(email, "home")],
    };

    public void Update(Contact contact)
    {
        var (_, version, modified) = _contacts[contact.Id];
        _contacts[contact.Id] = (contact, version + 1, modified + 1000);
    }

    public void Delete(string id) => _contacts.TryRemove(id, out _);

    public void DeleteAccountContacts(string accountId)
    {
        foreach (var id in _contacts.Where(c => c.Value.Contact.AccountId == accountId).Select(c => c.Key).ToList()) _contacts.TryRemove(id, out _);
    }

    public IReadOnlyList<ContactAccount> Accounts() =>
        [.. _accounts.Select(a => a with { Contacts = _contacts.Values.Count(c => c.Contact.AccountId == a.Id) })];

    public IReadOnlyList<ContactSummary> Contacts(IReadOnlySet<string>? accountIds = null) =>
        [.. _contacts.Values.Where(c => accountIds is null || accountIds.Contains(c.Contact.AccountId))
            .Select(c => new ContactSummary(c.Contact.Id, c.Contact.ShortId, c.Contact.AccountId, VCard.FormattedName(c.Contact),
                c.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Modified))];

    public Contact? Read(string id, bool withPhoto = true)
    {
        if (!_contacts.TryGetValue(id, out var c)) return null;
        if (withPhoto) Reads.AddOrUpdate(id, 1, (_, n) => n + 1);
        return withPhoto ? c.Contact : c.Contact with { Photo = null };
    }

    public Task<string> InsertAsync(ContactAccount account, Contact contact, CancellationToken ct)
    {
        Assert.True(CanWrite, "a restore must not write without the permission");
        lock (Inserted) Inserted.Add((account.Id, contact));
        return Task.FromResult(Add(account, contact).Id);
    }
}

/// <summary>Calendars in memory (task 20): events with exceptions, the "since" window as Android applies it (LAST_DATE).</summary>
public sealed class FakeCalendarSource : ICalendarSource
{
    private readonly ConcurrentDictionary<string, CalendarEvent> _events = new();
    private readonly List<CalendarInfo> _calendars = [];
    private int _next = 500;

    public MediaAccess Access { get; set; } = MediaAccess.Full;
    public bool CanWrite { get; set; } = true;
    public List<(string CalendarId, CalendarEvent Event)> Inserted { get; } = [];

    public CalendarInfo Calendar(string name, string account = "me@example.com", string color = "#3366cc", bool readOnly = false)
    {
        var id = (_calendars.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var c = new CalendarInfo(id, id, name, account, "com.google", color, 0, "Europe/Kyiv", readOnly);
        _calendars.Add(c);
        return c;
    }

    public CalendarEvent Add(CalendarInfo calendar, CalendarEvent e)
    {
        var n = Interlocked.Increment(ref _next);
        var ev = e with
        {
            Id = $"fake:event:{n}", ShortId = n.ToString(System.Globalization.CultureInfo.InvariantCulture), CalendarId = calendar.Id,
            Exceptions = [.. e.Exceptions.Select((x, i) => x with { Id = $"fake:event:{n}.{i}", ShortId = $"{n}-{i}", CalendarId = calendar.Id })],
        };
        _events[ev.Id] = ev;
        return ev;
    }

    public static CalendarEvent New(string title, DateTime startUtc, TimeSpan length, string? tz = "Europe/Kyiv") => new()
    {
        Id = "", ShortId = "", CalendarId = "", Title = title, Start = Ms(startUtc), End = Ms(startUtc + length), TimeZone = tz,
    };

    public static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public void Update(CalendarEvent e) => _events[e.Id] = e;

    public void Wipe(string calendarId)
    {
        foreach (var id in _events.Where(e => e.Value.CalendarId == calendarId).Select(e => e.Key).ToList()) _events.TryRemove(id, out _);
    }

    public IReadOnlyList<CalendarInfo> Calendars() =>
        [.. _calendars.Select(c => c with { Events = _events.Values.Count(e => e.CalendarId == c.Id) })];

    public IReadOnlyList<CalendarEvent> Events(IReadOnlySet<string>? calendarIds = null, long? since = null) =>
        [.. _events.Values.Where(e => calendarIds is null || calendarIds.Contains(e.CalendarId))
            .Where(e => since is not { } s || (e.Recurring ? e.LastEnd is null || e.LastEnd >= s : (e.End ?? e.Start) >= s))];

    public Task<string> InsertAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken ct)
    {
        Assert.True(CanWrite, "a restore must not write without the permission");
        lock (Inserted) Inserted.Add((calendar.Id, calendarEvent));
        return Task.FromResult(Add(calendar, calendarEvent).Id);
    }
}
