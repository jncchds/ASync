using ASync.App.iOS;
using ASync.Client.Core;
using ASync.Client.Core.Documents;
using CoreFoundation;
using Foundation;

namespace ASync.App.iOS.FileProvider;

/// <summary>
/// The extension process's one catalog, made on first use: the shared store in the app group (identity, token, unlocked
/// keys of end-to-end folders) opened with the Keychain-held sealing key, the device's own API client signing with the
/// Secure Enclave key (both through the app's Keychain group), the host over the app's account file, and the change log.
/// <para>
/// <b>Which process signs:</b> the extension, with the shared key. iOS starts the extension without the app (the app is
/// usually suspended or not running), and an extension cannot wake its app to ask; an XPC service of the app's own would
/// need the app running too. The key never leaves the Secure Enclave either way, the server sees the same device, and the
/// access token is shared through token.json, so app and extension rarely sign in twice.
/// </para>
/// </summary>
internal static class ExtensionRuntime
{
    private static readonly Lock Gate = new();
    private static DocumentCatalog? _catalog;
    private static FileProviderOperations? _operations;
    private static FileProviderHost? _host;
    private static FileProviderChangeLog? _log;
    private static FileClientStore? _store;
    private static (DateTime At, DeviceIdentity? Identity) _identity;
    private static AsyncApiClient? _api;

    public static string? GroupContainer { get; private set; }

    /// <summary>fileprovider/ in the group: the account file, the signals, the change log, the log.</summary>
    public static string Directory { get; private set; } = "";

    public static string? KeychainProblem { get; private set; }

    public static FileProviderOperations Operations { get { Start(); return _operations!; } }
    public static DocumentCatalog Catalog { get { Start(); return _catalog!; } }
    public static FileProviderHost Host { get { Start(); return _host!; } }
    public static FileProviderChangeLog Log { get { Start(); return _log!; } }

    public static FileProviderSignals Signals => FileProviderSignals.Read(Directory);

    public static void Start()
    {
        if (_catalog is not null) return;
        lock (Gate)
        {
            if (_catalog is not null) return;
            GroupContainer = NSFileManager.DefaultManager.GetContainerUrl(IosAppGroup.GroupId)?.Path;
            var container = GroupContainer ?? Path.Combine(NSFileManager.HomeDirectory ?? Path.GetTempPath(), "Library");
            Directory = IosAppGroup.FileProviderDirectory(container);
            System.IO.Directory.CreateDirectory(Directory);
            _store = new FileClientStore(IosAppGroup.StoreIn(container), new SealedStoreProtector(new KeychainSealingKey()));
            _host = new FileProviderHost(Identity, () => SharedAccount.Read(Directory), Server, Written);
            var caches = Path.Combine(container, "Library", "Caches", "fileprovider");
            // The system keeps the fetched files itself (the replicated extension hands each over): the catalog's cache only
            // holds what the catalog itself fetches through it, so it stays small.
            _catalog = new DocumentCatalog(_host, new DocumentCache(Path.Combine(caches, "files"), 32L * 1024 * 1024), Path.Combine(caches, "tmp"));
            _operations = new FileProviderOperations(_catalog);
            _log = new FileProviderChangeLog(Directory);
            Write($"started (group {(GroupContainer is null ? "missing" : "present")}, {Environment.Version})");
            Footprint("start");
        }
    }

    /// <summary>The identity in the shared store, read at most every five seconds (it is sealed: a Keychain read each time).</summary>
    private static DeviceIdentity? Identity()
    {
        var now = DateTime.UtcNow;
        if (now - _identity.At < TimeSpan.FromSeconds(5)) return _identity.Identity;
        DeviceIdentity? identity = null;
        try
        {
            identity = _store!.LoadIdentity();
            KeychainProblem = null;
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // The store's key not reachable: the build lacks the Keychain group, or the phone was not unlocked since it started.
            KeychainProblem = e.Message;
            Write($"identity: {e.Message}");
        }
        _identity = (now, identity);
        return identity;
    }

    private static IFileProviderServer? Server()
    {
        if (Identity() is not { } identity) return null;
        lock (Gate)
        {
            if (_api?.Identity?.DeviceId != identity.DeviceId || _api.Server.ToString().TrimEnd('/') != identity.ServerUrl.TrimEnd('/'))
            {
                _api?.Dispose();
                _api = new AsyncApiClient(identity.ServerUrl, identity)
                {
                    DeviceKeys = new SecureEnclaveDeviceKeys(),
                    TokenCache = _store,
                    AppVersion = NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString(),
                };
            }
            return new ApiFileProviderServer(_api, _store!);
        }
    }

    /// <summary>A write changed a folder this iPhone also syncs: tell the app (a note in the group and a Darwin notification).</summary>
    private static void Written(int folderId)
    {
        try
        {
            FileProviderWritten.Add(Directory, folderId);
            CFNotificationCenter.Darwin.PostNotification(IosAppGroup.ChangedNotification, null!, null, true, true);
        }
        catch (Exception e) { Write($"written: {e.Message}"); }
    }

    /// <summary>
    /// The managed heap at a moment, into fileprovider/footprint.txt (Environment.WorkingSet answers 0 on iOS: the process's real
    /// footprint is measured from the Mac by the smoke test, with `footprint`).
    /// </summary>
    public static void Footprint(string moment)
    {
        try
        {
            File.WriteAllText(Path.Combine(Directory, "footprint.txt"),
                $"{moment} managed={GC.GetTotalMemory(false)} gc-committed={GC.GetGCMemoryInfo().TotalCommittedBytes}\n");
        }
        catch (Exception) { /* only a measurement */ }
    }

    private static readonly Lock LogGate = new();

    /// <summary>A line in fileprovider/fileprovider.log (kept under 1 MB) and on stdout (the device log).</summary>
    public static void Write(string line)
    {
        var text = $"{DateTimeOffset.UtcNow:O} {line}";
        Console.WriteLine($"ASync FileProvider: {line}");
        try
        {
            lock (LogGate)
            {
                var path = Path.Combine(Directory, "fileprovider.log");
                if (File.Exists(path) && new System.IO.FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, text + "\n");
            }
        }
        catch (Exception) { /* the log is best effort */ }
    }
}
