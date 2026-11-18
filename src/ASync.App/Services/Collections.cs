using ASync.Contracts;

namespace ASync.App.Services;

/// <summary>
/// The phone's permission to read (and, for a restore only, add to) its contacts or calendars (task 20): Android's READ_CONTACTS /
/// WRITE_CONTACTS and READ_CALENDAR / WRITE_CALENDAR. Null on desktops (<see cref="IPlatformServices.Contacts"/>,
/// <see cref="IPlatformServices.Calendars"/>). iOS's Contacts and EventKit authorisation fit the same shape (limited contacts = Partial).
/// </summary>
public interface ICollectionAccess
{
    /// <summary><see cref="FolderKind.Contacts"/> or <see cref="FolderKind.Calendars"/>.</summary>
    FolderKind Kind { get; }

    /// <summary>What ASync may read now.</summary>
    MediaAccess Access { get; }

    /// <summary>Whether ASync may add contacts or events (only a restore asks for it).</summary>
    bool CanWrite { get; }

    /// <summary>Asks the system to let ASync read them (the permission dialog); the answer arrives through <see cref="Changed"/>.</summary>
    void RequestRead();

    /// <summary>Asks the system to let ASync add to them, for a restore.</summary>
    void RequestWrite();

    /// <summary>The app's page in the system settings (a permission the dialog can no longer ask for).</summary>
    void OpenSettings();

    /// <summary>The permission may have changed (a dialog was answered, the app came back to the front).</summary>
    event Action? Changed;

    /// <summary>The platform's own words below the explanation (iOS: which answer to choose, what iOS keeps from apps); null for none.</summary>
    string? Note => null;

    /// <summary>What partial access means here (iOS 18's limited contacts); null: the generic text.</summary>
    string? PartialText => null;
}

public static class CollectionTexts
{
    public static string Noun(FolderKind kind) => kind == FolderKind.Contacts ? "contacts" : "calendars";

    /// <summary>"312 contacts in 2 accounts", "1,840 events in 3 calendars".</summary>
    public static string Counts(FolderKind kind, int items, int collections) => kind == FolderKind.Contacts
        ? $"{Format.Count(items, "contact")} in {Format.Count(collections, "account")}"
        : $"{Format.Count(items, "event")} in {Format.Count(collections, "calendar")}";
}
