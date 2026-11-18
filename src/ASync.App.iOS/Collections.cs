using ASync.Agent;
using ASync.App.Services;
using ASync.Client.Core.Background;
using ASync.Contracts;
using Contacts;
using EventKit;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace ASync.App.iOS;

/// <summary>
/// The Contacts framework for the contacts backup (task 24), as thin as it can be: listings and save requests, no decisions (those are
/// <see cref="IosContactsSource"/> and <see cref="IosContactMapping"/> in Shared/, tested on Linux). Never fetches the note: that key
/// needs Apple's com.apple.developer.contacts.notes entitlement (fetching it without throws). AGENTS.md "iOS contacts and calendars".
/// </summary>
public sealed class ContactsBridge : ICNContacts
{
    private readonly CNContactStore _store = new();

    public CNAuth Authorization => (CNAuth)(int)CNContactStore.GetAuthorizationStatus(CNEntityType.Contacts);

    public string? DefaultContainerId => _store.DefaultContainerIdentifier;

    private static INativeObject[] Keys(bool image) =>
    [
        CNContactKey.Identifier, CNContactKey.Type, CNContactKey.NamePrefix, CNContactKey.GivenName, CNContactKey.MiddleName, CNContactKey.FamilyName,
        CNContactKey.NameSuffix, CNContactKey.Nickname, CNContactKey.PhoneticGivenName, CNContactKey.PhoneticMiddleName, CNContactKey.PhoneticFamilyName,
        CNContactKey.OrganizationName, CNContactKey.DepartmentName, CNContactKey.JobTitle, CNContactKey.PhoneNumbers, CNContactKey.EmailAddresses,
        CNContactKey.UrlAddresses, CNContactKey.PostalAddresses, CNContactKey.Birthday, CNContactKey.Dates, CNContactKey.Relations,
        CNContactKey.InstantMessageAddresses, CNContactKey.SocialProfiles, CNContactKey.ThumbnailImageData,
        CNContactFormatter.GetDescriptorForRequiredKeys(CNContactFormatterStyle.FullName),
        .. image ? new INativeObject[] { CNContactKey.ImageData } : [],
    ];

    private static string? S(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static CNDate? Date(NSDateComponents? d)
    {
        if (d is null || d.Month == NSDateComponents.Undefined || d.Day == NSDateComponents.Undefined) return null;
        return new CNDate(d.Year == NSDateComponents.Undefined ? null : (int)d.Year, (int)d.Month, (int)d.Day);
    }

    private static NSDateComponents Components(CNDate d)
    {
        var c = new NSDateComponents { Month = d.Month, Day = d.Day };
        if (d.Year is { } y) c.Year = y;
        return c;
    }

    private static CNContactData Data(CNContact c, string containerId) => new()
    {
        Id = c.Identifier, ContainerId = containerId, IsOrganization = c.ContactType == CNContactType.Organization,
        DisplayName = S(CNContactFormatter.GetStringFrom(c, CNContactFormatterStyle.FullName)),
        NamePrefix = S(c.NamePrefix), GivenName = S(c.GivenName), MiddleName = S(c.MiddleName), FamilyName = S(c.FamilyName), NameSuffix = S(c.NameSuffix),
        Nickname = S(c.Nickname), PhoneticGivenName = S(c.PhoneticGivenName), PhoneticMiddleName = S(c.PhoneticMiddleName),
        PhoneticFamilyName = S(c.PhoneticFamilyName), OrganizationName = S(c.OrganizationName), DepartmentName = S(c.DepartmentName), JobTitle = S(c.JobTitle),
        Phones = [.. c.PhoneNumbers.Select(p => new CNLabeled<string>(p.Label, p.Value.StringValue))],
        Emails = [.. c.EmailAddresses.Select(e => new CNLabeled<string>(e.Label, e.Value.ToString()))],
        Urls = [.. c.UrlAddresses.Select(u => new CNLabeled<string>(u.Label, u.Value.ToString()))],
        Addresses = [.. c.PostalAddresses.Select(a => new CNLabeled<CNAddress>(a.Label, new CNAddress(S(a.Value.Street), S(a.Value.SubLocality), S(a.Value.City),
            S(a.Value.SubAdministrativeArea), S(a.Value.State), S(a.Value.PostalCode), S(a.Value.Country), S(a.Value.IsoCountryCode))))],
        Birthday = Date(c.Birthday),
        Dates = [.. c.Dates.Select(d => Date(d.Value) is { } date ? new CNLabeled<CNDate>(d.Label, date) : null).OfType<CNLabeled<CNDate>>()],
        Relations = [.. c.ContactRelations.Select(r => new CNLabeled<string>(r.Label, r.Value.Name))],
        InstantMessages = [.. c.InstantMessageAddresses.Select(m => new CNLabeled<CNInstantMessage>(m.Label, new CNInstantMessage(m.Value.Username ?? "", m.Value.Service ?? "")))],
        SocialProfiles = [.. c.SocialProfiles.Select(s => new CNLabeled<CNSocial>(s.Label, new CNSocial(S(s.Value.UrlString), S(s.Value.Username), S(s.Value.UserIdentifier), s.Value.Service ?? "")))],
        ImageData = c.IsKeyAvailable(CNContactKey.ImageData) && c.ImageData is { Length: > 0 } image ? image.ToArray() : null,
        ThumbnailData = c.ThumbnailImageData is { Length: > 0 } thumb ? thumb.ToArray() : null,
    };

    public IReadOnlyList<CNContainerData> Containers()
    {
        var containers = _store.GetContainers(null, out var error) ?? throw Failed(error);
        return [.. containers.Select(c => new CNContainerData(c.Identifier, c.Name ?? "", (CNContainerKind)(int)c.ContainerType))];
    }

    private static IOException Failed(NSError? error) => new(error?.LocalizedDescription ?? "The contacts could not be read.");

    private List<CNContact> Fetch(NSPredicate predicate, INativeObject[] keys)
    {
        var list = new List<CNContact>();
        using var request = new CNContactFetchRequest(keys) { Predicate = predicate, UnifyResults = false };
        if (!_store.EnumerateContacts(request, out var error, (CNContact c, ref bool _) => list.Add(c))) throw Failed(error);
        return list;
    }

    public IReadOnlyList<string> ContactIds(string containerId) =>
        [.. Fetch(CNContact.GetPredicateForContactsInContainer(containerId), [CNContactKey.Identifier]).Select(c => c.Identifier)];

    public IReadOnlyList<CNContactData> Contacts(string containerId) =>
        [.. Fetch(CNContact.GetPredicateForContactsInContainer(containerId), Keys(image: false)).Select(c => Data(c, containerId))];

    public CNContactData? Read(string id, bool image)
    {
        var found = Fetch(CNContact.GetPredicateForContacts([id]), Keys(image)).FirstOrDefault();
        if (found is null) return null;
        var containers = _store.GetContainers(CNContainer.CreatePredicateForContainerOfContact(id), out _);
        return Data(found, containers?.FirstOrDefault()?.Identifier ?? "");
    }

    public IReadOnlyList<CNGroupData> Groups()
    {
        var result = new List<CNGroupData>();
        foreach (var container in _store.GetContainers(null, out _) ?? [])
            foreach (var g in _store.GetGroups(CNGroup.CreatePredicateForGroupsInContainer(container.Identifier), out _) ?? [])
                result.Add(new CNGroupData(g.Identifier, g.Name ?? "", container.Identifier,
                    [.. Fetch(CNContact.GetPredicateForContactsInGroup(g.Identifier), [CNContactKey.Identifier]).Select(c => c.Identifier)]));
        return result;
    }

    private void Save(CNSaveRequest request)
    {
        if (!_store.ExecuteSaveRequest(request, out var error)) throw new IOException(error?.LocalizedDescription ?? "The contacts did not take the change.");
    }

    public string Add(CNContactData c, string containerId)
    {
        using var contact = new CNMutableContact
        {
            ContactType = c.IsOrganization ? CNContactType.Organization : CNContactType.Person,
            NamePrefix = c.NamePrefix ?? "", GivenName = c.GivenName ?? "", MiddleName = c.MiddleName ?? "", FamilyName = c.FamilyName ?? "",
            NameSuffix = c.NameSuffix ?? "", Nickname = c.Nickname ?? "", PhoneticGivenName = c.PhoneticGivenName ?? "",
            PhoneticMiddleName = c.PhoneticMiddleName ?? "", PhoneticFamilyName = c.PhoneticFamilyName ?? "", OrganizationName = c.OrganizationName ?? "",
            DepartmentName = c.DepartmentName ?? "", JobTitle = c.JobTitle ?? "",
            PhoneNumbers = [.. c.Phones.Select(p => new CNLabeledValue<CNPhoneNumber>(p.Label, new CNPhoneNumber(p.Value)))],
            EmailAddresses = [.. c.Emails.Select(e => new CNLabeledValue<NSString>(e.Label, new NSString(e.Value)))],
            UrlAddresses = [.. c.Urls.Select(u => new CNLabeledValue<NSString>(u.Label, new NSString(u.Value)))],
            PostalAddresses = [.. c.Addresses.Select(a => new CNLabeledValue<CNPostalAddress>(a.Label, new CNMutablePostalAddress
            {
                Street = a.Value.Street ?? "", SubLocality = a.Value.SubLocality ?? "", City = a.Value.City ?? "",
                SubAdministrativeArea = a.Value.SubAdministrativeArea ?? "", State = a.Value.State ?? "", PostalCode = a.Value.PostalCode ?? "",
                Country = a.Value.Country ?? "", IsoCountryCode = a.Value.IsoCountryCode ?? "",
            }))],
            Dates = [.. c.Dates.Select(d => new CNLabeledValue<NSDateComponents>(d.Label, Components(d.Value)))],
            ContactRelations = [.. c.Relations.Select(r => new CNLabeledValue<CNContactRelation>(r.Label, new CNContactRelation(r.Value)))],
            InstantMessageAddresses = [.. c.InstantMessages.Select(m => new CNLabeledValue<CNInstantMessageAddress>(m.Label, new CNInstantMessageAddress(m.Value.Username, m.Value.Service)))],
            SocialProfiles = [.. c.SocialProfiles.Select(s => new CNLabeledValue<CNSocialProfile>(s.Label, new CNSocialProfile(s.Value.UrlString, s.Value.Username, s.Value.UserIdentifier, s.Value.Service)))],
        };
        if (c.Birthday is { } b) contact.Birthday = Components(b);
        if (c.ImageData is { Length: > 0 } image) contact.ImageData = NSData.FromArray(image);
        using var request = new CNSaveRequest();
        request.AddContact(contact, containerId);
        Save(request);
        return contact.Identifier;
    }

    public string AddGroup(string name, string containerId)
    {
        using var group = new CNMutableGroup { Name = name };
        using var request = new CNSaveRequest();
        request.AddGroup(group, containerId);
        Save(request);
        return group.Identifier;
    }

    public void AddMember(string groupId, string contactId)
    {
        var group = (_store.GetGroups(CNGroup.CreatePredicateForGroups([groupId]), out _) ?? []).FirstOrDefault()
                    ?? throw new IOException("The group is gone.");
        var contact = Fetch(CNContact.GetPredicateForContacts([contactId]), [CNContactKey.Identifier]).FirstOrDefault()
                      ?? throw new IOException("The contact is gone.");
        using var request = new CNSaveRequest();
        request.AddMember(contact, group);
        Save(request);
    }

    /// <summary>The framework's label constants (tests only: the smoke test checks the raw values <see cref="IosContactMapping"/> assumes).</summary>
    public static IReadOnlyDictionary<string, string> LabelConstants() => new Dictionary<string, string>
    {
        ["Home"] = CNLabelKey.Home, ["Work"] = CNLabelKey.Work, ["School"] = CNLabelKey.School, ["Other"] = CNLabelKey.Other,
        ["HomePage"] = CNLabelKey.UrlAddressHomePage, ["Anniversary"] = CNLabelKey.DateAnniversary, ["iCloud"] = CNLabelKey.EmailiCloud,
        ["Mobile"] = CNLabelPhoneNumberKey.Mobile, ["iPhone"] = CNLabelPhoneNumberKey.iPhone, ["Main"] = CNLabelPhoneNumberKey.Main,
        ["HomeFAX"] = CNLabelPhoneNumberKey.HomeFax, ["WorkFAX"] = CNLabelPhoneNumberKey.WorkFax, ["OtherFAX"] = CNLabelPhoneNumberKey.OtherFax,
        ["Pager"] = CNLabelPhoneNumberKey.Pager, ["AppleWatch"] = CNLabelPhoneNumberKey.AppleWatch,
        ["Father"] = CNLabelContactRelationKey.Father, ["Spouse"] = CNLabelContactRelationKey.Spouse, ["Son"] = CNLabelContactRelationKey.Son,
        ["Colleague"] = CNLabelContactRelationKey.Colleague,
    };
}

/// <summary>
/// EventKit for the calendars backup (task 24), as thin as it can be: calendars, occurrences, saves, no decisions (those are
/// <see cref="IosCalendarSource"/> and <see cref="IosEventMapping"/> in Shared/). One event store for the process.
/// </summary>
public sealed class EventKitBridge : IEventKit
{
    private readonly Lock _gate = new();
    private EKEventStore _store = new();

    /// <summary>After the permission changed: a store made before it may not see the calendars.</summary>
    public void Reset()
    {
        lock (_gate) _store = new EKEventStore();
    }

    private EKEventStore Store
    {
        get { lock (_gate) return _store; }
    }

    public EKEventStore Raw => Store;

    public EKAuth Authorization => (EKAuth)(int)EKEventStore.GetAuthorizationStatus(EKEntityType.Event);

    public string DeviceTimeZone => NSTimeZone.LocalTimeZone.Name;

    private static long Ms(NSDate d) => (long)Math.Round(d.SecondsSince1970 * 1000);
    private static long? MsOrNull(NSDate? d) => d is null ? null : (long)Math.Round(d.SecondsSince1970 * 1000);
    private static NSDate Date(long ms) => NSDate.FromTimeIntervalSince1970(ms / 1000.0);

    private static string? Hex(CoreGraphics.CGColor? color)
    {
        if (color?.Components is not { Length: >= 3 } c) return color?.Components is { Length: 2 } g ? Hex3(g[0], g[0], g[0]) : null;
        return Hex3(c[0], c[1], c[2]);
    }

    private static string Hex3(System.Runtime.InteropServices.NFloat r, System.Runtime.InteropServices.NFloat g, System.Runtime.InteropServices.NFloat b)
    {
        static int B(System.Runtime.InteropServices.NFloat v) => Math.Clamp((int)Math.Round((double)v * 255), 0, 255);
        return $"#{B(r):x2}{B(g):x2}{B(b):x2}";
    }

    public IReadOnlyList<EKCalendarData> Calendars() =>
        [.. Store.GetCalendars(EKEntityType.Event).Select(c => new EKCalendarData(c.CalendarIdentifier, c.Title ?? "", c.Source?.SourceIdentifier ?? "",
            c.Source?.Title ?? "", (EKSourceKind)(int)(c.Source?.SourceType ?? EKSourceType.Local), (EKCalendarKind)(int)c.Type, Hex(c.CGColor), c.AllowsContentModifications))];

    private static EKRule Rule(EKRecurrenceRule r) => new(
        (int)r.Frequency, (int)r.Interval, (int)r.FirstDayOfTheWeek,
        [.. (r.DaysOfTheWeek ?? []).Select(d => new EKDayOfWeek((int)d.DayOfTheWeek, (int)d.WeekNumber))],
        [.. (r.DaysOfTheMonth ?? []).Select(n => n.Int32Value)], [.. (r.MonthsOfTheYear ?? []).Select(n => n.Int32Value)],
        [.. (r.WeeksOfTheYear ?? []).Select(n => n.Int32Value)], [.. (r.DaysOfTheYear ?? []).Select(n => n.Int32Value)],
        [.. (r.SetPositions ?? []).Select(n => n.Int32Value)],
        r.RecurrenceEnd is { OccurrenceCount: > 0 } count ? (int)count.OccurrenceCount : null,
        r.RecurrenceEnd?.EndDate is { } end ? Ms(end) : null);

    private static EKParticipantData Participant(EKParticipant p) =>
        new(p.Url?.AbsoluteString, p.Name, (int)p.ParticipantRole, (int)p.ParticipantStatus, (int)p.ParticipantType);

    private static EKEventData Data(EKEvent e) => new()
    {
        EventId = e.EventIdentifier ?? "", ExternalId = e.CalendarItemExternalIdentifier, CalendarId = e.Calendar?.CalendarIdentifier ?? "",
        Title = e.Title, Location = e.Location, Notes = e.Notes, Start = Ms(e.StartDate), End = Ms(e.EndDate), AllDay = e.AllDay,
        TimeZone = e.TimeZone?.Name, OccurrenceDate = MsOrNull(e.OccurrenceDate), IsDetached = e.IsDetached,
        Rules = [.. (e.RecurrenceRules ?? []).Select(Rule)], Status = (int)e.Status, Availability = (int)e.Availability,
        Organizer = e.Organizer is { } o ? Participant(o) : null, Attendees = [.. (e.Attendees ?? []).Select(Participant)],
        Alarms = [.. (e.Alarms ?? []).Select(a => a.AbsoluteDate is { } at ? new EKAlarmData(null, Ms(at)) : new EKAlarmData(a.RelativeOffset))],
        Created = MsOrNull(e.CreationDate), LastModified = MsOrNull(e.LastModifiedDate),
    };

    private EKCalendar[] Resolve(IEnumerable<string> ids) => [.. ids.Select(id => Store.GetCalendar(id)).OfType<EKCalendar>()];

    public IReadOnlyList<EKEventData> Occurrences(IReadOnlyCollection<string> calendarIds, long from, long to)
    {
        var calendars = Resolve(calendarIds);
        if (calendars.Length == 0) return [];
        var store = Store;
        using var predicate = store.PredicateForEvents(Date(from), Date(to), calendars);
        return [.. store.EventsMatching(predicate).Select(Data)];
    }

    public EKEventData? Event(string eventId) => Store.EventFromIdentifier(eventId) is { } e ? Data(e) : null;

    private static EKRecurrenceRule Native(EKRule r)
    {
        var end = r.Count is { } count ? EKRecurrenceEnd.FromOccurrenceCount(count) : r.Until is { } until ? EKRecurrenceEnd.FromEndDate(Date(until)) : null;
        EKRecurrenceDayOfWeek[]? days = r.Days.Count == 0 ? null
            : [.. r.Days.Select(d => d.Week == 0 ? EKRecurrenceDayOfWeek.FromDay((EKWeekday)d.Day) : EKRecurrenceDayOfWeek.FromDay((EKWeekday)d.Day, d.Week))];
        static NSNumber[]? Numbers(IReadOnlyList<int> list) => list.Count == 0 ? null : [.. list.Select(NSNumber.FromInt32)];
        return new EKRecurrenceRule((EKRecurrenceFrequency)r.Frequency, r.Interval, days, Numbers(r.MonthDays), Numbers(r.Months), Numbers(r.Weeks),
            Numbers(r.YearDays), Numbers(r.Positions), end);
    }

    private static void Fill(EKEvent e, EKEventData d, bool alarms)
    {
        e.Title = d.Title ?? "";
        e.Location = d.Location;
        e.Notes = d.Notes;
        e.AllDay = d.AllDay;
        if (!d.AllDay && d.TimeZone is { Length: > 0 } tz) e.TimeZone = NSTimeZone.FromName(tz);
        e.StartDate = Date(d.Start);
        e.EndDate = Date(Math.Max(d.Start, d.End));
        if (d.Availability >= 0 && d.Availability <= 3) e.Availability = (EKEventAvailability)d.Availability;
        if (!alarms) return;
        foreach (var a in e.Alarms ?? []) e.RemoveAlarm(a);
        foreach (var a in d.Alarms)
            e.AddAlarm(a.RelativeSeconds is { } rel ? EKAlarm.FromTimeInterval(rel) : EKAlarm.FromDate(Date(a.Absolute ?? d.Start)));
    }

    public string Add(EKEventData d, string calendarId)
    {
        var store = Store;
        var calendar = store.GetCalendar(calendarId) ?? throw new IOException("The calendar is not on this iPhone any more.");
        var e = EKEvent.FromStore(store);
        e.Calendar = calendar;
        Fill(e, d, alarms: true);
        foreach (var r in d.Rules) e.AddRecurrenceRule(Native(r));
        if (!store.SaveEvent(e, EKSpan.ThisEvent, true, out var error))
            throw new IOException(error?.LocalizedDescription ?? "The calendar did not take the event.");
        return e.EventIdentifier ?? "";
    }

    public bool ChangeOccurrence(string eventId, string calendarId, long occurrenceDate, bool allDay, EKEventData? changes)
    {
        var store = Store;
        if (store.GetCalendar(calendarId) is not { } calendar) return false;
        const long Day = 86400000;
        using var predicate = store.PredicateForEvents(Date(occurrenceDate - Day - 1000), Date(occurrenceDate + 2 * Day), [calendar]);
        var target = store.EventsMatching(predicate).FirstOrDefault(e => e.EventIdentifier == eventId && e.OccurrenceDate is { } o
            && (allDay ? Math.Abs(Ms(o) - occurrenceDate) < 12 * 3600_000 : Math.Abs(Ms(o) - occurrenceDate) < 1000));
        if (target is null) return false;
        NSError? error;
        var ok = changes is null ? store.RemoveEvent(target, EKSpan.ThisEvent, true, out error) : Save(target, changes, out error);
        if (!ok) throw new IOException(error?.LocalizedDescription ?? "The calendar did not take the change.");
        return true;

        bool Save(EKEvent e, EKEventData c, out NSError? err)
        {
            Fill(e, c with { AllDay = allDay }, alarms: true);
            var saved = store.SaveEvent(e, EKSpan.ThisEvent, true, out var nsError);
            err = nsError;
            return saved;
        }
    }
}

/// <summary>
/// The permission to read (and, for a restore, add to) the contacts or calendars on an iPhone: one CNAuthorizationStatus for contacts
/// (Full, iOS 18's Limited = Partial), EventKit's full access for events (iOS 17+: requestFullAccessToEvents, NSCalendarsFullAccessUsageDescription;
/// iOS 15/16: requestAccess, NSCalendarsUsageDescription). Asked only from the Contacts or Calendars screen's Allow (as on Android); once
/// answered iOS asks no more, and Allow opens the app's page in Settings. Adding needs no second question on iOS.
/// </summary>
public sealed class IosCollectionAccess(FolderKind kind, Func<MediaAccess> access, Func<bool> canWrite, Func<bool> undetermined, Action<Action> request)
    : ICollectionAccess
{
    /// <summary>Tests only (<see cref="CollectionsAutomation"/>): what the screens show instead of the real permission; the agent reads the real one.</summary>
    internal MediaAccess? Forced;

    public FolderKind Kind => kind;
    public MediaAccess Access => Forced ?? access();
    public bool CanWrite => canWrite();

    public void RequestRead()
    {
        if (!undetermined())
        {
            OpenSettings(); // answered once: iOS does not ask again
            return;
        }
        request(NotifyChanged);
    }

    public void RequestWrite() => RequestRead();

    public void OpenSettings() => UIApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        if (NSUrl.FromString(UIApplication.OpenSettingsUrlString) is { } url)
            UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
    });

    public string? Note => kind == FolderKind.Contacts ? IosCollectionTexts.ContactsNote : IosCollectionTexts.CalendarsNote;

    public string? PartialText => kind == FolderKind.Contacts ? IosCollectionTexts.ContactsPartial : null;

    public event Action? Changed;

    public void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Console.Error.WriteLine($"ASync: a {kind} permission listener failed: {e.Message}"); }
        IosCollections.EnsureObservers();
    }
}

/// <summary>
/// The contacts and calendars backup's parts of the process (task 24): the sources, the permissions, and the change observers that
/// start the Contacts or Calendars folders when the address book or the calendars change while the app runs (CNContactStoreDidChange,
/// EKEventStoreChanged; debounced like the photos' observer: <see cref="PhotoChangeDebouncer"/>). In the background these folders run
/// with every other folder from task 22's BGTaskScheduler tasks.
/// </summary>
public static class IosCollections
{
    private static readonly Lock Gate = new();
    private static AgentRuntime? _runtime;
    private static NSObject? _contactsObserver, _calendarsObserver;
    private static PhotoChangeDebouncer? _contactsDebouncer, _calendarsDebouncer;

    public static ContactsBridge ContactsStore { get; private set; } = null!;
    public static EventKitBridge EventStore { get; private set; } = null!;
    public static IosContactsSource Contacts { get; private set; } = null!;
    public static IosCalendarSource Calendars { get; private set; } = null!;
    public static IosCollectionAccess ContactsAccess { get; private set; } = null!;
    public static IosCollectionAccess CalendarsAccess { get; private set; } = null!;

    /// <summary>The sources and the permissions, before the agent starts.</summary>
    public static void Create(string configDirectory)
    {
        ContactsStore = new ContactsBridge();
        EventStore = new EventKitBridge();
        Contacts = new IosContactsSource(ContactsStore, Path.Combine(configDirectory, "ios-contact-versions.json"));
        Calendars = new IosCalendarSource(EventStore, log: m => Console.WriteLine($"ASync: calendars: {m}"));
        ContactsAccess = new IosCollectionAccess(FolderKind.Contacts, () => Contacts.Access, () => Contacts.CanWrite,
            () => CNContactStore.GetAuthorizationStatus(CNEntityType.Contacts) == CNAuthorizationStatus.NotDetermined,
            done => new CNContactStore().RequestAccess(CNEntityType.Contacts, (_, _) => done()));
        CalendarsAccess = new IosCollectionAccess(FolderKind.Calendars, () => Calendars.Access, () => Calendars.CanWrite,
            () => EKEventStore.GetAuthorizationStatus(EKEntityType.Event) == EKAuthorizationStatus.NotDetermined,
            done =>
            {
                void Answered()
                {
                    EventStore.Reset();
                    done();
                }
                if (OperatingSystem.IsIOSVersionAtLeast(17)) EventStore.Raw.RequestFullAccessToEvents((_, _) => Answered());
                else RequestOld(Answered);
            });
    }

    [System.Runtime.Versioning.ObsoletedOSPlatform("ios17.0")]
    private static void RequestOld(Action done) => EventStore.Raw.RequestAccess(EKEntityType.Event, (_, _) => done());

    public static void Attach(AgentRuntime runtime)
    {
        _runtime = runtime;
        _contactsDebouncer = new PhotoChangeDebouncer(() => RunFolders(FolderKind.Contacts));
        _calendarsDebouncer = new PhotoChangeDebouncer(() => RunFolders(FolderKind.Calendars));
        EnsureObservers();
    }

    /// <summary>How many debounced change batches started the Contacts / Calendars folders (the smoke test reads them).</summary>
    public static int ContactsTriggered => _contactsDebouncer?.Fired ?? 0;
    public static int CalendarsTriggered => _calendarsDebouncer?.Fired ?? 0;

    /// <summary>Registers the change observers once ASync may read the contacts or calendars (no question is asked by registering).</summary>
    public static void EnsureObservers()
    {
        lock (Gate)
        {
            if (_runtime is null) return;
            if (_contactsObserver is null && Contacts.Access is MediaAccess.Full or MediaAccess.Partial)
                _contactsObserver = NSNotificationCenter.DefaultCenter.AddObserver(CNContactStore.NotificationDidChange, _ => _contactsDebouncer?.Changed());
            if (_calendarsObserver is null && Calendars.Access is MediaAccess.Full)
                _calendarsObserver = NSNotificationCenter.DefaultCenter.AddObserver(EKEventStore.ChangedNotification, _ => _calendarsDebouncer?.Changed());
        }
    }

    private static void RunFolders(FolderKind kind)
    {
        if (_runtime is not { FoldersLoaded: true } runtime) return;
        var ids = BackgroundPlanner.FoldersOfKind(runtime.Folders.Select(f => f.Folder), kind);
        Console.WriteLine($"ASync: {kind} changed: backing up {ids.Count} {kind} folder(s).");
        foreach (var id in ids) runtime.Trigger(id, kind == FolderKind.Contacts ? "contacts changed" : "calendars changed");
    }

    /// <summary>The app came to the front: a permission may have changed in Settings.</summary>
    public static void AppActive()
    {
        ContactsAccess.NotifyChanged();
        CalendarsAccess.NotifyChanged();
    }
}
