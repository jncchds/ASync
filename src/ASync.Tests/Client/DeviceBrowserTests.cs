using ASync.Client.Core.Browse;
using ASync.Contracts;
using ASync.Tests.Engine;

namespace ASync.Tests.Client;

/// <summary>Path rules for both styles (pure, so Windows paths are checked on Linux too).</summary>
public class BrowsePathsTests
{
    [Theory]
    [InlineData(@"c:\Users\me\", @"C:\Users\me")]
    [InlineData("C:/Users//me", @"C:\Users\me")]
    [InlineData("C:", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Users\..\..\Windows\.\System32", @"C:\Windows\System32")]
    [InlineData(@"\\nas\share\photos\", @"\\nas\share\photos")]
    [InlineData(@"\\nas\share", @"\\nas\share")]
    [InlineData("C:relative", null)]
    [InlineData(@"Users\me", null)]
    [InlineData(@"\\nas", null)]
    [InlineData(@"\\?\C:\x", null)]
    [InlineData("", null)]
    public void Windows_Normalize(string path, string? expected) => Assert.Equal(expected, BrowsePaths.Normalize(path, windows: true));

    [Theory]
    [InlineData("/home//me/", "/home/me")]
    [InlineData("/", "/")]
    [InlineData("/home/me/../../..", "/")]
    [InlineData("home/me", null)]
    [InlineData("  ", null)]
    public void Unix_Normalize(string path, string? expected) => Assert.Equal(expected, BrowsePaths.Normalize(path, windows: false));

    [Fact]
    public void Windows_ParentAndCrumbs()
    {
        Assert.Equal(@"C:\Users", BrowsePaths.Parent(@"C:\Users\me", true));
        Assert.Equal(@"C:\", BrowsePaths.Parent(@"C:\Users", true));
        Assert.Null(BrowsePaths.Parent(@"C:\", true));
        Assert.Null(BrowsePaths.Parent(@"\\nas\share", true));
        Assert.Equal(@"\\nas\share", BrowsePaths.Parent(@"\\nas\share\photos", true));
        Assert.Equal([("C:", @"C:\"), ("Users", @"C:\Users"), ("me", @"C:\Users\me")],
            BrowsePaths.Crumbs(@"C:\Users\me", true).Select(c => (c.Label, c.Path)));
        Assert.Equal([(@"\\nas\share", @"\\nas\share"), ("photos", @"\\nas\share\photos")],
            BrowsePaths.Crumbs(@"\\nas\share\photos", true).Select(c => (c.Label, c.Path)));
        Assert.Equal(@"C:\x", BrowsePaths.Combine(@"C:\", "x", true));
        Assert.Equal(@"C:\Users\x", BrowsePaths.Combine(@"C:\Users", "x", true));
        Assert.True(BrowsePaths.IsUnder(@"c:\users\ME\docs", @"C:\Users\me", true));
        Assert.False(BrowsePaths.IsUnder(@"C:\Users\meow", @"C:\Users\me", true));
    }

    [Fact]
    public void Unix_ParentAndCrumbs()
    {
        Assert.Equal("/home", BrowsePaths.Parent("/home/me", false));
        Assert.Equal("/", BrowsePaths.Parent("/home", false));
        Assert.Null(BrowsePaths.Parent("/", false));
        Assert.Equal([("/", "/"), ("home", "/home"), ("me", "/home/me")], BrowsePaths.Crumbs("/home/me", false).Select(c => (c.Label, c.Path)));
        Assert.Equal("/x", BrowsePaths.Combine("/", "x", false));
        Assert.False(BrowsePaths.IsUnder("/Home/me", "/home", false)); // case matters on Unix
    }

    [Theory]
    [InlineData("Photos", false, true)]
    [InlineData("a:b", false, true)]   // fine on Linux…
    [InlineData("a:b", true, false)]   // …not on Windows
    [InlineData("CON", true, false)]
    [InlineData("con.txt", true, false)]
    [InlineData("CON", false, true)]
    [InlineData("end.", true, false)]
    [InlineData("a/b", false, false)]
    [InlineData(@"a\b", true, false)]
    [InlineData("..", false, false)]
    [InlineData(" lead", false, false)]
    [InlineData("", false, false)]
    public void FolderNames(string name, bool windows, bool ok) => Assert.Equal(ok, BrowsePaths.CheckName(name, windows) is null);
}

/// <summary>The device's listing: limits, exclusions, unreadable entries, symlinks, refusals, new folders.</summary>
public class DeviceBrowserTests : IDisposable
{
    private readonly TempDir _root = new();
    private bool _allowed = true;

    public void Dispose() => _root.Dispose();

    /// <summary>A scope rooted at the temp directory, hiding "secret" anywhere and refusing "forbidden".</summary>
    private sealed class TestScope(string root) : IBrowseScope
    {
        public (DeviceBrowseRefusal Refusal, string Message)? Blocked { get; set; }
        public bool Windows => false;
        public (DeviceBrowseRefusal Refusal, string Message)? Unavailable => Blocked;
        public IReadOnlyList<BrowsePlace> Places() => [new("Test", root, BrowsePlaceKind.Home)];
        public string? Refuse(string path) => path.Split('/').Contains("forbidden") ? "That one is out of reach." : null;
        public bool Hides(string path) => Path.GetFileName(path) == "secret";
    }

    private DeviceBrowser Browser(TestScope? scope = null) => new(scope ?? new TestScope(_root.Path), () => _allowed);

    private string Dir(string relative) => Directory.CreateDirectory(Path.Combine(_root.Path, relative)).FullName.TrimEnd('/');

    [Fact]
    public void Lists_DirectoriesFirst_ThenFiles_WithSizes_AndTheirPlace()
    {
        Dir("b-dir");
        Dir("A-dir");
        _root.File("a.txt", new byte[5]);
        _root.File(".hidden", new byte[1]);

        var reply = Browser().Browse(new BrowseRequest(null));

        var listing = Assert.IsType<BrowseListing>(reply.Listing);
        Assert.Equal(_root.Path, listing.Path);
        Assert.Equal(BrowsePaths.Parent(_root.Path, false), listing.Parent);
        Assert.Equal(_root.Path, listing.Crumbs[^1].Path);
        Assert.Equal(["A-dir", "b-dir", ".hidden", "a.txt"], listing.Entries.Select(e => e.Name));
        Assert.Equal(Path.Combine(_root.Path, "A-dir"), listing.Entries[0].Path);
        Assert.Equal(5, listing.Entries.Single(e => e.Name == "a.txt").Size);
        Assert.Null(listing.Entries.Single(e => e.Name == "a.txt").Path); // files cannot be opened
        Assert.True(listing.Entries.Single(e => e.Name == ".hidden").Hidden);
        Assert.Equal(0, listing.More);
        Assert.Equal("Test", Assert.Single(listing.Places).Label);
        Assert.Equal("/", listing.Separator);

        var dirsOnly = Browser().Browse(new BrowseRequest(_root.Path, Files: false)).Listing!;
        Assert.Equal(["A-dir", "b-dir"], dirsOnly.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Limits_TheEntries_AndCountsTheRest()
    {
        for (var i = 0; i < 30; i++) Dir($"d{i:00}");
        for (var i = 0; i < 10; i++) _root.File($"f{i}", []);

        var listing = Browser().Browse(new BrowseRequest(_root.Path, MaxEntries: 25)).Listing!;

        Assert.Equal(25, listing.Entries.Count);
        Assert.All(listing.Entries, e => Assert.Equal(BrowseEntryKind.Directory, e.Kind)); // directories fill first
        Assert.Equal(15, listing.More);
        // The device never sends more than its own limit, whatever is asked.
        Assert.Equal(40, Browser().Browse(new BrowseRequest(_root.Path, MaxEntries: 1_000_000)).Listing!.Entries.Count);
    }

    [Fact]
    public void Limits_TheReplySize_ForLongNames()
    {
        var name = new string('n', 200);
        for (var i = 0; i < 1500; i++) _root.File($"{name}{i:0000}", []);

        var listing = Browser().Browse(new BrowseRequest(_root.Path)).Listing!;

        Assert.True(listing.Entries.Count < 1500);
        Assert.Equal(1500, listing.Entries.Count + listing.More);
        Assert.True(listing.Entries.Sum(e => e.Name.Length) <= BrowseLimits.MaxReplyChars);
    }

    [Fact]
    public void Symlinks_AreListedWithTheirTarget_NotFollowed()
    {
        var target = Dir("real");
        _root.File("real/inside.txt", [1]);
        Directory.CreateSymbolicLink(Path.Combine(_root.Path, "link"), target);
        File.CreateSymbolicLink(Path.Combine(_root.Path, "dangling"), Path.Combine(_root.Path, "missing"));

        var listing = Browser().Browse(new BrowseRequest(_root.Path)).Listing!;

        var link = listing.Entries.Single(e => e.Name == "link");
        Assert.Equal(BrowseEntryKind.Symlink, link.Kind);
        Assert.Equal(target, link.LinkTarget);
        Assert.Null(link.Path); // not something the picker opens
        Assert.Equal(BrowseEntryKind.Symlink, listing.Entries.Single(e => e.Name == "dangling").Kind);
        Assert.Equal(["real", "dangling", "link"], listing.Entries.Select(e => e.Name)); // directories, then links

        // A typed path that is a link lists (and names) where it points.
        var through = Browser().Browse(new BrowseRequest(Path.Combine(_root.Path, "link"))).Listing!;
        Assert.Equal(target, through.Path);
        Assert.Equal("inside.txt", Assert.Single(through.Entries).Name);
    }

    [Fact]
    public void UnreadableDirectories_AreMarked_NotAnErrorForTheListing()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // root reads everything
        var locked = Dir("locked");
        _root.File("locked/x", [1]);
        Dir("open");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var listing = Browser().Browse(new BrowseRequest(_root.Path)).Listing!;
            Assert.True(listing.Entries.Single(e => e.Name == "locked").Unreadable);
            Assert.False(listing.Entries.Single(e => e.Name == "open").Unreadable);

            var refused = Browser().Browse(new BrowseRequest(locked));
            Assert.Null(refused.Listing);
            Assert.Equal(DeviceBrowseRefusal.Unreadable, refused.Refusal);
            Assert.Contains("permission denied", refused.Message);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void HiddenAndOutOfReachPaths_AreNeverListedOrOpened()
    {
        Dir("secret");
        Dir("forbidden");
        Dir("ok");

        var listing = Browser().Browse(new BrowseRequest(_root.Path)).Listing!;
        Assert.DoesNotContain(listing.Entries, e => e.Name == "secret");

        var refused = Browser().Browse(new BrowseRequest(Path.Combine(_root.Path, "forbidden")));
        Assert.Equal(DeviceBrowseRefusal.OutOfReach, refused.Refusal);
        Assert.Equal("That one is out of reach.", refused.Message);
        Assert.Equal(DeviceBrowseRefusal.OutOfReach, Browser().Browse(new BrowseRequest(_root.Path, NewFolder: "forbidden")).Refusal);
        Assert.Equal(DeviceBrowseRefusal.OutOfReach, Browser().Browse(new BrowseRequest(_root.Path, NewFolder: "secret")).Refusal);
    }

    [Fact]
    public void Refuses_WhenTurnedOff_OrUnavailable_BeforeTouchingTheDisk()
    {
        _allowed = false;
        var off = Browser().Browse(new BrowseRequest("/definitely/not/there"));
        Assert.Equal(DeviceBrowseRefusal.TurnedOff, off.Refusal);
        Assert.Contains("turned off", off.Message);
        Assert.False(Browser().State.Allowed);

        _allowed = true;
        var scope = new TestScope(_root.Path) { Blocked = (DeviceBrowseRefusal.NoAllFilesAccess, "Turn on All files access.") };
        var browser = Browser(scope);
        Assert.Equal(DeviceBrowseRefusal.NoAllFilesAccess, browser.Browse(new BrowseRequest(_root.Path)).Refusal);
        Assert.Equal(new DeviceBrowseState(true, NeedsAllFilesAccess: true), browser.State);
    }

    [Fact]
    public void Refuses_BadPaths_WithAMessage()
    {
        _root.File("file.txt", [1]);
        Assert.Equal(DeviceBrowseRefusal.Invalid, Browser().Browse(new BrowseRequest("relative/path")).Refusal);
        Assert.Equal(DeviceBrowseRefusal.NotFound, Browser().Browse(new BrowseRequest(Path.Combine(_root.Path, "missing"))).Refusal);
        var file = Browser().Browse(new BrowseRequest(Path.Combine(_root.Path, "file.txt")));
        Assert.Equal(DeviceBrowseRefusal.Invalid, file.Refusal);
        Assert.Contains("is a file", file.Message);
        // ".." is resolved, not a way out of anything: it is a canonical path like any other.
        Assert.Equal(_root.Path, Browser().Browse(new BrowseRequest(Path.Combine(_root.Path, "x", ".."))).Listing!.Path);
    }

    [Fact]
    public void NewFolder_IsCreated_AndListed()
    {
        var reply = Browser().Browse(new BrowseRequest(_root.Path, NewFolder: "Photos"));

        Assert.Equal(Path.Combine(_root.Path, "Photos"), reply.Listing!.Path);
        Assert.True(Directory.Exists(Path.Combine(_root.Path, "Photos")));
        Assert.Empty(reply.Listing.Entries);

        Assert.Equal(DeviceBrowseRefusal.Exists, Browser().Browse(new BrowseRequest(_root.Path, NewFolder: "Photos")).Refusal);
        Assert.Equal(DeviceBrowseRefusal.Invalid, Browser().Browse(new BrowseRequest(_root.Path, NewFolder: "a/b")).Refusal);
        Assert.Equal(DeviceBrowseRefusal.Invalid, Browser().Browse(new BrowseRequest(_root.Path, NewFolder: "..")).Refusal);
        Assert.Equal(DeviceBrowseRefusal.NotFound, Browser().Browse(new BrowseRequest(Path.Combine(_root.Path, "nope"), NewFolder: "x")).Refusal);
        Assert.False(Directory.Exists(Path.Combine(_root.Path, "nope")));
    }

    [Fact]
    public void LinuxVolumes_OfferRealMounts_NotSystemOnes()
    {
        var mounts = new[]
        {
            ("/", "ext4"), ("/proc", "proc"), ("/sys/fs/cgroup", "cgroup2"), ("/dev/shm", "tmpfs"), ("/etc/hosts", "ext4"),
            ("/boot/efi", "vfat"), ("/run/user/1000", "tmpfs"), ("/snap/core/1", "squashfs"), ("/data", "ext4"),
            ("/media/me/USB", "vfat"), ("/run/media/me/SD", "exfat"), ("/mnt/nas", "nfs4"), ("/data", "ext4"), ("/tmp", "tmpfs"),
        };

        var volumes = DesktopBrowseScope.LinuxVolumes(mounts).ToList();

        Assert.Equal(["/data", "/media/me/USB", "/mnt/nas", "/run/media/me/SD"], volumes.Select(v => v.Path));
        Assert.Equal("USB", volumes[1].Label);
        Assert.All(volumes, v => Assert.Equal(BrowsePlaceKind.Volume, v.Kind));
    }

    [Fact]
    public void WindowsDrives_AreLabelledLikeExplorer()
    {
        var drives = DesktopBrowseScope.WindowsDrives(
        [
            (@"C:\", "NTFS", "", DriveType.Fixed), (@"D:\", "NTFS", "Data", DriveType.Fixed), (@"E:\", "FAT32", "", DriveType.Removable),
            (@"F:\", "", "", DriveType.CDRom), (@"Z:\", "NTFS", "", DriveType.Network),
        ]).ToList();

        Assert.Equal(["Local disk (C:)", "Data (D:)", "Removable disk (E:)", "Network drive (Z:)"], drives.Select(d => d.Label));
        Assert.Equal(@"D:\", drives[1].Path);
    }

    [Fact]
    public void DesktopPlaces_StartAtHome()
    {
        var places = DesktopBrowseScope.Instance.Places();
        Assert.Equal(BrowsePlaceKind.Home, places[0].Kind);
        Assert.Equal(places.Count, places.Select(p => p.Path).Distinct().Count());
        Assert.All(places, p => Assert.True(Directory.Exists(p.Path), p.Path));
    }

    [Fact]
    public void TheAgentsConfigDirectory_IsNeverOfferedAsAVolume()
    {
        // The Docker image's /config volume holds the device's key: a mount under a hidden place is left out.
        var hidden = DesktopBrowseScope.Instance.Hiding(OperatingSystem.IsWindows() ? @"C:\" : "/");
        var places = hidden.Places();
        Assert.DoesNotContain(places, p => p.Kind is BrowsePlaceKind.Volume);
        Assert.Equal(BrowsePlaceKind.Home, places[0].Kind); // home and the computer's root are still there
    }
}
