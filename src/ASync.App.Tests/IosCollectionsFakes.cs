using ASync.App.iOS;

namespace ASync.App.Tests;

/// <summary>The Contacts framework as <see cref="ICNContacts"/> for the tests: containers, contacts, groups, and the image reads counted.</summary>
internal sealed class FakeContacts : ICNContacts
{
    private readonly Lock _gate = new();
    private int _next;
    public CNAuth Authorization { get; set; } = CNAuth.Authorized;
    public List<CNContainerData> ContainerList { get; } = [];
    public Dictionary<string, CNContactData> ById { get; } = new(StringComparer.Ordinal);
    public List<(string Id, string Name, string ContainerId, List<string> Members)> GroupList { get; } = [];
    public int ImageReads;
    public int Adds;

    public string? DefaultContainerId => ContainerList.FirstOrDefault()?.Id;

    public IReadOnlyList<CNContainerData> Containers()
    {
        lock (_gate) return [.. ContainerList];
    }

    public IReadOnlyList<string> ContactIds(string containerId)
    {
        lock (_gate) return [.. ById.Values.Where(c => c.ContainerId == containerId).Select(c => c.Id)];
    }

    public IReadOnlyList<CNContactData> Contacts(string containerId)
    {
        lock (_gate) return [.. ById.Values.Where(c => c.ContainerId == containerId).OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => c with { ImageData = null })];
    }

    public CNContactData? Read(string id, bool image)
    {
        lock (_gate)
        {
            if (!ById.TryGetValue(id, out var c)) return null;
            if (image && c.ImageData is not null) ImageReads++;
            return image ? c : c with { ImageData = null };
        }
    }

    public IReadOnlyList<CNGroupData> Groups()
    {
        lock (_gate) return [.. GroupList.Select(g => new CNGroupData(g.Id, g.Name, g.ContainerId, [.. g.Members]))];
    }

    public string Add(CNContactData contact, string containerId)
    {
        lock (_gate)
        {
            if (ContainerList.All(c => c.Id != containerId)) throw new IOException("no such container");
            var id = $"NEW-{++_next}:ABPerson";
            Adds++;
            // The framework makes the thumbnail from the image.
            ById[id] = contact with { Id = id, ContainerId = containerId, ThumbnailData = Thumb(contact.ImageData) };
            return id;
        }
    }

    public string AddGroup(string name, string containerId)
    {
        lock (_gate)
        {
            var id = $"GROUP-{++_next}";
            GroupList.Add((id, name, containerId, []));
            return id;
        }
    }

    public void AddMember(string groupId, string contactId)
    {
        lock (_gate) GroupList.Single(g => g.Id == groupId).Members.Add(contactId);
    }

    public CNContactData Put(CNContactData c)
    {
        lock (_gate)
        {
            var stored = c with { ThumbnailData = c.ImageData is { } ? Thumb(c.ImageData) : c.ThumbnailData };
            ById[c.Id] = stored;
            return stored;
        }
    }

    /// <summary>The framework makes the thumbnail from the image: another image, another thumbnail.</summary>
    private static byte[]? Thumb(byte[]? image) => image is null ? null : System.Security.Cryptography.SHA256.HashData(image)[..8];

    public void Wipe(string containerId)
    {
        lock (_gate)
        {
            foreach (var id in ById.Values.Where(c => c.ContainerId == containerId).Select(c => c.Id).ToList()) ById.Remove(id);
            GroupList.RemoveAll(g => g.ContainerId == containerId);
        }
    }
}

/// <summary>
/// EventKit as <see cref="IEventKit"/> for the tests: single events and daily or weekly series (by wall time in their zone, COUNT or
/// UNTIL), with removed and detached occurrences, listed as EventKit lists them: occurrence by occurrence, overlapping the window.
/// </summary>
internal sealed class FakeEventKit : IEventKit
{
    public sealed class Item
    {
        public required EKEventData Master;
        public HashSet<long> Removed { get; } = [];
        public Dictionary<long, EKEventData> Detached { get; } = [];
        /// <summary>A detached occurrence listed under an identifier of its own (seen with some Exchange calendars).</summary>
        public string? DetachedId;
    }

    private int _next;
    public EKAuth Authorization { get; set; } = EKAuth.FullAccess;
    public string DeviceTimeZone { get; set; } = "Europe/Kyiv";
    public List<EKCalendarData> CalendarList { get; } = [];
    public List<Item> Items { get; } = [];
    public int OccurrenceCalls;

    public IReadOnlyList<EKCalendarData> Calendars() => [.. CalendarList];

    private TimeZoneInfo ZoneOf(EKEventData e) => IosEventMapping.Zone(e.AllDay ? DeviceTimeZone : e.TimeZone ?? DeviceTimeZone);

    /// <summary>Every original start of an item (a single event: its start), up to <paramref name="to"/>.</summary>
    private IEnumerable<long> Starts(Item item, long to)
    {
        var m = item.Master;
        if (m.Rules.Count == 0)
        {
            yield return m.Start;
            yield break;
        }
        var rule = m.Rules[0];
        var zone = ZoneOf(m);
        var local = IosRecurrence.Local(m.Start, zone);
        var step = rule.Frequency switch { 0 => 1, 1 => 7, _ => throw new NotSupportedException("the fake repeats daily or weekly") } * rule.Interval;
        for (var k = 0; k < 5000; k++)
        {
            if (rule.Count is { } c && k >= c) yield break;
            var t = IosRecurrence.Utc(local.AddDays((double)k * step), zone);
            if (rule.Until is { } u && t > u) yield break;
            if (t >= to) yield break;
            yield return t;
        }
    }

    private EKEventData Occurrence(Item item, long original)
    {
        var m = item.Master;
        if (item.Detached.TryGetValue(original, out var d))
            return d with { EventId = item.DetachedId ?? m.EventId, ExternalId = m.ExternalId, CalendarId = m.CalendarId, OccurrenceDate = original, IsDetached = true, Rules = [] };
        var length = m.End - m.Start;
        return m with { Start = original, End = original + length, OccurrenceDate = original };
    }

    public IReadOnlyList<EKEventData> Occurrences(IReadOnlyCollection<string> calendarIds, long from, long to)
    {
        if (to - from > 4L * 366 * 86400000) throw new InvalidOperationException("EventKit answers at most four years");
        OccurrenceCalls++;
        var list = new List<EKEventData>();
        foreach (var item in Items.Where(i => calendarIds.Contains(i.Master.CalendarId)))
            foreach (var original in Starts(item, to + 86400000))
            {
                if (item.Removed.Contains(original)) continue;
                var o = Occurrence(item, original);
                if (o.Start < to && o.End > from) list.Add(o);
            }
        return list;
    }

    public EKEventData? Event(string eventId)
    {
        var item = Items.FirstOrDefault(i => i.Master.EventId == eventId);
        if (item is null) return null;
        var first = Starts(item, long.MaxValue).First();
        return Occurrence(item, first);
    }

    public string Add(EKEventData e, string calendarId)
    {
        var id = $"EV-{++_next}";
        Items.Add(new Item { Master = e with { EventId = id, ExternalId = $"EXT-{_next}", CalendarId = calendarId, LastModified = 1_800_000_000_000 } });
        return id;
    }

    public bool ChangeOccurrence(string eventId, string calendarId, long occurrenceDate, bool allDay, EKEventData? changes)
    {
        var item = Items.FirstOrDefault(i => i.Master.EventId == eventId && i.Master.CalendarId == calendarId);
        if (item is null) return false;
        var original = Starts(item, long.MaxValue).Cast<long?>().FirstOrDefault(t => allDay ? Math.Abs(t!.Value - occurrenceDate) < 12 * 3600_000 : t == occurrenceDate);
        if (original is null) return false;
        if (changes is null) item.Removed.Add(original.Value);
        else item.Detached[original.Value] = changes;
        return true;
    }

    public Item Put(EKEventData master)
    {
        var item = new Item { Master = master };
        Items.Add(item);
        return item;
    }
}
