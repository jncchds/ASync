using System.Text.Json;
using ASync.Client.Core;
using ASync.Contracts;

namespace ASync.App.iOS;

// Plain C# (BCL and the client library): built into the iOS head and the File Provider extension, and linked into
// ASync.App.Tests. What the app and its File Provider extension (task 25) share: the app group's container with the store,
// the decision whether the group is there at all (an unsigned or re-signed build may lack it), the move of task 21's store
// into it, and the two small files the app writes for the extension (the folder list, the change signals).

/// <summary>The identifiers the app and the extension agree on.</summary>
public static class IosAppGroup
{
    /// <summary>The app group (<c>com.apple.security.application-groups</c>) of the app and the extension.</summary>
    public const string GroupId = "group.ua.pp.chds.async";

    public const string AppBundleId = "ua.pp.chds.async";
    public const string ExtensionBundleId = "ua.pp.chds.async.fileprovider";

    /// <summary>
    /// The Keychain group of the device key and the store's key (<c>$(AppIdentifierPrefix)ua.pp.chds.async</c>): the app's
    /// own application identifier, so items the app made before the extension existed (task 21) are in it already. The
    /// extension reaches it through its <c>keychain-access-groups</c> entitlement.
    /// </summary>
    public const string KeychainGroupSuffix = "ua.pp.chds.async";

    /// <summary>The Darwin notification the extension posts after a write (the app, when running, syncs the folder's local copy).</summary>
    public const string ChangedNotification = "ua.pp.chds.async.fileprovider.changed";

    /// <summary>The line in Settings when the build cannot have the Files app location.</summary>
    public const string NeedsSignedBuild = "The Files app location needs a signed build";

    /// <summary>The line in Settings while iOS has the location switched off (new locations start off).</summary>
    public const string TurnOnInFiles = "Turn on ASync in the Files app: Browse → ⋯ → Edit, then switch on ASync";

    /// <summary>The store inside an app's (or the group's) container: Library/Application Support/async.</summary>
    public static string StoreIn(string container) => Path.Combine(container, "Library", "Application Support", "async");

    /// <summary>The extension's own files in the group's container (its change log, its temporary files).</summary>
    public static string FileProviderDirectory(string groupContainer) => Path.Combine(groupContainer, "Library", "Application Support", "fileprovider");
}

/// <summary>Whether this build has what the File Provider needs, and where the store lives.</summary>
/// <param name="Shared">The app group is there: the store lives in it, the domain is registered.</param>
/// <param name="Note">The Settings line when it is not (null when it is).</param>
public sealed record AppGroupDecision(bool Shared, string StoreDirectory, string? GroupContainer, string? Note, string Why);

public static class AppGroupSupport
{
    /// <summary>
    /// Decides from what the system answered at start: <paramref name="groupContainer"/> is
    /// <c>containerURLForSecurityApplicationGroupIdentifier</c> (nil without the entitlement: the unsigned .ipa, a sideloader
    /// that strips or renames the group), <paramref name="groupWritable"/> a write probe in it, <paramref name="replicatedProviders"/>
    /// iOS 16 or later (<c>NSFileProviderReplicatedExtension</c>). Without the group the store stays in the app's container
    /// (task 21's place) and no domain is registered; the app runs as before.
    /// </summary>
    public static AppGroupDecision Decide(string home, string? groupContainer, bool groupWritable, bool replicatedProviders)
    {
        var own = IosAppGroup.StoreIn(home);
        if (string.IsNullOrEmpty(groupContainer))
            return new AppGroupDecision(false, own, null, IosAppGroup.NeedsSignedBuild,
                "This build has no app group (unsigned, or re-signed without it): the store stays in the app, the Files app shows no ASync location.");
        if (!groupWritable)
            return new AppGroupDecision(false, own, null, IosAppGroup.NeedsSignedBuild,
                "The app group's container cannot be written: the store stays in the app, the Files app shows no ASync location.");
        if (!replicatedProviders)
            // The group is there; the store moves anyway (a later iOS update needs nothing more), but iOS 15 has no replicated
            // File Provider extensions.
            return new AppGroupDecision(true, IosAppGroup.StoreIn(groupContainer), groupContainer, "The Files app location needs iOS 16 or later",
                "iOS 15: no replicated File Provider; the store is in the app group already.");
        return new AppGroupDecision(true, IosAppGroup.StoreIn(groupContainer), groupContainer, null, "App group present: the store is shared with the File Provider.");
    }
}

/// <summary>How the store's move into the app group went.</summary>
public enum StoreMigrationResult
{
    /// <summary>No store in the app's container: a new install, or moved long ago.</summary>
    NothingToMove,
    /// <summary>Moved now.</summary>
    Moved,
    /// <summary>Both had a store: the group's (newer) one is used, the app's is left as it was (logged).</summary>
    KeptShared,
    /// <summary>Could not be moved (the reason is returned): the app's store stays in use this time.</summary>
    Failed,
}

/// <summary>
/// Moves task 21's store (Library/Application Support/async of the app's container) into the app group's container when
/// the group is there. Both containers are on the same volume, so each entry is a rename; across volumes (never seen, but
/// cheap to handle) it is copied to a temporary name and renamed. Entry by entry, so a move cut short (the app killed)
/// continues at the next start: an entry already in the group is not moved again. The old directory goes once it is empty.
/// The Keychain items do not move: they are in the app's own Keychain group, which the extension shares by entitlement.
/// </summary>
public static class StoreMigration
{
    private const string Marker = ".moved-to-app-group";

    public static (StoreMigrationResult Result, string? Problem) Run(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from)) return (StoreMigrationResult.NothingToMove, null);
            var entries = Directory.EnumerateFileSystemEntries(from).Where(e => Path.GetFileName(e) != Marker).ToList();
            if (entries.Count == 0)
            {
                TryDeleteDirectory(from);
                return (StoreMigrationResult.NothingToMove, null);
            }
            Directory.CreateDirectory(to);
            var targetHasIdentity = File.Exists(Path.Combine(to, "identity.json"));
            var sourceHasIdentity = File.Exists(Path.Combine(from, "identity.json"));
            var resumed = File.Exists(Path.Combine(from, Marker));
            if (targetHasIdentity && sourceHasIdentity && !resumed)
                return (StoreMigrationResult.KeptShared, "Both the app and the app group have a store; the app group's is used.");
            File.WriteAllText(Path.Combine(from, Marker), DateTimeOffset.UtcNow.ToString("O"));
            foreach (var entry in entries)
            {
                var target = Path.Combine(to, Path.GetFileName(entry));
                if (File.Exists(target) || Directory.Exists(target))
                {
                    // Moved in a start that was cut short, or written by the group's side since: the group's copy wins.
                    if (File.Exists(entry)) File.Delete(entry); else Directory.Delete(entry, recursive: true);
                    continue;
                }
                MoveEntry(entry, target);
            }
            File.Delete(Path.Combine(from, Marker));
            TryDeleteDirectory(from);
            return (StoreMigrationResult.Moved, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (StoreMigrationResult.Failed, e.Message);
        }
    }

    private static void MoveEntry(string entry, string target)
    {
        var directory = Directory.Exists(entry);
        try
        {
            if (directory) Directory.Move(entry, target); else File.Move(entry, target);
            return;
        }
        catch (IOException) when (!File.Exists(target) && !Directory.Exists(target))
        {
            // Another volume: copy under a temporary name, then rename (a cut-short copy never looks moved).
        }
        var temp = target + ".moving";
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        if (File.Exists(temp)) File.Delete(temp);
        if (directory)
        {
            CopyDirectory(entry, temp);
            Directory.Move(temp, target);
            Directory.Delete(entry, recursive: true);
        }
        else
        {
            File.Copy(entry, temp);
            File.Move(temp, target);
            File.Delete(entry);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// What the app tells the extension about the account (fileprovider/account.json in the group): who is enrolled, whether
/// the device was removed, and the folder list as the agent last had it. The extension answers from it at once (no wait for
/// the server) and refreshes the folders from the server itself when it is older than a minute. Written atomically.
/// </summary>
public sealed record SharedAccount(Guid DeviceId, string ServerUrl, string Username, string? Revoked, List<DeviceFolderDto> Folders, DateTimeOffset At)
{
    public const string FileName = "account.json";

    public string Host => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) ? uri.Host : ServerUrl;

    /// <summary>The domain's name in the Files app: "ASync — &lt;server host&gt;".</summary>
    public string Title => DomainTitle(ServerUrl);

    public static string DomainTitle(string serverUrl) =>
        $"ASync — {(Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ? uri.Host : serverUrl)}";

    /// <summary>The domain's identifier: one per enrolled device (the account the app has).</summary>
    public static string DomainId(Guid deviceId) => deviceId.ToString("N");

    public static SharedAccount? Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<SharedAccount>(File.ReadAllBytes(path), AsyncApiClient.Json) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Write(string directory) => SharedFiles.WriteAtomically(directory, FileName, JsonSerializer.SerializeToUtf8Bytes(this, AsyncApiClient.Json));

    public static void Delete(string directory)
    {
        try { File.Delete(Path.Combine(directory, FileName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The change signals (fileprovider/signals.json): a counter the app raises whenever a folder changed (another device's
/// <c>folder.changed</c>, its own runs, a silent push's run) or the folder list or the account did, with the counter's value
/// per folder. The extension's sync anchors are this counter; on the working set's change enumeration it lists again the
/// directories it has handed out of the folders whose value is newer than the anchor (see <c>FileProviderChangeLog</c>).
/// The epoch changes when the file is made anew (a sign-out, a lost file): every anchor of the old one is expired.
/// </summary>
public sealed class FileProviderSignals
{
    public const string FileName = "signals.json";

    /// <summary>"0" until the first sign-out makes a random one (a missing file reads as "0" too, so its anchors expire).</summary>
    public string Epoch { get; set; } = "0";
    public long Generation { get; set; }
    /// <summary>The generation at which the folder list or the account last changed (every directory, and the root).</summary>
    public long All { get; set; }
    public Dictionary<int, long> Folders { get; set; } = [];

    /// <summary>The folders changed after <paramref name="generation"/> (null: everything did: the list or the account).</summary>
    public IReadOnlySet<int>? ChangedSince(long generation) =>
        All > generation ? null : Folders.Where(f => f.Value > generation).Select(f => f.Key).ToHashSet();

    private static readonly Lock Gate = new();

    public static FileProviderSignals Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<FileProviderSignals>(File.ReadAllBytes(path), AsyncApiClient.Json) is { } signals)
                return signals;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return new FileProviderSignals();
    }

    /// <summary>Raises the counter for these folders (none: for the folder list and the account) and writes the file.</summary>
    public static FileProviderSignals Bump(string directory, IEnumerable<int>? folders)
    {
        lock (Gate)
        {
            var signals = Read(directory);
            signals.Generation++;
            if (folders is null) signals.All = signals.Generation;
            else foreach (var id in folders) signals.Folders[id] = signals.Generation;
            SharedFiles.WriteAtomically(directory, FileName, JsonSerializer.SerializeToUtf8Bytes(signals, AsyncApiClient.Json));
            return signals;
        }
    }

    /// <summary>A new epoch (signing out): every anchor the system holds expires.</summary>
    public static void Reset(string directory)
    {
        lock (Gate)
            SharedFiles.WriteAtomically(directory, FileName,
                JsonSerializer.SerializeToUtf8Bytes(new FileProviderSignals { Epoch = Guid.NewGuid().ToString("N")[..12] }, AsyncApiClient.Json));
    }
}

/// <summary>
/// The folders the extension changed (fileprovider/written.json): the app syncs their local copies when it hears the
/// Darwin notification or the next time it runs, as Android's provider triggers the folder's run.
/// </summary>
public static class FileProviderWritten
{
    public const string FileName = "written.json";
    private static readonly Lock Gate = new();

    public static void Add(string directory, int folderId)
    {
        lock (Gate)
        {
            var set = Read(directory);
            if (!set.Add(folderId)) return;
            SharedFiles.WriteAtomically(directory, FileName, JsonSerializer.SerializeToUtf8Bytes(set.Order().ToList(), AsyncApiClient.Json));
        }
    }

    public static HashSet<int> Take(string directory)
    {
        lock (Gate)
        {
            var set = Read(directory);
            if (set.Count > 0)
                try { File.Delete(Path.Combine(directory, FileName)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return set;
        }
    }

    private static HashSet<int> Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            return File.Exists(path) ? [.. JsonSerializer.Deserialize<List<int>>(File.ReadAllBytes(path), AsyncApiClient.Json) ?? []] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }
}

internal static class SharedFiles
{
    /// <summary>Write to a unique temporary name, then rename over the file: the other process never reads half a file.</summary>
    public static void WriteAtomically(string directory, string name, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temp = Path.Combine(directory, $".{name}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
