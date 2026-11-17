using System.Text;
using ASync.Client.Core.Media;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using ASync.Tests.Engine;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests.Client;

/// <summary>
/// Photos folders (task 19) against a fake library: the permanent layout and sidecars, the selection and its filters, incremental
/// runs that do not read unchanged assets, deduplication across albums and renames, deleted photos kept in older snapshots, and the
/// restore into the library that skips what is already there.
/// </summary>
public class MediaBackupTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeMediaLibrary _lib = new();
    private static readonly Guid Phone = Guid.Parse("0190aaaa-0000-7000-8000-000000000001");

    public void Dispose() => _dir.Dispose();

    private static byte[] Bytes(int seed, int size = 20_000) => TestData.Random(size, seed);

    private static DeviceFolderDto Folder(MediaSelectionDto? media = null) => new(
        7, "Photos", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Photos, 60, [], new RetentionDto(), 30, 30, 1, true, "", null, false,
        Media: media ?? MediaSelectionDto.Everything);

    private async Task<Repo> RepoAsync() => await TestData.NewRepositoryAsync(new LocalBackend(_dir.Sub("repo")));

    private async Task<Snapshot> BackUpAsync(Repo repo, MediaSelectionDto? media = null) =>
        (await new BackupJob(repo, Folder(media), Phone, "Pixel", new MediaTree(_lib, media, "test-library")).RunAsync()).Snapshot;

    private async Task<string> RestoreToDiskAsync(Repo repo, Snapshot s, string name = "out")
    {
        var target = _dir.Sub(name);
        await new RestoreRunner(repo).RunAsync(s, target);
        return target;
    }

    private static List<string> Files(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    // ── Layout and sidecars ───────────────────────────────────────────────────

    [Fact]
    public async Task ASnapshot_HoldsOriginalsByYearAndMonth_WithASidecarEach_ByteForByte()
    {
        var camera = _lib.Album("Camera");
        var cat = _lib.Add("IMG_0001.JPG", Bytes(1), new DateTime(2026, 3, 9, 23, 30, 0, DateTimeKind.Utc), [camera], favourite: true);
        var clip = _lib.Add("VID_0002.mp4", Bytes(2, 50_000), new DateTime(2025, 12, 31, 12, 0, 0, DateTimeKind.Utc), [camera], MediaKind.Video);
        _lib.DetailsById[cat.Id] = new MediaDetails(50.4501, 30.5234);
        var repo = await RepoAsync();

        var snapshot = await BackUpAsync(repo);
        var root = await RestoreToDiskAsync(repo, snapshot);

        Assert.Equal(
        [
            $"2025/12/VID_0002~{clip.ShortId}.json", $"2025/12/VID_0002~{clip.ShortId}.mp4",
            $"2026/03/IMG_0001~{cat.ShortId}.jpg", $"2026/03/IMG_0001~{cat.ShortId}.json",
        ], Files(root));
        Assert.Equal(Bytes(1), File.ReadAllBytes(Path.Combine(root, $"2026/03/IMG_0001~{cat.ShortId}.jpg")));
        Assert.Equal(Bytes(2, 50_000), File.ReadAllBytes(Path.Combine(root, $"2025/12/VID_0002~{clip.ShortId}.mp4")));

        var sidecar = MediaSidecar.Parse(File.ReadAllBytes(Path.Combine(root, $"2026/03/IMG_0001~{cat.ShortId}.json")));
        Assert.Equal(cat.Id, sidecar.Id);
        Assert.Equal("IMG_0001.JPG", sidecar.FileName);
        Assert.Equal("image/jpeg", sidecar.Mime);
        Assert.Equal(20_000, sidecar.Size);
        Assert.Equal(FakeMediaLibrary.Ms(new DateTime(2026, 3, 9, 23, 30, 0, DateTimeKind.Utc)), sidecar.Taken);
        Assert.Equal((4000, 3000), (sidecar.Width, sidecar.Height));
        Assert.Equal((50.4501, 30.5234), (sidecar.Latitude!.Value, sidecar.Longitude!.Value));
        Assert.True(sidecar.Favourite);
        Assert.Equal([new MediaAlbumRef(camera, "Camera")], sidecar.Albums);
        Assert.Equal("DCIM/Camera/", sidecar.RelativePath);
        Assert.Equal("test-library", sidecar.Source);
        Assert.Equal((MediaResource.Original, $"IMG_0001~{cat.ShortId}.jpg", 20_000L), (sidecar.Resources[0].Role, sidecar.Resources[0].File, sidecar.Resources[0].Size));
        var text = File.ReadAllText(Path.Combine(root, $"2026/03/IMG_0001~{cat.ShortId}.json"));
        Assert.Contains("\"format\": 1", text);
        Assert.Contains("\"taken\": \"2026-03-09T23:30:00.000Z\"", text);
        Assert.Equal(MediaKind.Video, MediaSidecar.Parse(File.ReadAllBytes(Path.Combine(root, $"2025/12/VID_0002~{clip.ShortId}.json"))).Kind);
    }

    [Fact]
    public void Layout_UsesUtc_SafeNames_RolesForExtraResources_AndTheModifiedTimeWithoutADateTaken()
    {
        var a = new MediaAsset("ios:8A4D/L0/001", "8A4D/L0/001", MediaKind.Photo, "my: holiday?.HEIC", "image/heic",
            FakeMediaLibrary.Ms(new DateTime(2024, 2, 1, 0, 30, 0, DateTimeKind.Utc)), null,
            [new(MediaResource.Original, "my: holiday?.HEIC", 10, "image/heic"), new("live", "IMG_1.MOV", 20, "video/quicktime")], []);
        Assert.Equal("2024/02", MediaLayout.Directory(a));
        Assert.Equal("my_ holiday_~8A4D_L0_001", MediaLayout.BaseName(a));
        Assert.Equal("my_ holiday_~8A4D_L0_001.heic", MediaLayout.ResourceName(a, a.Resources[0]));
        Assert.Equal("my_ holiday_~8A4D_L0_001.live.mov", MediaLayout.ResourceName(a, a.Resources[1]));
        Assert.Equal("my_ holiday_~8A4D_L0_001.json", MediaLayout.SidecarName(a));
        Assert.Equal("asset~1", MediaLayout.BaseName(a with { FileName = "...", ShortId = "1" }));
    }

    [Fact]
    public void ASidecar_IsTheSameBytesForTheSameAsset_AndReadersIgnoreUnknownFields()
    {
        var a = _lib.Add("x.jpg", Bytes(3), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), []);
        var names = new Dictionary<string, string>();
        var one = MediaSidecar.For(a, names, new MediaDetails(), "s").ToJson();
        Assert.Equal(one, MediaSidecar.For(a, names, new MediaDetails(), "s").ToJson());
        var extended = Encoding.UTF8.GetString(one).Replace("\"format\": 1,", "\"format\": 1, \"futureField\": [1, 2],");
        Assert.Equal(a.Id, MediaSidecar.Parse(Encoding.UTF8.GetBytes(extended)).Id);
        Assert.Throws<InvalidDataException>(() => MediaSidecar.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(one).Replace("\"format\": 1", "\"format\": 2"))));
        Assert.Throws<InvalidDataException>(() => MediaSidecar.Parse("{}"u8));
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSelection_ChoosesAlbums_AndFiltersByDateVideosAndScreenshots()
    {
        var camera = _lib.Album("Camera");
        var shots = _lib.Album("Screenshots");
        var whatsapp = _lib.Album("WhatsApp Images");
        var old = _lib.Add("old.jpg", Bytes(10), new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc), [camera]);
        var recent = _lib.Add("new.jpg", Bytes(11), new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), [camera]);
        var video = _lib.Add("v.mp4", Bytes(12), new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc), [camera], MediaKind.Video);
        var screenshot = _lib.Add("s.png", Bytes(13), new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc), [shots], screenshot: true);
        var chat = _lib.Add("w.jpg", Bytes(14), new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc), [whatsapp]);

        string[] Ids(MediaSelectionDto s) => [.. _lib.Assets().Where(a => MediaFilter.Includes(s, a)).Select(a => a.FileName).Order()];

        Assert.Equal(["new.jpg", "old.jpg", "s.png", "v.mp4", "w.jpg"], Ids(MediaSelectionDto.Everything));
        Assert.Equal(["new.jpg", "old.jpg", "v.mp4"], Ids(new MediaSelectionDto(false, [new(camera, "Camera")])));
        Assert.Equal(["new.jpg", "v.mp4", "w.jpg"], Ids(new MediaSelectionDto(false, [new(camera, "Camera"), new(whatsapp, "WhatsApp")],
            Since: new DateTime(2020, 1, 1))));
        Assert.Equal(["new.jpg", "old.jpg", "w.jpg"], Ids(new MediaSelectionDto(IncludeVideos: false, IncludeScreenshots: false)));
        Assert.Equal(["s.png"], Ids(new MediaSelectionDto(false, [new(shots, "Screenshots")])));
        Assert.Empty(Ids(new MediaSelectionDto(false, [new(shots, "Screenshots")], IncludeScreenshots: false)));
        _ = (old, recent, video, screenshot, chat);

        // A backup of two albums holds exactly those, and says how many photos and videos.
        var repo = await RepoAsync();
        var selection = new MediaSelectionDto(false, [new(camera, "Camera")]);
        var tree = new MediaTree(_lib, selection);
        await new BackupJob(repo, Folder(selection), Phone, "Pixel", tree).RunAsync();
        Assert.Equal(new MediaCountsDto(2, 1, Bytes(10).Length * 3L, MediaAccess.Full), tree.Counts());
        Assert.Equal(6, Files(await RestoreToDiskAsync(repo, (await repo.ListSnapshotsAsync()).Single())).Count);
    }

    [Fact]
    public void SelectionRules_RefuseNoAlbums_AndDescribeThemselves()
    {
        Assert.Null(MediaSelectionDto.Everything.Problem());
        Assert.NotNull(new MediaSelectionDto(false, []).Problem());
        Assert.NotNull(new MediaSelectionDto(false, null).Problem());
        Assert.Equal("All photos and videos", MediaSelectionDto.Everything.Describe());
        Assert.Equal("2 albums: Camera, Trips · no videos · since 2024-01-01 · no screenshots",
            new MediaSelectionDto(false, [new("1", "Camera"), new("2", "Trips")], new DateTime(2024, 1, 1), false, false).Describe());
    }

    // ── Incremental runs and deduplication ────────────────────────────────────

    [Fact]
    public async Task ASecondRun_ReadsNoUnchangedOriginal_ButSeesAlbumChangesInTheSidecars()
    {
        var camera = _lib.Album("Camera");
        var assets = Enumerable.Range(0, 5).Select(i => _lib.Add($"IMG_{i}.jpg", Bytes(100 + i), new DateTime(2026, 6, 1 + i, 0, 0, 0, DateTimeKind.Utc), [camera])).ToList();
        var repo = await RepoAsync();
        var first = await BackUpAsync(repo);
        Assert.All(assets, a => Assert.Equal(1, _lib.Reads[a.Id]));
        Assert.Equal(10, first.Stats.FilesNew);

        _lib.Update(assets[0].Id, a => a with { Favourite = true });
        var second = await BackUpAsync(repo);
        Assert.All(assets, a => Assert.Equal(1, _lib.Reads[a.Id])); // no original read again
        Assert.Equal(1, second.Stats.FilesChanged); // the favourite's sidecar
        Assert.Equal(9, second.Stats.FilesUnmodified);
        Assert.Equal(first.Id, second.Parent);
        var sidecar = MediaSidecar.Parse(File.ReadAllBytes(Path.Combine(await RestoreToDiskAsync(repo, second), $"2026/06/IMG_0~{assets[0].ShortId}.json")));
        Assert.True(sidecar.Favourite);

        // A changed photo (edited: new size and time) is read again; the others still are not.
        _lib.Remove(assets[1].Id);
        _lib.Add("IMG_1.jpg", Bytes(999), new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc), [camera], id: assets[1].Id,
            modified: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var third = await BackUpAsync(repo);
        Assert.Equal(2, _lib.Reads[assets[1].Id]);
        Assert.Equal(1, _lib.Reads[assets[2].Id]);
        Assert.Equal(2, third.Stats.FilesChanged); // the photo and its sidecar (size, time)
    }

    [Fact]
    public async Task APhotoInTwoAlbums_AndARenamedOne_AreStoredOnce()
    {
        var camera = _lib.Album("Camera");
        var trip = _lib.Album("Trip");
        var bytes = Bytes(7, 200_000);
        _lib.Add("IMG_7.jpg", bytes, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), [camera]);
        var repo = await RepoAsync();
        var first = await BackUpAsync(repo);
        Assert.True(first.Stats.BytesAdded >= bytes.Length);

        // Android keeps a photo "in two albums" as a copy in each folder: another asset, the same bytes.
        _lib.Add("IMG_7 (copy).jpg", bytes, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), [trip]);
        var second = await BackUpAsync(repo);
        Assert.True(second.Stats.BytesAdded < 2_000, $"{second.Stats.BytesAdded} bytes added for a copy"); // only its sidecar

        // iOS keeps one asset in both albums: one file, both albums in its sidecar.
        var both = _lib.Add("IMG_8.jpg", Bytes(8), new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc), [camera, trip]);
        var third = await BackUpAsync(repo);
        var root = await RestoreToDiskAsync(repo, third);
        Assert.Single(Files(root), f => f.Contains($"~{both.ShortId}.jpg", StringComparison.Ordinal));
        Assert.Equal(["Camera", "Trip"], MediaSidecar.Parse(File.ReadAllBytes(Path.Combine(root, $"2026/08/IMG_8~{both.ShortId}.json"))).Albums.Select(a => a.Name));

        // Renamed on the phone: a new path, the same content: nothing stored again but the names.
        _lib.Update(both.Id, a => a with { FileName = "Sunset.jpg", Resources = [a.Resources[0] with { FileName = "Sunset.jpg" }] });
        var fourth = await BackUpAsync(repo);
        Assert.True(fourth.Stats.BytesAdded < 2_000, $"{fourth.Stats.BytesAdded} bytes added for a rename");
    }

    [Fact]
    public async Task APhotoDeletedOnThePhone_StaysInTheOlderSnapshot()
    {
        var camera = _lib.Album("Camera");
        var gone = _lib.Add("bye.jpg", Bytes(21), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), [camera]);
        _lib.Add("stay.jpg", Bytes(22), new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), [camera]);
        var repo = await RepoAsync();
        var first = await BackUpAsync(repo);
        _lib.Remove(gone.Id);
        var second = await BackUpAsync(repo);
        Assert.Equal(2, Files(await RestoreToDiskAsync(repo, second, "second")).Count);
        Assert.Contains($"2026/09/bye~{gone.ShortId}.jpg", Files(await RestoreToDiskAsync(repo, first, "first")));
    }

    [Fact]
    public async Task WithoutPermission_TheRunStopsWithTheReason_AndPartialAccessBacksUpWhatItSees()
    {
        var camera = _lib.Album("Camera");
        var a = _lib.Add("a.jpg", Bytes(31), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), [camera]);
        _lib.Add("b.jpg", Bytes(32), new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), [camera]);
        var repo = await RepoAsync();
        _lib.Access = MediaAccess.None;
        var refused = await Assert.ThrowsAsync<SyncRefusedException>(() => BackUpAsync(repo));
        Assert.Contains("may not read the photos", refused.Message);
        Assert.Empty(await repo.ListSnapshotsAsync());

        _lib.Access = MediaAccess.Partial;
        _lib.Visible.Add(a.Id);
        var tree = new MediaTree(_lib, null);
        var s = (await new BackupJob(repo, Folder(), Phone, "Pixel", tree).RunAsync()).Snapshot;
        Assert.Equal(2, Files(await RestoreToDiskAsync(repo, s)).Count);
        Assert.Equal(MediaAccess.Partial, tree.Counts().Access);
    }

    [Fact]
    public void TheTree_IsReadOnly_AndNeverWatched()
    {
        var tree = new MediaTree(_lib, null);
        Assert.False(tree.Watchable);
        Assert.Null(tree.FileSystemPath);
        Assert.Throws<NotSupportedException>(() => tree.OpenAppend("2026/01/x.jpg"));
        Assert.Throws<NotSupportedException>(() => tree.DeleteFile("2026/01/x.jpg"));
        Assert.Throws<DirectoryNotFoundException>(() => tree.List("1999"));
        Assert.Empty(tree.List(""));
    }

    // ── Restore into the library ──────────────────────────────────────────────

    [Fact]
    public async Task RestoringIntoAWipedLibrary_PutsEveryOriginalBack_AndAgain_SkipsThemAll()
    {
        var camera = _lib.Album("Camera");
        var trip = _lib.Album("Trip");
        var photo = _lib.Add("IMG_1.jpg", Bytes(41), new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), [camera], favourite: true);
        _lib.Add("IMG_2.jpg", Bytes(42), new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc), [trip]);
        _lib.Add("VID_3.mp4", Bytes(43, 70_000), new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc), [trip], MediaKind.Video);
        var repo = await RepoAsync();
        var snapshot = await BackUpAsync(repo);

        // Still all there: nothing to do (same id and size).
        var none = await new MediaRestorer(repo, _lib).RunAsync(snapshot);
        Assert.Equal((0, 3, 3), (none.Restored, none.Skipped, none.Total));
        Assert.Empty(_lib.Inserted);

        _lib.Clear();
        var progress = new List<MediaRestoreProgress>();
        var result = await new MediaRestorer(repo, _lib).RunAsync(snapshot, new SyncProgress<MediaRestoreProgress>(progress.Add));
        Assert.Equal((3, 0), (result.Restored, result.Skipped));
        Assert.Empty(result.Errors);
        Assert.Equal(3, _lib.Assets().Count);
        var first = _lib.Inserted.Single(i => i.Insert.FileName == "IMG_1.jpg");
        Assert.Equal(Bytes(41), first.Bytes);
        Assert.Equal("DCIM/Camera/", first.Insert.RelativePath);
        Assert.Equal("Camera", first.Insert.AlbumName);
        Assert.True(first.Insert.Favourite);
        Assert.Equal(FakeMediaLibrary.Ms(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc)), first.Insert.Taken);
        Assert.Equal(MediaKind.Video, _lib.Inserted.Single(i => i.Insert.FileName == "VID_3.mp4").Insert.Kind);
        Assert.Equal(3, progress[^1].Done);
        Assert.NotEqual(photo.Id, _lib.Assets().Single(a => a.FileName == "IMG_1.jpg").Id); // new ids: found again by content below

        var again = await new MediaRestorer(repo, _lib).RunAsync(snapshot);
        Assert.Equal((0, 3), (again.Restored, again.Skipped));
        Assert.Equal(3, _lib.Inserted.Count);
    }

    [Fact]
    public async Task ARestore_DoesNotSkipADifferentPhotoOfTheSameSize()
    {
        var camera = _lib.Album("Camera");
        var original = _lib.Add("IMG_1.jpg", Bytes(51), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), [camera]);
        var repo = await RepoAsync();
        var snapshot = await BackUpAsync(repo);
        _lib.Remove(original.Id);
        _lib.Add("other.jpg", Bytes(52), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), [camera]); // same size, other bytes
        var result = await new MediaRestorer(repo, _lib).RunAsync(snapshot);
        Assert.Equal((1, 0), (result.Restored, result.Skipped));
    }

    [Fact]
    public async Task ACopyInAnotherAlbum_DoesNotCountAsAlreadyThere()
    {
        var camera = _lib.Album("Camera");
        var trip = _lib.Album("Trip");
        var bytes = Bytes(61);
        var original = _lib.Add("IMG_1.jpg", bytes, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), [camera]);
        _lib.Add("IMG_1 copy.jpg", bytes, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), [trip]);
        var repo = await RepoAsync();
        var snapshot = await BackUpAsync(repo);
        _lib.Remove(original.Id); // the Camera album wiped; Trip keeps its copy
        var result = await new MediaRestorer(repo, _lib).RunAsync(snapshot);
        Assert.Equal((1, 1), (result.Restored, result.Skipped)); // the Camera photo back; Trip's copy is still there
        Assert.Equal("DCIM/Camera/", _lib.Inserted.Single().Insert.RelativePath);
    }

    /// <summary>Calls the handler on the reporting thread (Progress&lt;T&gt; posts to the thread pool, which a test cannot wait for).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
