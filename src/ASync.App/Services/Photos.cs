using ASync.Contracts;

namespace ASync.App.Services;

/// <summary>
/// The phone's permission to read its photos and videos (task 19): Android's READ_MEDIA_IMAGES/VIDEO (13+), READ_EXTERNAL_STORAGE
/// before, the "Select photos" partial access of Android 14, and ACCESS_MEDIA_LOCATION for the location in the originals. Null on
/// desktops (<see cref="IPlatformServices.Photos"/>). iOS's PhotoKit authorisation (task 23) fits the same shape: limited ("Selected
/// Photos") = Partial, with the system's picker to choose more; the location is always in what PhotoKit gives.
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

    /// <summary>What the Photos screen says on this platform.</summary>
    PhotoTexts Texts => PhotoTexts.Android;

    /// <summary>Whether the system can show its picker of the photos ASync may see (iOS's limited library picker).</summary>
    bool CanChooseMore => false;

    /// <summary>The system's picker of the photos ASync may see (iOS: presentLimitedLibraryPicker); the answer arrives through <see cref="Changed"/>.</summary>
    void ChooseMore() { }

    /// <summary>Whether originals kept only in the cloud can be downloaded for a backup here (iOS: iCloud Photos).</summary>
    bool OffersCloudDownloads => false;

    /// <summary>Whether a backup may download originals kept only in the cloud (a setting of this device, for every Photos folder on it).</summary>
    bool CloudDownloads { get => false; set { } }
}

/// <summary>The words of the Photos screen and card on a platform (<see cref="IPhotoAccess.Texts"/>).</summary>
/// <param name="Explanation">Why ASync asks, before the system's question.</param>
/// <param name="PartialText">What partial access means (empty where the platform has none).</param>
/// <param name="AllowAllLabel">The button that leads to full access.</param>
/// <param name="NoLocation">Shown when the originals lose their location without a further permission (null: never).</param>
/// <param name="CardPartial">The folder card's notice with partial access.</param>
/// <param name="CloudLabel">The switch for downloads of cloud-only originals (null: no such switch).</param>
/// <param name="CloudHint">What it means.</param>
public sealed record PhotoTexts(string Explanation, string PartialText, string AllowAllLabel, string? NoLocation, string CardPartial,
    string? CloudLabel = null, string? CloudHint = null)
{
    public static readonly PhotoTexts Android = new(
        Explanation: "To back up your photos and videos ASync needs to read them. It reads each original as it is, never changes or deletes " +
                     "anything, and sends it only to your own server's storage (encrypted there if the folder is end-to-end). " +
                     "Allow the location in photos too, or Android removes it from what ASync reads.",
        PartialText: "With \"Select photos\" Android lets ASync see only the photos you pick, and only those are backed up; new photos are not " +
                     "seen until you pick them too. Choose \"Allow all\" in the settings to back up everything.",
        AllowAllLabel: "Allow all photos…",
        NoLocation: "Android removes the location from photos unless ASync may read it: allow \"media location\" to keep it in the backup.",
        CardPartial: "ASync may read only the photos chosen for it (\"Select photos\"): only those are backed up.");
}
