using System.Collections.Concurrent;
using System.Text.Json;
using ASync.App.iOS;
using ASync.Client.Core.Background;
using ASync.Client.Core.Media;
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
/// The iPhone's photo library (task 23) on Linux: PhotoKit's mapping onto ASync's media model (ids, roles of the resources, smart
/// albums, an asset in several albums, subtypes, times in UTC), the authorisation, iCloud-only originals that wait for a later run and
/// are never taken for deleted, streamed reads, sizes PhotoKit does not tell, the restore (Live Photo pairs, albums made once,
/// what is there skipped and put back into its albums), and the change observer's debounce. Against <see cref="FakePhotoKit"/>,
/// the thin interface's fake; PhotoKit itself runs only on a Mac (scripts/smoke-ios.sh).
/// </summary>
public sealed class IosPhotosTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "async-iosphotos-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakePhotoKit _kit = new();
    private static readonly Guid Phone = Guid.Parse("0190aaaa-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset March = new(2026, 3, 9, 23, 30, 0, TimeSpan.FromHours(2)); // 21:30 UTC

    public IosPhotosTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private IosMediaLibrary Library(IosPhotoSettings? settings = null) =>
        new(_kit, settings ?? new IosPhotoSettings(Path.Combine(_dir, "ios-photos.json")), Path.Combine(_dir, "tmp"), Path.Combine(_dir, "sizes.json"));

    private static byte[] Bytes(int seed, int size = 20_000)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private async Task<Repo> RepoAsync()
    {
        var keys = RepositoryKeys.Generate();
        return await Repo.InitAsync(new LocalBackend(Path.Combine(_dir, "repo")), keys, [KeyFile.ForPassphrase(keys, "pass", iterations: 1000)],
            new ChunkerParameters(MinSize: 1024, AverageSize: 4096, MaxSize: 16384));
    }

    private static DeviceFolderDto Folder(MediaSelectionDto? media = null) => new(
        7, "Photos", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Photos, 60, [], new RetentionDto(), 30, 30, 1, true, "", null, false,
        Media: media ?? MediaSelectionDto.Everything);

    private static async Task<(Snapshot Snapshot, MediaTree Tree)> BackUpAsync(Repo repo, IMediaLibrary library, MediaReadOptions? options = null,
        MediaSelectionDto? media = null)
    {
        var tree = new MediaTree(library, media, IosPhotoMapping.Source, options: options);
        var (snapshot, _) = await new BackupJob(repo, Folder(media), Phone, "iPhone", tree).RunAsync();
        return (snapshot, tree);
    }

    private async Task<Dictionary<string, byte[]>> FilesAsync(Repo repo, Snapshot s)
    {
        var target = Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N")[..6]);
        await new RestoreRunner(repo).RunAsync(s, target);
        return Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(target, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
    }

    private const string LiveId = "8A4D9C5E-1F2B-4C3D-9E8F-0A1B2C3D4E5F/L0/001";
    private const string LiveShort = "8A4D9C5E-1F2B-4C3D-9E8F-0A1B2C3D4E5F_L0_001";

    /// <summary>A Live Photo, edited, with RAW: in the album Trip and in Favorites.</summary>
    private void SeedLivePhoto(bool cloudOnly = false)
    {
        _kit.Add(new PhotoKitAsset(LiveId, PhotoKitMediaType.Image, PhotoKitSubtypes.PhotoLive | PhotoKitSubtypes.PhotoHdr, March, March.AddDays(1),
                4032, 3024, 0, 50.4501, 30.5234, Favorite: true),
            (PhotoKitResourceType.Photo, "IMG_0001.HEIC", "public.heic", Bytes(1), cloudOnly),
            (PhotoKitResourceType.PairedVideo, "IMG_0001.MOV", "com.apple.quicktime-movie", Bytes(2, 60_000), cloudOnly),
            (PhotoKitResourceType.FullSizePhoto, "FullSizeRender.heic", "public.heic", Bytes(3), cloudOnly),
            (PhotoKitResourceType.AdjustmentData, "Adjustments.plist", "com.apple.property-list", Bytes(4, 300), false),
            (PhotoKitResourceType.AlternatePhoto, "IMG_0001.DNG", "com.adobe.raw-image", Bytes(5, 40_000), cloudOnly));
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PhotoKitResourceType.Photo, PhotoKitMediaType.Image, "original")]
    [InlineData(PhotoKitResourceType.Video, PhotoKitMediaType.Video, "original")]
    [InlineData(PhotoKitResourceType.PairedVideo, PhotoKitMediaType.Image, "live")]
    [InlineData(PhotoKitResourceType.FullSizePhoto, PhotoKitMediaType.Image, "edited")]
    [InlineData(PhotoKitResourceType.FullSizeVideo, PhotoKitMediaType.Video, "edited")]
    [InlineData(PhotoKitResourceType.FullSizePairedVideo, PhotoKitMediaType.Image, "live-edited")]
    [InlineData(PhotoKitResourceType.AlternatePhoto, PhotoKitMediaType.Image, "raw")]
    [InlineData(PhotoKitResourceType.AdjustmentData, PhotoKitMediaType.Image, null)]
    [InlineData(PhotoKitResourceType.AdjustmentBasePhoto, PhotoKitMediaType.Image, null)]
    [InlineData(PhotoKitResourceType.PhotoProxy, PhotoKitMediaType.Image, null)]
    [InlineData(PhotoKitResourceType.Audio, PhotoKitMediaType.Video, null)]
    public void ResourceTypes_MapToRoles(PhotoKitResourceType type, PhotoKitMediaType kind, string? role) =>
        Assert.Equal(role, IosPhotoMapping.Role(type, kind));

    [Fact]
    public void AnAsset_MapsIdsTimesInUtcSubtypesAndResources()
    {
        var created = new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.FromHours(2)); // still 2025 in UTC
        var asset = IosPhotoMapping.Asset(
            new PhotoKitAsset("AB/L0/001", PhotoKitMediaType.Image, PhotoKitSubtypes.PhotoScreenshot, created, created.AddHours(5), 1170, 2532, 0, null, null, false),
            [new(PhotoKitResourceType.AlternatePhoto, "IMG_1.DNG", "com.adobe.raw-image", 900), new(PhotoKitResourceType.Photo, "IMG_1.PNG", "public.png", 100),
             new(PhotoKitResourceType.AlternatePhoto, "IMG_1b.DNG", "com.adobe.raw-image", 800), new(PhotoKitResourceType.FullSizePhoto, "FullSizeRender.jpg", "public.jpeg", 120)],
            ["smart:screenshots", "user-1"])!;

        Assert.Equal("AB/L0/001", asset.Id);
        Assert.Equal("AB_L0_001", asset.ShortId);
        Assert.Equal(MediaKind.Photo, asset.Kind);
        Assert.Equal("IMG_1.PNG", asset.FileName);
        Assert.Equal("image/png", asset.Mime);
        Assert.Equal(["original", "raw", "raw-2", "edited"], asset.Resources.Select(r => r.Role));
        Assert.Equal(created.ToUnixTimeMilliseconds(), asset.Taken);
        Assert.Equal(created.AddHours(5).ToUnixTimeMilliseconds(), asset.Modified);
        // Originals keep the creation time (PhotoKit never changes them); the edit has the asset's modification time.
        Assert.All(asset.Resources.Where(r => r.Role != "edited"), r => Assert.Equal(asset.Taken, r.Modified));
        Assert.Equal(asset.Modified, asset.Resources.Single(r => r.Role == "edited").Modified);
        Assert.Equal("2025/12", MediaLayout.Directory(asset));
        Assert.True(asset.Screenshot);
        Assert.Null(asset.RelativePath);
        Assert.Equal(1920, asset.Size); // all resources count
        Assert.Equal("image/x-adobe-dng", asset.Resources[1].Mime);
        Assert.Equal(["smart:screenshots", "user-1"], asset.AlbumIds);
    }

    [Fact]
    public void Videos_AudioAndEmptyAssets()
    {
        var video = IosPhotoMapping.Asset(new PhotoKitAsset("V/L0/001", PhotoKitMediaType.Video, PhotoKitSubtypes.VideoHighFrameRate, March, null, 1920, 1080,
            12.3456, null, null, false), [new(PhotoKitResourceType.Video, "IMG_2.MOV", "com.apple.quicktime-movie", 5000)], [])!;
        Assert.Equal((MediaKind.Video, 12346L, "video/quicktime"), (video.Kind, video.DurationMs!.Value, video.Mime));
        Assert.Equal(video.Taken, video.Modified); // no modification date: the creation date
        Assert.Null(IosPhotoMapping.Asset(new PhotoKitAsset("A/L0/001", PhotoKitMediaType.Audio, 0, March, March, 0, 0, 3, null, null, false),
            [new(PhotoKitResourceType.Audio, "a.m4a", "public.mpeg-4-audio", 10)], []));
        Assert.Null(IosPhotoMapping.Asset(new PhotoKitAsset("B/L0/001", PhotoKitMediaType.Image, 0, March, March, 0, 0, 0, null, null, false), [], []));
        // Made from an edit only: its render stands for the original.
        var renderOnly = IosPhotoMapping.Asset(new PhotoKitAsset("C/L0/001", PhotoKitMediaType.Image, 0, March, March, 0, 0, 0, null, null, false),
            [new(PhotoKitResourceType.FullSizePhoto, "FullSizeRender.jpg", "public.jpeg", 10)], [])!;
        Assert.Equal("original", renderOnly.Resources.Single().Role);
        Assert.Equal("image/jpeg", IosPhotoMapping.Mime("", "x.JPG", PhotoKitMediaType.Image));
        Assert.Equal("image/x-raw", IosPhotoMapping.Mime("com.sony.arw-raw-image", "x.ARW", PhotoKitMediaType.Image));
    }

    [Theory]
    [InlineData(PhotoKitAuthorization.Authorized, MediaAccess.Full)]
    [InlineData(PhotoKitAuthorization.Limited, MediaAccess.Partial)]
    [InlineData(PhotoKitAuthorization.Denied, MediaAccess.None)]
    [InlineData(PhotoKitAuthorization.Restricted, MediaAccess.None)]
    [InlineData(PhotoKitAuthorization.NotDetermined, MediaAccess.None)]
    public void Authorisation_MapsToAccess(PhotoKitAuthorization status, MediaAccess access)
    {
        _kit.Authorization = status;
        Assert.Equal(access, Library().Access);
        Assert.Equal(access, IosPhotoMapping.Access(status));
    }

    [Fact]
    public async Task WithoutPermission_NothingIsListedAndTheRunStops()
    {
        SeedLivePhoto();
        _kit.Authorization = PhotoKitAuthorization.Denied;
        var library = Library();
        Assert.Throws<UnauthorizedAccessException>(() => library.Assets());
        var repo = await RepoAsync();
        var e = await Assert.ThrowsAsync<SyncRefusedException>(() => BackUpAsync(repo, library));
        Assert.Contains("may not read the photos", e.Message);
        Assert.Equal(0, _kit.AssetCalls); // never asked PhotoKit (which would make iOS ask for the permission)
    }

    [Theory]
    [InlineData("PHPhotosErrorDomain", 3164, typeof(MediaUnavailableException))]
    [InlineData("PHPhotosErrorDomain", 3169, typeof(MediaUnavailableException))]
    [InlineData("NSURLErrorDomain", -1009, typeof(MediaUnavailableException))]
    [InlineData("CloudPhotoLibraryErrorDomain", 1005, typeof(MediaUnavailableException))]
    [InlineData("PHPhotosErrorDomain", 3303, typeof(FileNotFoundException))]
    [InlineData("PHPhotosErrorDomain", 3311, typeof(UnauthorizedAccessException))]
    [InlineData("NSCocoaErrorDomain", 4, typeof(IOException))]
    public void ReadErrors_SayWhatTheRunDoes(string domain, long code, Type expected)
    {
        var e = IosPhotoMapping.ReadError(new PhotoKitError(domain, code, "x"), "IMG_1.HEIC", allowNetwork: false);
        Assert.Equal(expected, e.GetType());
        if (e is MediaUnavailableException) Assert.Contains("iCloud", e.Message);
    }

    // ── Albums ────────────────────────────────────────────────────────────────

    [Fact]
    public void Albums_UserAlbumsAndUsefulSmartAlbums_WithStableIdsAndCounts()
    {
        SeedLivePhoto();
        _kit.Add(new PhotoKitAsset("V/L0/001", PhotoKitMediaType.Video, 0, March, March, 1920, 1080, 3, null, null, false),
            (PhotoKitResourceType.Video, "IMG_0002.MOV", "com.apple.quicktime-movie", Bytes(6), false));
        _kit.Album("user-trip", "Trip", smart: false, subtype: 2, LiveId, "V/L0/001");
        _kit.Album("user-empty", "Empty", smart: false, subtype: 2);
        _kit.Album("shared-1", "Family", smart: false, subtype: 101, LiveId); // Shared Albums: not offered
        _kit.Album("sys-fav", "Favorites", smart: true, subtype: 203, LiveId);
        _kit.Album("sys-videos", "Videos", smart: true, subtype: 202, "V/L0/001");
        _kit.Album("sys-pano", "Panoramas", smart: true, subtype: 201); // empty smart album: not listed
        _kit.Album("sys-hidden", "Hidden", smart: true, subtype: 205, LiveId); // not offered
        _kit.Album("sys-recents", "Recents", smart: true, subtype: 209, LiveId, "V/L0/001"); // that is "all"

        var library = Library();
        var albums = library.Albums().OrderBy(a => a.Name).ToList();

        Assert.Equal(["Empty", "Favorites", "Trip", "Videos"], albums.Select(a => a.Name));
        Assert.Equal(("smart:favorites", 1, 0), (albums[1].Id, albums[1].Photos, albums[1].Videos));
        Assert.Equal(("user-trip", 1, 1), (albums[2].Id, albums[2].Photos, albums[2].Videos));
        Assert.Equal(("smart:videos", 0, 1), (albums[3].Id, albums[3].Photos, albums[3].Videos));
        Assert.Equal((0, 0), (albums[0].Photos, albums[0].Videos));
        Assert.All(albums, a => Assert.Null(a.Path));
        var live = library.Assets().Single(a => a.Id == LiveId);
        Assert.Equal(["user-trip", "smart:favorites"], live.AlbumIds);
    }

    [Fact]
    public async Task AnAssetInSeveralAlbums_IsOneSetOfFiles_ItsSidecarListsThemAll_AndTheSelectionTakesItByAnyOfThem()
    {
        SeedLivePhoto();
        _kit.Album("user-trip", "Trip", smart: false, subtype: 2, LiveId);
        _kit.Album("user-best", "Best", smart: false, subtype: 2, LiveId);
        _kit.Album("sys-fav", "Favorites", smart: true, subtype: 203, LiveId);
        var repo = await RepoAsync();

        var (snapshot, _) = await BackUpAsync(repo, Library(), media: new MediaSelectionDto(false, [new MediaAlbumRef("smart:favorites", "Favorites")]));
        var files = await FilesAsync(repo, snapshot);

        Assert.Equal([$"2026/03/IMG_0001~{LiveShort}.edited.heic", $"2026/03/IMG_0001~{LiveShort}.heic", $"2026/03/IMG_0001~{LiveShort}.json", $"2026/03/IMG_0001~{LiveShort}.live.mov",
            $"2026/03/IMG_0001~{LiveShort}.raw.dng"], files.Keys.Order(StringComparer.Ordinal));
        var sidecar = MediaSidecar.Parse(files[$"2026/03/IMG_0001~{LiveShort}.json"]);
        Assert.Equal(["Trip", "Best", "Favorites"], sidecar.Albums.Select(a => a.Name));
        Assert.Equal(["user-trip", "user-best", "smart:favorites"], sidecar.Albums.Select(a => a.Id));
    }

    // ── Backup ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ABackup_KeepsEveryResourceAsItIs_WithAnIosSidecar()
    {
        SeedLivePhoto();
        var repo = await RepoAsync();
        var library = Library();

        var (snapshot, tree) = await BackUpAsync(repo, library);
        var files = await FilesAsync(repo, snapshot);

        Assert.Equal(Bytes(1), files[$"2026/03/IMG_0001~{LiveShort}.heic"]);
        Assert.Equal(Bytes(2, 60_000), files[$"2026/03/IMG_0001~{LiveShort}.live.mov"]);
        Assert.Equal(Bytes(3), files[$"2026/03/IMG_0001~{LiveShort}.edited.heic"]);
        Assert.Equal(Bytes(5, 40_000), files[$"2026/03/IMG_0001~{LiveShort}.raw.dng"]);
        Assert.DoesNotContain(files.Keys, k => k.Contains("Adjustments", StringComparison.Ordinal) || k.Contains("plist", StringComparison.Ordinal));
        var json = JsonDocument.Parse(files[$"2026/03/IMG_0001~{LiveShort}.json"]).RootElement;
        Assert.Equal("ios-photokit", json.GetProperty("source").GetString());
        Assert.Equal(LiveId, json.GetProperty("id").GetString());
        Assert.Equal("IMG_0001.HEIC", json.GetProperty("fileName").GetString());
        Assert.Equal("image/heic", json.GetProperty("mime").GetString());
        Assert.Equal("2026-03-09T21:30:00.000Z", json.GetProperty("taken").GetString());
        Assert.Equal("2026-03-10T21:30:00.000Z", json.GetProperty("modified").GetString());
        Assert.Equal(50.4501, json.GetProperty("location").GetProperty("latitude").GetDouble());
        Assert.True(json.GetProperty("favourite").GetBoolean());
        Assert.False(json.TryGetProperty("relativePath", out _));
        Assert.Equal(["original", "live", "edited", "raw"], json.GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("role").GetString()));
        Assert.Equal(20_000 + 60_000 + 20_000 + 40_000, json.GetProperty("size").GetInt64());
        Assert.Equal((1, 0), (tree.Counts().Photos, tree.Counts().Videos));
        Assert.Empty(snapshot.Stats.Errors);
    }

    [Fact]
    public async Task AFavouriteOrAlbumChange_ReadsNoOriginalAgain_ButTheSidecarSeesIt()
    {
        SeedLivePhoto();
        _kit.Update(LiveId, a => a with { Favorite = false });
        var repo = await RepoAsync();
        var (first, _) = await BackUpAsync(repo, Library());
        Assert.Equal(4, _kit.Reads.Values.Sum());

        // PhotoKit moves the asset's modification date with a favourite or an album change.
        _kit.Update(LiveId, a => a with { Favorite = true, Modified = a.Modified!.Value.AddDays(30) });
        _kit.Album("user-new", "New", smart: false, subtype: 2, LiveId);
        _kit.Reads.Clear();
        var (second, _) = await BackUpAsync(repo, Library());

        // The edit's render has the asset's time, so it is read again (and deduplicated); the originals are not.
        Assert.Equal(["FullSizeRender.heic"], _kit.Reads.Keys.Select(k => k.Split('|')[1]));
        Assert.Equal(2, second.Stats.FilesChanged); // the render (read again, the same chunks) and the sidecar
        var sidecar = MediaSidecar.Parse((await FilesAsync(repo, second))[$"2026/03/IMG_0001~{LiveShort}.json"]);
        Assert.True(sidecar.Favourite);
        Assert.Equal(["New"], sidecar.Albums.Select(a => a.Name));
        Assert.NotEqual(first.Tree, second.Tree);
    }

    [Fact]
    public async Task SizesPhotoKitDoesNotTell_AreRememberedFromTheRead_SoTheNextRunReadsNothing()
    {
        _kit.SizesKnown = false;
        SeedLivePhoto();
        var repo = await RepoAsync();
        var first = Library();
        Assert.All(first.Assets().Single().Resources, r => Assert.Equal(0, r.Size));
        await BackUpAsync(repo, first);
        first.FlushSizes();
        _kit.Reads.Clear();

        // A new process: the sizes come from ios-photo-sizes.json.
        var library = Library();
        Assert.Equal(20_000, library.Assets().Single().OriginalResource.Size);
        var (second, _) = await BackUpAsync(repo, library);
        Assert.Empty(_kit.Reads);
        Assert.Equal(4, second.Stats.FilesUnmodified);
    }

    // ── iCloud-only originals ─────────────────────────────────────────────────

    [Fact]
    public async Task AnICloudOnlyOriginal_WaitsForALaterRun_WithoutAnError_AndIsNeverTakenForDeleted()
    {
        _kit.Add(new PhotoKitAsset("LOCAL/L0/001", PhotoKitMediaType.Image, 0, March, March, 10, 10, 0, null, null, false),
            (PhotoKitResourceType.Photo, "IMG_0005.JPG", "public.jpeg", Bytes(10), false));
        SeedLivePhoto(cloudOnly: true);
        var repo = await RepoAsync();
        var settings = new IosPhotoSettings(Path.Combine(_dir, "ios-photos.json"));

        // A Wi-Fi-only folder on mobile data: no iCloud downloads.
        var (first, tree) = await BackUpAsync(repo, Library(settings), new MediaReadOptions(AllowNetwork: false));
        Assert.All(first.Stats.Errors, e => Assert.True(tree.IsPostponed(e), e));
        // Every file of the Live Photo waits: its resources, and its sidecar (read after the edit, which had to wait), so a new
        // asset never lands as a sidecar without its original.
        Assert.Equal(5, tree.Postponed);
        Assert.Contains("iCloud only", tree.PostponedReason);
        var files = await FilesAsync(repo, first);
        Assert.Equal(["2026/03/IMG_0005~LOCAL_L0_001.jpg", "2026/03/IMG_0005~LOCAL_L0_001.json"], files.Keys.Order(StringComparer.Ordinal));

        // On Wi-Fi: everything comes down.
        var (second, tree2) = await BackUpAsync(repo, Library(settings));
        Assert.Equal(0, tree2.Postponed);
        Assert.Empty(second.Stats.Errors);
        Assert.Equal(7, (await FilesAsync(repo, second)).Count);

        // The photo is edited on another device (iCloud only), and downloads are switched off on this iPhone: the run keeps every file
        // of it from the last snapshot (the old edit and the old sidecar), nothing disappears.
        _kit.Replace(LiveId, PhotoKitResourceType.FullSizePhoto, Bytes(30), cloudOnly: true);
        _kit.Update(LiveId, a => a with { Modified = a.Modified!.Value.AddDays(3) });
        settings.CloudDownloads = false;
        var (third, tree3) = await BackUpAsync(repo, Library(settings));
        Assert.Equal(2, tree3.Postponed); // the new edit and the sidecar; the originals are unchanged and not read
        Assert.All(third.Stats.Errors, e => Assert.True(tree3.IsPostponed(e), e));
        var kept = await FilesAsync(repo, third);
        Assert.Equal(7, kept.Count);
        Assert.Equal(Bytes(3), kept[$"2026/03/IMG_0001~{LiveShort}.edited.heic"]);

        settings.CloudDownloads = true;
        var (fourth, _) = await BackUpAsync(repo, Library(settings));
        Assert.Equal(Bytes(30), (await FilesAsync(repo, fourth))[$"2026/03/IMG_0001~{LiveShort}.edited.heic"]);
    }

    [Fact]
    public async Task ICloudUnreachable_AlsoWaits()
    {
        SeedLivePhoto(cloudOnly: true);
        _kit.NetworkUp = false;
        var repo = await RepoAsync();
        var (snapshot, tree) = await BackUpAsync(repo, Library());
        Assert.True(tree.Postponed > 0);
        Assert.All(snapshot.Stats.Errors, e => Assert.True(tree.IsPostponed(e), e));
        Assert.Contains("could not be downloaded now", tree.PostponedReason);
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reads_ArriveInPieces_AndPhotoKitWaitsWhileTheReaderIsBehind()
    {
        var pushed = 0;
        long told = -1;
        var stream = new PhotoReadStream(e => new IOException(e.Message), size => told = size);
        var cancelled = false;
        stream.Attach(new Disposable(() => cancelled = true));
        var producer = new Thread(() =>
        {
            for (var i = 0; i < 40; i++)
            {
                stream.Push(new byte[64 * 1024]);
                Interlocked.Increment(ref pushed);
            }
            stream.Complete(null);
        });
        producer.Start();
        Thread.Sleep(200);
        Assert.InRange(Volatile.Read(ref pushed), 1, 9); // at most a few pieces wait in memory
        using var sink = new MemoryStream();
        stream.CopyTo(sink);
        producer.Join();
        Assert.Equal(40 * 64 * 1024, sink.Length);
        Assert.Equal(sink.Length, told);
        stream.Dispose();
        Assert.True(cancelled);
    }

    [Fact]
    public void AFailedRead_ThrowsTheMappedError_AndAnEarlyCloseCancelsTheRequest()
    {
        var stream = new PhotoReadStream(e => IosPhotoMapping.ReadError(e, "IMG.HEIC", true));
        stream.Push([1, 2, 3]);
        stream.Complete(new PhotoKitError("NSURLErrorDomain", -1001, "timed out"));
        var buffer = new byte[10];
        Assert.Equal(3, stream.Read(buffer, 0, 10));
        Assert.Throws<MediaUnavailableException>(() => stream.Read(buffer, 0, 10));

        var early = new PhotoReadStream(e => new IOException(e.Message));
        var cancelled = false;
        var producer = new Thread(() => { for (var i = 0; i < 100; i++) early.Push(new byte[1000]); });
        producer.Start();
        early.Attach(new Disposable(() => cancelled = true));
        Assert.Equal(10, early.Read(buffer, 0, 10));
        early.Dispose();
        Assert.True(producer.Join(TimeSpan.FromSeconds(5))); // a waiting Push does not hang PhotoKit's queue
        Assert.True(cancelled);
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    [Fact]
    public void RestorePlan_SkipsSmartAlbums_FindsByIdThenName_AndMakesEachMissingAlbumOnce()
    {
        var plan = IosRestorePlan.Albums(
            [new("smart:favorites", "Favorites"), new("user-1", "Trip"), new("gone", "best of 2026 "), new("other", "Kids"), new("again", "kids"),
             new("x", "Made before"), new("", "")],
            [("user-1", "Renamed trip"), ("user-2", "Best of 2026")], alreadyIn: [], made: new Dictionary<string, string> { ["Made before"] = "user-9" });
        Assert.Equal(["user-1", "user-2", "user-9"], plan.Existing);
        Assert.Equal(["Kids"], plan.Create);

        var none = IosRestorePlan.Albums([new("user-1", "Trip")], [("user-1", "Trip")], alreadyIn: ["user-1"], made: new Dictionary<string, string>());
        Assert.Empty(none.Existing);
        Assert.Empty(none.Create);
    }

    [Fact]
    public async Task Restore_PutsALivePhotoBackPaired_IntoItsAlbums_MakingTheMissingOnce_AndSkipsWhatIsThere()
    {
        SeedLivePhoto();
        _kit.Add(new PhotoKitAsset("P2/L0/001", PhotoKitMediaType.Image, 0, March, March, 10, 10, 0, null, null, false),
            (PhotoKitResourceType.Photo, "IMG_0002.JPG", "public.jpeg", Bytes(20), false));
        _kit.Album("user-trip", "Trip", smart: false, subtype: 2, LiveId, "P2/L0/001");
        _kit.Album("sys-fav", "Favorites", smart: true, subtype: 203, LiveId);
        var repo = await RepoAsync();
        var (snapshot, _) = await BackUpAsync(repo, Library());

        // Everything still there: nothing restored.
        var library = Library();
        var none = await new MediaRestorer(repo, library).RunAsync(snapshot, ct: TestContext.Current.CancellationToken);
        Assert.Equal((0, 2), (none.Restored, none.Skipped));
        Assert.Empty(_kit.Created);

        _kit.Wipe(); // a new iPhone: no photos, no albums
        var result = await new MediaRestorer(repo, library).RunAsync(snapshot, ct: TestContext.Current.CancellationToken);
        Assert.Equal((2, 0), (result.Restored, result.Skipped));
        Assert.Empty(result.Errors);
        var live = _kit.Created.Single(c => c.Creation.Files[0].FileName == "IMG_0001.HEIC");
        Assert.Equal([(PhotoKitResourceType.Photo, "IMG_0001.HEIC"), (PhotoKitResourceType.PairedVideo, "IMG_0001.mov"), (PhotoKitResourceType.AlternatePhoto, "IMG_0001.dng")],
            live.Creation.Files.Select(f => (f.Type, f.FileName)));
        Assert.Equal(Bytes(1), live.Bytes[0]);
        Assert.Equal(Bytes(2, 60_000), live.Bytes[1]);
        Assert.Equal(March.ToUnixTimeMilliseconds(), live.Creation.Created!.Value.ToUnixTimeMilliseconds());
        Assert.Equal((50.4501, 30.5234), (live.Creation.Latitude!.Value, live.Creation.Longitude!.Value));
        Assert.True(live.Creation.Favorite);
        var second = _kit.Created.Single(c => c != live);
        // "Trip" made once, by whichever came first; the other went into it. Favorites is never made.
        Assert.Equal(1, _kit.Created.Sum(c => c.Creation.NewAlbums.Count));
        Assert.Equal(["Trip"], _kit.Created.SelectMany(c => c.Creation.NewAlbums));
        Assert.Single(_kit.Created.SelectMany(c => c.Creation.AlbumIds));
        Assert.DoesNotContain(_kit.Created.SelectMany(c => c.Creation.NewAlbums), n => n == "Favorites");
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "tmp"))); // the spooled files are gone
        _ = second;

        // Again: both found by content (new ids), nothing duplicated.
        var again = await new MediaRestorer(repo, Library()).RunAsync(snapshot, ct: TestContext.Current.CancellationToken);
        Assert.Equal((0, 2), (again.Restored, again.Skipped));
        Assert.Equal(2, _kit.Created.Count);

        // Taken out of its album on the phone: a restore leaves the photo alone and puts it back into the album.
        var trip = _kit.AlbumList.Single(a => a.Title == "Trip");
        var restoredLive = _kit.AssetList.Single(a => _kit.FileNames(a.Id).Contains("IMG_0001.HEIC")).Id;
        _kit.RemoveFromAlbum(trip.Id, restoredLive);
        var back = await new MediaRestorer(repo, Library()).RunAsync(snapshot, ct: TestContext.Current.CancellationToken);
        Assert.Equal((0, 2), (back.Restored, back.Skipped));
        Assert.Contains(_kit.AlbumAdds, a => a.AssetId == restoredLive && a.AlbumIds.SequenceEqual([trip.Id]) && a.NewAlbums.Count == 0);
    }

    [Fact]
    public async Task ARestorePhotoKitRefuses_TriesWithoutTheRaw_ThenTheOriginalAlone()
    {
        SeedLivePhoto();
        var repo = await RepoAsync();
        var (snapshot, _) = await BackUpAsync(repo, Library());
        _kit.Wipe();
        _kit.MaxFiles = 2; // PhotoKit refuses photo + Live Photo video + RAW in one request
        var result = await new MediaRestorer(repo, Library()).RunAsync(snapshot, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Restored);
        Assert.Equal([PhotoKitResourceType.Photo, PhotoKitResourceType.PairedVideo], _kit.Created.Single().Creation.Files.Select(f => f.Type));
        Assert.Equal(1, _kit.Refused);

        var attempts = IosRestorePlan.Attempts([(PhotoKitResourceType.Video, "a", "a.mov")]);
        Assert.Single(attempts);
        Assert.Equal([3, 2, 1], IosRestorePlan.Attempts([(PhotoKitResourceType.Photo, "a", "a"), (PhotoKitResourceType.PairedVideo, "b", "b"),
            (PhotoKitResourceType.AlternatePhoto, "c", "c")]).Select(a => a.Count));
        Assert.Equal([2, 1], IosRestorePlan.Attempts([(PhotoKitResourceType.Photo, "a", "a"), (PhotoKitResourceType.PairedVideo, "b", "b")]).Select(a => a.Count));
    }

    // ── New photos ────────────────────────────────────────────────────────────

    [Fact]
    public void ChangeDebouncer_MakesOneRunOfABurst_AndWaitsAtMostAMinute()
    {
        var now = DateTimeOffset.UnixEpoch;
        var timers = new List<(TimeSpan Delay, Action Action, Disposable Handle)>();
        var fired = 0;
        var debouncer = new PhotoChangeDebouncer(() => fired++, now: () => now, schedule: (delay, action) =>
        {
            var handle = new Disposable(() => { });
            timers.Add((delay, action, handle));
            return handle;
        });

        debouncer.Changed();
        now += TimeSpan.FromSeconds(2);
        debouncer.Changed();
        Assert.Equal(TimeSpan.FromSeconds(5), timers[^1].Delay);
        timers[0].Action(); // the first timer was replaced: nothing
        Assert.Equal(0, fired);
        timers[^1].Action();
        Assert.Equal(1, fired);

        // A change every 4 seconds: the run is due 60 seconds after the first change, not 5 after the last.
        timers.Clear();
        var start = now;
        for (var i = 0; i <= 14; i++, now += TimeSpan.FromSeconds(4))
        {
            debouncer.Changed();
            Assert.Equal(i * 4 <= 55 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(60 - i * 4), timers[^1].Delay);
        }
        Assert.Equal(start + TimeSpan.FromSeconds(60), now - TimeSpan.FromSeconds(4) + timers[^1].Delay);
        timers[^1].Action();
        Assert.Equal(2, fired);
        Assert.Equal(2, debouncer.Fired);
    }

    [Fact]
    public void PhotosFolders_AreInTheBackgroundTasksPlan()
    {
        var photos = Folder() with { FolderId = 9 };
        var plan = BackgroundPlanner.Plan([photos]);
        Assert.Equal([9], plan.Select(p => p.FolderId));
        Assert.Equal(2, IosBackgroundPlan.Requests(plan).Count);
        Assert.Equal([9], IosBackgroundPlan.FoldersFor(IosTaskKind.Refresh, plan, new Dictionary<int, DateTimeOffset>(), DateTimeOffset.UtcNow));
        Assert.Equal([9], BackgroundPlanner.PhotoFolders([photos]));
    }

    [Fact]
    public void Settings_CloudDownloads_DefaultOn_AndKept()
    {
        var path = Path.Combine(_dir, "settings", "ios-photos.json");
        var settings = new IosPhotoSettings(path);
        Assert.True(settings.CloudDownloads);
        var changed = 0;
        settings.Changed += () => changed++;
        settings.CloudDownloads = false;
        Assert.False(new IosPhotoSettings(path).CloudDownloads);
        Assert.Equal(1, changed);
        File.WriteAllText(path, "not json");
        Assert.True(new IosPhotoSettings(path).CloudDownloads);
    }

    [Fact]
    public void IosTexts_SayLimitedAccess_AndOfferICloudDownloads()
    {
        var t = IosPhotoTexts.Texts;
        Assert.Contains("Full Access", t.Explanation);
        Assert.Contains("Limit Access", t.PartialText);
        Assert.Null(t.NoLocation);
        Assert.Contains("iCloud", t.CloudLabel);
        Assert.DoesNotContain("Android", t.Explanation + t.PartialText + t.CardPartial + t.CloudHint);
    }

    private sealed class Disposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>
/// PhotoKit in memory (<see cref="IPhotoKit"/>): assets with their resources (some only "in iCloud": readable only with network
/// access allowed and the network up), albums, reads counted per resource and delivered in pieces on another thread, creations
/// and album changes recorded (the spooled files' bytes copied, since the library deletes them after).
/// </summary>
internal sealed class FakePhotoKit : IPhotoKit
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<(PhotoKitResource Resource, byte[] Bytes, bool CloudOnly)>> _resources = new(StringComparer.Ordinal);
    private int _next;

    public PhotoKitAuthorization Authorization { get; set; } = PhotoKitAuthorization.Authorized;
    public List<PhotoKitAsset> AssetList { get; } = [];
    public List<PhotoKitAlbum> AlbumList { get; } = [];
    public bool SizesKnown { get; set; } = true;
    public bool NetworkUp { get; set; } = true;
    public int AssetCalls { get; private set; }
    public ConcurrentDictionary<string, int> Reads { get; } = new(); // "asset|file" → reads
    public List<(PhotoKitCreation Creation, List<byte[]> Bytes)> Created { get; } = [];
    public List<(string AssetId, IReadOnlyList<string> AlbumIds, IReadOnlyList<string> NewAlbums)> AlbumAdds { get; } = [];

    public void Add(PhotoKitAsset asset, params (PhotoKitResourceType Type, string Name, string Uti, byte[] Bytes, bool CloudOnly)[] resources)
    {
        lock (_gate)
        {
            AssetList.Add(asset);
            _resources[asset.Id] = [.. resources.Select(r => (new PhotoKitResource(r.Type, r.Name, r.Uti, r.Bytes.Length), r.Bytes, r.CloudOnly))];
        }
    }

    public void Update(string id, Func<PhotoKitAsset, PhotoKitAsset> change)
    {
        lock (_gate)
        {
            var i = AssetList.FindIndex(a => a.Id == id);
            AssetList[i] = change(AssetList[i]);
        }
    }

    public void Replace(string id, PhotoKitResourceType type, byte[] bytes, bool cloudOnly)
    {
        lock (_gate)
        {
            var list = _resources[id];
            var i = list.FindIndex(r => r.Resource.Type == type);
            list[i] = (list[i].Resource with { Size = bytes.Length }, bytes, cloudOnly);
        }
    }

    public void Album(string id, string title, bool smart, int subtype, params string[] assets)
    {
        lock (_gate) AlbumList.Add(new PhotoKitAlbum(id, title, smart, subtype, [.. assets]));
    }

    public void RemoveFromAlbum(string albumId, string assetId)
    {
        lock (_gate)
        {
            var i = AlbumList.FindIndex(a => a.Id == albumId);
            AlbumList[i] = AlbumList[i] with { AssetIds = [.. AlbumList[i].AssetIds.Where(a => a != assetId)] };
        }
    }

    public void Wipe()
    {
        lock (_gate)
        {
            AssetList.Clear();
            AlbumList.Clear();
            _resources.Clear();
        }
    }

    public IReadOnlyList<string> FileNames(string assetId)
    {
        lock (_gate) return [.. _resources[assetId].Select(r => r.Resource.FileName)];
    }

    public IReadOnlyList<PhotoKitAsset> Assets()
    {
        lock (_gate)
        {
            AssetCalls++;
            return [.. AssetList];
        }
    }

    public IReadOnlyList<PhotoKitResource> Resources(string assetId)
    {
        lock (_gate)
            return _resources.TryGetValue(assetId, out var list)
                ? [.. list.Select(r => SizesKnown ? r.Resource : r.Resource with { Size = null })]
                : [];
    }

    public IReadOnlyList<PhotoKitAlbum> Albums(IReadOnlyCollection<int> smartSubtypes)
    {
        lock (_gate) return [.. AlbumList.Where(a => !a.Smart || smartSubtypes.Contains(a.Subtype))];
    }

    public IDisposable Read(string assetId, PhotoKitResourceType type, string fileName, bool allowNetwork, Action<byte[]> data, Action<PhotoKitError?> done)
    {
        (PhotoKitResource Resource, byte[] Bytes, bool CloudOnly) item;
        lock (_gate) item = _resources[assetId].First(r => r.Resource.Type == type && r.Resource.FileName == fileName);
        var cancelled = new CancellationTokenSource();
        _ = Task.Run(() =>
        {
            if (item.CloudOnly && !allowNetwork)
            {
                done(new PhotoKitError("PHPhotosErrorDomain", 3164, "Network access required"));
                return;
            }
            if (item.CloudOnly && !NetworkUp)
            {
                done(new PhotoKitError("NSURLErrorDomain", -1009, "The Internet connection appears to be offline."));
                return;
            }
            Reads.AddOrUpdate($"{assetId}|{fileName}", 1, (_, n) => n + 1);
            for (var offset = 0; offset < item.Bytes.Length && !cancelled.IsCancellationRequested; offset += 7000)
                data(item.Bytes[offset..Math.Min(item.Bytes.Length, offset + 7000)]);
            done(null);
        });
        return cancelled;
    }

    /// <summary>More files in one creation request than this: refused, as PhotoKit refuses combinations it does not take.</summary>
    public int? MaxFiles { get; set; }
    public int Refused { get; private set; }

    public Task<PhotoKitCreated> CreateAsync(PhotoKitCreation creation, CancellationToken ct)
    {
        lock (_gate)
        {
            if (creation.Files.Count > MaxFiles)
            {
                Refused++;
                return Task.FromException<PhotoKitCreated>(new IOException("The operation couldn't be completed. (PHPhotosErrorDomain error -1.)"));
            }
            var bytes = creation.Files.Select(f => File.ReadAllBytes(f.Path)).ToList();
            Created.Add((creation, bytes));
            var id = $"NEW{++_next}/L0/001";
            AssetList.Add(new PhotoKitAsset(id, creation.Files[0].Type == PhotoKitResourceType.Video ? PhotoKitMediaType.Video : PhotoKitMediaType.Image, 0,
                creation.Created, creation.Created, 10, 10, 0, creation.Latitude, creation.Longitude, creation.Favorite));
            _resources[id] = [.. creation.Files.Select((f, i) => (new PhotoKitResource(f.Type, f.FileName, "", bytes[i].Length), bytes[i], false))];
            foreach (var album in creation.AlbumIds)
            {
                var i = AlbumList.FindIndex(a => a.Id == album);
                AlbumList[i] = AlbumList[i] with { AssetIds = [.. AlbumList[i].AssetIds, id] };
            }
            var made = new Dictionary<string, string>();
            foreach (var title in creation.NewAlbums)
            {
                var albumId = $"made-{++_next}";
                AlbumList.Add(new PhotoKitAlbum(albumId, title, false, 2, [id]));
                made[title] = albumId;
            }
            return Task.FromResult(new PhotoKitCreated(id, made));
        }
    }

    public Task<IReadOnlyDictionary<string, string>> AddToAlbumsAsync(string assetId, IReadOnlyList<string> albumIds, IReadOnlyList<string> newAlbums, CancellationToken ct)
    {
        lock (_gate)
        {
            AlbumAdds.Add((assetId, albumIds, newAlbums));
            foreach (var album in albumIds)
            {
                var i = AlbumList.FindIndex(a => a.Id == album);
                AlbumList[i] = AlbumList[i] with { AssetIds = [.. AlbumList[i].AssetIds, assetId] };
            }
            var made = new Dictionary<string, string>();
            foreach (var title in newAlbums)
            {
                var albumId = $"made-{++_next}";
                AlbumList.Add(new PhotoKitAlbum(albumId, title, false, 2, [assetId]));
                made[title] = albumId;
            }
            return Task.FromResult<IReadOnlyDictionary<string, string>>(made);
        }
    }
}
