using System.Text.Json;
using System.Text.Json.Serialization;
using ASync.Contracts;

namespace ASync.App.Services;

/// <summary>Light, dark, or whatever the system uses.</summary>
public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// What the shared UI needs from the platform it runs on. The desktop head implements it with Avalonia's launcher and
/// folder picker plus its own notifications and start-at-login; tests use a fake.
/// </summary>
public interface IPlatformServices
{
    /// <summary>The default name for this device.</summary>
    string MachineName { get; }

    /// <summary>What this device is registered as.</summary>
    DevicePlatform Platform { get; }

    /// <summary>The app's own version.</summary>
    string AppVersion { get; }

    Task OpenUrlAsync(string url);

    /// <summary>Shows a folder in the file manager.</summary>
    Task OpenFolderAsync(string path);

    /// <summary>The native folder picker; null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startAt);

    /// <summary>A system notification (best effort: no error when the system has no notification service).</summary>
    void Notify(string title, string message);

    void ApplyTheme(ThemeChoice theme);

    /// <summary>Whether <see cref="IsStartAtLoginEnabled"/>/<see cref="SetStartAtLogin"/> work here.</summary>
    bool SupportsStartAtLogin { get; }
    bool IsStartAtLoginEnabled();
    void SetStartAtLogin(bool enabled);

    /// <summary>Whether <see cref="ScanQrCodeAsync"/> works here (a camera: phones).</summary>
    bool CanScanQrCode => false;

    /// <summary>Scans a QR code with the camera; its text, or null when cancelled.</summary>
    Task<string?> ScanQrCodeAsync() => Task.FromResult<string?>(null);

    /// <summary>A folder's local path as people read it (Android shows "Internal storage › Sync" for a content URI).</summary>
    string DisplayPath(string location) => location;

    /// <summary>"computer" or "phone": what the UI calls this device.</summary>
    string DeviceNoun => "computer";

    /// <summary>
    /// Whether the app offers to open the web UI on this device. Phones do not: everything a phone needs is in the app, and
    /// the server's own setup (folders, storage, people) is done in the web UI on a computer.
    /// </summary>
    bool OffersWebUi => true;

    /// <summary>Which run conditions this platform cannot check (shown under a folder's conditions); null = it checks all.</summary>
    string? ConditionsNote =>
        OperatingSystem.IsLinux() ? "On this computer the Wi-Fi rule needs NetworkManager; without it the rule is ignored."
        : "This computer cannot tell metered networks apart: the Wi-Fi rule is ignored here (it applies on phones).";

    /// <summary>Background work, battery and push (phones); null on desktops, where the agent is a service.</summary>
    IBackgroundServices? Background => null;

    /// <summary>Real paths on a phone's shared storage ("All files access"); null on desktops.</summary>
    IStorageAccess? Storage => null;

    /// <summary>The permission to read the photo library (phones: Photos folders, task 19); null where there is none.</summary>
    IPhotoAccess? Photos => null;

    /// <summary>The permissions for contacts and calendars (phones: Contacts and Calendars folders, task 20); null where there are none.</summary>
    ICollectionAccess? Contacts => null;
    ICollectionAccess? Calendars => null;

    ICollectionAccess? CollectionAccess(FolderKind kind) => kind == FolderKind.Contacts ? Contacts : kind == FolderKind.Calendars ? Calendars : null;
}

/// <summary>A UnifiedPush distributor installed on the phone (ntfy, say).</summary>
public sealed record PushDistributor(string Id, string Name);

/// <summary>
/// Where the phone's UnifiedPush registration is: the distributor chosen (null = none), whether it gave an endpoint,
/// whether the server took it, and the last problem.
/// </summary>
public sealed record PushState(string? Distributor, string? DistributorName, bool HasEndpoint, bool Registered, string? Problem)
{
    public static readonly PushState None = new(null, null, false, false, null);
}

/// <summary>
/// The phone's background side (Android: WorkManager, the battery-optimisation exemption, UnifiedPush, the local "no
/// backup" reminder). <see cref="Changed"/> may fire on any thread.
/// </summary>
public interface IBackgroundServices
{
    /// <summary>Exempt from battery optimisation (Doze and App Standby do not hold its background work back as much).</summary>
    bool IsIgnoringBatteryOptimizations { get; }

    /// <summary>The system's "Let the app always run in the background?" question.</summary>
    void RequestIgnoreBatteryOptimizations();

    /// <summary>The system's battery-optimisation list (to undo it, or where the question is not offered).</summary>
    void OpenBatterySettings();

    /// <summary>What this phone's maker additionally needs, as text (Xiaomi's autostart, Samsung's sleeping apps…), or null.</summary>
    string? VendorHint { get; }

    IReadOnlyList<PushDistributor> Distributors();

    PushState Push { get; }

    /// <summary>Registers with a distributor; the endpoint it answers with goes to the server.</summary>
    void RegisterPush(string distributorId);

    /// <summary>Unregisters, and removes the endpoint from the server.</summary>
    void UnregisterPush();

    /// <summary>Days without a successful run before the phone itself reminds the person (0 = never).</summary>
    int ReminderDays { get; set; }

    event Action? Changed;
}

/// <summary>The app's own preferences (not the agent's: those live on the server).</summary>
public sealed record AppSettings
{
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;
    /// <summary>System notifications for failed runs and conflicts.</summary>
    public bool Notifications { get; init; } = true;
    /// <summary>The server address last used to enroll, offered again after signing out.</summary>
    public string? LastServer { get; init; }
    /// <summary>Look for new versions after start-up and once a day.</summary>
    public bool CheckForUpdates { get; init; } = true;
    /// <summary>
    /// Where updates come from instead of the project's GitHub Releases: a GitHub repository URL, another feed URL or a
    /// local folder with Velopack's release files (no UI; for testing and private mirrors). <c>ASYNC_UPDATE_URL</c> wins.
    /// </summary>
    public string? UpdateFeed { get; init; }
    /// <summary>Phones: the battery-optimisation prompt on the dashboard was dismissed ("Not now").</summary>
    public bool BatteryPromptDismissed { get; init; }
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>Settings in a JSON file (<c>app-settings.json</c> next to the agent's config, by default).</summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    public AppSettings Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.AppSettings) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings(); // damaged or unreadable: defaults rather than no app
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* kept in memory for this run */ }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
