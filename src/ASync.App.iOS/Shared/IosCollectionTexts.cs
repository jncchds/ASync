namespace ASync.App.iOS;

/// <summary>What the Contacts and Calendars screens and cards say on an iPhone (<c>ICollectionAccess.Note</c> / <c>PartialText</c>): iOS's words.</summary>
public static class IosCollectionTexts
{
    public const string ContactsNote =
        "Choose \"Allow Full Access\" when iOS asks. With \"Limited Access\" only the contacts you share with ASync are backed up. " +
        "iOS keeps contacts' notes from apps Apple has not approved for them, so notes are not backed up from an iPhone.";

    public const string ContactsPartial =
        "ASync may read only the contacts you shared with it (Limited Access): only those are backed up. Choose \"Full Access\" in " +
        "Settings › Apps › ASync › Contacts to back up everything.";

    public const string CalendarsNote =
        "Choose \"Allow Full Access\" when iOS asks: \"Add Events Only\" lets ASync add events but not read them, so nothing could be " +
        "backed up. Guests are backed up but cannot be put back by an app; a restore adds the events without them.";
}
