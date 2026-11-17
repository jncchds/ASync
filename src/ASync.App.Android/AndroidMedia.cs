using System.Collections.Concurrent;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Media;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Util;
using AndroidX.Work;
using ASync.App.Services;
using ASync.Client.Core.Background;
using ASync.Client.Core.Media;
using ASync.Contracts;
using Java.Util.Concurrent;
using Uri = Android.Net.Uri;
using Stream = System.IO.Stream;

namespace ASync.App.Android;

/// <summary>
/// The permission to read photos and videos (task 19): READ_MEDIA_IMAGES and READ_MEDIA_VIDEO on Android 13+, with Android 14's
/// READ_MEDIA_VISUAL_USER_SELECTED ("Select photos": partial access); READ_EXTERNAL_STORAGE before 13; All files access also lets
/// MediaStore show everything. ACCESS_MEDIA_LOCATION keeps the location in the originals (<see cref="MediaStore.SetRequireOriginal"/>).
/// </summary>
public sealed class AndroidPhotoAccess : IPhotoAccess
{
    public static readonly AndroidPhotoAccess Instance = new();
    public const int RequestCode = 3;

    private static Context Context => global::Android.App.Application.Context;

    private static bool Granted(string permission) => Context.CheckSelfPermission(permission) == Permission.Granted;

    public MediaAccess Access
    {
        get
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                if (Granted(Manifest.Permission.ReadMediaImages) && Granted(Manifest.Permission.ReadMediaVideo)) return MediaAccess.Full;
                if (OperatingSystem.IsAndroidVersionAtLeast(30) && global::Android.OS.Environment.IsExternalStorageManager) return MediaAccess.Full;
                if (OperatingSystem.IsAndroidVersionAtLeast(34) && Granted(Manifest.Permission.ReadMediaVisualUserSelected)) return MediaAccess.Partial;
                // Images without videos (or the reverse) is "some": what MediaStore shows is what is backed up.
                return Granted(Manifest.Permission.ReadMediaImages) || Granted(Manifest.Permission.ReadMediaVideo) ? MediaAccess.Partial : MediaAccess.None;
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(30) && global::Android.OS.Environment.IsExternalStorageManager) return MediaAccess.Full;
            return Granted(Manifest.Permission.ReadExternalStorage) ? MediaAccess.Full : MediaAccess.None;
        }
    }

    public bool HasLocation => !OperatingSystem.IsAndroidVersionAtLeast(29) || Granted(Manifest.Permission.AccessMediaLocation);

    public bool OffersPartial => OperatingSystem.IsAndroidVersionAtLeast(34);

    public static string[] Wanted() =>
        OperatingSystem.IsAndroidVersionAtLeast(34)
            ? [Manifest.Permission.ReadMediaImages, Manifest.Permission.ReadMediaVideo, Manifest.Permission.ReadMediaVisualUserSelected, Manifest.Permission.AccessMediaLocation]
            : OperatingSystem.IsAndroidVersionAtLeast(33)
                ? [Manifest.Permission.ReadMediaImages, Manifest.Permission.ReadMediaVideo, Manifest.Permission.AccessMediaLocation]
                : OperatingSystem.IsAndroidVersionAtLeast(29)
                    ? [Manifest.Permission.ReadExternalStorage, Manifest.Permission.AccessMediaLocation]
                    : [Manifest.Permission.ReadExternalStorage];

    public void Request()
    {
        if (MainActivity.Current is not { } activity) return;
        activity.RunOnUiThread(() => activity.RequestPermissions(Wanted(), RequestCode));
    }

    public void OpenSettings()
    {
        var intent = new Intent(Settings.ActionApplicationDetailsSettings, Uri.Parse($"package:{Context.PackageName}"));
        intent.AddFlags(ActivityFlags.NewTask);
        Context.StartActivity(intent);
    }

    public event Action? Changed;

    /// <summary>The permission dialog was answered, or the app came back from the settings.</summary>
    public void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Log.Warn("ASync", $"A photo-permission listener failed: {e.Message}"); }
        BackgroundWork.EnsureMediaTrigger();
    }
}

/// <summary>
/// Android's photo library (MediaStore) as an <see cref="IMediaLibrary"/>: every image and video of every external volume the
/// permissions show (Android 14's "Select photos" shows only the chosen ones). An album is a bucket (its directory, such as
/// DCIM/Camera): Android keeps a photo in exactly one, so a photo "in two albums" is two files with the same bytes, stored once
/// by the deduplication. Motion photos are one JPEG with the video inside, so they are one resource. Asset ids are
/// <c>android:&lt;volume&gt;:&lt;_id&gt;</c> (stable while the file exists; MediaStore gives a moved or re-scanned file the same id,
/// a copied one a new id).
/// </summary>
public sealed class AndroidMediaLibrary(Context context) : IMediaLibrary
{
    public const string Source = "android-mediastore";
    private const int MediaTypeImage = 1, MediaTypeVideo = 3;
    private readonly ConcurrentDictionary<(string Id, long Modified, long Size), MediaDetails> _details = new();

    private ContentResolver Resolver => context.ContentResolver!;

    public MediaAccess Access => AndroidPhotoAccess.Instance.Access;

    private static IReadOnlyList<string> Volumes(Context context) =>
        OperatingSystem.IsAndroidVersionAtLeast(29) ? [.. MediaStore.GetExternalVolumeNames(context)] : ["external"];

    private static string ShortId(string volume, long id) =>
        volume is "external_primary" or "external" ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{volume}-{id}";

    private static bool IsScreenshot(string? bucket, string? relativePath) =>
        string.Equals(bucket, "Screenshots", StringComparison.OrdinalIgnoreCase)
        || (relativePath?.Split('/').Any(s => s.Equals("Screenshots", StringComparison.OrdinalIgnoreCase)) ?? false);

    public IReadOnlyList<MediaAsset> Assets()
    {
        if (Access is MediaAccess.None) throw new UnauthorizedAccessException("ASync may not read the photos on this phone.");
        var result = new List<MediaAsset>();
        var q = OperatingSystem.IsAndroidVersionAtLeast(29);
        string[] projection =
        [
            "_id", "media_type", "_display_name", "mime_type", "_size", "date_modified", "datetaken", "bucket_id", "bucket_display_name",
            "width", "height", "duration", .. q ? new[] { "relative_path" } : [], .. OperatingSystem.IsAndroidVersionAtLeast(30) ? new[] { "is_favorite" } : [],
        ];
        foreach (var volume in Volumes(context))
        {
            var uri = MediaStore.Files.GetContentUri(volume)!;
            using var cursor = Resolver.Query(uri, projection, $"media_type IN ({MediaTypeImage},{MediaTypeVideo})" + (q ? " AND is_pending = 0" : ""), null, null);
            if (cursor is null) continue;
            int Col(string name) => cursor.GetColumnIndex(name);
            int cId = Col("_id"), cType = Col("media_type"), cName = Col("_display_name"), cMime = Col("mime_type"), cSize = Col("_size"),
                cModified = Col("date_modified"), cTaken = Col("datetaken"), cBucket = Col("bucket_id"), cBucketName = Col("bucket_display_name"),
                cWidth = Col("width"), cHeight = Col("height"), cDuration = Col("duration"), cRel = Col("relative_path"), cFav = Col("is_favorite");
            while (cursor.MoveToNext())
            {
                var id = cursor.GetLong(cId);
                var video = cursor.GetInt(cType) == MediaTypeVideo;
                var name = cursor.GetString(cName) ?? $"{id}";
                var mime = cursor.GetString(cMime) ?? (video ? "video/mp4" : "image/jpeg");
                var size = cursor.IsNull(cSize) ? 0 : cursor.GetLong(cSize);
                var bucket = cursor.IsNull(cBucket) ? null : cursor.GetLong(cBucket).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var bucketName = cursor.GetString(cBucketName);
                var rel = cRel >= 0 ? cursor.GetString(cRel) : null;
                long? taken = cTaken >= 0 && !cursor.IsNull(cTaken) && cursor.GetLong(cTaken) > 0 ? cursor.GetLong(cTaken) : null;
                result.Add(new MediaAsset(
                    $"android:{volume}:{id}", ShortId(volume, id), video ? MediaKind.Video : MediaKind.Photo, name, mime,
                    cursor.GetLong(cModified) * 1000, taken,
                    [new MediaResource(MediaResource.Original, name, size, mime)],
                    bucket is null ? [] : [bucket],
                    Width: cWidth >= 0 && !cursor.IsNull(cWidth) ? cursor.GetInt(cWidth) : null,
                    Height: cHeight >= 0 && !cursor.IsNull(cHeight) ? cursor.GetInt(cHeight) : null,
                    DurationMs: video && cDuration >= 0 && !cursor.IsNull(cDuration) ? cursor.GetLong(cDuration) : null,
                    Favourite: cFav >= 0 && cursor.GetInt(cFav) == 1,
                    Screenshot: IsScreenshot(bucketName, rel),
                    RelativePath: rel));
                if (bucket is not null && bucketName is not null) _bucketNames[bucket] = bucketName;
                if (bucket is not null && rel is not null) _bucketPaths[bucket] = rel.TrimEnd('/');
            }
        }
        return result;
    }

    private readonly ConcurrentDictionary<string, string> _bucketNames = new();
    private readonly ConcurrentDictionary<string, string> _bucketPaths = new();

    public IReadOnlyList<MediaAlbum> Albums()
    {
        var assets = Assets();
        return [.. assets.Where(a => a.AlbumIds.Count > 0).GroupBy(a => a.AlbumIds[0])
            .Select(g => new MediaAlbum(g.Key, _bucketNames.GetValueOrDefault(g.Key, g.Key),
                g.Count(a => a.Kind == MediaKind.Photo), g.Count(a => a.Kind == MediaKind.Video), _bucketPaths.GetValueOrDefault(g.Key)))];
    }

    private static Uri ContentUri(MediaAsset asset)
    {
        var parts = asset.Id.Split(':');
        var (volume, id) = (parts[1], long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
        var collection = asset.Kind == MediaKind.Video ? MediaStore.Video.Media.GetContentUri(volume)! : MediaStore.Images.Media.GetContentUri(volume)!;
        return ContentUris.WithAppendedId(collection, id);
    }

    public Stream OpenRead(MediaAsset asset, MediaResource resource)
    {
        var uri = ContentUri(asset);
        // Without this Android removes the location from what ASync reads: the backup would not be the original.
        if (OperatingSystem.IsAndroidVersionAtLeast(29) && AndroidPhotoAccess.Instance.HasLocation) uri = MediaStore.SetRequireOriginal(uri)!;
        try
        {
            return Resolver.OpenInputStream(uri) ?? throw new FileNotFoundException($"{asset.FileName} cannot be opened.");
        }
        catch (Java.IO.FileNotFoundException e) { throw new FileNotFoundException($"{asset.FileName}: {e.Message}"); }
        catch (Java.Lang.SecurityException e) { throw new UnauthorizedAccessException($"{asset.FileName}: {e.Message}"); }
    }

    /// <summary>The EXIF location of a photo (kept per asset version for the process: sidecars are written on every run).</summary>
    public MediaDetails Details(MediaAsset asset)
    {
        if (asset.Kind != MediaKind.Photo || !AndroidPhotoAccess.Instance.HasLocation) return new MediaDetails();
        return _details.GetOrAdd((asset.Id, asset.Modified, asset.Size), _ =>
        {
            try
            {
                using var stream = OpenRead(asset, asset.OriginalResource);
                var exif = new ExifInterface(stream);
                var latLong = new float[2];
                return exif.GetLatLong(latLong) ? new MediaDetails(latLong[0], latLong[1]) : new MediaDetails();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Java.Lang.Exception) { return new MediaDetails(); }
        });
    }

    public async Task<string> InsertAsync(MediaInsert insert, Stream content, CancellationToken ct)
    {
        var video = insert.Kind == MediaKind.Video;
        var collection = OperatingSystem.IsAndroidVersionAtLeast(29)
            ? video ? MediaStore.Video.Media.GetContentUri("external_primary")! : MediaStore.Images.Media.GetContentUri("external_primary")!
            : video ? MediaStore.Video.Media.ExternalContentUri! : MediaStore.Images.Media.ExternalContentUri!;
        var values = new ContentValues();
        values.Put("_display_name", insert.FileName);
        values.Put("mime_type", insert.Mime);
        if (insert.Taken is { } taken) values.Put("datetaken", taken);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            values.Put("relative_path", RestorePath(insert));
            values.Put("is_pending", 1);
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && insert.Favourite) values.Put("is_favorite", 1);
        Uri? uri;
        try { uri = Resolver.Insert(collection, values); }
        catch (Java.Lang.IllegalArgumentException) when (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            // A relative path this collection does not take (a video's old place in Pictures/, a removed volume's): the default.
            values.Put("relative_path", video ? "Movies/ASync/" : "Pictures/ASync/");
            uri = Resolver.Insert(collection, values);
        }
        if (uri is null) throw new IOException($"{insert.FileName} could not be added to the photo library.");
        try
        {
            await using (var output = Resolver.OpenOutputStream(uri) ?? throw new IOException($"{insert.FileName} cannot be written."))
                await content.CopyToAsync(output, 1 << 20, ct);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                var done = new ContentValues();
                done.Put("is_pending", 0);
                Resolver.Update(uri, done, null, null);
            }
        }
        catch
        {
            Resolver.Delete(uri, null, null);
            throw;
        }
        return uri.LastPathSegment ?? "";
    }

    /// <summary>Where a restored asset goes: where it was (DCIM/Camera/), else Pictures/&lt;album&gt;/ (Movies/ for videos).</summary>
    private static string RestorePath(MediaInsert insert)
    {
        if (insert.RelativePath is { Length: > 0 } rel && !rel.Contains("..", StringComparison.Ordinal)) return rel.EndsWith('/') ? rel : rel + "/";
        var album = MediaLayout.Safe(insert.AlbumName, 60, "ASync");
        return insert.Kind == MediaKind.Video ? $"Movies/{album}/" : $"Pictures/{album}/";
    }
}

/// <summary>
/// New photos and videos start a backup (task 19): a one-time WorkManager job with MediaStore content-URI triggers on the image and
/// video collections (JobScheduler's TriggerContentUri: it fires on changes made after it was scheduled), a few seconds' update delay
/// so a burst of photos is one run, and the network as its constraint. When it fires it runs every Photos folder as an automatic run
/// (their run conditions apply: on mobile data a Wi-Fi-only folder waits for its periodic job), then schedules the next trigger
/// (appended after itself, since a content trigger fires once).
/// </summary>
[Register("ua.pp.chds.async.MediaTriggerWorker")]
public sealed class MediaTriggerWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    public const string Name = "media-trigger";
    private readonly CancellationTokenSource _stop = new();

    public static OneTimeWorkRequest Build()
    {
        var constraints = new Constraints.Builder()
            .AddContentUriTrigger(MediaStore.Images.Media.ExternalContentUri!, true)
            .AddContentUriTrigger(MediaStore.Video.Media.ExternalContentUri!, true)
            .SetTriggerContentUpdateDelay(5, TimeUnit.Seconds!)
            .SetTriggerContentMaxDelay(60, TimeUnit.Seconds!)
            .SetRequiredNetworkType(NetworkType.Connected!)
            .Build();
        return (OneTimeWorkRequest)new OneTimeWorkRequest.Builder(typeof(MediaTriggerWorker))
            .SetConstraints(constraints).AddTag(BackgroundWork.Tag).AddTag(Name).Build();
    }

    public override Result DoWork()
    {
        var context = ApplicationContext!;
        AndroidAgent.EnsureStarted(context);
        var runtime = AndroidAgent.Runtime;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!runtime.FoldersLoaded && DateTime.UtcNow < until && !_stop.IsCancellationRequested) Thread.Sleep(250);
            var ids = BackgroundPlanner.PhotoFolders(runtime.Folders.Select(f => f.Folder));
            Log.Info("ASync", $"New photos or videos: backing up {ids.Count} Photos folder(s).");
            foreach (var id in ids)
            {
                if (_stop.IsCancellationRequested) break;
                var result = runtime.RunAutomaticAsync(id, TimeSpan.FromSeconds(90), _stop.Token).GetAwaiter().GetResult();
                Log.Info("ASync", $"Background run of \"{result.Name}\" (new photos): {result.Message}");
            }
        }
        catch (Exception e) when (e is Agent.Control.ControlException or System.OperationCanceledException)
        {
            Log.Info("ASync", $"New photos: no run ({e.Message}).");
        }
        finally
        {
            BackgroundWork.EnsureMediaTrigger(next: true);
        }
        return Result.InvokeSuccess()!;
    }

    public override void OnStopped()
    {
        _stop.Cancel();
        base.OnStopped();
    }
}
