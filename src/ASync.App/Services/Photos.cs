using ASync.Contracts;

namespace ASync.App.Services;

/// <summary>
/// The phone's permission to read its photos and videos (task 19): Android's READ_MEDIA_IMAGES/VIDEO (13+), READ_EXTERNAL_STORAGE
/// before, the "Select photos" partial access of Android 14, and ACCESS_MEDIA_LOCATION for the location in the originals. Null on
/// desktops (<see cref="IPlatformServices.Photos"/>). iOS's PhotoKit authorisation fits the same shape (limited = Partial).
/// </summary>
public interface IPhotoAccess
{
    /// <summary>What ASync may read now.</summary>
    MediaAccess Access { get; }

    /// <summary>Whether the originals keep their location (ACCESS_MEDIA_LOCATION granted; without it Android removes it from what ASync reads).</summary>
    bool HasLocation { get; }

    /// <summary>Whether this phone can give access to some photos only (Android 14+: "Select photos").</summary>
    bool OffersPartial { get; }

    /// <summary>Asks the system (the permission dialog); the answer arrives through <see cref="Changed"/>.</summary>
    void Request();

    /// <summary>The app's page in the system settings (permissions a dialog can no longer ask for, or "Allow all").</summary>
    void OpenSettings();

    /// <summary>The permission may have changed (the dialog was answered, the app came back to the front).</summary>
    event Action? Changed;
}
