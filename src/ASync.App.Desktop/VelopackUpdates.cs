using ASync.App.Services;
using Velopack;
using Velopack.Sources;

namespace ASync.App.Desktop;

/// <summary>
/// Self-update through Velopack, for the copies its installers made: Setup.exe on Windows, the .pkg (or the .app from
/// the .dmg/zip) on macOS, the AppImage on Linux. The feed is this project's GitHub Releases (each release carries
/// Velopack's <c>releases.&lt;channel&gt;.json</c> and packages; the channel, e.g. <c>linux-x64</c>, is built into the
/// package, so every OS and architecture finds its own), or what <c>ASYNC_UPDATE_URL</c> / <c>updateFeed</c> in
/// <c>app-settings.json</c> names: another GitHub repository, a web feed or a local folder.
/// </summary>
internal sealed class VelopackUpdates : IUpdateService
{
    public const string DefaultFeed = "https://github.com/jncchds/ASync";

    /// <summary>Where the .deb and .rpm put the app (see packaging/linux/nfpm-desktop.yaml).</summary>
    public const string PackageInstallDirectory = "/opt/async-desktop/";

    private readonly UpdateManager? _manager;
    private readonly string[] _restartArgs;

    public VelopackUpdates(string? configuredFeed, DesktopOptions options)
    {
        _restartArgs = ["--config", options.ConfigDirectory, "--after-update"];
        var feed = Feed(Environment.GetEnvironmentVariable("ASYNC_UPDATE_URL"), configuredFeed);
        try
        {
            var manager = new UpdateManager(Source(feed));
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Not a Velopack install (development build, .deb/.rpm) or an unusable feed: no self-update.
        }
        UnavailableReason = _manager is null ? WhyNot(Environment.ProcessPath) : null;
    }

    public string? UnavailableReason { get; }

    internal static string Feed(string? environment, string? configured) =>
        environment is { Length: > 0 } e ? e : configured is { Length: > 0 } c ? c : DefaultFeed;

    internal static bool IsGitHub(string feed) =>
        Uri.TryCreate(feed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com"
        && uri.AbsolutePath.Trim('/').Split('/').Length == 2;

    internal static IUpdateSource Source(string feed) =>
        IsGitHub(feed) ? new GithubSource(feed, accessToken: null, prerelease: false)
        : Directory.Exists(feed) ? new SimpleFileSource(new DirectoryInfo(feed))
        : new SimpleWebSource(feed);

    internal static string WhyNot(string? processPath) =>
        processPath?.StartsWith(PackageInstallDirectory, StringComparison.Ordinal) == true
            ? "ASync was installed with the system's package manager (.deb or .rpm), which also updates it (apt upgrade, dnf upgrade)."
            : "This copy of ASync was not installed with its installer, so it does not update itself.";

    public async Task<AppUpdate?> CheckAsync(CancellationToken ct)
    {
        if (_manager is null) return null;
        var info = await _manager.CheckForUpdatesAsync();
        return info is null ? null : new AppUpdate(info.TargetFullRelease.Version.ToString(), info.TargetFullRelease.NotesMarkdown, info);
    }

    public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken ct) =>
        _manager is null ? Task.CompletedTask : _manager.DownloadUpdatesAsync((UpdateInfo)update.Handle!, progress, ct);

    public void ApplyAndRestart(AppUpdate update)
    {
        // The updater waits (up to 60 s) for this process to exit, replaces the program and starts it with these arguments.
        _manager?.WaitExitThenApplyUpdates(((UpdateInfo)update.Handle!).TargetFullRelease, silent: false, restart: true, _restartArgs);
    }
}
