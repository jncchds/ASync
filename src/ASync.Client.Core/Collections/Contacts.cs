using ASync.Contracts;

namespace ASync.Client.Core.Collections;

// ── The address book, platform-neutral (task 20) ──────────────────────────────
//
// What a phone's contacts provider gives (Android's ContactsContract; iOS's Contacts framework later) in a shape close to vCard 4.0
// (RFC 6350), so VCard.Write is a plain mapping and a platform adapter maps its own fields in both directions. See AGENTS.md "Extra
// data sources".

/// <summary>
/// A contact account: where the phone keeps contacts (a Google account, the phone itself, the SIM, an app's account).
/// <paramref name="Id"/> is stable on the device (Android: <c>&lt;type&gt;:&lt;name&gt;</c>, <c>local</c> for contacts in no account);
/// <paramref name="ShortId"/> goes into the snapshot's directory name; <paramref name="TypeLabel"/> is for people ("Google", "Phone", "SIM").
/// </summary>
public sealed record ContactAccount(string Id, string ShortId, string Name, string Type, string TypeLabel, int Contacts, bool ReadOnly = false);

/// <summary>A value with its vCard TYPE (<c>cell</c>, <c>work,fax</c>, <c>home</c>; empty for none) and, when the phone has one, its own label.</summary>
public sealed record LabeledValue(string Value, string Type = "", string? Label = null, bool Preferred = false);

public sealed record StructuredName(string? Family = null, string? Given = null, string? Middle = null, string? Prefix = null, string? Suffix = null,
    string? PhoneticFamily = null, string? PhoneticGiven = null, string? PhoneticMiddle = null);

public sealed record PostalAddress(string? Street = null, string? PoBox = null, string? Neighborhood = null, string? City = null, string? Region = null,
    string? PostalCode = null, string? Country = null, string? Formatted = null, string Type = "", string? Label = null, bool Preferred = false);

public sealed record Organization(string? Company = null, string? Department = null, string? Title = null, string Type = "", string? Label = null);

/// <summary>A date of the contact. <paramref name="Date"/>: ISO 8601 as the phone gives it, <c>1980-01-15</c> or <c>--01-15</c> without a year.</summary>
/// <param name="Kind"><c>birthday</c>, <c>anniversary</c> or <c>other</c>.</param>
public sealed record ContactDate(string Date, string Kind, string? Label = null);

/// <summary>
/// One contact as stored in one account (Android: a raw contact, not the merged contact the Contacts app shows). Lists are in the
/// provider's own stable order. <paramref name="Id"/> is stable on the device (Android: <c>android:raw:&lt;_id&gt;</c>);
/// <paramref name="ShortId"/> goes into the file name; <paramref name="SourceId"/> is the sync server's id where there is one.
/// </summary>
public sealed record Contact
{
    public required string Id { get; init; }
    public required string ShortId { get; init; }
    public required string AccountId { get; init; }
    public string? DisplayName { get; init; }
    public StructuredName? Name { get; init; }
    public IReadOnlyList<string> Nicknames { get; init; } = [];
    public IReadOnlyList<LabeledValue> Phones { get; init; } = [];
    public IReadOnlyList<LabeledValue> Emails { get; init; } = [];
    public IReadOnlyList<PostalAddress> Addresses { get; init; } = [];
    public IReadOnlyList<Organization> Organizations { get; init; } = [];
    public IReadOnlyList<LabeledValue> Websites { get; init; } = [];
    /// <summary>Instant messaging: <see cref="LabeledValue.Value"/> the handle, <see cref="LabeledValue.Type"/> the protocol (<c>xmpp</c>, <c>skype</c>, …).</summary>
    public IReadOnlyList<LabeledValue> Messaging { get; init; } = [];
    /// <summary>SIP addresses (<c>sip:</c> URIs or bare addresses).</summary>
    public IReadOnlyList<LabeledValue> Sip { get; init; } = [];
    /// <summary>Related people by name: <see cref="LabeledValue.Type"/> <c>spouse</c>, <c>child</c>, <c>parent</c>, <c>friend</c>, ….</summary>
    public IReadOnlyList<LabeledValue> Relations { get; init; } = [];
    public IReadOnlyList<ContactDate> Dates { get; init; } = [];
    public string? Note { get; init; }
    /// <summary>The largest photo the phone has (Android: the display photo, else the thumbnail), as stored (JPEG usually).</summary>
    public byte[]? Photo { get; init; }
    /// <summary>The groups (labels) the contact is in, by title.</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];
    public bool Starred { get; init; }
    public string? SourceId { get; init; }
}

/// <summary>A contact as listed (no photo, no details): enough for the tree to decide whether it changed.</summary>
/// <param name="Version">Changes whenever the contact (or its photo) changes (Android: RawContacts.VERSION); null when the platform has none.</param>
/// <param name="Modified">Unix milliseconds of its last change where known (Android: the merged contact's last update), else 0.</param>
public sealed record ContactSummary(string Id, string ShortId, string AccountId, string? DisplayName, string? Version, long Modified);

/// <summary>
/// A device's address book: Android's ContactsContract (<c>AndroidContactsSource</c>), iOS's Contacts framework later, a fake in the tests.
/// Reading is synchronous and may block (called from a backup run, off the UI thread); implementations must be safe from several
/// threads. Nothing here knows about folders, snapshots or the server.
/// </summary>
public interface IContactsSource
{
    /// <summary>Whether ASync may read contacts now (READ_CONTACTS), asked before every run.</summary>
    MediaAccess Access { get; }

    /// <summary>Whether ASync may also write them (WRITE_CONTACTS: asked only for a restore).</summary>
    bool CanWrite { get; }

    IReadOnlyList<ContactAccount> Accounts();

    /// <summary>Every contact of the accounts (all of them when null), in any order. Deleted ones are not listed.</summary>
    IReadOnlyList<ContactSummary> Contacts(IReadOnlySet<string>? accountIds = null);

    /// <summary>The whole contact, or null when it is gone. <paramref name="withPhoto"/> false skips the photo (a restore comparing contents).</summary>
    Contact? Read(string id, bool withPhoto = true);

    /// <summary>Adds a contact to an account (Android: a raw contact with its data rows); returns its new id. Never changes or deletes others.</summary>
    Task<string> InsertAsync(ContactAccount account, Contact contact, CancellationToken ct);
}
