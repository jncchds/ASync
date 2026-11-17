using ASync.Client.Core.Browse;
using ASync.Contracts;

namespace ASync.App.iOS;

/// <summary>
/// What the web UI may browse on an iPhone (task 17): the app's own Documents ("On My iPhone › ASync"), the only place an
/// app reaches without the person picking a folder. Nothing above it is listed (the rest of the container is the app's
/// private data, with the device's key), and folders chosen with the document picker are not browsable from the web:
/// like Android's document trees, they need the system's picker on the phone. Plain C#, linked into ASync.App.Tests.
/// </summary>
public sealed class IosBrowseScope(string home, string device = "iPhone") : IBrowseScope
{
    public string DocumentsPath => Path.Combine(home, "Documents");

    public bool Windows => false;

    public (DeviceBrowseRefusal Refusal, string Message)? Unavailable => null;

    public IReadOnlyList<BrowsePlace> Places()
    {
        try { Directory.CreateDirectory(DocumentsPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return [new BrowsePlace($"On My {device} › ASync", DocumentsPath, BrowsePlaceKind.Documents)];
    }

    private bool Inside(string path) => IosLocations.InDocuments(path, home);

    public string? Refuse(string path) =>
        Inside(path) ? null : $"Only On My {device} › ASync can be browsed from the web; choose other folders in the app on the {device}.";

    public string? Parent(string path) =>
        BrowsePaths.Normalize(path, false) is { } p && p != BrowsePaths.Normalize(DocumentsPath, false) && Inside(p) ? BrowsePaths.Parent(p, false) : null;

    public List<BrowseCrumb> Crumbs(string path)
    {
        var root = BrowsePaths.Normalize(DocumentsPath, false)!;
        var crumbs = new List<BrowseCrumb> { new($"On My {device} › ASync", root) };
        var p = BrowsePaths.Normalize(path, false);
        if (p is null || p == root || !p.StartsWith(root + "/", StringComparison.Ordinal)) return crumbs;
        var current = root;
        foreach (var name in p[(root.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + name;
            crumbs.Add(new BrowseCrumb(name, current));
        }
        return crumbs;
    }
}
