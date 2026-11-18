using ASync.Engine.Local;
using Foundation;

namespace ASync.App.iOS;

/// <summary>
/// <see cref="IFileCoordination"/> on iOS: a file's state from its URL's resource values (<c>NSURLIsUbiquitousItemKey</c>,
/// <c>NSURLUbiquitousItemDownloadingStatusKey</c>: iCloud Drive and other apps' File Providers answer both), and coordinated
/// reads through <see cref="NSFileCoordinator"/>, which ask the provider for the file's newest bytes and wait for them. The
/// file is opened inside the coordination block: the descriptor stays valid after it, so the sync reads it at its own pace.
/// The decisions (when to coordinate, iCloud's placeholders) are plain C# in Shared/IosCoordination.cs.
/// </summary>
public sealed class NSFileCoordination : IFileCoordination
{
    public static readonly NSFileCoordination Instance = new();

    public CloudFileState State(string path)
    {
        try
        {
            using var url = NSUrl.CreateFileUrl(path, false, null);
            var ubiquitous = url.TryGetResource(NSUrl.IsUbiquitousItemKey, out var u) && u is NSNumber { BoolValue: true };
            if (!ubiquitous) return CloudFileState.Local;
            var status = url.TryGetResource(NSUrl.UbiquitousItemDownloadingStatusKey, out var s) ? s?.ToString() : null;
            return new CloudFileState(true, status);
        }
        catch (Exception) { return CloudFileState.Local; }
    }

    public Stream OpenCoordinated(string path)
    {
        using var url = NSUrl.CreateFileUrl(path, false, null);
        using var coordinator = new NSFileCoordinator();
        Stream? stream = null;
        Exception? failure = null;
        coordinator.CoordinateRead(url, NSFileCoordinatorReadingOptions.WithoutChanges, out var error, actual =>
        {
            try { stream = new FileStream(actual.Path ?? path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: false); }
            catch (Exception e) { failure = e; }
        });
        if (stream is not null) return stream;
        if (error is not null) throw new IOException($"The provider could not give {Path.GetFileName(path)}: {error.LocalizedDescription}");
        throw failure as IOException ?? new IOException($"{Path.GetFileName(path)} could not be read: {failure?.Message}", failure);
    }

    public LocalEntry? Describe(string path)
    {
        try
        {
            using var url = NSUrl.CreateFileUrl(path, false, null);
            if (!url.TryGetResource(NSUrl.FileSizeKey, out var size) || size is not NSNumber n) return null;
            var modified = url.TryGetResource(NSUrl.ContentModificationDateKey, out var d) && d is NSDate date
                ? (long)(date.SecondsSince1970 * 1000)
                : 0;
            return new LocalEntry(Path.GetFileName(path), LocalEntryKind.File, n.Int64Value, modified);
        }
        catch (Exception) { return null; }
    }
}
