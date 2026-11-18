using ASync.Agent;
using ASync.Client.Core;
using CoreFoundation;
using FileProvider;
using Foundation;
using System.Runtime.Versioning;

namespace ASync.App.iOS;

/// <summary>
/// The app's side of the File Provider extension (task 25). At start it decides whether this build has the app group
/// (<see cref="AppGroupSupport"/>): with it, the store lives in the group's container (task 21's store moved there once,
/// <see cref="StoreMigration"/>) and the app keeps one <see cref="NSFileProviderDomain"/> per enrolled account, "ASync —
/// &lt;server host&gt;"; without it (the unsigned .ipa, a sideloader that strips the entitlements) the store stays where it was,
/// no domain is registered and Settings says "The Files app location needs a signed build". Nothing else changes either way.
/// <para>
/// While the agent runs it writes what the extension needs into the group (<see cref="SharedAccount"/>: the account, the
/// folder list, "removed"), raises <see cref="FileProviderSignals"/> and calls <c>signalEnumerator</c> on the working set when a
/// folder changed (another device's <c>folder.changed</c>, this iPhone's own runs, the runs a silent push starts) or the folder
/// list did, so open places in the Files app refresh; and it syncs the local copy of folders the extension wrote to
/// (a Darwin notification while the app runs, the note in the group otherwise).
/// </para>
/// </summary>
public static class FileProviderDomains
{
    private static readonly Lazy<(AppGroupDecision Decision, string Store)> Setup = new(Prepare, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lock Gate = new();
    private static AgentRuntime? _runtime;
    private static string _signature = "";
    private static string? _registered;
    private static NSFileProviderDomain? _domain;
    private static Timer? _signal;
    private static readonly HashSet<int> PendingFolders = [];
    private static bool _pendingAll;

    public static AppGroupDecision Decision => Setup.Value.Decision;

    /// <summary>Where the agent's store is (the group's container when shared, the app's otherwise).</summary>
    public static string StoreDirectory => Setup.Value.Store;

    /// <summary>fileprovider/ in the group (null without the group).</summary>
    public static string? SharedDirectory => Decision.GroupContainer is { } g ? IosAppGroup.FileProviderDirectory(g) : null;

    /// <summary>Whether the domain is offered: the group is there and iOS has replicated providers (16+).</summary>
    public static bool Enabled => Decision.Shared && Replicated;

    /// <summary>iOS 16 or later, not Mac Catalyst: NSFileProviderReplicatedExtension and its domains.</summary>
    [SupportedOSPlatformGuard("ios16.0")]
    [UnsupportedOSPlatformGuard("maccatalyst")]
    internal static bool Replicated => OperatingSystem.IsIOSVersionAtLeast(16) && !OperatingSystem.IsMacCatalyst();

    /// <summary>What happened at start and since (the automation's <c>fp-state</c>, the log).</summary>
    public static string Migration { get; private set; } = "";
    public static string? LastProblem { get; private set; }
    public static string? Registered => _registered;
    public static int Signals { get; private set; }

    /// <summary>
    /// Set while iOS has the location switched off (a new one starts off until the person turns it on in Files): what to do, for
    /// Settings. Null when it is on, or not known yet.
    /// </summary>
    public static string? DisabledNote { get; private set; }

    private static (AppGroupDecision, string) Prepare()
    {
        var home = IosAgent.Home;
        string? group = null;
        var writable = false;
        try
        {
            // Tests only: ASYNC_NO_APP_GROUP=1 (only simctl launch sets an app's environment) runs it as a build without the group.
            group = Environment.GetEnvironmentVariable("ASYNC_NO_APP_GROUP") == "1" ? null : NSFileManager.DefaultManager.GetContainerUrl(IosAppGroup.GroupId)?.Path;
            if (group is not null)
            {
                var probe = Path.Combine(group, "Library", ".async-probe");
                Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                writable = true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastProblem = $"app group: {e.Message}";
        }
        var decision = AppGroupSupport.Decide(home, group, writable, OperatingSystem.IsIOSVersionAtLeast(16));
        if (!decision.Shared)
        {
            Migration = "not shared";
            Console.WriteLine($"ASync: {decision.Why}");
            return (decision, decision.StoreDirectory);
        }
        var (result, problem) = StoreMigration.Run(IosAppGroup.StoreIn(home), decision.StoreDirectory);
        Migration = problem is null ? result.ToString() : $"{result}: {problem}";
        Console.WriteLine($"ASync: store in the app group ({Migration})");
        if (result == StoreMigrationResult.Failed)
        {
            // The old store stays in use: the extension could not read it, so no domain this time.
            var fallback = decision with { Shared = false, Note = IosAppGroup.NeedsSignedBuild, Why = $"The store could not be moved: {problem}" };
            return (fallback, IosAppGroup.StoreIn(home));
        }
        return (decision, decision.StoreDirectory);
    }

    /// <summary>Follows the agent (called once it started). Does nothing without the group.</summary>
    public static void Attach(AgentRuntime runtime)
    {
        _runtime = runtime;
        if (!Enabled || SharedDirectory is not { } shared) return;
        runtime.FoldersChanged += () => FoldersMaybeChanged(runtime, shared);
        runtime.RemoteFolderChanged += id => Changed(id == 0 ? null : id);
        runtime.FolderRan += r => { if (!r.Skipped) Changed(r.FolderId); };
        CFNotificationCenter.Darwin.AddObserver(IosAppGroup.ChangedNotification, null!, (_, _) => SyncWritten(), CFNotificationSuspensionBehavior.DeliverImmediately);
        NSNotificationCenter.DefaultCenter.AddObserver(UIKit.UIApplication.DidBecomeActiveNotification, _ => SyncWritten());
        FoldersMaybeChanged(runtime, shared);
    }

    /// <summary>The account or the folder list changed (the agent says so with every status: compared by a signature).</summary>
    private static void FoldersMaybeChanged(AgentRuntime runtime, string shared)
    {
        // The store says whether the device is enrolled (as on Android): the signed-in client may still hold the identity for
        // a moment after a sign-out (task 25's seventh Mac run: the domain stayed).
        DeviceIdentity? stored;
        try { stored = runtime.Store.LoadIdentity(); }
        catch (Exception e)
        {
            // Not readable now (the iPhone not unlocked since it started): not a sign-out; nothing changes until it is.
            Interlocked.Exchange(ref _signature, "");
            Problem($"store: {e.Message}");
            return;
        }
        var identity = stored is null ? null : runtime.Api?.Identity ?? stored;
        var now = $"{identity?.DeviceId}|{runtime.RevokedMessage is not null}|{runtime.FoldersLoaded}|" +
                  string.Join(",", runtime.Folders.Select(f => $"{f.Folder.FolderId}:{f.Folder.Revision}"));
        if (Interlocked.Exchange(ref _signature, now) == now) return;
        try
        {
            if (identity is null)
            {
                if (runtime.DeviceRunning)
                {
                    // Starting, or stopping after a sign-out: look again at the next event (the signature must not stick),
                    // and in a few seconds in case no event follows.
                    Interlocked.Exchange(ref _signature, "");
                    _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => FoldersMaybeChanged(runtime, shared), TaskScheduler.Default);
                    return;
                }
                SharedAccount.Delete(shared);
                FileProviderSignals.Reset(shared);
                TryDelete(Path.Combine(shared, "known.json"));
                if (Replicated) RemoveDomains(keep: null);
                return;
            }
            if (runtime.FoldersLoaded || runtime.RevokedMessage is not null)
            {
                var previous = SharedAccount.Read(shared);
                var folders = runtime.FoldersLoaded ? runtime.Folders.Select(f => f.Folder).ToList() : previous?.Folders ?? [];
                new SharedAccount(identity.DeviceId, identity.ServerUrl, identity.Username, runtime.RevokedMessage, folders, DateTimeOffset.UtcNow).Write(shared);
            }
            if (Replicated) AddDomain(identity);
            Changed(null);
        }
        catch (Exception e) { Problem($"account: {e.Message}"); }
    }

    // ── Domains ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("ios16.0")]
    [UnsupportedOSPlatform("maccatalyst")]
    private static void AddDomain(DeviceIdentity identity)
    {
        var id = SharedAccount.DomainId(identity.DeviceId);
        if (_registered == id) return;
        var domain = new NSFileProviderDomain(id, SharedAccount.DomainTitle(identity.ServerUrl));
        // Tests only: iOS adds a new location switched off until the person turns it on in Files (Browse → ⋯ → Edit); the smoke
        // test (ASYNC_FP_TESTING=1, which only simctl sets, and the simulator build's testing-mode entitlement) keeps it on.
        if (Environment.GetEnvironmentVariable("ASYNC_FP_TESTING") == "1") domain.TestingModes = NSFileProviderDomainTestingModes.AlwaysEnabled;
        NSFileProviderManager.AddDomain(domain, error =>
        {
            if (error is not null)
            {
                Problem($"addDomain: {error.LocalizedDescription} ({error.Domain} {error.Code})");
                return;
            }
            _registered = id;
            _domain = domain;
            Console.WriteLine($"ASync: File Provider domain {domain.DisplayName} registered");
            RemoveDomains(keep: id); // an earlier enrollment's
            CheckEnabled(id);
        });
    }

    /// <summary>Removes every domain but <paramref name="keep"/> (signing out: all of them; the replica's files go too).</summary>
    [SupportedOSPlatform("ios16.0")]
    [UnsupportedOSPlatform("maccatalyst")]
    private static void RemoveDomains(string? keep)
    {
        NSFileProviderManager.GetDomains((domains, error) =>
        {
            if (error is not null) { Problem($"getDomains: {error.LocalizedDescription}"); return; }
            foreach (var d in domains ?? [])
            {
                if (d.Identifier == keep) continue;
                NSFileProviderManager.RemoveDomain(d, e =>
                {
                    if (e is not null) Problem($"removeDomain: {e.LocalizedDescription}");
                    else Console.WriteLine($"ASync: File Provider domain {d.DisplayName} removed");
                });
            }
            if (keep is null) (_registered, _domain) = (null, null);
        });
    }

    /// <summary>Whether the person has the location on (iOS keeps <c>userEnabled</c> on the domain it hands back).</summary>
    [SupportedOSPlatform("ios16.0")]
    [UnsupportedOSPlatform("maccatalyst")]
    private static void CheckEnabled(string id)
    {
        NSFileProviderManager.GetDomains((domains, error) =>
        {
            if (error is not null || domains?.FirstOrDefault(d => d.Identifier == id) is not { } d) return;
            DisabledNote = d.UserEnabled ? null : IosAppGroup.TurnOnInFiles;
        });
    }

    /// <summary>The domains iOS has for this app (the automation's <c>fp-state</c>).</summary>
    public static Task<string[]> DomainsAsync()
    {
        var done = new TaskCompletionSource<string[]>();
        if (!Replicated) { done.SetResult([]); return done.Task; }
        ListDomains(done);
        return done.Task;
    }

    [SupportedOSPlatform("ios16.0")]
    [UnsupportedOSPlatform("maccatalyst")]
    private static void ListDomains(TaskCompletionSource<string[]> done)
    {
        NSFileProviderManager.GetDomains((domains, error) =>
            done.TrySetResult(error is not null ? [$"error: {error.LocalizedDescription}"] : [.. (domains ?? []).Select(d => $"{d.Identifier} {d.DisplayName} userEnabled={d.UserEnabled}")]));
    }

    // ── Signals ───────────────────────────────────────────────────────────────

    /// <summary>A folder changed (null: the folder list, the account, or a reconnect after which events may be missing).</summary>
    public static void Changed(int? folderId)
    {
        if (!Enabled || SharedDirectory is null) return;
        lock (Gate)
        {
            if (folderId is { } id) PendingFolders.Add(id); else _pendingAll = true;
            // Changes come in bursts (a run reports, the hub echoes): one signal per second at most.
            _signal ??= new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    private static void Flush()
    {
        int[] folders;
        bool all;
        lock (Gate)
        {
            folders = [.. PendingFolders];
            all = _pendingAll;
            PendingFolders.Clear();
            _pendingAll = false;
            _signal?.Dispose();
            _signal = null;
        }
        if (SharedDirectory is not { } shared) return;
        try
        {
            if (all) FileProviderSignals.Bump(shared, null);
            if (folders.Length > 0) FileProviderSignals.Bump(shared, folders);
            SignalWorkingSet();
        }
        catch (Exception e) { Problem($"signals: {e.Message}"); }
    }

    /// <summary>Tells the system to ask the working set for its changes (and so the open Files windows refresh).</summary>
    public static void SignalWorkingSet()
    {
        if (!Replicated || _domain is not { } domain) return;
        var manager = NSFileProviderManager.FromDomain(domain);
        if (manager is null) { Problem("no manager for the domain"); return; }
        manager.SignalEnumerator(NSFileProviderItemIdentifier.WorkingSetContainer, error =>
        {
            if (error is not null) Problem($"signalEnumerator: {error.LocalizedDescription}");
            else Signals++;
        });
    }

    /// <summary>The domain's manager (the automation's reads through the user-visible URL).</summary>
    public static NSFileProviderManager? Manager()
    {
        if (!Replicated || _domain is not { } domain) return null;
        return NSFileProviderManager.FromDomain(domain);
    }

    /// <summary>The extension wrote to folders this iPhone syncs: their automatic runs bring the local copies up to date.</summary>
    private static void SyncWritten()
    {
        if (SharedDirectory is not { } shared || _runtime is not { } runtime) return;
        foreach (var id in FileProviderWritten.Take(shared)) runtime.Trigger(id, "changed through the Files app");
    }

    private static void Problem(string message)
    {
        LastProblem = message;
        Console.WriteLine($"ASync: File Provider: {message}");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* nothing to forget */ }
    }
}
