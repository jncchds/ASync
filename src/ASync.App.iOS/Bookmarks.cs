using Foundation;

namespace ASync.App.iOS;

/// <summary>
/// Security-scoped bookmarks through NSURL (<see cref="IBookmarkResolver"/>): the folder a person picked stays reachable
/// after the app restarts. On iOS a bookmark of a picked URL carries its scope implicitly (there is no
/// <c>withSecurityScope</c> option as on macOS); it must be made while the URL is accessed.
/// </summary>
public sealed class NSUrlBookmarks : IBookmarkResolver
{
    public static readonly NSUrlBookmarks Instance = new();

    public byte[] Create(string path)
    {
        using var url = NSUrl.CreateFileUrl(path, isDir: true, relativeToUrl: null);
        return Create(url);
    }

    /// <summary>A bookmark of a URL the document picker returned (accessed for as long as it takes).</summary>
    public static byte[] Create(NSUrl url)
    {
        var accessing = url.StartAccessingSecurityScopedResource();
        try
        {
            using var data = url.CreateBookmarkData(NSUrlBookmarkCreationOptions.MinimalBookmark, null, null, out var error)
                             ?? throw new BookmarkException(error?.LocalizedDescription ?? "the system made no bookmark");
            return data.ToArray();
        }
        finally
        {
            if (accessing) url.StopAccessingSecurityScopedResource();
        }
    }

    public IBookmarkAccess Open(byte[] bookmark)
    {
        using var data = NSData.FromArray(bookmark);
        var url = NSUrl.FromBookmarkData(data, NSUrlBookmarkResolutionOptions.WithoutUI, null, out var stale, out var error);
        if (url?.Path is not { } path)
            throw new BookmarkException(error?.LocalizedDescription ?? "the bookmark cannot be resolved");
        return new Access(url, path, stale, url.StartAccessingSecurityScopedResource());
    }

    private sealed class Access(NSUrl url, string path, bool stale, bool accessing) : IBookmarkAccess
    {
        private int _disposed;
        public string Path => path;
        public bool Stale => stale;

        public byte[]? Recreate()
        {
            try
            {
                using var data = url.CreateBookmarkData(NSUrlBookmarkCreationOptions.MinimalBookmark, null, null, out _);
                return data?.ToArray();
            }
            catch (Exception) { return null; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (accessing) url.StopAccessingSecurityScopedResource();
            url.Dispose();
        }
    }
}
