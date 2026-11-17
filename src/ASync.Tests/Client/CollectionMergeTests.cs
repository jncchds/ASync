using System.Text;
using ASync.Api.Services;
using ASync.Client.Core.Collections;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Storage;
using ASync.Tests.Engine;

namespace ASync.Tests.Client;

/// <summary>The web snapshot browser's "one .vcf" / "one .ics" (task 20): an account's cards in one file, a calendar's events in one VCALENDAR.</summary>
public class CollectionMergeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static readonly Guid Phone = Guid.Parse("0190aaaa-0000-7000-8000-000000000003");

    public void Dispose() => _dir.Dispose();

    private static DeviceFolderDto Folder(FolderKind kind) => new(
        9, kind.ToString(), SyncMode.Backup, FolderEncryption.Transport, kind, 60, [], new RetentionDto(), 30, 30, 1, true, "", null, false,
        Collections: CollectionSelectionDto.Everything);

    [Fact]
    public async Task AnAccount_IsOneVcf_AndACalendar_OneIcs()
    {
        var contacts = new FakeContactsSource();
        var google = contacts.Account("me@gmail.com");
        contacts.Add(google, FakeContactsSource.New("Ann Lee", "+1 1"));
        contacts.Add(google, FakeContactsSource.New("Bob Smith", "+1 2"));
        var calendars = new FakeCalendarSource();
        var work = calendars.Calendar("Work; and more");
        calendars.Add(work, FakeCalendarSource.New("Lunch", new DateTime(2026, 11, 18, 11, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1)) with
        {
            Exceptions = [FakeCalendarSource.New("Lunch (moved)", new DateTime(2026, 11, 19, 11, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1)) with
                { OriginalStart = FakeCalendarSource.Ms(new DateTime(2026, 11, 18, 11, 0, 0, DateTimeKind.Utc)) }],
            RRule = "FREQ=DAILY;COUNT=3", End = null, Duration = "PT1H",
        });
        calendars.Add(work, FakeCalendarSource.New("Dinner", new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(2)));
        var now = FakeCalendarSource.Ms(new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc));

        var cRepo = await TestData.NewRepositoryAsync(new LocalBackend(_dir.Sub("c")));
        var cSnap = (await new BackupJob(cRepo, Folder(FolderKind.Contacts), Phone, "Pixel", CollectionTree.Contacts(contacts, null)).RunAsync()).Snapshot;
        var kRepo = await TestData.NewRepositoryAsync(new LocalBackend(_dir.Sub("k")));
        var kSnap = (await new BackupJob(kRepo, Folder(FolderKind.Calendars), Phone, "Pixel", CollectionTree.Calendars(calendars, null, now)).RunAsync()).Snapshot;

        var cBrowser = new SnapshotBrowser(cRepo);
        using var vcf = new MemoryStream();
        await CollectionMerge.WriteAsync(cBrowser, await cBrowser.FindAsync(cSnap, CollectionLayout.AccountDirectory(google)), calendars: false, vcf, default);
        var cards = VCard.Parse(vcf.ToArray());
        Assert.Equal(["Ann Lee", "Bob Smith"], cards.Select(c => c.DisplayName).Order());

        var kBrowser = new SnapshotBrowser(kRepo);
        using var ics = new MemoryStream();
        await CollectionMerge.WriteAsync(kBrowser, await kBrowser.FindAsync(kSnap, CollectionLayout.CalendarDirectory(work)), calendars: true, ics, default);
        var text = Encoding.UTF8.GetString(ics.ToArray());
        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//ASync//Calendars 1//EN\r\nCALSCALE:GREGORIAN\r\nX-WR-CALNAME:Work\\; and more\r\nBEGIN:VTIMEZONE\r\nTZID:Europe/Kyiv\r\n", text);
        Assert.Equal(1, CountOf(text, "BEGIN:VTIMEZONE"));
        Assert.Equal(3, CountOf(text, "BEGIN:VEVENT"));
        Assert.EndsWith("END:VEVENT\r\nEND:VCALENDAR\r\n", text);
        var events = ICalendar.Parse(ics.ToArray());
        Assert.Equal(["Dinner", "Lunch"], events.Select(e => e.Title).Order());
        Assert.Single(events.Single(e => e.Title == "Lunch").Exceptions);
    }

    private static int CountOf(string text, string what) => text.Split("\r\n").Count(l => l == what);
}
