namespace ASync.App.Services;

/// <summary>A newer version of the app found on the release feed.</summary>
/// <param name="Version">The new version, e.g. <c>0.1.6</c>.</param>
/// <param name="Notes">Its release notes (Markdown), when the feed carries them.</param>
/// <param name="Handle">The update service's own object for it (Velopack's <c>UpdateInfo</c> on the desktop).</param>
public sealed record AppUpdate(string Version, string? Notes, object? Handle = null);

/// <summary>
/// Where the app finds and installs new versions of itself. The desktop head uses Velopack (Setup.exe, the macOS
/// package, the AppImage); installs that something else updates (a .deb or .rpm, a development build) say why not in
/// <see cref="UnavailableReason"/>. The app never installs an update without the person's consent.
/// </summary>
public interface IUpdateService
{
    /// <summary>Null when this copy can update itself; otherwise why not, for people (shown in Settings).</summary>
    string? UnavailableReason { get; }

    /// <summary>The newest version on the feed when it is newer than this one, otherwise null.</summary>
    Task<AppUpdate?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the update (progress 0–100).</summary>
    Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken ct);

    /// <summary>
    /// Hands the downloaded update to the updater, which waits for this process to exit, installs it and starts the new
    /// version. The caller exits the app right after.
    /// </summary>
    void ApplyAndRestart(AppUpdate update);
}

/// <summary>For heads and installs without self-update.</summary>
public sealed class NoUpdates(string reason) : IUpdateService
{
    public string? UnavailableReason { get; } = reason;
    public Task<AppUpdate?> CheckAsync(CancellationToken ct) => Task.FromResult<AppUpdate?>(null);
    public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken ct) => Task.CompletedTask;
    public void ApplyAndRestart(AppUpdate update) { }
}
