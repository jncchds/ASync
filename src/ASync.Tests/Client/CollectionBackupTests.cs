using System.Text;
using ASync.Client.Core.Collections;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using ASync.Tests.Engine;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests.Client;

/// <summary>
/// Contacts and Calendars folders (task 20) against fake sources: the permanent layout, vCard 4.0 and iCalendar output (recurring
/// events with exceptions, all-day events, time zones, Unicode, photos), determinism, incremental runs that do not generate unchanged
/// contacts again, the selection, the refusals, and the restore that skips what is already there and never deletes.
/// </summary>
public class CollectionBackupTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeContactsSource _contacts = new();
    private readonly FakeCalendarSource _calendars = new();
    private static readonly Guid Phone = Guid.Parse("0190aaaa-0000-7000-8000-000000000002");
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _dir.Dispose();

    private static DeviceFolderDto Folder(FolderKind kind, CollectionSelectionDto? sel = null) => new(
        8, kind.ToString(), SyncMode.Backup, FolderEncryption.Transport, kind, 60, [], new RetentionDto(), 30, 30, 1, true, "", null, false,
        Collections: sel ?? CollectionSelectionDto.Everything);

    private async Task<Repo> RepoAsync() => await TestData.NewRepositoryAsync(new LocalBackend(_dir.Sub("repo")));

    private GeneratedSizeCache Cache() => new(Path.Combine(_dir.Path, "state", "contacts-sizes.json"));

    private async Task<Snapshot> BackUpContactsAsync(Repo repo, CollectionSelectionDto? sel = null, bool cache = true) =>
        (await new BackupJob(repo, Folder(FolderKind.Contacts, sel), Phone, "Pixel",
            CollectionTree.Contacts(_contacts, sel, "test-contacts", cache ? Cache() : null)).RunAsync()).Snapshot;

    private async Task<Snapshot> BackUpCalendarsAsync(Repo repo, CollectionSelectionDto? sel = null) =>
        (await new BackupJob(repo, Folder(FolderKind.Calendars, sel), Phone, "Pixel",
            CollectionTree.Calendars(_calendars, sel, FakeCalendarSource.Ms(Now), "test-calendar")).RunAsync()).Snapshot;

    private async Task<string> RestoreToDiskAsync(Repo repo, Snapshot s, string name = "out")
    {
        var target = _dir.Sub(name);
        await new RestoreRunner(repo).RunAsync(s, target);
        return target;
    }

    private static List<string> Files(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0xFF, 0xD9];

    private static Contact Rich() => FakeContactsSource.New("Олена Шевченко") with
    {
        Name = new StructuredName("Шевченко", "Олена", "Петрівна", "Dr.", null, PhoneticGiven: "Olena"),
        Nicknames = ["Lena"],
        Phones = [new LabeledValue("+380 44 123 45 67", "cell", Preferred: true), new LabeledValue("+380 44 000 00 00", "work,fax"),
                  new LabeledValue("555", "", "Dacha")],
        Emails = [new LabeledValue("olena@example.com", "work")],
        Addresses = [new PostalAddress("Khreshchatyk 1; apt. 2", null, null, "Kyiv", null, "01001", "Ukraine", "Khreshchatyk 1, Kyiv", "home")],
        Organizations = [new Organization("Acme, Inc.", "R&D", "Engineer")],
        Websites = [new LabeledValue("https://example.com/olena")],
        Messaging = [new LabeledValue("olena", "skype")],
        Relations = [new LabeledValue("Taras", "spouse")],
        Dates = [new ContactDate("1985-04-15", "birthday"), new ContactDate("--06-01", "other", "Name day")],
        Note = "Line one\nline two, with a comma; and a semicolon \\ backslash. " + new string('x', 90),
        Photo = Jpeg,
        Groups = ["Family", "Friends"],
        Starred = true,
    };

    // ── vCard ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AContact_IsWrittenAsVCard4_ExactlyAsTheFormatSays()
    {
        var c = Rich() with { Id = "android:raw:42", ShortId = "42", AccountId = "com.google:me@gmail.com" };

        var text = Encoding.UTF8.GetString(VCard.Write(c));

        var expected = string.Join("\r\n",
            "BEGIN:VCARD", "VERSION:4.0", "PRODID:-//ASync//Contacts 1//EN", "UID:android:raw:42", "FN:Олена Шевченко",
            "N:Шевченко;Олена;Петрівна;Dr.;", "X-PHONETIC-FIRST-NAME:Olena", "NICKNAME:Lena", "BDAY:19850415",
            "item1.X-ABDATE:--0601", "item1.X-ABLABEL:Name day",
            "TEL;TYPE=cell;PREF=1:+380 44 123 45 67", "TEL;TYPE=work,fax:+380 44 000 00 00", "item2.TEL:555", "item2.X-ABLABEL:Dacha",
            "EMAIL;TYPE=work:olena@example.com",
            "ADR;TYPE=home;LABEL=\"Khreshchatyk 1, Kyiv\":;;Khreshchatyk 1\\; apt. 2;Kyiv;;01001;Ukraine",
            "ORG:Acme\\, Inc.;R&D", "TITLE:Engineer", "URL:https://example.com/olena", "IMPP:skype:olena", "RELATED;TYPE=spouse;VALUE=text:Taras");
        var unfolded = text.Replace("\r\n ", "");
        Assert.Equal(expected.Split("\r\n"), unfolded.Split("\r\n")[..22]);
        Assert.StartsWith(expected + "\r\nNOTE:Line one\\nline two\\, with a comma\\; and a semicolon \\\\ backslash.", unfolded);
        Assert.Contains("\r\nCATEGORIES:Family,Friends\r\nX-ASYNC-STARRED:TRUE\r\nPHOTO:data:image/jpeg;base64,/9j/4AECAwQFBgcICf/Z\r\nEND:VCARD\r\n", unfolded);
        // folded at 75 octets, never inside a UTF-8 sequence
        foreach (var line in text.Split("\r\n")) Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line);
        Assert.Contains("\r\n x", text);
    }

    [Fact]
    public void AVCard_ReadsBackToTheSameContact()
    {
        var c = Rich() with { Id = "android:raw:42", ShortId = "42", AccountId = "a" };

        var back = Assert.Single(VCard.Parse(VCard.Write(c)));

        Assert.Equal(c.Id, back.Id);
        Assert.Equal(c.DisplayName, back.DisplayName);
        Assert.Equal(c.Name, back.Name);
        Assert.Equal(c.Phones, back.Phones);
        Assert.Equal(c.Emails, back.Emails);
        Assert.Equal(c.Addresses, back.Addresses);
        Assert.Equal(c.Organizations, back.Organizations);
        Assert.Equal(c.Websites, back.Websites);
        Assert.Equal(c.Messaging, back.Messaging);
        Assert.Equal(c.Relations, back.Relations);
        Assert.Equal(c.Dates, back.Dates);
        Assert.Equal(c.Nicknames, back.Nicknames);
        Assert.Equal(c.Note, back.Note);
        Assert.Equal(c.Photo, back.Photo);
        Assert.Equal(c.Groups, back.Groups);
        Assert.True(back.Starred);
        Assert.Equal(VCard.Write(c), VCard.Write(back with { ShortId = "42", AccountId = "a" }));
    }

    [Fact]
    public void VCardsOfOtherPrograms_AreRead()
    {
        var v3 = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Bob Smith\r\nN:Smith;Bob;;;\r\nTEL;TYPE=CELL,VOICE:+1 555\r\nEMAIL;TYPE=INTERNET:bob@x.org\r\n" +
                 "PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AECAwQFBgcICf/Z\r\nBDAY:1970-01-02\r\nEND:VCARD\r\nBEGIN:VCARD\r\nVERSION:4.0\r\nFN:Second\r\nEND:VCARD\r\n";

        var cards = VCard.Parse(Encoding.UTF8.GetBytes(v3));

        Assert.Equal(2, cards.Count);
        Assert.Equal("Bob Smith", cards[0].DisplayName);
        Assert.Equal([new LabeledValue("+1 555", "cell")], cards[0].Phones);
        Assert.Equal([new LabeledValue("bob@x.org")], cards[0].Emails);
        Assert.Equal(Jpeg, cards[0].Photo);
        Assert.Equal([new ContactDate("1970-01-02", "birthday")], cards[0].Dates);
        Assert.Throws<InvalidDataException>(() => VCard.Parse("hello"u8));
    }

    // ── Contacts folders ──────────────────────────────────────────────────────

    [Fact]
    public async Task AContactsSnapshot_HasOneDirectoryPerChosenAccount_AndOneVcfPerContact()
    {
        var google = _contacts.Account("me@gmail.com");
        var phone = _contacts.Account("Phone", "local", "Phone");
        var olena = _contacts.Add(google, Rich());
        var bob = _contacts.Add(google, FakeContactsSource.New("Bob Smith", "+1 555"));
        _contacts.Add(phone, FakeContactsSource.New("Plumber", "+1 777"));
        var repo = await RepoAsync();

        var snapshot = await BackUpContactsAsync(repo, new CollectionSelectionDto(false, [new CollectionRef(google.Id, google.Name)]));
        var root = await RestoreToDiskAsync(repo, snapshot);

        var dir = $"me@gmail.com~{google.ShortId}";
        Assert.Equal([$"{dir}/Bob Smith~{bob.ShortId}.vcf", $"{dir}/account.json", $"{dir}/Олена Шевченко~{olena.ShortId}.vcf"], Files(root));
        Assert.Equal(VCard.Write(olena), File.ReadAllBytes(Path.Combine(root, $"{dir}/Олена Шевченко~{olena.ShortId}.vcf")));
        Assert.Equal(
            "{\n  \"format\": 1,\n  \"source\": \"test-contacts\",\n  \"kind\": \"contacts\",\n  \"id\": \"com.google:me@gmail.com\",\n  \"name\": \"me@gmail.com\",\n" +
            "  \"type\": \"com.google\",\n  \"typeLabel\": \"Google\",\n  \"readOnly\": false\n}",
            File.ReadAllText(Path.Combine(root, $"{dir}/account.json")).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task ASecondRun_DoesNotGenerateUnchangedContacts_AndSeesAChangedOne()
    {
        var google = _contacts.Account("me@gmail.com");
        var a = _contacts.Add(google, Rich());
        var b = _contacts.Add(google, FakeContactsSource.New("Bob Smith", "+1 555"));
        var repo = await RepoAsync();
        var first = await BackUpContactsAsync(repo);
        _contacts.Reads.Clear();

        var second = await BackUpContactsAsync(repo);

        Assert.Empty(_contacts.Reads);
        Assert.Equal(0, second.Stats.FilesNew + second.Stats.FilesChanged);
        Assert.Equal(first.Tree, second.Tree);

        _contacts.Update(b with { Phones = [new LabeledValue("+1 556", "cell")] });
        var third = await BackUpContactsAsync(repo);

        Assert.Equal([b.Id], _contacts.Reads.Keys);
        Assert.Equal(1, third.Stats.FilesChanged);
        var root = await RestoreToDiskAsync(repo, third);
        Assert.Contains("TEL;TYPE=cell:+1 556", File.ReadAllText(Path.Combine(root, $"me@gmail.com~{google.ShortId}/Bob Smith~{b.ShortId}.vcf")));
        _ = a;
    }

    [Fact]
    public async Task WithoutTheSizeCache_AnUnchangedContactStillGivesTheSameBytes()
    {
        var google = _contacts.Account("me@gmail.com");
        _contacts.Add(google, Rich());
        var repo = await RepoAsync();
        var first = await BackUpContactsAsync(repo, cache: false);

        var second = await BackUpContactsAsync(repo, cache: false);

        Assert.Equal(first.Tree, second.Tree);
        Assert.Equal(0, second.Stats.BytesAdded);
    }

    [Fact]
    public async Task ADeletedContact_StaysInTheOlderSnapshot()
    {
        var google = _contacts.Account("me@gmail.com");
        var a = _contacts.Add(google, FakeContactsSource.New("Ann Lee", "+1 1"));
        _contacts.Add(google, FakeContactsSource.New("Bob Smith", "+1 2"));
        var repo = await RepoAsync();
        var first = await BackUpContactsAsync(repo);
        _contacts.Delete(a.Id);

        var second = await BackUpContactsAsync(repo);

        Assert.Equal(3, Files(await RestoreToDiskAsync(repo, first, "one")).Count);
        Assert.Equal(2, Files(await RestoreToDiskAsync(repo, second, "two")).Count);
    }

    [Fact]
    public async Task WithoutPermission_TheRunStopsWithTheReason_AndNothingIsWritten()
    {
        _contacts.Account("me@gmail.com");
        _contacts.Access = MediaAccess.None;
        var repo = await RepoAsync();

        var e = await Assert.ThrowsAsync<SyncRefusedException>(() => BackUpContactsAsync(repo));

        Assert.Contains("may not read the contacts", e.Message);
        Assert.Empty(await repo.ListSnapshotsAsync());
    }

    [Fact]
    public async Task AnEmptiedAccount_MakesNoEmptySnapshotOverAGoodOne()
    {
        var google = _contacts.Account("me@gmail.com");
        _contacts.Add(google, FakeContactsSource.New("Ann Lee", "+1 1"));
        var repo = await RepoAsync();
        await BackUpContactsAsync(repo);
        _contacts.DeleteAccountContacts(google.Id);

        var e = await Assert.ThrowsAsync<SyncRefusedException>(() => BackUpContactsAsync(repo));

        Assert.Contains("No snapshot was made", e.Message);
        Assert.Single(await repo.ListSnapshotsAsync());
    }

    [Fact]
    public void TheTree_IsReadOnly_AndCountsWhatItHolds()
    {
        var google = _contacts.Account("me@gmail.com");
        _contacts.Add(google, FakeContactsSource.New("Ann Lee", "+1 1"));
        var tree = CollectionTree.Contacts(_contacts, null);

        Assert.Equal(new CollectionCountsDto(1, 1, tree.Counts().Bytes), tree.Counts());
        Assert.True(tree.Counts().Bytes > 50);
        Assert.Throws<NotSupportedException>(() => tree.OpenAppend("x"));
        Assert.Throws<NotSupportedException>(() => tree.DeleteFile("x"));
    }

    // ── Restoring contacts ────────────────────────────────────────────────────

    [Fact]
    public async Task RestoringContacts_IntoAWipedAccount_BringsThemBack_ThenSkipsThem_AndDeletesNothing()
    {
        var google = _contacts.Account("me@gmail.com");
        var olena = _contacts.Add(google, Rich());
        _contacts.Add(google, FakeContactsSource.New("Bob Smith", "+1 555"));
        var repo = await RepoAsync();
        var snapshot = await BackUpContactsAsync(repo);
        _contacts.DeleteAccountContacts(google.Id);
        var newcomer = _contacts.Add(google, FakeContactsSource.New("New Person", "+1 999"));
        var progress = new List<CollectionRestoreProgress>();

        var result = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, _contacts, null, new SyncProgress<CollectionRestoreProgress>(progress.Add));

        Assert.Equal((2, 0, 2), (result.Restored, result.Skipped, result.Total));
        Assert.Empty(result.Errors);
        var restored = _contacts.Inserted.Single(i => i.Contact.DisplayName == "Олена Шевченко").Contact;
        Assert.Equal(olena.Phones, restored.Phones);
        Assert.Equal(olena.Photo, restored.Photo);
        Assert.Equal(olena.Addresses, restored.Addresses);
        Assert.Equal(3, _contacts.Contacts().Count);
        Assert.NotNull(_contacts.Read(newcomer.Id)); // nothing there was deleted
        Assert.Equal(2, progress[^1].Done);

        var again = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, _contacts, null);

        Assert.Equal((0, 2), (again.Restored, again.Skipped));
        Assert.Equal(3, _contacts.Contacts().Count);
    }

    [Fact]
    public async Task RestoringContacts_SkipsByIdOnThisPhone_AndIntoAnotherAccountByContent()
    {
        var google = _contacts.Account("me@gmail.com");
        var phone = _contacts.Account("Phone", "local", "Phone");
        var ann = _contacts.Add(google, FakeContactsSource.New("Ann Lee", "+1 1"));
        var repo = await RepoAsync();
        var snapshot = await BackUpContactsAsync(repo);
        _contacts.Update(ann with { Emails = [new LabeledValue("ann@new.org")] }); // changed since, same id and name: already there

        var same = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, _contacts, null);
        var other = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, _contacts, phone.Id);
        var otherAgain = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, _contacts, phone.Id);

        Assert.Equal((0, 1), (same.Restored, same.Skipped));
        Assert.Equal((1, 0), (other.Restored, other.Skipped));
        Assert.Equal((0, 1), (otherAgain.Restored, otherAgain.Skipped));
        Assert.Equal(phone.Id, Assert.Single(_contacts.Inserted).AccountId);
    }

    [Fact]
    public async Task RestoringContacts_ToAnAccountThePhoneNoLongerHas_SaysSo()
    {
        var google = _contacts.Account("me@gmail.com");
        _contacts.Add(google, FakeContactsSource.New("Ann Lee", "+1 1"));
        var repo = await RepoAsync();
        var snapshot = await BackUpContactsAsync(repo);
        var elsewhere = new FakeContactsSource();
        elsewhere.Account("other@gmail.com");

        var result = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, elsewhere, null);

        Assert.Equal(0, result.Restored);
        Assert.Contains("not on this phone", Assert.Single(result.Errors));
    }

    // ── iCalendar ─────────────────────────────────────────────────────────────

    private static CalendarEvent Weekly() => FakeCalendarSource.New("Стендап ☕", new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc), TimeSpan.Zero) with
    {
        End = null, Duration = "P900S", RRule = "FREQ=WEEKLY;BYDAY=MO;UNTIL=20261231T215959Z", ExDate = "20260309T080000Z",
        LastEnd = FakeCalendarSource.Ms(new DateTime(2026, 12, 28, 8, 15, 0, DateTimeKind.Utc)),
        Location = "Room 1, floor 2", Description = "Daily-ish\nweekly", Organizer = "boss@example.com",
        Attendees = [new Attendee("me@example.com", "Me", "REQ-PARTICIPANT", "ACCEPTED"), new Attendee("room@example.com", "Room 1", "NON-PARTICIPANT", "ACCEPTED", "ROOM")],
        Alarms = [new Alarm(10), new Alarm(1440, "email")], Status = "CONFIRMED", Transparency = "OPAQUE", Color = "#FF8800",
        Exceptions =
        [
            FakeCalendarSource.New("Стендап (moved)", new DateTime(2026, 3, 17, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(15)) with
                { OriginalStart = FakeCalendarSource.Ms(new DateTime(2026, 3, 16, 8, 0, 0, DateTimeKind.Utc)) },
            FakeCalendarSource.New("Стендап ☕", new DateTime(2026, 3, 23, 8, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(15)) with
                { OriginalStart = FakeCalendarSource.Ms(new DateTime(2026, 3, 23, 8, 0, 0, DateTimeKind.Utc)), Status = "CANCELLED" },
        ],
    };

    [Fact]
    public void ARecurringEvent_IsWrittenWithItsZone_Rule_Exceptions_Attendees_AndAlarms()
    {
        var e = Weekly() with { Id = "android:event:7", ShortId = "7", CalendarId = "1" };

        var text = Encoding.UTF8.GetString(ICalendar.Write(e));
        var lines = text.Replace("\r\n ", "").Split("\r\n");

        Assert.Equal(["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//ASync//Calendars 1//EN", "CALSCALE:GREGORIAN", "BEGIN:VTIMEZONE", "TZID:Europe/Kyiv"], lines[..6]);
        // 2026: daylight saving from the last Sunday of March 03:00 (+02 → +03), back the last Sunday of October 04:00
        Assert.Contains("BEGIN:DAYLIGHT\r\nDTSTART:20260329T030000\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0300\r\nEND:DAYLIGHT", text);
        Assert.Contains("BEGIN:STANDARD\r\nDTSTART:20261025T040000\r\nRRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU\r\nTZOFFSETFROM:+0300\r\nTZOFFSETTO:+0200\r\nEND:STANDARD", text);
        var master = lines.SkipWhile(l => l != "BEGIN:VEVENT").TakeWhile(l => l != "END:VEVENT").ToList();
        Assert.Equal(
        [
            "BEGIN:VEVENT", "UID:android:event:7", "DTSTAMP:20260302T080000Z", "DTSTART;TZID=Europe/Kyiv:20260302T100000", "DURATION:PT15M",
            "SUMMARY:Стендап ☕", "LOCATION:Room 1\\, floor 2", "DESCRIPTION:Daily-ish\\nweekly", "RRULE:FREQ=WEEKLY;BYDAY=MO;UNTIL=20261231T215959Z",
            "EXDATE:20260309T080000Z", "STATUS:CONFIRMED", "TRANSP:OPAQUE", "ORGANIZER:mailto:boss@example.com",
            "ATTENDEE;CN=Me;CUTYPE=INDIVIDUAL;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:me@example.com",
            "ATTENDEE;CN=\"Room 1\";CUTYPE=ROOM;ROLE=NON-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:room@example.com",
            "X-ASYNC-COLOR:#ff8800",
            "BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:Стендап ☕", "TRIGGER:-PT10M", "END:VALARM",
            "BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:Стендап ☕", "TRIGGER:-P1D", "X-ASYNC-METHOD:email", "END:VALARM",
        ], master);
        Assert.Contains("BEGIN:VEVENT\r\nUID:android:event:7\r\nDTSTAMP:20260317T090000Z\r\nRECURRENCE-ID;TZID=Europe/Kyiv:20260316T100000\r\n" +
                        "DTSTART;TZID=Europe/Kyiv:20260317T110000\r\nDTEND;TZID=Europe/Kyiv:20260317T111500\r\nSUMMARY:Стендап (moved)", text);
        Assert.Contains("RECURRENCE-ID;TZID=Europe/Kyiv:20260323T100000", text);
        Assert.Contains("STATUS:CANCELLED", text);
        Assert.EndsWith("END:VEVENT\r\nEND:VCALENDAR\r\n", text);
        Assert.Equal(ICalendar.Write(e), ICalendar.Write(e)); // deterministic
    }

    [Fact]
    public void AnICalendarFile_ReadsBackToTheSameEvent_WithItsExceptions()
    {
        var e = Weekly() with { Id = "android:event:7", ShortId = "7", CalendarId = "1" };

        var back = Assert.Single(ICalendar.Parse(ICalendar.Write(e)));

        Assert.Equal("android:event:7", back.Uid);
        Assert.Equal((e.Title, e.Start, e.TimeZone, "PT15M", e.RRule, e.ExDate), (back.Title, back.Start, back.TimeZone, back.Duration, back.RRule, back.ExDate));
        Assert.Equal(e.Attendees, back.Attendees);
        Assert.Equal(e.Alarms, back.Alarms);
        Assert.Equal(2, back.Exceptions.Count);
        Assert.Equal(e.Exceptions[0].OriginalStart, back.Exceptions[0].OriginalStart);
        Assert.Equal(e.Exceptions[0].Start, back.Exceptions[0].Start);
        Assert.Equal("CANCELLED", back.Exceptions[1].Status);
        Assert.Equal(ICalendar.Write(e), ICalendar.Write(back with { Id = e.Id, Uid = null, LastEnd = e.LastEnd, Color = "#FF8800" }));
    }

    [Fact]
    public void AllDayEvents_AreDates_AndUtcEventsHaveNoZone()
    {
        var day = new CalendarEvent
        {
            Id = "e1", ShortId = "1", CalendarId = "1", Title = "Holiday", AllDay = true, TimeZone = "UTC",
            Start = FakeCalendarSource.Ms(new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc)), End = FakeCalendarSource.Ms(new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc)),
        };
        var utc = FakeCalendarSource.New("Call", new DateTime(2026, 1, 5, 15, 30, 0, DateTimeKind.Utc), TimeSpan.FromHours(1), "UTC") with { Id = "e2", ShortId = "2" };

        var dayText = Encoding.UTF8.GetString(ICalendar.Write(day));
        var utcText = Encoding.UTF8.GetString(ICalendar.Write(utc));

        Assert.Contains("\r\nDTSTART;VALUE=DATE:20260824\r\nDTEND;VALUE=DATE:20260825\r\n", dayText);
        Assert.DoesNotContain("VTIMEZONE", dayText);
        Assert.Contains("\r\nDTSTART:20260105T153000Z\r\nDTEND:20260105T163000Z\r\n", utcText);
        var back = Assert.Single(ICalendar.Parse(Encoding.UTF8.GetBytes(dayText)));
        Assert.True(back.AllDay);
        Assert.Equal((day.Start, day.End), (back.Start, back.End));
    }

    // ── Calendars folders ─────────────────────────────────────────────────────

    [Fact]
    public async Task ACalendarsSnapshot_HoldsTheChosenCalendar_WithCalendarJson_AndOnlyRecentEvents()
    {
        var work = _calendars.Calendar("Work");
        var family = _calendars.Calendar("Family", color: "#00aa00");
        var weekly = _calendars.Add(work, Weekly());
        var old = _calendars.Add(work, FakeCalendarSource.New("Old meeting", new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1)));
        var forever = _calendars.Add(work, FakeCalendarSource.New("Gym", new DateTime(2019, 1, 1, 6, 0, 0, DateTimeKind.Utc), TimeSpan.Zero) with
            { End = null, Duration = "P3600S", RRule = "FREQ=DAILY" });
        _calendars.Add(family, FakeCalendarSource.New("Dinner", new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(2)));
        var repo = await RepoAsync();

        var snapshot = await BackUpCalendarsAsync(repo, new CollectionSelectionDto(false, [new CollectionRef(work.Id, "Work")], 730));
        var root = await RestoreToDiskAsync(repo, snapshot);

        var dir = $"Work~{work.ShortId}";
        Assert.Equal([$"{dir}/Gym~{forever.ShortId}.ics", $"{dir}/calendar.json", $"{dir}/Стендап ☕~{weekly.ShortId}.ics"], Files(root));
        Assert.Equal(ICalendar.Write(weekly), File.ReadAllBytes(Path.Combine(root, $"{dir}/Стендап ☕~{weekly.ShortId}.ics")));
        var info = CollectionInfoFile.Parse(File.ReadAllBytes(Path.Combine(root, $"{dir}/calendar.json")));
        Assert.Equal(("calendar", "1", "Work", "me@example.com", "#3366cc", "Europe/Kyiv", "test-calendar"),
            (info.Kind, info.Id, info.Name, info.Account, info.Color, info.TimeZone, info.Source));
        _ = old;

        var everything = await BackUpCalendarsAsync(repo, new CollectionSelectionDto(PastDays: null));
        Assert.Equal(6, Files(await RestoreToDiskAsync(repo, everything, "all")).Count);
    }

    [Fact]
    public async Task UnchangedEvents_AreStoredOnce_AndAChangedTitleIsSeen()
    {
        var work = _calendars.Calendar("Work");
        var e = _calendars.Add(work, FakeCalendarSource.New("Lunch", new DateTime(2026, 11, 18, 11, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1)));
        var repo = await RepoAsync();
        var first = await BackUpCalendarsAsync(repo);

        var second = await BackUpCalendarsAsync(repo);

        Assert.Equal(first.Tree, second.Tree);
        Assert.Equal(0, second.Stats.BytesAdded);
        Assert.Equal(0, second.Stats.FilesChanged);

        _calendars.Update(e with { Title = "Lunk" }); // same size and time: still seen (events are always read)
        var third = await BackUpCalendarsAsync(repo);
        Assert.Contains("SUMMARY:Lunk", File.ReadAllText(Files(await RestoreToDiskAsync(repo, third)).Select(f => Path.Combine(_dir.Path, "out", f)).Single(f => f.EndsWith(".ics"))));
    }

    [Fact]
    public async Task RestoringCalendars_IntoAWipedCalendar_BringsBackEventsWithExceptions_ThenSkipsThem()
    {
        var work = _calendars.Calendar("Work");
        _calendars.Add(work, Weekly());
        _calendars.Add(work, FakeCalendarSource.New("Lunch", new DateTime(2026, 11, 18, 11, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1)));
        var repo = await RepoAsync();
        var snapshot = await BackUpCalendarsAsync(repo);
        _calendars.Wipe(work.Id);
        var kept = _calendars.Add(work, FakeCalendarSource.New("Added later", new DateTime(2026, 11, 19, 11, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1)));

        var result = await new CollectionRestorer(repo).RestoreCalendarsAsync(snapshot, _calendars, null);

        Assert.Equal((2, 0), (result.Restored, result.Skipped));
        var weekly = _calendars.Inserted.Single(i => i.Event.RRule is not null).Event;
        Assert.Equal(2, weekly.Exceptions.Count);
        Assert.StartsWith("fake:event:", weekly.Uid); // the UID it had, so a later restore finds it by UID
        Assert.Contains(_calendars.Events(), x => x.Id == kept.Id);
        Assert.Equal(3, _calendars.Events().Count);

        var again = await new CollectionRestorer(repo).RestoreCalendarsAsync(snapshot, _calendars, null);
        Assert.Equal((0, 2), (again.Restored, again.Skipped));

        var other = _calendars.Calendar("Imported");
        var into = await new CollectionRestorer(repo).RestoreCalendarsAsync(snapshot, _calendars, other.Id);
        Assert.Equal((2, 0), (into.Restored, into.Skipped));
        Assert.Equal(5, _calendars.Events().Count);
    }

    [Fact]
    public async Task ReadOnlyCalendars_AreNotRestoredInto()
    {
        var holidays = _calendars.Calendar("Holidays", readOnly: true);
        _calendars.Add(holidays, FakeCalendarSource.New("New Year", new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromDays(1)));
        var repo = await RepoAsync();
        var snapshot = await BackUpCalendarsAsync(repo);

        var result = await new CollectionRestorer(repo).RestoreCalendarsAsync(snapshot, _calendars, null);

        Assert.Equal(0, result.Restored);
        Assert.Contains("does not take new events", Assert.Single(result.Errors));
        Assert.Empty(_calendars.Inserted);
    }

    // ── The selection ─────────────────────────────────────────────────────────

    [Fact]
    public void TheSelection_IsValidated_AndDescribed()
    {
        Assert.Null(CollectionSelectionDto.Everything.Problem(FolderKind.Contacts));
        Assert.Contains("at least one account", new CollectionSelectionDto(false, []).Problem(FolderKind.Contacts));
        Assert.Contains("at least one calendar", new CollectionSelectionDto(false).Problem(FolderKind.Calendars));
        Assert.NotNull(new CollectionSelectionDto(PastDays: 0).Problem(FolderKind.Calendars));
        Assert.Equal("All contacts", CollectionSelectionDto.Everything.Describe(FolderKind.Contacts));
        Assert.Equal("All calendars · events of the last 2 years", CollectionSelectionDto.Everything.Describe(FolderKind.Calendars));
        Assert.Equal("1 calendar: Work · all events", new CollectionSelectionDto(false, [new("1", "Work")], null).Describe(FolderKind.Calendars));
    }
}

/// <summary>An IProgress that reports on the calling thread (Progress&lt;T&gt; posts to the thread pool and the test would race it).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
