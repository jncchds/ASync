using System.Security.Cryptography;
using System.Text;
using ASync.App.iOS;
using ASync.App.ViewModels;
using ASync.Client.Core;
using ASync.Client.Core.Browse;
using ASync.Client.Core.Secrets;
using ASync.Contracts;
using ASync.Engine.Local;
using ASync.Tests.Local;

namespace ASync.App.Tests;

/// <summary>
/// The iOS head's plain C# (src/ASync.App.iOS/Shared, linked here): how folders are written and shown, the bookmark trees
/// and their failures (behind a fake resolver), the sealed store's format, the Secure Enclave key's encodings, the browse
/// scope and the pairing links. The parts that call iOS (Keychain, NSURL, UIKit) are only built and run on a Mac (ios.yml).
/// </summary>
public sealed class IosTests : IDisposable
{
    private const string OldContainer = "/var/mobile/Containers/Data/Application/0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "async-ios-" + Guid.NewGuid().ToString("N")[..8]);

    public IosTests() => Directory.CreateDirectory(Path.Combine(_home, "Documents"));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
    }

    // iOS paths are Unix paths; CI also runs these tests on Windows, where they mean nothing.
    private static bool Unix => !OperatingSystem.IsWindows();

    // ── Locations ──────────────────────────────────────────────────────────────

    [Fact]
    public void AContainerPath_IsWrittenRelativeToHome_AlsoOneOfAnEarlierInstall()
    {
        if (!Unix) return;
        Assert.Equal("~/Documents/Sync", IosLocations.HomeRelative(_home + "/Documents/Sync", _home));
        Assert.Equal("~/Documents/Sync", IosLocations.HomeRelative(_home + "/Documents/./Sync/", _home));
        Assert.Equal("~/Documents/Sync", IosLocations.HomeRelative(OldContainer + "/Documents/Sync", _home)); // the container moved
        Assert.Equal("~/Documents/Sync", IosLocations.HomeRelative("/private" + OldContainer + "/Documents/Sync", _home));
        Assert.Equal("~/Documents", IosLocations.HomeRelative("~/Documents/x/..", _home));
        Assert.Equal("~", IosLocations.HomeRelative(_home, _home));
        Assert.Null(IosLocations.HomeRelative("~/../etc", _home)); // above the container
        Assert.Null(IosLocations.HomeRelative("/etc/hosts", _home));
        Assert.Null(IosLocations.HomeRelative("bookmark:0a1b/Sync", _home));
        Assert.Null(IosLocations.HomeRelative("Documents/Sync", _home));

        Assert.Equal(Path.Combine(_home, "Documents", "Sync"), IosLocations.Absolute(OldContainer + "/Documents/Sync", _home));
        Assert.True(IosLocations.InDocuments("~/Documents", _home));
        Assert.True(IosLocations.InDocuments(_home + "/Documents/a/b", _home));
        Assert.False(IosLocations.InDocuments("~/Documents2", _home));
        Assert.False(IosLocations.InDocuments("~/Library/Application Support/async", _home));
    }

    [Fact]
    public void Bookmarks_AreAnIdAndAReadablePart()
    {
        var location = IosLocations.Bookmark("0a1b2c3d4e5f6a7b", "iCloud Drive › Work/2026");
        Assert.Equal("bookmark:0a1b2c3d4e5f6a7b/iCloud Drive › Work-2026", location);
        Assert.True(IosLocations.TryParseBookmark(location, out var id, out var where));
        Assert.Equal(("0a1b2c3d4e5f6a7b", "iCloud Drive › Work-2026"), (id, where));
        Assert.True(IosLocations.TryParseBookmark("bookmark:abc", out id, out where));
        Assert.Equal(("abc", ""), (id, where));
        Assert.False(IosLocations.TryParseBookmark("bookmark:/x", out _, out _));
        Assert.False(IosLocations.TryParseBookmark("bookmark:a.b/x", out _, out _));
        Assert.False(IosLocations.TryParseBookmark("~/Documents", out _, out _));
        Assert.True(IosLocations.Bookmark("ab", new string('x', 300)).Length <= "bookmark:ab/".Length + 120);
    }

    [Fact]
    public void Display_ShowsWherePeopleFindTheFolder()
    {
        if (!Unix) return;
        Assert.Equal("On My iPhone › ASync", IosLocations.Display("~/Documents", _home));
        Assert.Equal("On My iPad › ASync › Sync › Photos", IosLocations.Display(_home + "/Documents/Sync/Photos", _home, "iPad"));
        Assert.Equal("ASync (app data) › tmp", IosLocations.Display("~/tmp", _home));
        Assert.Equal("iCloud Drive › Work", IosLocations.Display("bookmark:0a1b/iCloud Drive › Work", _home));
        Assert.Equal("A folder chosen on this iPhone", IosLocations.Display("bookmark:0a1b", _home));
        Assert.Equal("/somewhere/else", IosLocations.Display("/somewhere/else", _home));

        const string cloud = "/private/var/mobile/Library/Mobile Documents/";
        Assert.Equal("iCloud Drive › Work › 2026", IosLocations.DescribePicked(cloud + "com~apple~CloudDocs/Work/2026"));
        Assert.Equal("iCloud Drive", IosLocations.DescribePicked(cloud + "com~apple~CloudDocs"));
        Assert.Equal("iCloud Drive › Notes", IosLocations.DescribePicked(cloud + "iCloud~com~example~app/Documents/Notes"));
        Assert.Equal("On My iPhone › Sync",
            IosLocations.DescribePicked("/private/var/mobile/Containers/Shared/AppGroup/1234/File Provider Storage/Sync"));
        Assert.Equal("Volumes › Card", IosLocations.DescribePicked("/Volumes/Card"));
    }

    // ── The bookmark store ────────────────────────────────────────────────────

    [Fact]
    public void BookmarkStore_KeepsBookmarksAcrossRestarts_AndADamagedFileIsEmpty()
    {
        var path = Path.Combine(_home, "Library", "bookmarks.json");
        var store = new BookmarkStore(path);
        var id = store.Add([1, 2, 3], "On My iPhone › Sync");
        Assert.Matches("^[0-9a-f]{16}$", id);
        store.Replace(id, [4, 5]);
        var again = new BookmarkStore(path);
        Assert.Equal([4, 5], again.Get(id)!.Data);
        Assert.Equal("On My iPhone › Sync", again.Get(id)!.Where);
        again.Remove(id);
        Assert.Null(new BookmarkStore(path).Get(id));

        File.WriteAllText(path, "{ not json");
        Assert.Empty(new BookmarkStore(path).Ids());
    }

    // ── Trees ─────────────────────────────────────────────────────────────────

    private sealed class FakeResolver(string home) : IBookmarkResolver
    {
        /// <summary>Bookmark bytes → the folder they lead to now; a missing entry fails like a deleted folder's bookmark.</summary>
        public Dictionary<string, string> Targets { get; } = [];
        public HashSet<string> StaleOnes { get; } = [];
        public string? Failure { get; set; }
        public int Opened, Closed, Recreated;

        public byte[] Create(string path)
        {
            var key = "bm-" + Targets.Count;
            Targets[key] = path;
            return Encoding.UTF8.GetBytes(key);
        }

        public IBookmarkAccess Open(byte[] bookmark)
        {
            var key = Encoding.UTF8.GetString(bookmark);
            if (Failure is { } failure) throw new BookmarkException(failure);
            if (!Targets.TryGetValue(key, out var path)) throw new BookmarkException("The file couldn’t be opened because it doesn’t exist.");
            Opened++;
            return new Access(this, key, Path.IsPathRooted(path) ? path : Path.Combine(home, path));
        }

        private sealed class Access(FakeResolver resolver, string key, string path) : IBookmarkAccess
        {
            public string Path => path;
            public bool Stale => resolver.StaleOnes.Contains(key);
            public byte[] Recreate()
            {
                resolver.Recreated++;
                resolver.StaleOnes.Remove(key);
                return Encoding.UTF8.GetBytes(key);
            }
            public void Dispose() => resolver.Closed++;
        }
    }

    private (IosTreeFactory Factory, FakeResolver Resolver) Factory()
    {
        var resolver = new FakeResolver(_home);
        return (new IosTreeFactory(_home, new BookmarkStore(Path.Combine(_home, "Library", "bookmarks.json")), resolver), resolver);
    }

    private string Picked(IosTreeFactory factory, FakeResolver resolver, string directory, string where = "On My iPhone › Picked")
    {
        Directory.CreateDirectory(directory);
        return IosLocations.Bookmark(factory.Bookmarks.Add(resolver.Create(directory), where), where);
    }

    [Fact]
    public void TheAppsOwnFolder_KeepsItsHomeRelativeLocation_WhereverTheContainerIs()
    {
        if (!Unix) return;
        var (factory, _) = Factory();
        Directory.CreateDirectory(Path.Combine(_home, "Documents", "Sync"));
        File.WriteAllText(Path.Combine(_home, "Documents", "Sync", "a.txt"), "hello");
        foreach (var location in new[] { "~/Documents/Sync", OldContainer + "/Documents/Sync", _home + "/Documents/Sync" })
        {
            var tree = factory.Open(location);
            Assert.Equal("~/Documents/Sync", tree.Location); // the sync state's location does not move with the container
            Assert.False(tree.Watchable);
            Assert.Equal("a.txt", Assert.Single(tree.List("")).Name);
        }
        Assert.True(factory.SamePlace("~/Documents/Sync", OldContainer + "/Documents/Sync"));
        Assert.False(factory.SamePlace("~/Documents/Sync", "~/Documents/Other"));
    }

    [Fact]
    public void Validate_SaysWhatToDo()
    {
        if (!Unix) return;
        var (factory, resolver) = Factory();
        Directory.CreateDirectory(Path.Combine(_home, "Documents", "Sync"));
        Assert.Null(factory.Validate("~/Documents/Sync"));
        Assert.Null(factory.Validate(_home + "/Documents/Sync")); // what the web UI's browsing hands over
        Assert.Contains("does not exist", factory.Validate("~/Documents/Missing"));
        Assert.Contains("Change folder…", factory.Validate("~/Library/Application Support/async"));
        Assert.Contains("Change folder…", factory.Validate("/etc"));
        Assert.Contains("Change folder…", factory.Validate("Sync"));
        var picked = Picked(factory, resolver, Path.Combine(_home, "elsewhere", "Work"));
        Assert.Null(factory.Validate(picked));
        Assert.Contains("Choose the folder again", factory.Validate("bookmark:0000000000000000/Gone"));
    }

    [Fact]
    public void ABookmarkTree_PassesTheLocalTreeContract()
    {
        if (!Unix) return;
        var (factory, resolver) = Factory();
        var results = LocalTreeContract.RunAll(name =>
            factory.Open(Picked(factory, resolver, Path.Combine(_home, "picked", string.Concat(name.Where(char.IsAsciiLetterOrDigit))))));
        Assert.All(results, r => Assert.True(r.Error is null, $"{r.Name}: {r.Error}"));
        Assert.IsType<BookmarkTree>(factory.Open(Picked(factory, resolver, Path.Combine(_home, "picked", "x"))));
    }

    [Fact]
    public void ABookmarkTree_UsesTheResolvedFolder_ResolvingOnceAndAgainWhenItMoved()
    {
        if (!Unix) return;
        var (factory, resolver) = Factory();
        var location = Picked(factory, resolver, Path.Combine(_home, "a", "Work"), "On My iPhone › Work");
        var tree = factory.Open(location);
        File.WriteAllText(Path.Combine(_home, "a", "Work", "f.txt"), "x");
        Assert.Equal(location, tree.Location);
        Assert.Equal("Work", tree.Name);
        Assert.True(tree.Exists());
        Assert.Equal("f.txt", Assert.Single(tree.List("")).Name);
        Assert.Equal(1, resolver.Opened); // kept open, not resolved per call

        // The person moved the folder in the Files app: the bookmark follows it.
        Directory.Move(Path.Combine(_home, "a"), Path.Combine(_home, "b"));
        resolver.Targets[resolver.Targets.Single().Key] = Path.Combine(_home, "b", "Work");
        Assert.True(tree.Exists());
        Assert.Equal("f.txt", Assert.Single(tree.List("")).Name);
        Assert.Equal(2, resolver.Opened);
        Assert.Equal(1, resolver.Closed); // the old access was stopped
    }

    [Fact]
    public void AStaleBookmark_IsMadeAgainAndKept()
    {
        if (!Unix) return;
        var (factory, resolver) = Factory();
        var location = Picked(factory, resolver, Path.Combine(_home, "w"));
        resolver.StaleOnes.Add(resolver.Targets.Single().Key);
        Assert.True(factory.Open(location).Exists());
        Assert.Equal(1, resolver.Recreated);
    }

    [Fact]
    public void ABookmarkThatNoLongerResolves_RefusesEveryCall_AndSaysChooseTheFolderAgain()
    {
        if (!Unix) return;
        var (factory, resolver) = Factory();
        var location = Picked(factory, resolver, Path.Combine(_home, "w"), "iCloud Drive › Work");
        resolver.Failure = "The file couldn’t be opened because you don’t have permission to view it.";
        var tree = factory.Open(location);
        Assert.False(tree.Exists());
        Assert.Contains("ASync can no longer open Work (The file couldn’t be opened because you don’t have permission to view it)", tree.UnavailableReason);
        Assert.Contains(IosTreeFactory.ChooseAgain, tree.UnavailableReason);
        // Never empty: a run must stop rather than take the files for deleted.
        Assert.Contains(IosTreeFactory.ChooseAgain, Assert.Throws<UnauthorizedAccessException>(() => tree.List("")).Message);
        Assert.Throws<UnauthorizedAccessException>(() => tree.OpenAppend("x"));
        Assert.Null(tree.FileSystemPath);
    }

    [Fact]
    public void ADeletedFolder_OrAnUnknownBookmark_SaysChooseTheFolderAgain()
    {
        if (!Unix) return;
        var (factory, resolver) = Factory();
        var location = Picked(factory, resolver, Path.Combine(_home, "w"), "On My iPhone › Work");
        var tree = factory.Open(location);
        Assert.True(tree.Exists());
        Directory.Delete(Path.Combine(_home, "w"));
        Assert.False(tree.Exists());
        Assert.Contains("Work is not there any more", tree.UnavailableReason);
        Assert.Throws<UnauthorizedAccessException>(() => tree.List(""));

        var unknown = factory.Open("bookmark:00ff00ff00ff00ff/On My iPhone › Old");
        Assert.False(unknown.Exists());
        Assert.Contains("no permission for Old any more (it was reinstalled, or its data cleared). Choose the folder again", unknown.UnavailableReason);
    }

    [Fact]
    public void Prune_ForgetsBookmarksNoFolderUses_ButNotNewOnes()
    {
        if (!Unix) return;
        var (factory, resolver) = Factory();
        var now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.Combine(_home, "u"));
        var used = IosLocations.Bookmark(factory.Bookmarks.Add(resolver.Create(Path.Combine(_home, "u")), "u", now.AddDays(-1)), "u");
        var oldUnused = IosLocations.Bookmark(factory.Bookmarks.Add(resolver.Create(Path.Combine(_home, "u")), "o", now.AddDays(-1)), "o");
        var fresh = IosLocations.Bookmark(factory.Bookmarks.Add(resolver.Create(Path.Combine(_home, "u")), "f", now), "f");
        Assert.True(factory.Open(oldUnused).Exists()); // accessed: pruning must stop that too
        Assert.Equal(1, factory.Prune([used, "~/Documents/Sync"], TimeSpan.FromHours(1), now));
        Assert.True(IosLocations.TryParseBookmark(oldUnused, out var gone, out _));
        Assert.Null(factory.Bookmarks.Get(gone));
        Assert.Equal(1, resolver.Closed);
        Assert.Equal(2, factory.Bookmarks.Ids().Count);
        Assert.Contains(IosTreeFactory.ChooseAgain, factory.Validate(oldUnused));
        Assert.Null(factory.Validate(used));
        Assert.Null(factory.Validate(fresh));
    }

    [Fact]
    public void ABookmarkOfAFolderInTheContainer_IsTheSamePlaceAsItsHomePath()
    {
        if (!Unix) return;
        var (factory, resolver) = Factory();
        var location = Picked(factory, resolver, Path.Combine(_home, "Documents", "Sync"));
        Assert.True(factory.Open(location).Exists());
        Assert.True(factory.SamePlace(location, "~/Documents/Sync"));
    }

    // ── The sealed store ──────────────────────────────────────────────────────

    private sealed class MemoryKey : ISealingKey
    {
        public byte[] Key { get; set; } = RandomNumberGenerator.GetBytes(32);
        public byte[] Get() => Key;
    }

    [Fact]
    public void Sealing_RoundTrips_AndBindsTheIdAndTheVersion()
    {
        var key = new MemoryKey();
        ISecretProtector protector = new SealedStoreProtector(key);
        Assert.True(protector.SealsFiles);
        Assert.Equal("ioskc", protector.Kind);
        var secret = RandomNumberGenerator.GetBytes(100);
        var sealedValue = protector.Protect("folder-12", secret);
        Assert.StartsWith("ioskc:1:folder-12:", sealedValue);
        Assert.Equal("ioskc", SecretProtectors.KindOf(sealedValue));
        Assert.NotEqual(sealedValue, protector.Protect("folder-12", secret)); // a new nonce each time
        Assert.True(protector.TryUnprotect(sealedValue, out var back));
        Assert.Equal(secret, back);
        Assert.Equal(secret, SecretProtectors.Unprotect(sealedValue, _home, protector));

        // Another secret's id, another format version, another key, damage: never read as something else.
        Assert.Throws<CryptographicException>(() => protector.TryUnprotect(sealedValue.Replace("folder-12", "folder-13"), out _)); // the id is associated data
        Assert.Contains("newer version", Assert.Throws<CryptographicException>(() => protector.TryUnprotect(sealedValue.Replace("ioskc:1:", "ioskc:2:"), out _)).Message);
        Assert.Contains("damaged", Assert.Throws<CryptographicException>(() => protector.TryUnprotect("ioskc:1:x:AAAA", out _)).Message);
        Assert.Contains("damaged", Assert.Throws<CryptographicException>(() => protector.TryUnprotect("ioskc:1:x", out _)).Message);
        key.Key = RandomNumberGenerator.GetBytes(32);
        Assert.Contains("can no longer be read here", Assert.Throws<CryptographicException>(() => protector.TryUnprotect(sealedValue, out _)).Message);
        Assert.False(protector.TryUnprotect("androidks:identity:AAAA", out _)); // not ours
        Assert.Throws<ArgumentException>(() => protector.Protect("a:b", secret));
    }

    [Fact]
    public void TheStore_IsSealedWholly_AndKeepsTheSecureEnclaveKeysReference()
    {
        var dir = Path.Combine(_home, "Library", "store");
        var protector = new SealedStoreProtector(new MemoryKey());
        var store = new FileClientStore(dir, protector);
        var identity = new DeviceIdentity("https://sync.example.com", Guid.NewGuid(), 1, "alice", "Alice's iPhone", "ioskey:ua.pp.chds.async.device.0a1b");
        store.SaveIdentity(identity);
        store.SaveToken(new CachedToken(identity.DeviceId, "token-text", DateTime.UtcNow.AddMinutes(15)));
        store.SaveRepositoryKeys(7, [9, 9, 9]);
        foreach (var file in new[] { "identity.json", "token.json", Path.Combine("keys", "7.key") })
        {
            var text = File.ReadAllText(Path.Combine(dir, file));
            Assert.StartsWith("ioskc:1:", text);
            Assert.DoesNotContain("alice", text);
            Assert.DoesNotContain("token-text", text);
        }
        var again = new FileClientStore(dir, protector);
        Assert.Equal(identity, again.LoadIdentity());
        Assert.Equal("token-text", again.LoadToken()!.AccessToken);
        Assert.Equal([9, 9, 9], again.LoadRepositoryKeys(7));
    }

    [Fact]
    public void TheStore_ReadsFilesOfAnEarlierFormat_AndRewritesThemSealed()
    {
        var dir = Path.Combine(_home, "Library", "store");
        var (pkcs8, _) = DeviceIdentity.NewKeyPair();
        var identity = new DeviceIdentity("http://10.0.0.2:5100", Guid.NewGuid(), 1, "alice", "iPhone", pkcs8);
        var plain = new FileClientStore(dir, PlainSecretProtector.Instance); // unsealed JSON, a plain key
        plain.SaveIdentity(identity);
        plain.SaveRepositoryKeys(3, [1, 2, 3]);
        Assert.StartsWith("{", File.ReadAllText(Path.Combine(dir, "identity.json")).TrimStart());

        var store = new FileClientStore(dir, new SealedStoreProtector(new MemoryKey()));
        Assert.Equal(identity, store.LoadIdentity());
        Assert.Equal([1, 2, 3], store.LoadRepositoryKeys(3));
        Assert.StartsWith("ioskc:1:identity:", File.ReadAllText(Path.Combine(dir, "identity.json")));
        Assert.StartsWith("ioskc:1:folder-3:", File.ReadAllText(Path.Combine(dir, "keys", "3.key")));
    }

    // ── The device key's encodings ────────────────────────────────────────────

    [Fact]
    public void TheSecureEnclavesPublicKey_IsTheSpkiTheServerKeeps_AndItsDerSignaturesVerify()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(false);
        byte[] x963 = [0x04, .. p.Q.X!, .. p.Q.Y!]; // SecKeyCopyExternalRepresentation of a public key
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), AppleKeys.SpkiFromX963(x963));
        Assert.Throws<CryptographicException>(() => AppleKeys.SpkiFromX963(x963[..64]));
        Assert.Throws<CryptographicException>(() => AppleKeys.SpkiFromX963([0x02, .. x963[1..]]));

        // SecKeyCreateSignature (ecdsaSignatureMessageX962SHA256) answers DER; the server checks P1363 against the SPKI.
        using var server = ECDsa.Create();
        server.ImportSubjectPublicKeyInfo(AppleKeys.SpkiFromX963(x963), out _);
        for (var i = 0; i < 50; i++)
        {
            var message = DeviceAuthMessage.Build(Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var der = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            Assert.True(server.VerifyData(message, EcdsaSignatures.DerToP1363(der), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    // ── Browsing from the web ─────────────────────────────────────────────────

    [Fact]
    public void Browsing_ListsTheAppsDocumentsOnly()
    {
        if (!Unix) return;
        var scope = new IosBrowseScope(_home);
        var documents = Path.Combine(_home, "Documents");
        Directory.CreateDirectory(Path.Combine(documents, "Sync", "Photos"));
        var place = Assert.Single(scope.Places());
        Assert.Equal(("On My iPhone › ASync", documents, BrowsePlaceKind.Documents), (place.Label, place.Path, place.Kind));
        Assert.Null(scope.Parent(documents));
        Assert.Equal(documents, scope.Parent(Path.Combine(documents, "Sync")));
        Assert.Null(scope.Refuse(Path.Combine(documents, "Sync")));
        Assert.NotNull(scope.Refuse(_home));
        Assert.NotNull(scope.Refuse(Path.Combine(_home, "Library", "Application Support", "async")));
        Assert.Equal(["On My iPhone › ASync", "Sync", "Photos"], scope.Crumbs(Path.Combine(documents, "Sync", "Photos")).Select(c => c.Label));

        var browser = new DeviceBrowser(scope, () => true);
        var reply = browser.Browse(new BrowseRequest(null, false, 100));
        Assert.Equal(documents, reply.Listing!.Path);
        Assert.Equal("Sync", Assert.Single(reply.Listing.Entries).Name);
        Assert.Equal(DeviceBrowseRefusal.OutOfReach, browser.Browse(new BrowseRequest(_home + "/Library", false, 100)).Refusal);
    }

    // ── Pairing links ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AnOpenedLink_WaitsForTheEnrollmentForm_AndIsDroppedOnceEnrolled()
    {
        var inbox = new PairingLinkInbox();
        Assert.False(inbox.Offer("https://example.com/"));
        Assert.True(inbox.Offer("async://pair?s=http%3A%2F%2F192.168.1.5%3A5100&c=ABCD-EFGH-JKMN"));
        Assert.False(inbox.Deliver(null)); // launched by the link: no shell yet

        await using var f = new ShellFixture();
        f.Agent.Status = Statuses.NotEnrolled();
        f.Start();
        inbox.Attach(f.Shell);
        await ShellFixture.Until(() => f.Shell.Page is EnrollViewModel, "the enrollment form");
        await ShellFixture.Until(() => !inbox.HasPending, "the link delivered");
        Assert.Equal(("http://192.168.1.5:5100", "ABCD-EFGH-JKMN"), (f.Shell.Enroll.Server, f.Shell.Enroll.Code));
        Assert.True(f.Shell.Enroll.UseCode);

        await using var enrolled = await new ShellFixture().StartAndWait(Statuses.Connected());
        await ShellFixture.Until(() => enrolled.Shell.Page is DashboardViewModel);
        Assert.True(inbox.Offer("ASYNC://pair?s=https%3A%2F%2Fother&c=QQQQ-RRRR-SSSS"));
        Assert.False(inbox.Deliver(enrolled.Shell));
        Assert.False(inbox.HasPending); // of no use when enrolled: dropped
    }
}
