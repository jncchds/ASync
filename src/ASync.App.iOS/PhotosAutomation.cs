using System.Globalization;
using System.Text.Json;
using ASync.App.ViewModels;
using ASync.Contracts;
using Foundation;
using Photos;

namespace ASync.App.iOS;

/// <summary>
/// Tests only: the photo commands of <see cref="Automation"/> (task 23, <c>scripts/smoke-ios.sh</c>). The Photos screen:
/// <c>photos-dialog &lt;folder&gt;</c>, <c>photos-state</c>, <c>photos-allow</c> (the system's question), <c>photos-more</c> (the limited
/// library picker), <c>photos-albums all|&lt;name&gt;,…</c>, <c>photos-cloud on|off</c>, <c>photos-save</c>, <c>photos-force
/// none|partial|off</c> (what the screen and card show, for a screenshot of a state simctl cannot grant; the agent keeps reading the
/// real permission), <c>photos-restore-choose|photos-restore-cancel &lt;folder&gt;</c>. The agent: <c>photos</c> (access, albums, the
/// restore's progress, the Photos folders, the change observer's runs), <c>photos-set &lt;folder&gt; all|&lt;album id&gt;,…</c>,
/// <c>photos-restore &lt;folder&gt; [snapshot]</c>, <c>photos-assets</c> (what the library lists). The library, as a person changes it
/// in the Photos app: <c>photos-album-make &lt;title&gt; &lt;file&gt;,…</c>, <c>photos-album-remove &lt;title&gt; &lt;file&gt;</c>,
/// <c>photos-favourite &lt;file&gt;</c> (album and favourite changes need no confirmation; deleting a photo does, which simctl cannot
/// answer).
/// </summary>
internal static class PhotosAutomation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static PhotosViewModel Dialog(ShellViewModel shell) => shell.PhotosDialog ?? throw new InvalidOperationException("The Photos screen is not open.");

    private static FolderViewModel Folder(ShellViewModel shell, string id) =>
        shell.Dashboard.Folders.FirstOrDefault(f => f.Id.ToString(CultureInfo.InvariantCulture) == id)
        ?? throw new InvalidOperationException($"No folder {id} on the dashboard.");

    /// <summary>The answer to a photo command, or null when <paramref name="op"/> is not one.</summary>
    public static async Task<string?> RunAsync(string op, string arg, ShellViewModel shell)
    {
        switch (op)
        {
            case "photos-dialog":
                await Folder(shell, arg).ChoosePhotosCommand.ExecuteAsync(null);
                return State(shell);
            case "photos-state":
                return State(shell);
            case "photos-allow":
                Dialog(shell).AllowCommand.Execute(null);
                return "asked";
            case "photos-more":
                Dialog(shell).ChooseMoreCommand.Execute(null);
                return "shown";
            case "photos-albums":
            {
                var photos = Dialog(shell);
                if (arg == "all") photos.BackUpAll = true;
                else
                {
                    photos.ChooseAlbums = true;
                    var names = arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    foreach (var album in photos.Albums) album.IsChecked = names.Contains(album.Name);
                }
                return State(shell);
            }
            case "photos-cloud":
                IosPhotos.Access.CloudDownloads = arg != "off";
                return IosPhotos.Access.CloudDownloads ? "on" : "off";
            case "photos-save":
                await Dialog(shell).SaveCommand.ExecuteAsync(null);
                return shell.PhotosDialog is null ? "saved" : State(shell);
            case "photos-force":
                IosPhotoAccess.Forced = arg switch { "none" => MediaAccess.None, "partial" => MediaAccess.Partial, "full" => MediaAccess.Full, _ => null };
                IosPhotos.Access.NotifyChanged();
                return IosPhotos.Access.Access.ToString();
            case "photos-restore-choose":
            {
                var card = Folder(shell, arg);
                await card.RestorePhotosCommand.ExecuteAsync(null);
                return JsonSerializer.Serialize(new { card.ChoosingRestore, choices = card.RestoreChoices.Select(c => c.Label) }, Json);
            }
            case "photos-restore-cancel":
                Folder(shell, arg).CancelRestoreCommand.Execute(null);
                return "ok";
            case "photos":
                return await AgentState();
            case "photos-set":
            {
                var a = arg.Split(' ', 2);
                var folderId = int.Parse(a[0], CultureInfo.InvariantCulture);
                var runtime = IosAgent.Runtime;
                if (a[1] == "all") return await runtime.SetMediaAsync(folderId, MediaSelectionDto.Everything, CancellationToken.None);
                var library = await runtime.MediaLibraryAsync(CancellationToken.None);
                var ids = a[1].Split(',');
                return await runtime.SetMediaAsync(folderId, new MediaSelectionDto(false,
                    [.. library.Albums.Where(x => ids.Contains(x.Id)).Select(x => new MediaAlbumRef(x.Id, x.Name))]), CancellationToken.None);
            }
            case "photos-restore":
            {
                var a = arg.Split(' ', 2);
                return await IosAgent.Runtime.RestoreMediaAsync(int.Parse(a[0], CultureInfo.InvariantCulture), a.Length > 1 ? a[1] : null, CancellationToken.None);
            }
            case "photos-assets":
                return await Task.Run(() => JsonSerializer.Serialize(IosPhotos.Library.Assets().Select(x => new
                {
                    x.Id, x.FileName, kind = x.Kind.ToString(), x.Favourite, x.Screenshot, x.Taken, albums = x.AlbumIds,
                    resources = x.Resources.Select(r => new { r.Role, r.FileName, r.Size, r.Mime }),
                    location = IosPhotos.Library.Details(x),
                }), Json));
            case "photos-album-make":
            {
                var a = arg.Split(' ', 2);
                return await ChangeAsync(() =>
                {
                    var request = PHAssetCollectionChangeRequest.CreateAssetCollection(a[0]);
                    request.AddAssets(Assets(a.Length > 1 ? a[1].Split(',') : []));
                });
            }
            case "photos-album-remove":
            {
                var a = arg.Split(' ', 2);
                var album = Album(a[0]) ?? throw new InvalidOperationException($"No album \"{a[0]}\".");
                return await ChangeAsync(() => PHAssetCollectionChangeRequest.ChangeRequest(album)?.RemoveAssets(Assets([a[1]])));
            }
            case "photos-favourite":
                return await ChangeAsync(() =>
                {
                    foreach (var asset in Assets([arg])) PHAssetChangeRequest.ChangeRequest(asset).Favorite = true;
                });
            default:
                return null;
        }
    }

    private static string State(ShellViewModel shell) => shell.PhotosDialog is not { } d ? "closed" : JsonSerializer.Serialize(new
    {
        access = d.Access.ToString(), d.NeedsPermission, d.IsPartial, d.CanChooseMore, d.HasLocation, d.NoLocation, d.BackUpAll, d.IncludeVideos,
        d.IncludeScreenshots, d.OffersCloudDownloads, d.CloudDownloads, d.LibraryText, d.Error, d.Explanation, d.PartialText,
        albums = d.Albums.Select(a => new { a.Id, a.Name, a.Counts, a.IsChecked }),
    }, Json);

    private static async Task<string> AgentState()
    {
        var runtime = IosAgent.Runtime;
        var library = await runtime.MediaLibraryAsync(CancellationToken.None);
        var status = await runtime.StatusAsync(CancellationToken.None);
        return JsonSerializer.Serialize(new
        {
            access = library.Access.ToString(), library.Photos, library.Videos, library.Problem, cloud = IosPhotos.Settings.CloudDownloads,
            authorization = PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite).ToString(), triggered = IosPhotos.Triggered,
            albums = library.Albums.Select(a => new { a.Id, a.Name, a.Photos, a.Videos }),
            restore = status.MediaRestore,
            folders = status.Folders.Where(f => f.Kind == FolderKind.Photos)
                .Select(f => new { id = f.FolderId, f.Name, media = f.Media?.Describe(), counts = f.MediaCounts, last = f.LastResult?.Message, ok = f.LastResult?.Ok }),
        }, Json);
    }

    /// <summary>The library's assets with these original file names.</summary>
    private static PHAsset[] Assets(IEnumerable<string> fileNames)
    {
        var wanted = fileNames.Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<PHAsset>();
        using var all = PHAsset.FetchAssets((PHFetchOptions?)null);
        for (nint i = 0; i < all.Count; i++)
        {
            if (all[i] is not PHAsset a) continue;
            if (PHAssetResource.GetAssetResources(a).Any(r => wanted.Contains(r.OriginalFilename ?? ""))) found.Add(a);
        }
        return [.. found];
    }

    private static PHAssetCollection? Album(string title)
    {
        using var all = PHAssetCollection.FetchAssetCollections(PHAssetCollectionType.Album, PHAssetCollectionSubtype.Any, null);
        for (nint i = 0; i < all.Count; i++)
            if (all[i] is PHAssetCollection c && c.LocalizedTitle == title) return c;
        return null;
    }

    private static Task<string> ChangeAsync(Action changes)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(changes, (ok, error) => done.TrySetResult(ok ? "ok" : $"error: {error?.LocalizedDescription}"));
        return done.Task;
    }
}
