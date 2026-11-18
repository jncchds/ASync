using System.Text;
using System.Text.Json;
using ASync.App.iOS;
using ASync.Client.Core.Background;
using ASync.Client.Core.Collections;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Chunking;
using ASync.Engine.Crypto;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.App.Tests;

/// <summary>
/// The iPhone's contacts and calendars (task 24) on Linux: the Contacts framework's labels, containers, groups and photos onto vCard;
/// EventKit's occurrences back into series (moved and deleted occurrences, all-day events, zones, a series that was split), rules both
/// ways and their occurrences, alarms and attendees, the window, the backup's files and their `ios-*` sources, unchanged contacts not
/// read again, and the restore (original container or calendar, one of the same name, nothing duplicated, nothing deleted). Against
/// <see cref="FakeContacts"/> and <see cref="FakeEventKit"/>, the thin interfaces' fakes; the frameworks run only on a Mac
/// (scripts/smoke-ios-collections.sh).
/// </summary>
public sealed class IosCollectionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "async-ioscoll-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeContacts _contacts = new();
    private readonly FakeEventKit _kit = new();
    private static readonly Guid Phone = Guid.Parse("0190aaaa-0000-7000-8000-0000000000a2");
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    private const string Local = "LOCAL-CONTAINER";
    private const string ICloud = "ICLOUD-CONTAINER";
    private const string Cal = "CAL-SMOKE";

    public IosCollectionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    private static long KyivMs(int y, int mo, int d, int h = 0, int mi = 0) => IosRecurrence.Utc(new DateTime(y, mo, d, h, mi, 0), Kyiv);

    private async Task<Repo> RepoAsync()
    {
        var keys = RepositoryKeys.Generate();
        return await Repo.InitAsync(new LocalBackend(Path.Combine(_dir, "repo")), keys, [KeyFile.ForPassphrase(keys, "pass", iterations: 1000)],
            new ChunkerParameters(MinSize: 1024, AverageSize: 4096, MaxSize: 16384));
    }

    private static DeviceFolderDto Folder(FolderKind kind, CollectionSelectionDto? sel = null) => new(
        8, kind.ToString(), SyncMode.Backup, FolderEncryption.Transport, kind, 60, [], new RetentionDto(), 30, 30, 1, true, "", null, false,
        Collections: sel ?? CollectionSelectionDto.Everything);

    private IosContactsSource ContactsSource() => new(_contacts, Path.Combine(_dir, "state", "ios-contact-versions.json"));
    private IosCalendarSource CalendarSource() => new(_kit, () => Ms(Now));
    private GeneratedSizeCache Cache() => new(Path.Combine(_dir, "state", "contacts-sizes.json"));

    private async Task<Snapshot> BackUpContactsAsync(Repo repo, IosContactsSource? source = null) =>
        (await new BackupJob(repo, Folder(FolderKind.Contacts), Phone, "iPhone",
            CollectionTree.Contacts(source ?? ContactsSource(), null, IosContactMapping.Source, Cache())).RunAsync()).Snapshot;

    private async Task<Snapshot> BackUpCalendarsAsync(Repo repo, CollectionSelectionDto? sel = null) =>
        (await new BackupJob(repo, Folder(FolderKind.Calendars, sel), Phone, "iPhone",
            CollectionTree.Calendars(CalendarSource(), sel, Ms(Now), IosEventMapping.Source)).RunAsync()).Snapshot;

    private async Task<Dictionary<string, string>> FilesAsync(Repo repo, Snapshot s)
    {
        var target = Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N")[..6]);
        await new RestoreRunner(repo).RunAsync(s, target);
        return Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(target, f).Replace('\\', '/'), f => File.ReadAllText(f, Encoding.UTF8), StringComparer.Ordinal);
    }

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAFklEQVR4nGOImZFCFGIYVTiqkK4KASuQ6UFXcysBAAAAAElFTkSuQmCC");

    private const string AliceId = "410FE041-5C4E-48DA-B4DE-04C15EA3DBAC:ABPerson";

    private void SeedContacts(string container = Local)
    {
        _contacts.ContainerList.Add(new CNContainerData(container, container == ICloud ? "iCloud" : "", container == ICloud ? CNContainerKind.CardDav : CNContainerKind.Local));
        _contacts.Put(new CNContactData
        {
            Id = AliceId, ContainerId = container, DisplayName = "Alice Smoke", GivenName = "Alice", FamilyName = "Smoke", Nickname = "Al",
            OrganizationName = "Smoke Labs", JobTitle = "Engineer",
            Phones = [new("_$!<Mobile>!$_", "+380 50 111 2233"), new("iPhone", "+380 50 444 5566"), new("Dacha", "+380 44 777 8899")],
            Emails = [new("_$!<Home>!$_", "alice@example.com")],
            Urls = [new("_$!<HomePage>!$_", "https://alice.example.com")],
            Addresses = [new("_$!<Work>!$_", new CNAddress("1 Khreshchatyk", City: "Kyiv", PostalCode: "01001", Country: "Ukraine"))],
            Birthday = new CNDate(1985, 4, 15),
            Relations = [new("_$!<Spouse>!$_", "Bob Smoke")],
            InstantMessages = [new("_$!<Home>!$_", new CNInstantMessage("alice.smoke", "Skype"))],
            SocialProfiles = [new(null, new CNSocial(null, "alicesmoke", null, "twitter"))],
            ImageData = Png,
        });
        _contacts.Put(new CNContactData
        {
            Id = "B0B00000-0000-0000-0000-000000000001:ABPerson", ContainerId = container, DisplayName = "Bob Smoke", GivenName = "Bob", FamilyName = "Smoke",
            Phones = [new("_$!<Mobile>!$_", "+380 67 000 1122")], Emails = [new("_$!<Work>!$_", "bob@example.com")],
            Dates = [new("_$!<Anniversary>!$_", new CNDate(null, 6, 1))],
        });
        _contacts.Put(new CNContactData
        {
            Id = "0B6A0000-0000-0000-0000-000000000002:ABPerson", ContainerId = container, IsOrganization = true, OrganizationName = "Smoke Bakery",
            Phones = [new("_$!<Main>!$_", "+380 44 123 4567")],
        });
        _contacts.GroupList.Add(("GROUP-FRIENDS", "Smoke friends", container, [AliceId, "B0B00000-0000-0000-0000-000000000001:ABPerson"]));
    }

    // ── Contacts: mapping ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("_$!<Mobile>!$_", IosContactMapping.LabelKind.Phone, "cell", null)]
    [InlineData("iPhone", IosContactMapping.LabelKind.Phone, "cell", "iPhone")]
    [InlineData("_$!<AppleWatch>!$_", IosContactMapping.LabelKind.Phone, "cell", "Apple Watch")]
    [InlineData("_$!<HomeFAX>!$_", IosContactMapping.LabelKind.Phone, "home,fax", null)]
    [InlineData("_$!<OtherFAX>!$_", IosContactMapping.LabelKind.Phone, "fax", null)]
    [InlineData("_$!<Main>!$_", IosContactMapping.LabelKind.Phone, "", "Main")]
    [InlineData("_$!<Pager>!$_", IosContactMapping.LabelKind.Phone, "pager", null)]
    [InlineData("_$!<Home>!$_", IosContactMapping.LabelKind.Generic, "home", null)]
    [InlineData("_$!<Other>!$_", IosContactMapping.LabelKind.Generic, "", null)]
    [InlineData("_$!<School>!$_", IosContactMapping.LabelKind.Generic, "", "School")]
    [InlineData("iCloud", IosContactMapping.LabelKind.Generic, "", "iCloud")]
    [InlineData("_$!<HomePage>!$_", IosContactMapping.LabelKind.Generic, "", "Homepage")]
    [InlineData("_$!<Spouse>!$_", IosContactMapping.LabelKind.Relation, "spouse", null)]
    [InlineData("_$!<Father>!$_", IosContactMapping.LabelKind.Relation, "", "Father")]
    [InlineData("Dacha", IosContactMapping.LabelKind.Phone, "", "Dacha")]
    [InlineData(null, IosContactMapping.LabelKind.Phone, "", null)]
    public void Labels_FromTheFramework(string? label, IosContactMapping.LabelKind kind, string type, string? custom) =>
        Assert.Equal((type, custom), IosContactMapping.FromLabel(label, kind));

    [Theory]
    [InlineData("cell", null, IosContactMapping.LabelKind.Phone, "_$!<Mobile>!$_")]
    [InlineData("cell", "iPhone", IosContactMapping.LabelKind.Phone, "iPhone")]
    [InlineData("cell", "Apple Watch", IosContactMapping.LabelKind.Phone, "_$!<AppleWatch>!$_")]
    [InlineData("work,fax", null, IosContactMapping.LabelKind.Phone, "_$!<WorkFAX>!$_")]
    [InlineData("fax", null, IosContactMapping.LabelKind.Phone, "_$!<OtherFAX>!$_")]
    [InlineData("work,cell", null, IosContactMapping.LabelKind.Phone, "_$!<Mobile>!$_")]
    [InlineData("cell,voice", null, IosContactMapping.LabelKind.Phone, "_$!<Mobile>!$_")]
    [InlineData("", "Main", IosContactMapping.LabelKind.Phone, "_$!<Main>!$_")]
    [InlineData("", "Dacha", IosContactMapping.LabelKind.Phone, "Dacha")]
    [InlineData("", null, IosContactMapping.LabelKind.Phone, "_$!<Other>!$_")]
    [InlineData("home", null, IosContactMapping.LabelKind.Generic, "_$!<Home>!$_")]
    [InlineData("", "Homepage", IosContactMapping.LabelKind.Generic, "_$!<HomePage>!$_")]
    [InlineData("", "iCloud", IosContactMapping.LabelKind.Generic, "iCloud")]
    [InlineData("spouse", null, IosContactMapping.LabelKind.Relation, "_$!<Spouse>!$_")]
    [InlineData("", "Father", IosContactMapping.LabelKind.Relation, "_$!<Father>!$_")]
    [InlineData("", "Godmother", IosContactMapping.LabelKind.Relation, "Godmother")]
    public void Labels_BackToTheFramework(string type, string? label, IosContactMapping.LabelKind kind, string expected) =>
        Assert.Equal(expected, IosContactMapping.ToLabel(type, label, kind));

    [Fact]
    public void Labels_EverySystemLabelRoundTrips()
    {
        foreach (var name in new[] { "Home", "Work", "Other", "Mobile", "iPhone", "Main", "HomeFAX", "WorkFAX", "OtherFAX", "Pager", "AppleWatch", "School" })
        {
            var raw = IosContactMapping.SystemLabel(name);
            var (type, label) = IosContactMapping.FromLabel(raw, IosContactMapping.LabelKind.Phone);
            Assert.Equal(raw, IosContactMapping.ToLabel(type, label, IosContactMapping.LabelKind.Phone));
        }
    }

    [Fact]
    public void Containers_AreAccounts_WithTheirKind()
    {
        string Label(string name, CNContainerKind kind) => IosContactMapping.TypeLabel(new CNContainerData("x", name, kind));
        Assert.Equal("iPhone", Label("", CNContainerKind.Local));
        Assert.Equal("iCloud", Label("iCloud", CNContainerKind.CardDav));
        Assert.Equal("iCloud", Label("Card", CNContainerKind.CardDav));
        Assert.Equal("CardDAV", Label("Fastmail", CNContainerKind.CardDav));
        Assert.Equal("Exchange", Label("Work", CNContainerKind.Exchange));
        var icloud = IosContactMapping.Account(new CNContainerData(ICloud, "Card", CNContainerKind.CardDav), 12);
        Assert.Equal((ICloud, "iCloud", "carddav", "iCloud", 12, false), (icloud.Id, icloud.Name, icloud.Type, icloud.TypeLabel, icloud.Contacts, icloud.ReadOnly));
        Assert.Equal(CollectionLayout.HashId(ICloud), icloud.ShortId);
        var local = IosContactMapping.Account(new CNContainerData(Local, "", CNContainerKind.Local), 0);
        Assert.Equal(("iPhone", "local"), (local.Name, local.Type));
        Assert.Equal("410FE041-5C4E-48DA-B4DE-04C15EA3DBAC_ABPerson", IosContactMapping.ShortId(AliceId));
    }

    [Fact]
    public void Access_FromTheAuthorization()
    {
        Assert.Equal(MediaAccess.Full, IosContactMapping.Access(CNAuth.Authorized));
        Assert.Equal(MediaAccess.Partial, IosContactMapping.Access(CNAuth.Limited));
        Assert.Equal(MediaAccess.None, IosContactMapping.Access(CNAuth.Denied));
        Assert.Equal(MediaAccess.None, IosContactMapping.Access(CNAuth.NotDetermined));
        Assert.Equal(MediaAccess.None, IosContactMapping.Access(CNAuth.Restricted));
        _contacts.Authorization = CNAuth.Limited;
        Assert.True(ContactsSource().CanWrite); // limited access still adds (the new ones join the shared ones)
        _contacts.Authorization = CNAuth.Denied;
        var source = ContactsSource();
        Assert.False(source.CanWrite);
        var tree = CollectionTree.Contacts(source, null, IosContactMapping.Source);
        Assert.False(tree.Exists());
        Assert.Contains("may not read the contacts", tree.UnavailableReason);
        Assert.Throws<UnauthorizedAccessException>(() => source.Accounts());
    }

    [Fact]
    public void Contact_MapsEveryField_AndTheGroups()
    {
        SeedContacts();
        var source = ContactsSource();
        _ = source.Contacts();
        var alice = source.Read(AliceId)!;
        Assert.Equal(("Alice Smoke", Local, "410FE041-5C4E-48DA-B4DE-04C15EA3DBAC_ABPerson"), (alice.DisplayName, alice.AccountId, alice.ShortId));
        Assert.Equal(["Al"], alice.Nicknames);
        Assert.Equal([new LabeledValue("+380 50 111 2233", "cell"), new LabeledValue("+380 50 444 5566", "cell", "iPhone"), new LabeledValue("+380 44 777 8899", "", "Dacha")], alice.Phones);
        Assert.Equal([new LabeledValue("https://alice.example.com", "", "Homepage")], alice.Websites);
        Assert.Equal([new ContactDate("1985-04-15", "birthday")], alice.Dates);
        Assert.Equal([new LabeledValue("Bob Smoke", "spouse")], alice.Relations);
        Assert.Equal([new LabeledValue("alice.smoke", "skype"), new LabeledValue("alicesmoke", "x-social-twitter")], alice.Messaging);
        Assert.Equal(new Organization("Smoke Labs", null, "Engineer"), Assert.Single(alice.Organizations));
        Assert.Equal(("Kyiv", "work"), (alice.Addresses[0].City, alice.Addresses[0].Type));
        Assert.Equal(["Smoke friends"], alice.Groups);
        Assert.Equal(Png, alice.Photo);
        Assert.Null(alice.Note); // never read: needs Apple's entitlement
        Assert.Null(source.Read(AliceId, withPhoto: false)!.Photo);
        var bob = source.Read("B0B00000-0000-0000-0000-000000000001:ABPerson")!;
        Assert.Equal([new ContactDate("--06-01", "anniversary")], bob.Dates);
        var bakery = source.Read("0B6A0000-0000-0000-0000-000000000002:ABPerson")!;
        Assert.Equal("Smoke Bakery", VCard.FormattedName(bakery));
        Assert.Equal([new LabeledValue("+380 44 123 4567", "", "Main")], bakery.Phones);
    }

    [Fact]
    public void Contact_BackToTheFramework()
    {
        var c = new Contact
        {
            Id = "android:raw:5", ShortId = "5", AccountId = "x", DisplayName = "Олена Шевченко", Name = new StructuredName("Шевченко", "Олена", PhoneticGiven: "Olena"),
            Phones = [new LabeledValue("+380 44 1", "cell", Preferred: true), new LabeledValue("+380 44 2", "", "Car")],
            Dates = [new ContactDate("1990-02-03", "birthday"), new ContactDate("--07-04", "anniversary"), new ContactDate("2001-01-01", "other", "Wedding"), new ContactDate("soon", "other")],
            Messaging = [new LabeledValue("lena", "xmpp"), new LabeledValue("olena", "x-social-twitter")], Sip = [new LabeledValue("sip:lena@example.com")],
            Addresses = [new PostalAddress("1 Main St", "PO 5", City: "Kyiv", Type: "home")],
            Organizations = [new Organization("Acme", "R&D", "Boss")], Note = "secret", Groups = ["Family"], Photo = Png,
        };
        var d = IosContactMapping.FromContact(c);
        Assert.Equal(("Олена", "Шевченко", "Olena", false), (d.GivenName, d.FamilyName, d.PhoneticGivenName, d.IsOrganization));
        Assert.Equal([new CNLabeled<string>("_$!<Mobile>!$_", "+380 44 1"), new CNLabeled<string>("Car", "+380 44 2")], d.Phones);
        Assert.Equal(new CNDate(1990, 2, 3), d.Birthday);
        Assert.Equal([new CNLabeled<CNDate>("_$!<Anniversary>!$_", new CNDate(null, 7, 4)), new CNLabeled<CNDate>("Wedding", new CNDate(2001, 1, 1))], d.Dates);
        Assert.Equal([new CNLabeled<CNInstantMessage>(null, new CNInstantMessage("lena", "Jabber")), new CNLabeled<CNInstantMessage>(null, new CNInstantMessage("lena@example.com", "SIP"))],
            d.InstantMessages);
        Assert.Equal("olena", Assert.Single(d.SocialProfiles).Value.Username);
        Assert.Equal("1 Main St\nPO 5", d.Addresses[0].Value.Street);
        Assert.Equal(("Acme", "R&D", "Boss"), (d.OrganizationName, d.DepartmentName, d.JobTitle));
        Assert.Equal(Png, d.ImageData);
        var company = IosContactMapping.FromContact(new Contact { Id = "x", ShortId = "x", AccountId = "x", DisplayName = "Acme", Organizations = [new Organization("Acme")] });
        Assert.True(company.IsOrganization);
        Assert.Null(company.GivenName);
        var bare = IosContactMapping.FromContact(new Contact { Id = "x", ShortId = "x", AccountId = "x", DisplayName = "Just a name" });
        Assert.Equal("Just a name", bare.GivenName);
    }

    // ── Contacts: backup ──────────────────────────────────────────────────────

    [Fact]
    public async Task Backup_WritesTheCards_WithIosSource()
    {
        SeedContacts();
        var repo = await RepoAsync();
        var files = await FilesAsync(repo, await BackUpContactsAsync(repo));
        var dir = $"iPhone~{CollectionLayout.HashId(Local)}";
        Assert.Equal(
            [$"{dir}/Alice Smoke~410FE041-5C4E-48DA-B4DE-04C15EA3DBAC_ABPerson.vcf", $"{dir}/Bob Smoke~B0B00000-0000-0000-0000-000000000001_ABPerson.vcf",
             $"{dir}/Smoke Bakery~0B6A0000-0000-0000-0000-000000000002_ABPerson.vcf", $"{dir}/account.json"],
            files.Keys.Order(StringComparer.Ordinal));
        var info = CollectionInfoFile.Parse(Encoding.UTF8.GetBytes(files[$"{dir}/account.json"]));
        Assert.Equal(("ios-contacts", Local, "iPhone", "local", "iPhone"), (info.Source, info.Id, info.Name, info.Type, info.TypeLabel));
        var card = files[$"{dir}/Alice Smoke~410FE041-5C4E-48DA-B4DE-04C15EA3DBAC_ABPerson.vcf"].Replace("\r\n ", "");
        Assert.Contains($"UID:{AliceId}\r\n", card);
        Assert.Contains("TEL;TYPE=cell:+380 50 111 2233\r\n", card);
        Assert.Contains("item1.TEL;TYPE=cell:+380 50 444 5566\r\nitem1.X-ABLABEL:iPhone\r\n", card);
        Assert.Contains("item2.TEL:+380 44 777 8899\r\nitem2.X-ABLABEL:Dacha\r\n", card);
        Assert.Contains("BDAY:19850415\r\n", card);
        Assert.Contains("IMPP:skype:alice.smoke\r\n", card);
        Assert.Contains("IMPP:x-social-twitter:alicesmoke\r\n", card);
        Assert.Contains("RELATED;TYPE=spouse;VALUE=text:Bob Smoke\r\n", card);
        Assert.Contains("CATEGORIES:Smoke friends\r\n", card);
        Assert.Contains("PHOTO:data:image/png;base64,", card);
        Assert.DoesNotContain("NOTE", card);
        Assert.Contains("ANNIVERSARY:--0601\r\n", files[$"{dir}/Bob Smoke~B0B00000-0000-0000-0000-000000000001_ABPerson.vcf"]);
    }

    [Fact]
    public async Task SecondRun_ReadsNoUnchangedPhoto_AndSeesAChangedContact()
    {
        SeedContacts();
        var repo = await RepoAsync();
        var first = await BackUpContactsAsync(repo);
        Assert.Equal(1, _contacts.ImageReads); // Alice, the only one with a photo… read once to write her card
        _contacts.ImageReads = 0;
        var second = await BackUpContactsAsync(repo);
        Assert.Equal(0, _contacts.ImageReads);
        Assert.Equal((0, 0), (second.Stats.FilesNew, second.Stats.FilesChanged));
        Assert.Equal(first.Tree, second.Tree);

        // Bob's number changes: only Bob's card is generated and read again; a new photo for Alice too.
        var bob = _contacts.ById["B0B00000-0000-0000-0000-000000000001:ABPerson"];
        _contacts.Put(bob with { Phones = [new("_$!<Mobile>!$_", "+380 67 999 0000")] });
        var third = await BackUpContactsAsync(repo);
        Assert.Equal((0, 1, 0), (third.Stats.FilesNew, third.Stats.FilesChanged, _contacts.ImageReads));
        Assert.Contains("TEL;TYPE=cell:+380 67 999 0000", (await FilesAsync(repo, third)).Single(f => f.Key.Contains("Bob")).Value);

        var newPhoto = Png.ToArray();
        newPhoto[^20] ^= 0x5A;
        _contacts.Put(_contacts.ById[AliceId] with { ImageData = newPhoto, ThumbnailData = null });
        var fourth = await BackUpContactsAsync(repo);
        Assert.Equal((1, 1), (fourth.Stats.FilesChanged, _contacts.ImageReads));
    }

    [Fact]
    public async Task Versions_SurviveTheProcess_AndAreForgottenWithTheContact()
    {
        SeedContacts();
        var repo = await RepoAsync();
        await BackUpContactsAsync(repo);
        _contacts.ImageReads = 0;
        await BackUpContactsAsync(repo, ContactsSource()); // a new process: the versions and sizes from disk
        Assert.Equal(0, _contacts.ImageReads);
        _contacts.ById.Remove(AliceId);
        var after = await BackUpContactsAsync(repo);
        var json = File.ReadAllText(Path.Combine(_dir, "state", "ios-contact-versions.json"));
        Assert.DoesNotContain(AliceId, json);
        Assert.Contains("B0B00000", json);
        Assert.Equal(0, after.Stats.FilesNew);
    }

    // ── Contacts: restore ─────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_IntoTheWipedContainer_GroupsMadeAgain_NothingDuplicated()
    {
        SeedContacts();
        var repo = await RepoAsync();
        var snapshot = await BackUpContactsAsync(repo);
        _contacts.Wipe(Local);
        var source = ContactsSource();
        var result = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, source, null, ct: TestContext.Current.CancellationToken);
        Assert.Equal((3, 0), (result.Restored, result.Skipped));
        Assert.Empty(result.Errors);
        var alice = _contacts.ById.Values.Single(c => c.GivenName == "Alice");
        Assert.Equal(Local, alice.ContainerId);
        Assert.Equal([new CNLabeled<string>("_$!<Mobile>!$_", "+380 50 111 2233"), new CNLabeled<string>("iPhone", "+380 50 444 5566"), new CNLabeled<string>("Dacha", "+380 44 777 8899")],
            alice.Phones);
        Assert.Equal(new CNDate(1985, 4, 15), alice.Birthday);
        Assert.Equal("_$!<Spouse>!$_", alice.Relations[0].Label);
        Assert.Equal("_$!<HomePage>!$_", alice.Urls[0].Label);
        Assert.Equal("alicesmoke", alice.SocialProfiles[0].Value.Username);
        Assert.Equal("Skype", alice.InstantMessages[0].Value.Service);
        Assert.Equal(Png, alice.ImageData);
        Assert.True(_contacts.ById.Values.Single(c => c.OrganizationName == "Smoke Bakery").IsOrganization);
        var group = Assert.Single(_contacts.GroupList);
        Assert.Equal(("Smoke friends", Local, 2), (group.Name, group.ContainerId, group.Members.Count));

        var again = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, ContactsSource(), null, ct: TestContext.Current.CancellationToken);
        Assert.Equal((0, 3), (again.Restored, again.Skipped));
        Assert.Equal(3, _contacts.ById.Count);
        Assert.Single(_contacts.GroupList);
    }

    [Fact]
    public async Task Restore_OnAnotherIPhone_FindsICloudByName_OrReportsTheAccount()
    {
        SeedContacts(ICloud);
        var repo = await RepoAsync();
        var snapshot = await BackUpContactsAsync(repo);

        // A new iPhone: iCloud's container has another identifier.
        _contacts.ById.Clear();
        _contacts.GroupList.Clear();
        _contacts.ContainerList.Clear();
        _contacts.ContainerList.Add(new CNContainerData("ICLOUD-ON-THE-NEW-PHONE", "Card", CNContainerKind.CardDav));
        _contacts.ContainerList.Add(new CNContainerData(Local, "", CNContainerKind.Local));
        var result = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, ContactsSource(), null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Restored);
        Assert.All(_contacts.ById.Values, c => Assert.Equal("ICLOUD-ON-THE-NEW-PHONE", c.ContainerId));

        // No such account at all: reported, nothing guessed, nothing added; a chosen one takes them.
        _contacts.ById.Clear();
        _contacts.ContainerList.RemoveAt(0);
        var missing = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, ContactsSource(), null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, missing.Restored);
        Assert.Contains("that account is not on this phone", Assert.Single(missing.Errors));
        Assert.Empty(_contacts.ById);
        var chosen = await new CollectionRestorer(repo).RestoreContactsAsync(snapshot, ContactsSource(), Local, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, chosen.Restored);
    }

    // ── Calendars: rules ──────────────────────────────────────────────────────

    public static TheoryData<string, EKRule, bool> Rules => new()
    {
        { "FREQ=WEEKLY;COUNT=20;BYDAY=MO", new EKRule(1, DaysOfWeek: [new EKDayOfWeek(2)], Count: 20), false },
        { "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;WKST=SU", new EKRule(1, 2, 1, [new EKDayOfWeek(2), new EKDayOfWeek(4)]), false },
        { "FREQ=MONTHLY;BYDAY=2TU", new EKRule(2, DaysOfWeek: [new EKDayOfWeek(3, 2)]), false },
        { "FREQ=MONTHLY;BYMONTHDAY=-1", new EKRule(2, DaysOfMonth: [-1]), false },
        { "FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1", new EKRule(2, DaysOfWeek: [new EKDayOfWeek(2), new EKDayOfWeek(3), new EKDayOfWeek(4), new EKDayOfWeek(5), new EKDayOfWeek(6)], SetPositions: [-1]), false },
        { "FREQ=YEARLY;BYMONTH=5;BYDAY=2SU", new EKRule(3, MonthsOfYear: [5], DaysOfWeek: [new EKDayOfWeek(1, 2)]), false },
        { "FREQ=DAILY;INTERVAL=3;UNTIL=20261231T220000Z", new EKRule(0, 3, Until: Ms(new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero))), false },
        { "FREQ=DAILY;UNTIL=20261231", new EKRule(0, Until: KyivMs(2026, 12, 31)), true },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Rules_AsRRule_AndBack(string rrule, EKRule rule, bool allDay)
    {
        Assert.Equal(rrule, IosRecurrence.ToRRule(rule, allDay, Kyiv));
        var parsed = IosRecurrence.Parse(rrule, Kyiv);
        Assert.Equal(rrule, IosRecurrence.ToRRule(parsed, allDay, Kyiv));
    }

    [Theory]
    [InlineData("FREQ=HOURLY")]
    [InlineData("FREQ=DAILY;BYHOUR=9,17")]
    [InlineData("FREQ=DAILY\nFREQ=WEEKLY")]
    [InlineData("INTERVAL=2")]
    public void Rules_EventKitCannotHold_AreRefused(string rrule) =>
        Assert.Throws<NotSupportedException>(() => IosRecurrence.Parse(rrule, Kyiv));

    private static List<string> Expand(string rrule, DateTime start, int n = 12) =>
        [.. IosRecurrence.Occurrences(IosRecurrence.Parse(rrule, Kyiv), start, Kyiv, 0, KyivMs(2030, 1, 1))!.Take(n)
            .Select(t => IosRecurrence.Local(t, Kyiv).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))];

    [Fact]
    public void Occurrences_FollowTheRule_AtTheSameLocalTime()
    {
        // Weekly at 10:00 in Kyiv across the end of summer time (25 October 2026): still 10:00.
        Assert.Equal(["2026-10-19 10:00", "2026-10-26 10:00", "2026-11-02 10:00"], Expand("FREQ=WEEKLY;COUNT=3;BYDAY=MO", new DateTime(2026, 10, 19, 10, 0, 0)));
        // Every other week on Monday and Wednesday, weeks from Sunday.
        Assert.Equal(["2026-11-02 09:00", "2026-11-04 09:00", "2026-11-16 09:00", "2026-11-18 09:00"],
            Expand("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;WKST=SU", new DateTime(2026, 11, 2, 9, 0, 0), 4));
        // The 31st: months without one have no occurrence.
        Assert.Equal(["2026-01-31 08:00", "2026-03-31 08:00", "2026-05-31 08:00"], Expand("FREQ=MONTHLY;COUNT=3", new DateTime(2026, 1, 31, 8, 0, 0)));
        Assert.Equal(["2026-01-31 08:00", "2026-02-28 08:00", "2026-03-31 08:00"], Expand("FREQ=MONTHLY;BYMONTHDAY=-1;COUNT=3", new DateTime(2026, 1, 31, 8, 0, 0)));
        Assert.Equal(["2026-11-10 18:00", "2026-12-08 18:00"], Expand("FREQ=MONTHLY;BYDAY=2TU;COUNT=2", new DateTime(2026, 11, 10, 18, 0, 0)));
        Assert.Equal(["2026-11-30 17:00", "2026-12-31 17:00"], Expand("FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=2", new DateTime(2026, 11, 30, 17, 0, 0)));
        Assert.Equal(["2027-05-09 12:00", "2028-05-14 12:00"], Expand("FREQ=YEARLY;BYMONTH=5;BYDAY=2SU;COUNT=2", new DateTime(2027, 5, 9, 12, 0, 0)));
        // UNTIL includes its own occurrence; COUNT counts the start.
        Assert.Equal(["2026-11-01 07:00", "2026-11-04 07:00"], Expand("FREQ=DAILY;INTERVAL=3;UNTIL=20261104T050000Z", new DateTime(2026, 11, 1, 7, 0, 0)));
        Assert.Null(IosRecurrence.Occurrences(new EKRule(3, WeeksOfYear: [20]), new DateTime(2026, 5, 11), Kyiv, 0, long.MaxValue));
    }

    [Fact]
    public void ContentKey_ComparesRulesInANormalForm()
    {
        var e = new CalendarEvent { Id = "a", ShortId = "a", CalendarId = "c", Title = "Standup", Start = 1, Duration = "PT30M", RRule = "FREQ=WEEKLY;BYDAY=MO;INTERVAL=1;WKST=MO;UNTIL=20261231T215959Z" };
        Assert.Equal(ICalendar.ContentKey(e), ICalendar.ContentKey(e with { RRule = "BYDAY=MO;FREQ=WEEKLY;UNTIL=20261231" }));
        Assert.NotEqual(ICalendar.ContentKey(e), ICalendar.ContentKey(e with { RRule = "FREQ=WEEKLY;BYDAY=TU" }));
    }

    // ── Calendars: occurrences → events ───────────────────────────────────────

    private void SeedCalendar(string id = Cal, string title = "Smoke", EKSourceKind source = EKSourceKind.Local, string sourceTitle = "Default")
    {
        _kit.CalendarList.Add(new EKCalendarData(id, title, "SRC-" + sourceTitle, sourceTitle, source, source == EKSourceKind.CalDav ? EKCalendarKind.CalDav : EKCalendarKind.Local, "#8c3d8c", true));
    }

    private static readonly long StandupStart = KyivMs(2026, 10, 19, 10);

    /// <summary>A weekly Monday 10:00 (Kyiv) series of 20, the second occurrence moved to 12:00, the third deleted; an all-day event; one with an alarm and a guest.</summary>
    private FakeEventKit.Item SeedEvents(string calendar = Cal)
    {
        var standup = _kit.Put(new EKEventData
        {
            EventId = "SERIES-1", ExternalId = "uid-standup@example.com", CalendarId = calendar, Title = "Standup", Start = StandupStart, End = StandupStart + 1800_000,
            TimeZone = "Europe/Kyiv", Rules = [new EKRule(1, DaysOfWeek: [new EKDayOfWeek(2)], Count: 20)], Status = 1, Availability = 0,
            Alarms = [new EKAlarmData(-600)], Created = Ms(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero)), LastModified = Ms(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero)),
        });
        standup.Detached[KyivMs(2026, 10, 26, 10)] = standup.Master with
        {
            Title = "Standup (moved)", Start = KyivMs(2026, 10, 26, 12), End = KyivMs(2026, 10, 26, 12, 30), LastModified = Ms(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)),
        };
        standup.Removed.Add(KyivMs(2026, 11, 2, 10));
        _kit.Put(new EKEventData
        {
            EventId = "HOLIDAY", ExternalId = "uid-holiday", CalendarId = calendar, Title = "Holiday", AllDay = true,
            Start = KyivMs(2026, 11, 27), End = KyivMs(2026, 11, 28, 23, 59) + 59_000, LastModified = Ms(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)),
        });
        _kit.Put(new EKEventData
        {
            EventId = "DENTIST", ExternalId = "uid-dentist", CalendarId = calendar, Title = "Dentist", Location = "Clinic", Notes = "Bring the card",
            Start = KyivMs(2026, 11, 20, 15), End = KyivMs(2026, 11, 20, 16), TimeZone = "Europe/Kyiv", Status = 1, Availability = 1,
            Organizer = new EKParticipantData("mailto:alice@example.com", "Alice"), Attendees = [new EKParticipantData("mailto:bob@example.com", "Bob", Role: 2, Status: 2)],
            Alarms = [new EKAlarmData(-1800)], LastModified = Ms(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero)),
        });
        return standup;
    }

    [Fact]
    public void Events_SeriesWithAMovedAndADeletedOccurrence()
    {
        SeedCalendar();
        SeedEvents();
        var events = CalendarSource().Events();
        Assert.Equal(["DENTIST", "HOLIDAY", "SERIES-1"], events.Select(e => e.Id));
        var standup = events[2];
        Assert.Equal(("uid-standup@example.com", StandupStart, (long?)null, "PT30M", "Europe/Kyiv"), (standup.Uid, standup.Start, standup.End, standup.Duration, standup.TimeZone));
        Assert.Equal("FREQ=WEEKLY;COUNT=20;BYDAY=MO", standup.RRule);
        Assert.Equal("Europe/Kyiv;20261102T100000", standup.ExDate);
        var moved = Assert.Single(standup.Exceptions);
        Assert.Equal(("Standup (moved)", KyivMs(2026, 10, 26, 10), KyivMs(2026, 10, 26, 12)), (moved.Title, moved.OriginalStart, moved.Start));
        Assert.Equal(Ms(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)), standup.LastModified); // the series' newest change

        var ics = Encoding.UTF8.GetString(ICalendar.Write(standup)).Replace("\r\n ", "");
        Assert.Contains("DTSTART;TZID=Europe/Kyiv:20261019T100000\r\nDURATION:PT30M\r\nSUMMARY:Standup\r\nRRULE:FREQ=WEEKLY;COUNT=20;BYDAY=MO\r\nEXDATE;TZID=Europe/Kyiv:20261102T100000\r\n", ics);
        Assert.Contains("RECURRENCE-ID;TZID=Europe/Kyiv:20261026T100000\r\nDTSTART;TZID=Europe/Kyiv:20261026T120000\r\nDTEND;TZID=Europe/Kyiv:20261026T123000\r\nSUMMARY:Standup (moved)\r\n", ics);
        Assert.Contains("TRIGGER:-PT10M", ics);
        Assert.Contains("BEGIN:VTIMEZONE\r\nTZID:Europe/Kyiv\r\n", ics);
        var back = Assert.Single(ICalendar.Parse(Encoding.UTF8.GetBytes(ics)));
        Assert.Equal((standup.Start, standup.RRule, 1), (back.Start, back.RRule, back.Exceptions.Count));
    }

    [Fact]
    public void Events_AllDay_AndAttendees_AndAlarms()
    {
        SeedCalendar();
        SeedEvents();
        var events = CalendarSource().Events();
        var holiday = events.Single(e => e.Id == "HOLIDAY");
        Assert.Equal((true, Ms(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero)), (long?)Ms(new DateTimeOffset(2026, 11, 29, 0, 0, 0, TimeSpan.Zero)), (string?)null),
            (holiday.AllDay, holiday.Start, holiday.End, holiday.TimeZone));
        var ics = Encoding.UTF8.GetString(ICalendar.Write(holiday));
        Assert.Contains("DTSTART;VALUE=DATE:20261127\r\nDTEND;VALUE=DATE:20261129\r\n", ics);

        var dentist = events.Single(e => e.Id == "DENTIST");
        Assert.Equal(("Clinic", "Bring the card", "CONFIRMED", "TRANSPARENT", "alice@example.com"), (dentist.Location, dentist.Description, dentist.Status, dentist.Transparency, dentist.Organizer));
        Assert.Equal(new Attendee("bob@example.com", "Bob", "OPT-PARTICIPANT", "ACCEPTED"), Assert.Single(dentist.Attendees));
        Assert.Equal(new Alarm(30), Assert.Single(dentist.Alarms));
        Assert.Equal(IosEventMapping.ShortId(Cal, "uid-dentist"), dentist.ShortId);
        Assert.Contains("ATTENDEE;CN=Bob;CUTYPE=INDIVIDUAL;ROLE=OPT-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:bob@example.com", Encoding.UTF8.GetString(ICalendar.Write(dentist)).Replace("\r\n ", ""));
        Assert.Equal(new Alarm(15), IosEventMapping.Alarm(new EKAlarmData(null, KyivMs(2026, 11, 20, 14, 45)), KyivMs(2026, 11, 20, 15)));
    }

    [Fact]
    public void Events_AllDaySeries_DeletedDayAsADate()
    {
        SeedCalendar();
        var series = _kit.Put(new EKEventData
        {
            EventId = "DAILY", ExternalId = "uid-daily", CalendarId = Cal, Title = "Gym", AllDay = true, Start = KyivMs(2026, 11, 2), End = KyivMs(2026, 11, 3) - 1000,
            Rules = [new EKRule(0, Count: 10)],
        });
        series.Removed.Add(KyivMs(2026, 11, 5));
        series.Detached[KyivMs(2026, 11, 7)] = series.Master with { Title = "Gym (late)", Start = KyivMs(2026, 11, 8), End = KyivMs(2026, 11, 9) - 1000 };
        var gym = Assert.Single(CalendarSource().Events());
        Assert.Equal((Ms(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)), "P1D", "20261105"), (gym.Start, gym.Duration, gym.ExDate));
        var late = Assert.Single(gym.Exceptions);
        Assert.Equal((Ms(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero)), Ms(new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero))), (late.OriginalStart, late.Start));
        var ics = Encoding.UTF8.GetString(ICalendar.Write(gym));
        Assert.Contains("EXDATE;VALUE=DATE:20261105\r\n", ics);
        Assert.Contains("RECURRENCE-ID;VALUE=DATE:20261107\r\nDTSTART;VALUE=DATE:20261108\r\n", ics);
    }

    [Fact]
    public void Events_ASeriesSplitFromAnOccurrenceOn_IsTwoSeries_WithoutFalseDeletions()
    {
        // "This and future events" changed: EventKit ends the old series (UNTIL) and starts a new one with another identifier.
        SeedCalendar();
        _kit.Put(new EKEventData
        {
            EventId = "OLD", ExternalId = "uid-old", CalendarId = Cal, Title = "Sync", Start = KyivMs(2026, 9, 1, 9), End = KyivMs(2026, 9, 1, 10), TimeZone = "Europe/Kyiv",
            Rules = [new EKRule(1, DaysOfWeek: [new EKDayOfWeek(3)], Until: KyivMs(2026, 10, 13, 9))],
        });
        _kit.Put(new EKEventData
        {
            EventId = "NEW", ExternalId = "uid-new", CalendarId = Cal, Title = "Sync (longer)", Start = KyivMs(2026, 10, 20, 9), End = KyivMs(2026, 10, 20, 11), TimeZone = "Europe/Kyiv",
            Rules = [new EKRule(1, DaysOfWeek: [new EKDayOfWeek(3)])],
        });
        var events = CalendarSource().Events();
        Assert.Equal(2, events.Count);
        var old = events.Single(e => e.Id == "OLD");
        Assert.Equal(("FREQ=WEEKLY;UNTIL=20261013T060000Z;BYDAY=TU", null), (old.RRule, old.ExDate));
        var current = events.Single(e => e.Id == "NEW");
        Assert.Equal(("PT2H", (string?)null, 0), (current.Duration, current.ExDate, current.Exceptions.Count)); // a never-ending series: up to 5 years ahead, nothing deleted
    }

    [Fact]
    public void Events_ADetachedOccurrenceUnderItsOwnIdentifier_JoinsItsSeries()
    {
        SeedCalendar();
        var standup = SeedEvents();
        standup.DetachedId = "SERIES-1/RID=20261026";
        var series = CalendarSource().Events().Single(e => e.Uid == "uid-standup@example.com");
        Assert.Equal("Standup (moved)", Assert.Single(series.Exceptions).Title);
        Assert.Equal("Europe/Kyiv;20261102T100000", series.ExDate);
    }

    [Fact]
    public void Events_ARuleThisDoesNotComputeTheSame_InfersNoDeletions()
    {
        SeedCalendar();
        // EventKit lists an occurrence the rule (as read) does not have: nothing is inferred rather than wrong dates excluded.
        var odd = _kit.Put(new EKEventData
        {
            EventId = "ODD", CalendarId = Cal, Title = "Odd", Start = KyivMs(2026, 11, 2, 9), End = KyivMs(2026, 11, 2, 10), TimeZone = "Europe/Kyiv",
            Rules = [new EKRule(0, Count: 10)],
        });
        odd.Removed.Add(KyivMs(2026, 11, 4, 9));
        var messages = new List<string>();
        var events = new IosCalendarSource(new ShiftedKit(_kit), () => Ms(Now), messages.Add).Events();
        Assert.Null(Assert.Single(events).ExDate);
        Assert.Contains(messages, m => m.Contains("deleted occurrences are not inferred"));
    }

    /// <summary>EventKit answering with every occurrence an hour late: a rule read differently than EventKit computes it.</summary>
    private sealed class ShiftedKit(FakeEventKit inner) : IEventKit
    {
        public EKAuth Authorization => inner.Authorization;
        public string DeviceTimeZone => inner.DeviceTimeZone;
        public IReadOnlyList<EKCalendarData> Calendars() => inner.Calendars();
        public IReadOnlyList<EKEventData> Occurrences(IReadOnlyCollection<string> calendarIds, long from, long to) =>
            [.. inner.Occurrences(calendarIds, from, to).Select(o => o with { OccurrenceDate = o.OccurrenceDate + 3600_000, Start = o.Start + 3600_000, End = o.End + 3600_000 })];
        public EKEventData? Event(string eventId) => inner.Event(eventId);
        public string Add(EKEventData e, string calendarId) => inner.Add(e, calendarId);
        public bool ChangeOccurrence(string eventId, string calendarId, long occurrenceDate, bool allDay, EKEventData? changes) =>
            inner.ChangeOccurrence(eventId, calendarId, occurrenceDate, allDay, changes);
    }

    [Fact]
    public void Calendars_FromTheSources_BirthdaysLeftOut_ReadOnlyMarked()
    {
        SeedCalendar();
        SeedCalendar("CAL-ICLOUD", "Home", EKSourceKind.CalDav, "iCloud");
        _kit.CalendarList.Add(new EKCalendarData("CAL-BDAY", "Birthdays", "SRC-B", "Other", EKSourceKind.Birthdays, EKCalendarKind.Birthday, "#888888", false));
        _kit.CalendarList.Add(new EKCalendarData("CAL-HOL", "Holidays", "SRC-S", "Subscribed Calendars", EKSourceKind.Subscribed, EKCalendarKind.Subscription, "#00aa00", false));
        SeedEvents();
        var list = CalendarSource().Calendars();
        Assert.Equal(["Holidays", "Home", "Smoke"], list.Select(c => c.Name));
        var smoke = list.Single(c => c.Id == Cal);
        Assert.Equal(("iPhone", "iPhone", "#8c3d8c", 3, false), (smoke.AccountName, smoke.AccountType, smoke.Color, smoke.Events, smoke.ReadOnly));
        Assert.Equal(("iCloud", "iCloud"), (list.Single(c => c.Id == "CAL-ICLOUD").AccountName, list.Single(c => c.Id == "CAL-ICLOUD").AccountType));
        Assert.True(list.Single(c => c.Id == "CAL-HOL").ReadOnly);
        Assert.Equal(CollectionLayout.HashId(Cal), smoke.ShortId);
    }

    [Fact]
    public void Access_FullOnly_WriteOnlyCannotBackUp()
    {
        Assert.Equal(MediaAccess.Full, IosEventMapping.Access(EKAuth.FullAccess));
        Assert.Equal(MediaAccess.None, IosEventMapping.Access(EKAuth.WriteOnly));
        _kit.Authorization = EKAuth.WriteOnly;
        var source = CalendarSource();
        Assert.False(source.CanWrite);
        Assert.Throws<UnauthorizedAccessException>(() => source.Events());
        Assert.False(CollectionTree.Calendars(source, null, Ms(Now), IosEventMapping.Source).Exists());
    }

    [Fact]
    public void Window_SinceAndFiveYearsAhead_InPiecesEventKitAnswers()
    {
        SeedCalendar();
        _kit.Put(new EKEventData { EventId = "OLD", CalendarId = Cal, Title = "Long ago", Start = KyivMs(2020, 1, 10, 9), End = KyivMs(2020, 1, 10, 10) });
        _kit.Put(new EKEventData { EventId = "FAR", CalendarId = Cal, Title = "Far ahead", Start = KyivMs(2033, 1, 10, 9), End = KyivMs(2033, 1, 10, 10) });
        _kit.Put(new EKEventData { EventId = "WEEKLY", CalendarId = Cal, Title = "Forever", Start = KyivMs(2019, 1, 7, 9), End = KyivMs(2019, 1, 7, 10), TimeZone = "Europe/Kyiv", Rules = [new EKRule(1)] });
        var since = Ms(Now) - 730L * 86400000;
        var events = CalendarSource().Events(null, since);
        Assert.Equal(["WEEKLY"], events.Select(e => e.Id)); // the old event out, the never-ending series in (from its first start), beyond five years out
        Assert.Equal(KyivMs(2019, 1, 7, 9), events[0].Start);
        Assert.Null(events[0].ExDate);
        Assert.Equal(["OLD", "WEEKLY"], CalendarSource().Events().Select(e => e.Id)); // everything: from 1970
        Assert.True(_kit.OccurrenceCalls > 10); // in pieces (the fake refuses more than four years)
    }

    [Fact]
    public async Task Backup_TheCalendarTree_WithIosSource_AndUnchangedEventsStoredOnce()
    {
        SeedCalendar();
        SeedEvents();
        var repo = await RepoAsync();
        var first = await BackUpCalendarsAsync(repo);
        var files = await FilesAsync(repo, first);
        var dir = $"Smoke~{CollectionLayout.HashId(Cal)}";
        Assert.Equal([$"{dir}/Dentist~{IosEventMapping.ShortId(Cal, "uid-dentist")}.ics", $"{dir}/Holiday~{IosEventMapping.ShortId(Cal, "uid-holiday")}.ics",
            $"{dir}/Standup~{IosEventMapping.ShortId(Cal, "uid-standup@example.com")}.ics", $"{dir}/calendar.json"], files.Keys.Order(StringComparer.Ordinal));
        var info = CollectionInfoFile.Parse(Encoding.UTF8.GetBytes(files[$"{dir}/calendar.json"]));
        Assert.Equal(("ios-eventkit", Cal, "Smoke", "iPhone", "iPhone", "#8c3d8c"), (info.Source, info.Id, info.Name, info.Account, info.AccountType, info.Color));

        // The file's time is the event's last modification (the series': its newest occurrence change).
        var tree = CollectionTree.Calendars(CalendarSource(), null, Ms(Now), IosEventMapping.Source);
        Assert.Equal(Ms(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero)), tree.Stat($"{dir}/Dentist~{IosEventMapping.ShortId(Cal, "uid-dentist")}.ics")!.Modified);

        var second = await BackUpCalendarsAsync(repo);
        Assert.Equal((0, 0L), (second.Stats.FilesChanged, second.Stats.BytesAdded));
        _kit.Items.Single(i => i.Master.EventId == "DENTIST").Master = _kit.Items.Single(i => i.Master.EventId == "DENTIST").Master with { Location = "Clinik" };
        var third = await BackUpCalendarsAsync(repo);
        Assert.Equal(1, third.Stats.FilesChanged); // the same size, still seen
    }

    // ── Calendars: restore ────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_IntoTheWipedCalendar_SeriesWithItsChanges_NothingDuplicated()
    {
        SeedCalendar();
        SeedEvents();
        var repo = await RepoAsync();
        var snapshot = await BackUpCalendarsAsync(repo);
        _kit.Items.Clear();
        var result = await new CollectionRestorer(repo).RestoreCalendarsAsync(snapshot, CalendarSource(), null, ct: TestContext.Current.CancellationToken);
        Assert.Equal((3, 0), (result.Restored, result.Skipped));
        Assert.Empty(result.Errors);
        var standup = _kit.Items.Single(i => i.Master.Title == "Standup");
        Assert.Equal((Cal, StandupStart, StandupStart + 1800_000, "Europe/Kyiv"), (standup.Master.CalendarId, standup.Master.Start, standup.Master.End, standup.Master.TimeZone));
        Assert.Equal("FREQ=WEEKLY;COUNT=20;BYDAY=MO", IosRecurrence.ToRRule(Assert.Single(standup.Master.Rules), false, Kyiv));
        Assert.Equal([KyivMs(2026, 11, 2, 10)], standup.Removed);
        var moved = Assert.Single(standup.Detached);
        Assert.Equal((KyivMs(2026, 10, 26, 10), "Standup (moved)", KyivMs(2026, 10, 26, 12)), (moved.Key, moved.Value.Title, moved.Value.Start));
        Assert.Equal(-600, Assert.Single(standup.Master.Alarms).RelativeSeconds);
        var holiday = _kit.Items.Single(i => i.Master.Title == "Holiday").Master;
        Assert.Equal((true, KyivMs(2026, 11, 27), KyivMs(2026, 11, 29) - 1000), (holiday.AllDay, holiday.Start, holiday.End));
        var dentist = _kit.Items.Single(i => i.Master.Title == "Dentist").Master;
        Assert.Equal(("Clinic", "Bring the card", 1, -1800.0), (dentist.Location, dentist.Notes, dentist.Availability, dentist.Alarms[0].RelativeSeconds));
        Assert.Empty(dentist.Attendees); // EventKit cannot write guests

        // The restored events read back as they were backed up: the same files.
        var again = await new CollectionRestorer(repo).RestoreCalendarsAsync(snapshot, CalendarSource(), null, ct: TestContext.Current.CancellationToken);
        Assert.Equal((0, 3), (again.Restored, again.Skipped));
        Assert.Equal(3, _kit.Items.Count);
        var standupBack = CalendarSource().Events().Single(e => e.Title == "Standup");
        Assert.Equal(("Europe/Kyiv;20261102T100000", 1), (standupBack.ExDate, standupBack.Exceptions.Count));
    }

    [Fact]
    public async Task Restore_IntoAnotherCalendar_OrOneOfTheSameName_ReadOnlyRefused()
    {
        SeedCalendar("CAL-ICLOUD", "Home", EKSourceKind.CalDav, "iCloud");
        SeedEvents("CAL-ICLOUD");
        var repo = await RepoAsync();
        var snapshot = await BackUpCalendarsAsync(repo);

        // Another iPhone: iCloud's "Home" has a new identifier.
        _kit.Items.Clear();
        _kit.CalendarList.Clear();
        SeedCalendar("CAL-ICLOUD-NEW", "Home", EKSourceKind.CalDav, "iCloud");
        SeedCalendar();
        var result = await new CollectionRestorer(repo).RestoreCalendarsAsync(snapshot, CalendarSource(), null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Restored);
        Assert.All(_kit.Items, i => Assert.Equal("CAL-ICLOUD-NEW", i.Master.CalendarId));

        var chosen = await new CollectionRestorer(repo).RestoreCalendarsAsync(snapshot, CalendarSource(), Cal, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, chosen.Restored);
        Assert.Equal(3, _kit.Items.Count(i => i.Master.CalendarId == Cal));

        _kit.CalendarList.Add(new EKCalendarData("CAL-HOL", "Holidays", "SRC-S", "Subscribed", EKSourceKind.Subscribed, EKCalendarKind.Subscription, null, false));
        var refused = await new CollectionRestorer(repo).RestoreCalendarsAsync(snapshot, CalendarSource(), "CAL-HOL", ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, refused.Restored);
        Assert.Contains("does not take new events", Assert.Single(refused.Errors));
    }

    [Fact]
    public async Task Restore_WhatIOSCannotStore_IsReported_AfterTheSeriesIsIn()
    {
        SeedCalendar();
        var source = CalendarSource();
        var calendar = source.Calendars().Single();
        var withRDate = new CalendarEvent
        {
            Id = "a", ShortId = "a", CalendarId = Cal, Title = "Extra", Start = KyivMs(2026, 11, 2, 9), Duration = "PT1H", TimeZone = "Europe/Kyiv",
            RRule = "FREQ=WEEKLY;COUNT=3", RDate = "Europe/Kyiv;20261125T090000",
        };
        var e = await Assert.ThrowsAsync<IOException>(() => source.InsertAsync(calendar, withRDate, CancellationToken.None));
        Assert.Contains("RDATE", e.Message);
        Assert.Single(_kit.Items); // the series is in
        await Assert.ThrowsAsync<NotSupportedException>(() => source.InsertAsync(calendar, withRDate with { RRule = "FREQ=HOURLY", RDate = null }, CancellationToken.None));
        Assert.Single(_kit.Items); // nothing added for a rule iOS cannot hold
        var utc = IosEventMapping.ToEventKit(new CalendarEvent { Id = "u", ShortId = "u", CalendarId = Cal, Start = 0, End = 3600_000 }, Kyiv);
        Assert.Equal("UTC", utc.TimeZone);
    }

    [Fact]
    public void Dates_OfAnExDate_InTheirZone()
    {
        Assert.Equal([KyivMs(2026, 11, 2, 10), KyivMs(2026, 11, 9, 10)], IosEventMapping.Dates("TZID=Europe/Kyiv:20261102T100000,20261109T100000", TimeZoneInfo.Utc));
        Assert.Equal([KyivMs(2026, 11, 2, 10)], IosEventMapping.Dates("20261102T100000", Kyiv));
        Assert.Equal([Ms(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero))], IosEventMapping.Dates("20261102T080000Z", Kyiv));
        Assert.Equal([Ms(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero))], IosEventMapping.Dates("20261105", Kyiv));
    }

    // ── The process: change triggers and background work ─────────────────────

    [Fact]
    public void ContactsAndCalendarsFolders_AreInTheBackgroundPlan_AndTheirChangesDebounced()
    {
        var contacts = Folder(FolderKind.Contacts) with { FolderId = 4 };
        var calendars = Folder(FolderKind.Calendars) with { FolderId = 5 };
        var plan = BackgroundPlanner.Plan([contacts, calendars]);
        Assert.Equal([4, 5], plan.Select(p => p.FolderId).Order());
        Assert.Equal([4], BackgroundPlanner.FoldersOfKind([contacts, calendars], FolderKind.Contacts));
        Assert.Equal([5], BackgroundPlanner.FoldersOfKind([contacts, calendars], FolderKind.Calendars));
        Assert.Equal([4, 5], IosBackgroundPlan.FoldersFor(IosTaskKind.Refresh, plan, new Dictionary<int, DateTimeOffset>(), DateTimeOffset.UtcNow).Order());

        // CNContactStoreDidChange arriving in a burst (a sync, a restore's inserts): one run.
        var now = DateTimeOffset.UtcNow;
        var scheduled = new List<(TimeSpan Delay, Action Action)>();
        var fired = 0;
        var debouncer = new PhotoChangeDebouncer(() => fired++, now: () => now, schedule: (d, a) => { scheduled.Add((d, a)); return new NoDispose(); });
        for (var i = 0; i < 5; i++) debouncer.Changed();
        foreach (var (_, action) in scheduled) action();
        Assert.Equal(1, fired);
    }

    private sealed class NoDispose : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public void Texts_InIOSWords()
    {
        Assert.Contains("Allow Full Access", IosCollectionTexts.ContactsNote);
        Assert.Contains("notes", IosCollectionTexts.ContactsNote);
        Assert.Contains("Limited Access", IosCollectionTexts.ContactsPartial);
        Assert.Contains("Add Events Only", IosCollectionTexts.CalendarsNote);
        Assert.Contains("Guests", IosCollectionTexts.CalendarsNote);
        Assert.DoesNotContain("Android", IosCollectionTexts.ContactsNote + IosCollectionTexts.ContactsPartial + IosCollectionTexts.CalendarsNote);
        _ = JsonSerializer.Serialize(new { IosCollectionTexts.ContactsNote });
    }
}
