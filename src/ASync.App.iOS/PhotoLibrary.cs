using ASync.Agent;
using ASync.App.Services;
using ASync.Client.Core.Background;
using ASync.Contracts;
using CoreLocation;
using Foundation;
using Photos;
using PhotosUI;
using UIKit;

namespace ASync.App.iOS;

/// <summary>
/// PhotoKit for the photo backup (task 23), as thin as it can be: listings, reads and creation requests, no decisions (those are
/// <see cref="IosMediaLibrary"/> and <see cref="IosPhotoMapping"/> in Shared/, tested on Linux). AGENTS.md "iOS photos (task 23)".
/// </summary>
public sealed class PhotoKitBridge : IPhotoKit
{
    public const string ErrorDomain = "PHPhotosErrorDomain";

    public PhotoKitAuthorization Authorization => (PhotoKitAuthorization)(int)PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite);

    /// <summary>Images and videos only; hidden assets and a burst's other photos are not listed (PhotoKit's defaults).</summary>
    private static PHFetchOptions Visual() => new()
    {
        Predicate = NSPredicate.FromFormat("mediaType == %@ || mediaType == %@",
            [NSNumber.FromInt32((int)PHAssetMediaType.Image), NSNumber.FromInt32((int)PHAssetMediaType.Video)]),
        IncludeHiddenAssets = false,
        IncludeAllBurstAssets = false,
    };

    private static DateTimeOffset? Time(NSDate? date) => date is null ? null : new DateTimeOffset((DateTime)date, TimeSpan.Zero);

    public IReadOnlyList<PhotoKitAsset> Assets()
    {
        using var result = PHAsset.FetchAssets(Visual());
        var list = new List<PhotoKitAsset>((int)result.Count);
        for (nint i = 0; i < result.Count; i++)
        {
            if (result[i] is not PHAsset a) continue;
            var location = a.Location;
            list.Add(new PhotoKitAsset(a.LocalIdentifier, (PhotoKitMediaType)(int)a.MediaType, (PhotoKitSubtypes)(ulong)a.MediaSubtypes,
                Time(a.CreationDate), Time(a.ModificationDate), (int)a.PixelWidth, (int)a.PixelHeight, a.Duration,
                location?.Coordinate.Latitude, location?.Coordinate.Longitude, a.Favorite));
        }
        return list;
    }

    private static PHAsset? Asset(string id)
    {
        using var result = PHAsset.FetchAssetsUsingLocalIdentifiers([id], null);
        return result.FirstObject as PHAsset;
    }

    /// <summary>The resources; the size through KVC's <c>fileSize</c> (no public property; null when it is not there).</summary>
    public IReadOnlyList<PhotoKitResource> Resources(string assetId)
    {
        if (Asset(assetId) is not { } asset) return [];
        return [.. PHAssetResource.GetAssetResources(asset).Select(r => new PhotoKitResource((PhotoKitResourceType)(int)r.ResourceType,
            r.OriginalFilename ?? "asset", Uti(r), FileSize(r)))];
    }

    private static string Uti(PHAssetResource r) =>
        OperatingSystem.IsIOSVersionAtLeast(26) ? r.ContentType?.Identifier ?? "" : r.UniformTypeIdentifier ?? "";

    private static long? FileSize(PHAssetResource r)
    {
        try { return r.ValueForKey(new NSString("fileSize")) is NSNumber n ? n.Int64Value : null; }
        catch (Exception) { return null; }
    }

    public IReadOnlyList<PhotoKitAlbum> Albums(IReadOnlyCollection<int> smartSubtypes)
    {
        var list = new List<PhotoKitAlbum>();
        void Add(PHAssetCollectionType type, bool smart)
        {
            using var collections = PHAssetCollection.FetchAssetCollections(type, PHAssetCollectionSubtype.Any, null);
            for (nint i = 0; i < collections.Count; i++)
            {
                if (collections[i] is not PHAssetCollection c) continue;
                var subtype = (int)c.AssetCollectionSubtype;
                if (smart && !smartSubtypes.Contains(subtype)) continue;
                using var members = PHAsset.FetchAssets(c, Visual());
                var ids = new List<string>((int)members.Count);
                for (nint j = 0; j < members.Count; j++)
                    if (members[j] is PHAsset a) ids.Add(a.LocalIdentifier);
                list.Add(new PhotoKitAlbum(c.LocalIdentifier, c.LocalizedTitle ?? "", smart, subtype, ids));
            }
        }
        Add(PHAssetCollectionType.Album, smart: false);
        if (smartSubtypes.Count > 0) Add(PHAssetCollectionType.SmartAlbum, smart: true);
        return list;
    }

    public IDisposable Read(string assetId, PhotoKitResourceType type, string fileName, bool allowNetwork, Action<byte[]> data, Action<PhotoKitError?> done)
    {
        var resource = Asset(assetId) is { } asset
            ? PHAssetResource.GetAssetResources(asset).FirstOrDefault(r => (int)r.ResourceType == (int)type && r.OriginalFilename == fileName)
              ?? PHAssetResource.GetAssetResources(asset).FirstOrDefault(r => (int)r.ResourceType == (int)type)
            : null;
        if (resource is null)
        {
            done(new PhotoKitError(ErrorDomain, (long)PHPhotosError.MissingResource, $"{fileName} is no longer in the photo library"));
            return new Cancel(0);
        }
        var options = new PHAssetResourceRequestOptions { NetworkAccessAllowed = allowNetwork };
        var id = PHAssetResourceManager.DefaultManager.RequestData(resource, options,
            chunk => data(chunk.ToArray()),
            error => done(error is null ? null : new PhotoKitError(error.Domain, error.Code, error.LocalizedDescription)));
        return new Cancel(id);
    }

    private sealed class Cancel(int id) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (id != 0 && Interlocked.Exchange(ref _done, 1) == 0) PHAssetResourceManager.DefaultManager.CancelDataRequest(id);
        }
    }

    private static PHAssetCollection? Collection(string id)
    {
        using var result = PHAssetCollection.FetchAssetCollections([id], null);
        return result.FirstObject as PHAssetCollection;
    }

    /// <summary>PHPhotoLibrary.performChanges; a cancellation stops waiting (PhotoKit finishes or drops the change on its own).</summary>
    private static async Task PerformAsync(Action changes, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(changes, (ok, error) =>
        {
            if (ok) done.TrySetResult(true);
            else done.TrySetException(new IOException(error?.LocalizedDescription ?? "The photo library refused the change."));
        });
        await done.Task.WaitAsync(ct);
    }

    public async Task<PhotoKitCreated> CreateAsync(PhotoKitCreation creation, CancellationToken ct)
    {
        PHObjectPlaceholder? asset = null;
        var albums = new Dictionary<string, PHObjectPlaceholder>(StringComparer.Ordinal);
        await PerformAsync(() =>
        {
            var request = PHAssetCreationRequest.CreationRequestForAsset();
            foreach (var (type, path, name) in creation.Files)
            {
                var options = new PHAssetResourceCreationOptions { OriginalFilename = name, ShouldMoveFile = true };
                request.AddResource((PHAssetResourceType)(int)type, NSUrl.CreateFileUrl(path, false, null), options);
            }
            if (creation.Created is { } created) request.CreationDate = (NSDate)created.UtcDateTime;
            if (creation.Latitude is { } lat && creation.Longitude is { } lon) request.Location = new CLLocation(lat, lon);
            request.Favorite = creation.Favorite;
            asset = request.PlaceholderForCreatedAsset;
            if (asset is null) return;
            foreach (var id in creation.AlbumIds)
                if (Collection(id) is { } c) PHAssetCollectionChangeRequest.ChangeRequest(c)?.AddAssets([asset]);
            foreach (var title in creation.NewAlbums)
            {
                var made = PHAssetCollectionChangeRequest.CreateAssetCollection(title);
                made.AddAssets([asset]);
                albums[title] = made.PlaceholderForCreatedAssetCollection;
            }
        }, ct);
        return new PhotoKitCreated(asset?.LocalIdentifier ?? "", albums.ToDictionary(p => p.Key, p => p.Value.LocalIdentifier));
    }

    public async Task<IReadOnlyDictionary<string, string>> AddToAlbumsAsync(string assetId, IReadOnlyList<string> albumIds, IReadOnlyList<string> newAlbums,
        CancellationToken ct)
    {
        if (Asset(assetId) is not { } asset) return new Dictionary<string, string>();
        var albums = new Dictionary<string, PHObjectPlaceholder>(StringComparer.Ordinal);
        await PerformAsync(() =>
        {
            foreach (var id in albumIds)
                if (Collection(id) is { } c) PHAssetCollectionChangeRequest.ChangeRequest(c)?.AddAssets([asset]);
            foreach (var title in newAlbums)
            {
                var made = PHAssetCollectionChangeRequest.CreateAssetCollection(title);
                made.AddAssets([asset]);
                albums[title] = made.PlaceholderForCreatedAssetCollection;
            }
        }, ct);
        return albums.ToDictionary(p => p.Key, p => p.Value.LocalIdentifier);
    }
}

/// <summary>
/// The permission to read the photo library (PHAccessLevel.readWrite): Full Access = Full, Limited Access ("Selected Photos") =
/// Partial with the system's picker to change the selection, denied or restricted = None. Asked only from the Photos screen's
/// Allow button (as on Android), never at launch; once answered, iOS asks no more and Allow opens the app's page in Settings.
/// Adding to the library is covered by read/write access; NSPhotoLibraryAddUsageDescription is there for a restore all the same.
/// </summary>
public sealed class IosPhotoAccess(IosPhotoSettings settings) : IPhotoAccess
{
    /// <summary>Tests only (<see cref="PhotosAutomation"/>): what the screens show instead of the real permission; the agent reads the real one.</summary>
    internal static MediaAccess? Forced;

    public MediaAccess Access => Forced ?? IosPhotoMapping.Access((PhotoKitAuthorization)(int)PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite));

    /// <summary>PhotoKit gives the originals with their location; there is no separate permission for it.</summary>
    public bool HasLocation => true;

    public bool OffersPartial => true;

    public PhotoTexts Texts => IosPhotoTexts.Texts;

    public void Request()
    {
        if (PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite) != PHAuthorizationStatus.NotDetermined)
        {
            OpenSettings(); // answered once: iOS does not ask again
            return;
        }
        PHPhotoLibrary.RequestAuthorization(PHAccessLevel.ReadWrite, _ => NotifyChanged());
    }

    public void OpenSettings() => UIApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        if (NSUrl.FromString(UIApplication.OpenSettingsUrlString) is { } url)
            UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
    });

    public bool CanChooseMore => Forced == MediaAccess.Partial || PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite) == PHAuthorizationStatus.Limited;

    public void ChooseMore() => UIApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        if (IosPlatform.Top() is { } top) PHPhotoLibrary.SharedPhotoLibrary.PresentLimitedLibraryPicker(top, _ => NotifyChanged());
    });

    public bool OffersCloudDownloads => true;

    public bool CloudDownloads
    {
        get => settings.CloudDownloads;
        set => settings.CloudDownloads = value;
    }

    public event Action? Changed;

    public void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Console.Error.WriteLine($"ASync: a photo-permission listener failed: {e.Message}"); }
        IosPhotos.EnsureObserver();
    }
}

/// <summary>
/// The photo backup's parts of the process (task 23): the library, the permission, the setting, and the change observer that starts
/// the Photos folders when photos arrive while the app runs (debounced: <see cref="PhotoChangeDebouncer"/>). In the background
/// the Photos folders run with every other folder from task 22's BGTaskScheduler tasks.
/// </summary>
public static class IosPhotos
{
    private static readonly Lock Gate = new();
    private static AgentRuntime? _runtime;
    private static Observer? _observer;
    private static PhotoChangeDebouncer? _debouncer;

    public static IosPhotoSettings Settings { get; private set; } = null!;
    public static IosMediaLibrary Library { get; private set; } = null!;
    public static IosPhotoAccess Access { get; private set; } = null!;

    /// <summary>The library and the permission, before the agent starts.</summary>
    public static IosMediaLibrary Create(string configDirectory, string home)
    {
        Settings = new IosPhotoSettings(Path.Combine(configDirectory, "ios-photos.json"));
        Library = new IosMediaLibrary(new PhotoKitBridge(), Settings, Path.Combine(home, "tmp", "async-restore"),
            Path.Combine(configDirectory, "ios-photo-sizes.json"));
        Access = new IosPhotoAccess(Settings);
        return Library;
    }

    public static void Attach(AgentRuntime runtime)
    {
        _runtime = runtime;
        _debouncer = new PhotoChangeDebouncer(RunPhotoFolders);
        EnsureObserver();
    }

    /// <summary>How many debounced change batches started the Photos folders (the smoke test reads it).</summary>
    public static int Triggered => _debouncer?.Fired ?? 0;

    /// <summary>
    /// Registers the change observer once ASync may read photos: registering earlier would make iOS ask for the permission at
    /// launch, which the app never does.
    /// </summary>
    public static void EnsureObserver()
    {
        lock (Gate)
        {
            if (_observer is not null || _runtime is null || Access.Access is MediaAccess.None or MediaAccess.Unsupported) return;
            _observer = new Observer();
            PHPhotoLibrary.SharedPhotoLibrary.RegisterChangeObserver(_observer);
        }
    }

    private static void RunPhotoFolders()
    {
        if (_runtime is not { FoldersLoaded: true } runtime) return;
        var ids = BackgroundPlanner.PhotoFolders(runtime.Folders.Select(f => f.Folder));
        Console.WriteLine($"ASync: new photos or videos: backing up {ids.Count} Photos folder(s).");
        foreach (var id in ids) runtime.Trigger(id, "new photos");
    }

    private sealed class Observer : PHPhotoLibraryChangeObserver
    {
        public override void PhotoLibraryDidChange(PHChange changeInstance)
        {
            // Also a limited selection changed in the system's picker: the Photos screen reads the access and albums again.
            Access.NotifyChanged();
            _debouncer?.Changed();
        }
    }
}
