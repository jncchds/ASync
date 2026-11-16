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
