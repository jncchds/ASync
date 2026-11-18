using System.Globalization;
using System.Text.Json;
using ASync.App.ViewModels;
using ASync.Contracts;
using Contacts;
using EventKit;
using Foundation;
using ObjCRuntime;

namespace ASync.App.iOS;

/// <summary>
/// Tests only: the contacts and calendars commands of <see cref="Automation"/> (task 24, <c>scripts/smoke-ios-collections.sh</c>). The
/// screens: <c>pim-dialog &lt;folder&gt;</c>, <c>pim-state</c>, <c>pim-allow</c>, <c>pim-ask contacts|calendars</c> (the system's question
/// without the screen), <c>pim-choose all|&lt;name&gt;,…</c>, <c>pim-save</c>, <c>pim-force contacts|calendars none|partial|off</c> (what the
/// screens and cards show, for a screenshot of a state simctl cannot grant), <c>pim-restore-choose|pim-restore-cancel &lt;folder&gt;</c>,
/// <c>pim-restore-confirm &lt;folder&gt; [target index]</c>. The agent: <c>pim contacts|calendars</c> (access, the list, the folders, the
/// restore, the change observers' runs), <c>pim-set &lt;folder&gt; all|&lt;id&gt;,… [days|all]</c>, <c>pim-restore &lt;folder&gt;
/// &lt;snapshot|-&gt; &lt;target|-&gt;</c>. The address book and calendars as a person (or another app) changes them, written through the
/// frameworks, not ASync's import: <c>pim-seed</c> (three contacts in the default container, a group, a photo; a calendar "ASync smoke"
/// with an all-day event, a weekly series with one occurrence moved and one deleted, an event with an alarm), <c>pim-edit</c> (Bob's
/// number changes), <c>pim-wipe contacts|calendar</c>, <c>pim-dump contacts|calendar</c>, <c>pim-labels</c> (the framework's label
/// constants).
/// </summary>
internal static class CollectionsAutomation
{
    public const string SmokeCalendar = "ASync smoke";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static FolderViewModel Folder(ShellViewModel shell, string id) =>
        shell.Dashboard.Folders.FirstOrDefault(f => f.Id.ToString(CultureInfo.InvariantCulture) == id)
        ?? throw new InvalidOperationException($"No folder {id} on the dashboard.");

    private static string State(ShellViewModel shell) => shell.CollectionsDialog is not { } d ? "closed" : JsonSerializer.Serialize(new
    {
        kind = d.Kind.ToString(), access = d.Access.ToString(), d.NeedsPermission, d.BackUpAll, past = d.SelectedPast.Days, d.ListText, d.Error,
        d.Explanation, note = d.PlatformNote, d.AccessText,
        choices = d.Choices.Select(c => new { c.Id, c.Name, c.Details, c.IsChecked }),
    }, Json);

    private static IosCollectionAccess Access(string kind) =>
        kind.StartsWith("cal", StringComparison.Ordinal) ? IosCollections.CalendarsAccess : IosCollections.ContactsAccess;

    public static async Task<string?> RunAsync(string op, string arg, ShellViewModel shell)
    {
        CollectionsViewModel Dialog() => shell.CollectionsDialog ?? throw new InvalidOperationException("The Contacts / Calendars screen is not open.");
        var runtime = IosAgent.Runtime;
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (op)
        {
            case "pim-dialog":
                await Folder(shell, arg).ChooseCollectionsCommand.ExecuteAsync(null);
                return State(shell);
            case "pim-state":
                return State(shell);
            case "pim-allow":
                Dialog().AllowCommand.Execute(null);
                return "asked";
            case "pim-ask":
                Access(arg).RequestRead();
                return "asked";
            case "pim-choose":
            {
                var d = Dialog();
                if (arg == "all") d.BackUpAll = true;
                else
                {
                    d.ChooseSome = true;
                    var names = arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    foreach (var c in d.Choices) c.IsChecked = names.Contains(c.Name);
                }
                return State(shell);
            }
            case "pim-save":
                await Dialog().SaveCommand.ExecuteAsync(null);
                return shell.CollectionsDialog is null ? "saved" : State(shell);
            case "pim-force":
            {
                var access = Access(parts[0]);
                access.Forced = parts[1] switch { "none" => MediaAccess.None, "partial" => MediaAccess.Partial, "full" => MediaAccess.Full, _ => null };
                access.NotifyChanged();
                return access.Access.ToString();
            }
            case "pim-restore-choose":
            {
                var card = Folder(shell, arg);
                await card.RestorePhotosCommand.ExecuteAsync(null);
                return JsonSerializer.Serialize(new { card.ChoosingRestore, choices = card.RestoreChoices.Select(c => c.Label), targets = card.RestoreTargets.Select(t => t.Label) }, Json);
            }
            case "pim-restore-confirm":
            {
                var card = Folder(shell, parts[0]);
                if (parts.Length > 1) card.SelectedTarget = card.RestoreTargets[int.Parse(parts[1], CultureInfo.InvariantCulture)];
                _ = card.ConfirmRestoreCommand.ExecuteAsync(null);
                return "started";
            }
            case "pim-restore-cancel":
                Folder(shell, arg).CancelRestoreCommand.Execute(null);
                return "ok";
            case "pim":
            {
                var kind = arg.StartsWith("cal", StringComparison.Ordinal) ? FolderKind.Calendars : FolderKind.Contacts;
                var list = await runtime.CollectionListAsync(kind, CancellationToken.None);
                var status = await runtime.StatusAsync(CancellationToken.None);
                return JsonSerializer.Serialize(new
                {
                    access = list.Access.ToString(), list.CanWrite, list.Items, list.Problem, collections = list.Collections, restore = status.CollectionRestore,
                    authorization = kind == FolderKind.Contacts
                        ? CNContactStore.GetAuthorizationStatus(CNEntityType.Contacts).ToString()
                        : EKEventStore.GetAuthorizationStatus(EKEntityType.Event).ToString(),
                    triggered = kind == FolderKind.Contacts ? IosCollections.ContactsTriggered : IosCollections.CalendarsTriggered,
                    folders = status.Folders.Where(f => f.Kind == kind).Select(f => new
                    {
                        id = f.FolderId, f.Name, selection = f.Collections?.Describe(kind), counts = f.CollectionCounts, last = f.LastResult?.Message,
                        ok = f.LastResult?.Ok, at = f.LastResult?.At,
                    }),
                }, Json);
            }
            case "pim-set":
            {
                var folderId = int.Parse(parts[0], CultureInfo.InvariantCulture);
                var kind = (await runtime.StatusAsync(CancellationToken.None)).Folders.Single(f => f.FolderId == folderId).Kind;
                int? days = parts.Length > 2 ? parts[2] == "all" ? null : int.Parse(parts[2], CultureInfo.InvariantCulture) : kind == FolderKind.Calendars ? CollectionLimits.DefaultPastDays : null;
                if (parts[1] == "all") return await runtime.SetCollectionsAsync(folderId, new CollectionSelectionDto(PastDays: days), CancellationToken.None);
                var list = await runtime.CollectionListAsync(kind, CancellationToken.None);
                var ids = parts[1].Split(',');
                return await runtime.SetCollectionsAsync(folderId, new CollectionSelectionDto(false,
                    [.. list.Collections.Where(c => ids.Contains(c.Id)).Select(c => new CollectionRef(c.Id, c.Name))], days), CancellationToken.None);
            }
            case "pim-restore":
                return await runtime.RestoreCollectionsAsync(int.Parse(parts[0], CultureInfo.InvariantCulture), parts[1] == "-" ? null : parts[1],
                    parts.Length < 3 || parts[2] == "-" ? null : parts[2], CancellationToken.None);
            case "pim-seed":
                return await Task.Run(Seed);
            case "pim-edit":
                return await Task.Run(Edit);
            case "pim-wipe":
                return await Task.Run(() => arg.StartsWith("cal", StringComparison.Ordinal) ? WipeCalendar() : WipeContacts());
            case "pim-dump":
                return await Task.Run(() => arg.StartsWith("cal", StringComparison.Ordinal) ? DumpCalendar() : DumpContacts());
            case "pim-labels":
                return JsonSerializer.Serialize(ContactsBridge.LabelConstants(), Json);
            default:
                return null;
        }
    }

    // ── The address book and calendars, as a person changes them ──────────────

    private static readonly CNContactStore Contacts = new();

    /// <summary>An 8×8 PNG (a plum square): the photo of a seeded contact.</summary>
    private static readonly byte[] Photo = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAFklEQVR4nGOImZFCFGIYVTiqkK4KASuQ6UFXcysBAAAAAElFTkSuQmCC");

    private static void Save(CNSaveRequest request)
    {
        if (!Contacts.ExecuteSaveRequest(request, out var error)) throw new IOException(error?.LocalizedDescription ?? "save failed");
    }

    private static string Seed()
    {
        var container = Contacts.DefaultContainerIdentifier;
        var alice = new CNMutableContact
        {
            GivenName = "Alice", FamilyName = "Smoke", OrganizationName = "Smoke Labs", JobTitle = "Engineer", Nickname = "Al",
            PhoneNumbers =
            [
                new CNLabeledValue<CNPhoneNumber>(CNLabelPhoneNumberKey.Mobile, new CNPhoneNumber("+380 50 111 2233")),
                new CNLabeledValue<CNPhoneNumber>(CNLabelPhoneNumberKey.iPhone, new CNPhoneNumber("+380 50 444 5566")),
                new CNLabeledValue<CNPhoneNumber>("Dacha", new CNPhoneNumber("+380 44 777 8899")),
            ],
            EmailAddresses = [new CNLabeledValue<NSString>(CNLabelKey.Home, new NSString("alice@example.com"))],
            UrlAddresses = [new CNLabeledValue<NSString>(CNLabelKey.UrlAddressHomePage, new NSString("https://alice.example.com"))],
            PostalAddresses = [new CNLabeledValue<CNPostalAddress>(CNLabelKey.Work, new CNMutablePostalAddress { Street = "1 Khreshchatyk", City = "Kyiv", PostalCode = "01001", Country = "Ukraine" })],
            Birthday = new NSDateComponents { Year = 1985, Month = 4, Day = 15 },
            ContactRelations = [new CNLabeledValue<CNContactRelation>(CNLabelContactRelationKey.Spouse, new CNContactRelation("Bob Smoke"))],
            InstantMessageAddresses = [new CNLabeledValue<CNInstantMessageAddress>(CNLabelKey.Home, new CNInstantMessageAddress("alice.smoke", "Skype"))],
            SocialProfiles = [new CNLabeledValue<CNSocialProfile>(null, new CNSocialProfile(null, "alicesmoke", null, CNSocialProfileServiceKey.Twitter))],
            ImageData = NSData.FromArray(Photo),
        };
        var bob = new CNMutableContact
        {
            GivenName = "Bob", FamilyName = "Smoke",
            PhoneNumbers = [new CNLabeledValue<CNPhoneNumber>(CNLabelPhoneNumberKey.Mobile, new CNPhoneNumber("+380 67 000 1122"))],
            EmailAddresses = [new CNLabeledValue<NSString>(CNLabelKey.Work, new NSString("bob@example.com"))],
            Dates = [new CNLabeledValue<NSDateComponents>(CNLabelKey.DateAnniversary, new NSDateComponents { Month = 6, Day = 1 })],
        };
        var org = new CNMutableContact { ContactType = CNContactType.Organization, OrganizationName = "Smoke Bakery",
            PhoneNumbers = [new CNLabeledValue<CNPhoneNumber>(CNLabelPhoneNumberKey.Main, new CNPhoneNumber("+380 44 123 4567"))] };
        var group = new CNMutableGroup { Name = "Smoke friends" };
        using (var request = new CNSaveRequest())
        {
            request.AddContact(alice, container);
            request.AddContact(bob, container);
            request.AddContact(org, container);
            request.AddGroup(group, container);
            Save(request);
        }
        using (var request = new CNSaveRequest())
        {
            request.AddMember(alice, group);
            request.AddMember(bob, group);
            Save(request);
        }

        // Calendars: a calendar of the iPhone's own source with three events.
        var store = IosCollections.EventStore.Raw;
        var source = store.Sources.FirstOrDefault(s => s.SourceType == EKSourceType.Local)
                     ?? store.DefaultCalendarForNewEvents?.Source ?? throw new IOException("no calendar source");
        var calendar = store.GetCalendars(EKEntityType.Event).FirstOrDefault(c => c.Title == SmokeCalendar);
        if (calendar is null)
        {
            calendar = EKCalendar.Create(EKEntityType.Event, store);
            calendar.Title = SmokeCalendar;
            calendar.Source = source;
            calendar.CGColor = UIKit.UIColor.FromRGB(140, 61, 140).CGColor;
            if (!store.SaveCalendar(calendar, true, out var calError)) throw new IOException(calError?.LocalizedDescription ?? "calendar");
        }
        var kyiv = NSTimeZone.FromName("Europe/Kyiv");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var today = DateTime.UtcNow.Date;
        NSDate At(DateTime local) => NSDate.FromTimeIntervalSince1970(IosRecurrence.Utc(local, zone) / 1000.0);
        NSDate Device(DateTime local) => NSDate.FromTimeIntervalSince1970(IosRecurrence.Utc(local, TimeZoneInfo.Local) / 1000.0);
        void SaveEvent(EKEvent e)
        {
            if (!store.SaveEvent(e, EKSpan.ThisEvent, true, out var error)) throw new IOException(error?.LocalizedDescription ?? "event");
        }
        var holiday = EKEvent.FromStore(store);
        holiday.Calendar = calendar;
        holiday.Title = "Smoke holiday";
        holiday.AllDay = true;
        holiday.StartDate = Device(today.AddDays(10));
        holiday.EndDate = Device(today.AddDays(11));
        SaveEvent(holiday);

        var dentist = EKEvent.FromStore(store);
        dentist.Calendar = calendar;
        dentist.Title = "Smoke dentist";
        dentist.Location = "Clinic";
        dentist.TimeZone = kyiv;
        dentist.StartDate = At(today.AddDays(3).AddHours(15));
        dentist.EndDate = At(today.AddDays(3).AddHours(16));
        dentist.AddAlarm(EKAlarm.FromTimeInterval(-30 * 60));
        SaveEvent(dentist);

        // A weekly series on Mondays at 10:00 in Kyiv, from four weeks back, 20 times; the second occurrence moved, the third deleted.
        var monday = today.AddDays(-28);
        while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(-1);
        var standup = EKEvent.FromStore(store);
        standup.Calendar = calendar;
        standup.Title = "Smoke standup";
        standup.TimeZone = kyiv;
        standup.StartDate = At(monday.AddHours(10));
        standup.EndDate = At(monday.AddHours(10.5));
        standup.AddRecurrenceRule(new EKRecurrenceRule(EKRecurrenceFrequency.Weekly, 1,
            [EKRecurrenceDayOfWeek.FromDay(EKWeekday.Monday)], null, null, null, null, null, EKRecurrenceEnd.FromOccurrenceCount(20)));
        SaveEvent(standup);
        var seriesId = standup.EventIdentifier;
        EKEvent Occurrence(DateTime day)
        {
            using var p = store.PredicateForEvents(At(day), At(day.AddDays(1)), [calendar]);
            return store.EventsMatching(p).First(e => e.EventIdentifier == seriesId);
        }
        var second = Occurrence(monday.AddDays(7));
        second.StartDate = At(monday.AddDays(7).AddHours(12));
        second.EndDate = At(monday.AddDays(7).AddHours(12.5));
        second.Title = "Smoke standup (moved)";
        SaveEvent(second);
        var third = Occurrence(monday.AddDays(14));
        if (!store.RemoveEvent(third, EKSpan.ThisEvent, true, out var removeError)) throw new IOException(removeError?.LocalizedDescription ?? "remove");
        return JsonSerializer.Serialize(new { contacts = 3, calendar = calendar.CalendarIdentifier, series = seriesId, monday = monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, Json);
    }

    private static CNContact[] All(params INativeObject[] keys)
    {
        var list = new List<CNContact>();
        INativeObject[] wanted = [CNContactKey.Identifier, CNContactKey.GivenName, CNContactKey.FamilyName, CNContactKey.OrganizationName,
            CNContactKey.PhoneNumbers, CNContactKey.EmailAddresses, CNContactKey.ImageDataAvailable, .. keys];
        using var request = new CNContactFetchRequest(wanted) { UnifyResults = false };
        if (!Contacts.EnumerateContacts(request, out var error, (CNContact c, ref bool _) => list.Add(c))) throw new IOException(error?.LocalizedDescription ?? "list");
        return [.. list];
    }

    private static string Edit()
    {
        var bob = All().First(c => c.GivenName == "Bob" && c.FamilyName == "Smoke");
        var changed = (CNMutableContact)bob.MutableCopy();
        changed.PhoneNumbers = [new CNLabeledValue<CNPhoneNumber>(CNLabelPhoneNumberKey.Mobile, new CNPhoneNumber("+380 67 999 0000"))];
        using var request = new CNSaveRequest();
        request.UpdateContact(changed);
        Save(request);
        return "ok";
    }

    private static string WipeContacts()
    {
        var all = All();
        using var request = new CNSaveRequest();
        foreach (var c in all) request.DeleteContact((CNMutableContact)c.MutableCopy());
        foreach (var g in Contacts.GetGroups(null, out _) ?? []) request.DeleteGroup((CNMutableGroup)g.MutableCopy());
        Save(request);
        return JsonSerializer.Serialize(new { deleted = all.Length }, Json);
    }

    private static string WipeCalendar()
    {
        var store = IosCollections.EventStore.Raw;
        var calendar = store.GetCalendars(EKEntityType.Event).First(c => c.Title == SmokeCalendar);
        var deleted = 0;
        var now = DateTime.UtcNow;
        for (var from = now.AddYears(-3); from < now.AddYears(6); from = from.AddYears(3))
        {
            using var p = store.PredicateForEvents((NSDate)from, (NSDate)from.AddYears(3), [calendar]);
            foreach (var e in store.EventsMatching(p))
                if (store.RemoveEvent(e, EKSpan.FutureEvents, true, out _)) deleted++;
        }
        return JsonSerializer.Serialize(new { deleted }, Json);
    }

    private static string DumpContacts() => JsonSerializer.Serialize(All().Select(c => new
    {
        c.Identifier, given = c.GivenName, family = c.FamilyName, org = c.OrganizationName, photo = c.ImageDataAvailable,
        phones = c.PhoneNumbers.Select(p => new { p.Label, value = p.Value.StringValue }), emails = c.EmailAddresses.Select(e => e.Value.ToString()),
    }), Json);

    private static string DumpCalendar()
    {
        var calendar = IosCollections.Calendars.Calendars().First(c => c.Name == SmokeCalendar);
        var events = IosCollections.Calendars.Events(new HashSet<string>([calendar.Id]), null);
        return JsonSerializer.Serialize(new
        {
            calendar = calendar.Id,
            events = events.Select(e => new
            {
                e.Id, e.Uid, e.Title, e.Start, e.End, e.Duration, e.AllDay, e.TimeZone, e.RRule, e.ExDate, alarms = e.Alarms.Select(a => a.MinutesBefore),
                exceptions = e.Exceptions.Select(x => new { x.Title, x.Start, x.OriginalStart, x.Status }),
            }),
        }, Json);
    }
}
