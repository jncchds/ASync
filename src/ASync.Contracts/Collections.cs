namespace ASync.Contracts;

// ── Contacts and calendars (task 20) ──────────────────────────────────────────
//
// A Backup folder of Kind = Contacts or Calendars backs up a phone's address book or calendars instead of a directory. Each device
// in it has a collection selection (everything, or chosen contact accounts / calendars, plus "events since" for calendars) in place
// of a local path, chosen from the web the same way a Photos folder chooses albums. The phone reads its providers (Android's
// ContactsContract and CalendarContract; iOS's Contacts framework and EventKit later); the server never sees more than the names and
// counts of accounts and calendars. See AGENTS.md "Extra data sources".

/// <summary>A contact account or calendar the selection names: its stable id on the device, and the name it had when it was chosen.</summary>
public sealed record CollectionRef(string Id, string Name);

/// <summary>
/// What a device backs up into a Contacts or Calendars folder. <paramref name="All"/>: every contact account / every calendar;
/// otherwise only <paramref name="Collections"/>. <paramref name="PastDays"/> (calendars only): events that ended within that many
/// days before the run, and every later or still recurring one; null = all of them, however old.
/// </summary>
public sealed record CollectionSelectionDto(bool All = true, List<CollectionRef>? Collections = null, int? PastDays = CollectionLimits.DefaultPastDays)
{
    /// <summary>Everything: every account or calendar, events of the last two years and later.</summary>
    public static readonly CollectionSelectionDto Everything = new();

    /// <summary>Null when the selection can be saved for a folder of <paramref name="kind"/>; otherwise why not, for people.</summary>
    public string? Problem(FolderKind kind) =>
        !All && (Collections is null || Collections.Count == 0)
            ? kind == FolderKind.Contacts ? "Choose at least one account, or back up all contacts." : "Choose at least one calendar, or back up all calendars."
        : Collections is { Count: > CollectionLimits.MaxSelected } ? $"Choose at most {CollectionLimits.MaxSelected}."
        : Collections?.Any(c => string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 300 || (c.Name?.Length ?? 0) > 200) == true ? "An account or calendar id is not valid."
        : PastDays is < 1 or > 36500 ? "Events since: between 1 day and 100 years, or everything."
        : null;

    /// <summary>One line for people: "All contacts", "2 calendars: Work, Family · events of the last 2 years".</summary>
    public string Describe(FolderKind kind)
    {
        var noun = kind == FolderKind.Contacts ? "account" : "calendar";
        var what = All ? kind == FolderKind.Contacts ? "All contacts" : "All calendars"
            : $"{Collections!.Count} {noun}{(Collections.Count == 1 ? "" : "s")}: {string.Join(", ", Collections.Select(c => c.Name))}";
        if (kind != FolderKind.Calendars) return what;
        return $"{what} · {DescribePast(PastDays)}";
    }

    public static string DescribePast(int? days) => days switch
    {
        null => "all events",
        var d when d % 365 == 0 => $"events of the last {(d / 365 == 1 ? "year" : $"{d / 365} years")}",
        var d when d % 30 == 0 => $"events of the last {(d / 30 == 1 ? "month" : $"{d / 30} months")}",
        var d => $"events of the last {d} days",
    };
}

public static class CollectionLimits
{
    public const int MaxSelected = 500;
    /// <summary>Accounts or calendars per answer (and stored per device).</summary>
    public const int MaxCollections = 1000;
    /// <summary>"Events since": two years by default.</summary>
    public const int DefaultPastDays = 730;
}

/// <summary>
/// One contact account or calendar of a device, as the web UI's picker shows it. <paramref name="Count"/>: contacts in the account,
/// events in the calendar (all of them, before "events since").
/// </summary>
/// <param name="Id">Stable on the device (Android: an account's <c>type:name</c>, a calendar's <c>_id</c>).</param>
/// <param name="Account">Calendars: the account the calendar belongs to (its name); contacts: the account's type, for people ("Google").</param>
/// <param name="Color">Calendars: <c>#rrggbb</c>.</param>
/// <param name="ReadOnly">A calendar ASync could not restore into (holidays, contacts' birthdays).</param>
public sealed record CollectionDto(string Id, string Name, int Count, string? Account = null, string? Color = null, bool ReadOnly = false);

/// <summary>The server asking a device for its contact accounts or calendars (the hub's <c>collections</c> client result).</summary>
public sealed record CollectionsRequest(FolderKind Kind, int Max = CollectionLimits.MaxCollections);

/// <summary>
/// The device's answer. <paramref name="Items"/>: contacts or events in all of them. <paramref name="Refusal"/> (with
/// <paramref name="Message"/>) when it does not list them (browsing turned off, no permission, no such data).
/// </summary>
public sealed record CollectionsReply(List<CollectionDto>? Collections = null, MediaAccess Access = MediaAccess.Full, int Items = 0,
    DeviceBrowseRefusal? Refusal = null, string? Message = null);

/// <summary>
/// <c>GET /api/devices/{id}/collections?kind=Contacts|Calendars</c>: the device's accounts or calendars, live (<paramref name="Live"/>)
/// or as last reported (<paramref name="ReportedAt"/>, null = never), with why it was not asked in <paramref name="Message"/>.
/// </summary>
public sealed record DeviceCollectionsDto(bool Live, List<CollectionDto> Collections, MediaAccess? Access, DateTime? ReportedAt, string? Message = null,
    int Items = 0);

/// <summary>A device telling the server its accounts or calendars after a backup (<c>PUT /api/sync/collections</c>).</summary>
public sealed record ReportCollectionsRequest(FolderKind Kind, List<CollectionDto> Collections, MediaAccess Access, int Items = 0);

/// <summary>What a Contacts or Calendars folder's last backup on a device holds: contacts or events, in how many accounts or calendars, their bytes.</summary>
public sealed record CollectionCountsDto(int Items, int Collections, long Bytes);
