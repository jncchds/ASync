using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASync.Client.Core.Collections;
using ASync.Contracts;

namespace ASync.App.iOS;

// The iPhone's contacts (task 24): the Contacts framework behind a thin interface (ICNContacts, implemented by ContactsBridge in the
// head), and everything that decides something in plain C# here, linked into ASync.App.Tests and tested on Linux. AGENTS.md "Extra data
// sources" → "iOS contacts and calendars (task 24)".

/// <summary>CNAuthorizationStatus (Limited: iOS 18's "Limited Access").</summary>
public enum CNAuth { NotDetermined = 0, Restricted = 1, Denied = 2, Authorized = 3, Limited = 4 }

/// <summary>CNContainerType.</summary>
public enum CNContainerKind { Unassigned = 0, Local = 1, Exchange = 2, CardDav = 3 }

/// <summary>A CNContainer: where contacts live (the iPhone itself, iCloud, an Exchange or CardDAV account).</summary>
public sealed record CNContainerData(string Id, string Name, CNContainerKind Kind);

/// <summary>A CNGroup of a container, with the identifiers of the contacts in it.</summary>
public sealed record CNGroupData(string Id, string Name, string ContainerId, IReadOnlyList<string> Members);

/// <summary>A CNLabeledValue: its label as the framework keeps it (<c>_$!&lt;Home&gt;!$_</c>, <c>iPhone</c>, or the person's own) and the value.</summary>
public sealed record CNLabeled<T>(string? Label, T Value);

/// <summary>NSDateComponents of a contact's date (no year: null).</summary>
public sealed record CNDate(int? Year, int Month, int Day);

public sealed record CNAddress(string? Street = null, string? SubLocality = null, string? City = null, string? SubAdministrativeArea = null,
    string? State = null, string? PostalCode = null, string? Country = null, string? IsoCountryCode = null);

public sealed record CNInstantMessage(string Username, string Service);

public sealed record CNSocial(string? UrlString, string? Username, string? UserIdentifier, string Service);

/// <summary>
/// A CNContact with the keys ASync reads (never the note: <c>CNContactNoteKey</c> needs Apple's <c>com.apple.developer.contacts.notes</c>
/// entitlement, and fetching it without throws). <see cref="ImageData"/> only when asked for (a listing has the thumbnail only).
/// </summary>
public sealed record CNContactData
{
    public required string Id { get; init; }
    public string ContainerId { get; init; } = "";
    public bool IsOrganization { get; init; }
    /// <summary>CNContactFormatter's full name (the person's name order), when the bridge made one.</summary>
    public string? DisplayName { get; init; }
    public string? NamePrefix { get; init; }
    public string? GivenName { get; init; }
    public string? MiddleName { get; init; }
    public string? FamilyName { get; init; }
    public string? NameSuffix { get; init; }
    public string? Nickname { get; init; }
    public string? PhoneticGivenName { get; init; }
    public string? PhoneticMiddleName { get; init; }
    public string? PhoneticFamilyName { get; init; }
    public string? OrganizationName { get; init; }
    public string? DepartmentName { get; init; }
    public string? JobTitle { get; init; }
    public IReadOnlyList<CNLabeled<string>> Phones { get; init; } = [];
    public IReadOnlyList<CNLabeled<string>> Emails { get; init; } = [];
    public IReadOnlyList<CNLabeled<string>> Urls { get; init; } = [];
    public IReadOnlyList<CNLabeled<CNAddress>> Addresses { get; init; } = [];
    public CNDate? Birthday { get; init; }
    public IReadOnlyList<CNLabeled<CNDate>> Dates { get; init; } = [];
    public IReadOnlyList<CNLabeled<string>> Relations { get; init; } = [];
    public IReadOnlyList<CNLabeled<CNInstantMessage>> InstantMessages { get; init; } = [];
    public IReadOnlyList<CNLabeled<CNSocial>> SocialProfiles { get; init; } = [];
    public byte[]? ImageData { get; init; }
    public byte[]? ThumbnailData { get; init; }
}

/// <summary>
/// The Contacts framework, as thin as it can be: listings and save requests, no decisions. <c>ContactsBridge</c> in the head implements
/// it; the tests use a fake. Calls block (CNContactStore is synchronous) and are made off the main thread.
/// </summary>
public interface ICNContacts
{
    CNAuth Authorization { get; }

    IReadOnlyList<CNContainerData> Containers();

    /// <summary>CNContactStore.defaultContainerIdentifier.</summary>
    string? DefaultContainerId { get; }

    /// <summary>The identifiers of a container's contacts (a count without reading them).</summary>
    IReadOnlyList<string> ContactIds(string containerId);

    /// <summary>A container's contacts with every key but the full image (the thumbnail instead).</summary>
    IReadOnlyList<CNContactData> Contacts(string containerId);

    /// <summary>One contact (its <see cref="CNContactData.ContainerId"/> filled in), with the full image when <paramref name="image"/>; null when it is gone.</summary>
    CNContactData? Read(string id, bool image);

    /// <summary>Every group, with its members.</summary>
    IReadOnlyList<CNGroupData> Groups();

    /// <summary>A CNSaveRequest adding the contact to the container; answers its new identifier.</summary>
    string Add(CNContactData contact, string containerId);

    /// <summary>A CNSaveRequest adding a group to the container; answers its identifier.</summary>
    string AddGroup(string name, string containerId);

    /// <summary>A CNSaveRequest adding a member to a group.</summary>
    void AddMember(string groupId, string contactId);
}

/// <summary>
/// The mapping of the Contacts framework onto <see cref="Contact"/>s and <see cref="ContactAccount"/>s (AGENTS.md "iOS contacts and
/// calendars"): containers, labels both ways, dates, messaging and social profiles, the version that lets a backup skip unchanged ones.
/// </summary>
public static class IosContactMapping
{
    public const string Source = "ios-contacts";

    public static MediaAccess Access(CNAuth status) => status switch
    {
        CNAuth.Authorized => MediaAccess.Full,
        CNAuth.Limited => MediaAccess.Partial,
        _ => MediaAccess.None,
    };

    // ── Containers ──────────────────────────────────────────────────────────

    /// <summary>
    /// What people call a container: the iPhone's own (local), iCloud (a CardDAV container named "iCloud", or "Card" as some iOS
    /// versions name it: the framework does not say it is iCloud), Exchange, another CardDAV account.
    /// </summary>
    public static string TypeLabel(CNContainerData c) => c.Kind switch
    {
        CNContainerKind.Local => "iPhone",
        CNContainerKind.Exchange => "Exchange",
        CNContainerKind.CardDav when IsICloud(c) => "iCloud",
        CNContainerKind.CardDav => "CardDAV",
        _ => "Contacts",
    };

    public static bool IsICloud(CNContainerData c) =>
        c.Kind == CNContainerKind.CardDav && c.Name.Trim() is "iCloud" or "Card" or "";

    public static string Type(CNContainerData c) => c.Kind switch
    {
        CNContainerKind.Local => "local",
        CNContainerKind.Exchange => "exchange",
        CNContainerKind.CardDav => "carddav",
        _ => "unassigned",
    };

    public static ContactAccount Account(CNContainerData c, int contacts)
    {
        var label = TypeLabel(c);
        var name = IsICloud(c) ? "iCloud" : c.Name is { Length: > 0 } n ? n : label;
        return new ContactAccount(c.Id, CollectionLayout.HashId(c.Id), name, Type(c), label, contacts);
    }

    /// <summary>The short id for file names: the identifier with <c>:</c> and <c>/</c> as underscores (<c>410F…:ABPerson</c> → <c>410F…_ABPerson</c>).</summary>
    public static string ShortId(string identifier) => identifier.Replace(':', '_').Replace('/', '_');

    // ── Labels ──────────────────────────────────────────────────────────────

    /// <summary>The framework's own labels are <c>_$!&lt;Name&gt;!$_</c> (CNLabelHome, CNLabelPhoneNumberMobile, …); a few are plain words.</summary>
    public static string SystemLabel(string name) => name is "iPhone" or "iCloud" ? name : $"_$!<{name}>!$_";

    /// <summary>The name of a system label (<c>_$!&lt;Home&gt;!$_</c> → <c>Home</c>), or null for the person's own label.</summary>
    public static string? SystemName(string? label) =>
        label is null ? null
        : label is "iPhone" or "iCloud" ? label
        : label.StartsWith("_$!<", StringComparison.Ordinal) && label.EndsWith(">!$_", StringComparison.Ordinal) && label.Length > 8 ? label[4..^4]
        : null;

    /// <summary>
    /// The system labels a restore writes back as system labels (a label of these names from elsewhere, Android's "Main" say, becomes
    /// iOS's own): those of CNLabelKey, CNLabelPhoneNumberKey and the common CNLabelContactRelationKey ones. Others stay labels.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownSystemNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Home", "Work", "School", "Other", "HomePage", "Anniversary", "iPhone", "iCloud", "Mobile", "Main", "HomeFAX", "WorkFAX", "OtherFAX",
        "Pager", "AppleWatch", "Assistant", "Brother", "Child", "Father", "Friend", "Manager", "Mother", "Parent", "Partner", "Sister",
        "Spouse", "Son", "Daughter", "Husband", "Wife", "Boyfriend", "Girlfriend", "Colleague", "Teacher", "Cousin", "Grandfather",
        "Grandmother", "Grandparent", "Grandchild", "Uncle", "Aunt", "Nephew", "Niece", "Sibling", "Stepfather", "Stepmother",
    };

    private static readonly Dictionary<string, (string Type, string? Label)> PhoneLabels = new(StringComparer.Ordinal)
    {
        ["Home"] = ("home", null), ["Work"] = ("work", null), ["Other"] = ("", null), ["Mobile"] = ("cell", null), ["iPhone"] = ("cell", "iPhone"),
        ["AppleWatch"] = ("cell", "Apple Watch"), ["Main"] = ("", "Main"), ["HomeFAX"] = ("home,fax", null), ["WorkFAX"] = ("work,fax", null),
        ["OtherFAX"] = ("fax", null), ["Pager"] = ("pager", null), ["School"] = ("", "School"),
    };

    private static readonly Dictionary<string, (string Type, string? Label)> GenericLabels = new(StringComparer.Ordinal)
    {
        ["Home"] = ("home", null), ["Work"] = ("work", null), ["Other"] = ("", null), ["School"] = ("", "School"), ["iCloud"] = ("", "iCloud"),
        ["HomePage"] = ("", "Homepage"),
    };

    private static readonly Dictionary<string, (string Type, string? Label)> RelationLabels = new(StringComparer.Ordinal)
    {
        ["Parent"] = ("parent", null), ["Child"] = ("child", null), ["Friend"] = ("friend", null), ["Spouse"] = ("spouse", null),
    };

    /// <summary>"AppleWatch" → "Apple Watch", "HomePage" → "Homepage": the label people see for a system name without a vCard type.</summary>
    private static string Pretty(string name) => name switch
    {
        "AppleWatch" => "Apple Watch",
        "HomePage" => "Homepage",
        _ => name,
    };

    public enum LabelKind { Phone, Generic, Relation }

    private static Dictionary<string, (string Type, string? Label)> Table(LabelKind kind) => kind switch
    {
        LabelKind.Phone => PhoneLabels,
        LabelKind.Relation => RelationLabels,
        _ => GenericLabels,
    };

    /// <summary>A framework label as a vCard TYPE and, where vCard has no type for it, a label (X-ABLABEL): custom labels are kept.</summary>
    public static (string Type, string? Label) FromLabel(string? label, LabelKind kind)
    {
        if (label is not { Length: > 0 }) return ("", null);
        if (SystemName(label) is { } name)
            return Table(kind).TryGetValue(name, out var t) ? t : ("", Pretty(name));
        return ("", label);
    }

    private static string Norm(string type) =>
        string.Join(',', type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToLowerInvariant())
            .Where(t => t is not ("voice" or "pref" or "internet")).Distinct().Order(StringComparer.Ordinal));

    /// <summary>A vCard TYPE and label as a framework label: the system label it came from, else the label, else the closest type.</summary>
    public static string? ToLabel(string type, string? label, LabelKind kind)
    {
        var table = Table(kind);
        var norm = Norm(type);
        if (label is { Length: > 0 })
        {
            foreach (var (name, t) in table)
                if (t.Label is { } l && string.Equals(l, label, StringComparison.OrdinalIgnoreCase) && (Norm(t.Type) == norm || norm.Length == 0)) return SystemLabel(name);
            var bare = label.Replace(" ", "", StringComparison.Ordinal);
            foreach (var name in KnownSystemNames)
                if (string.Equals(name, bare, StringComparison.OrdinalIgnoreCase)) return SystemLabel(name);
            return label;
        }
        foreach (var (name, t) in table)
            if (t.Label is null && Norm(t.Type) == norm) return SystemLabel(name);
        if (kind == LabelKind.Relation) return norm.Length == 0 ? null : norm switch
        {
            "kin" => null,
            _ => norm[..1].ToUpperInvariant() + norm[1..],
        };
        var parts = norm.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (kind == LabelKind.Phone)
        {
            if (parts.Contains("fax")) return SystemLabel(parts.Contains("home") ? "HomeFAX" : parts.Contains("work") ? "WorkFAX" : "OtherFAX");
            if (parts.Contains("cell") || parts.Contains("text") || parts.Contains("textphone")) return SystemLabel("Mobile");
            if (parts.Contains("pager")) return SystemLabel("Pager");
        }
        if (parts.Contains("home")) return SystemLabel("Home");
        if (parts.Contains("work")) return SystemLabel("Work");
        return SystemLabel("Other");
    }

    // ── Messaging ───────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> ImSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AIM"] = "aim", ["Jabber"] = "xmpp", ["GoogleTalk"] = "xmpp", ["MSN"] = "msnim", ["Yahoo"] = "ymsgr", ["Skype"] = "skype",
        ["QQ"] = "qq", ["ICQ"] = "icq", ["Facebook"] = "x-facebook-messenger", ["GaduGadu"] = "x-gadugadu",
    };

    /// <summary>The prefix of a social profile's scheme (<c>IMPP:x-social-twitter:alice</c>): vCard 4.0 has no property for them.</summary>
    public const string SocialPrefix = "x-social-";

    public static string ImScheme(string service) =>
        ImSchemes.TryGetValue(service, out var s) ? s
        : new string(service.ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').ToArray()) is { Length: > 0 } t ? "x-" + t : "x-im";

    public static string ImService(string scheme) =>
        scheme.ToLowerInvariant() switch
        {
            "aim" => "AIM", "xmpp" or "jabber" => "Jabber", "msnim" or "msn" => "MSN", "ymsgr" or "yahoo" => "Yahoo", "skype" => "Skype",
            "qq" => "QQ", "icq" => "ICQ", "x-facebook-messenger" => "Facebook", "x-gadugadu" => "GaduGadu",
            var s when s.StartsWith("x-", StringComparison.Ordinal) && s.Length > 2 => s[2..],
            var s => s,
        };

    // ── Dates ───────────────────────────────────────────────────────────────

    public static string DateText(CNDate d) => d.Year is { } y
        ? string.Create(CultureInfo.InvariantCulture, $"{y:0000}-{d.Month:00}-{d.Day:00}")
        : string.Create(CultureInfo.InvariantCulture, $"--{d.Month:00}-{d.Day:00}");

    /// <summary>A contact date as the framework takes it; null for text it cannot hold (a vCard's free-text date).</summary>
    public static CNDate? ParseDate(string text)
    {
        var t = text.Trim().Replace("-", "", StringComparison.Ordinal);
        if (text.Trim().StartsWith("--", StringComparison.Ordinal))
            return t.Length == 4 && int.TryParse(t[..2], CultureInfo.InvariantCulture, out var m) && int.TryParse(t[2..], CultureInfo.InvariantCulture, out var d) && Valid(m, d)
                ? new CNDate(null, m, d) : null;
        if (t.Length >= 8 && int.TryParse(t[..4], CultureInfo.InvariantCulture, out var year) && int.TryParse(t[4..6], CultureInfo.InvariantCulture, out var month)
            && int.TryParse(t[6..8], CultureInfo.InvariantCulture, out var day) && Valid(month, day))
            return new CNDate(year, month, day);
        return null;
    }

    private static bool Valid(int month, int day) => month is >= 1 and <= 12 && day is >= 1 and <= 31;

    // ── Contacts ────────────────────────────────────────────────────────────

    private static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>A framework contact as ASync's model; the photo is the full image, else the thumbnail (null when <paramref name="withPhoto"/> is false).</summary>
    public static Contact ToContact(CNContactData c, IReadOnlyList<string> groups, bool withPhoto)
    {
        LabeledValue Labeled(CNLabeled<string> v, LabelKind kind)
        {
            var (type, label) = FromLabel(v.Label, kind);
            return new LabeledValue(v.Value, type, label);
        }
        var dates = new List<ContactDate>();
        if (c.Birthday is { } b) dates.Add(new ContactDate(DateText(b), "birthday"));
        foreach (var d in c.Dates)
        {
            var system = SystemName(d.Label);
            dates.Add(system == "Anniversary" ? new ContactDate(DateText(d.Value), "anniversary")
                : new ContactDate(DateText(d.Value), "other", system is null ? d.Label is { Length: > 0 } l ? l : "Other" : Pretty(system)));
        }
        var messaging = c.InstantMessages.Where(m => m.Value.Username.Length > 0)
            .Select(m => new LabeledValue(m.Value.Username, ImScheme(m.Value.Service), FromLabel(m.Label, LabelKind.Generic).Label)).ToList();
        var websites = c.Urls.Where(u => u.Value.Length > 0).Select(u => Labeled(u, LabelKind.Generic)).ToList();
        foreach (var s in c.SocialProfiles)
        {
            var service = new string(s.Value.Service.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
            if (Text(s.Value.Username) is { } user) messaging.Add(new LabeledValue(user, SocialPrefix + (service.Length > 0 ? service : "profile")));
            else if (Text(s.Value.UrlString) is { } url) websites.Add(new LabeledValue(url, "", s.Value.Service.Length > 0 ? s.Value.Service : "Profile"));
        }
        var org = Text(c.OrganizationName) is not null || Text(c.DepartmentName) is not null || Text(c.JobTitle) is not null
            ? new List<Organization> { new(Text(c.OrganizationName), Text(c.DepartmentName), Text(c.JobTitle)) } : [];
        var name = new StructuredName(Text(c.FamilyName), Text(c.GivenName), Text(c.MiddleName), Text(c.NamePrefix), Text(c.NameSuffix),
            Text(c.PhoneticFamilyName), Text(c.PhoneticGivenName), Text(c.PhoneticMiddleName));
        var display = Text(c.DisplayName) ?? (c.IsOrganization ? Text(c.OrganizationName) : null);
        return new Contact
        {
            Id = c.Id, ShortId = ShortId(c.Id), AccountId = c.ContainerId, DisplayName = display, Name = name,
            Nicknames = Text(c.Nickname) is { } nick ? [nick] : [],
            Phones = [.. c.Phones.Where(p => p.Value.Length > 0).Select(p => Labeled(p, LabelKind.Phone))],
            Emails = [.. c.Emails.Where(e => e.Value.Length > 0).Select(e => Labeled(e, LabelKind.Generic))],
            Addresses = [.. c.Addresses.Select(a =>
            {
                var (type, label) = FromLabel(a.Label, LabelKind.Generic);
                var v = a.Value;
                return new PostalAddress(Text(v.Street), null, Text(v.SubLocality), Text(v.City), Text(v.State), Text(v.PostalCode), Text(v.Country), null, type, label);
            })],
            Organizations = org, Websites = websites, Messaging = messaging,
            Relations = [.. c.Relations.Where(r => r.Value.Length > 0).Select(r => Labeled(r, LabelKind.Relation))],
            Dates = dates,
            Photo = withPhoto ? c.ImageData is { Length: > 0 } image ? image : c.ThumbnailData is { Length: > 0 } thumb ? thumb : null : null,
            Groups = groups,
        };
    }

    /// <summary>ASync's model as a framework contact to add (no note: see <see cref="CNContactData"/>; no starred flag: iOS has favourites elsewhere).</summary>
    public static CNContactData FromContact(Contact c)
    {
        var n = c.Name ?? new StructuredName();
        var org = c.Organizations.FirstOrDefault();
        CNDate? birthday = null;
        var dates = new List<CNLabeled<CNDate>>();
        foreach (var d in c.Dates)
        {
            if (ParseDate(d.Date) is not { } date) continue;
            if (d.Kind == "birthday" && birthday is null) birthday = date;
            else if (d.Kind == "anniversary") dates.Add(new CNLabeled<CNDate>(SystemLabel("Anniversary"), date));
            else dates.Add(new CNLabeled<CNDate>(ToLabel("", d.Label is "Other" or null ? null : d.Label, LabelKind.Generic), date));
        }
        var im = new List<CNLabeled<CNInstantMessage>>();
        var social = new List<CNLabeled<CNSocial>>();
        foreach (var m in c.Messaging)
        {
            if (m.Type.StartsWith(SocialPrefix, StringComparison.OrdinalIgnoreCase))
                social.Add(new CNLabeled<CNSocial>(null, new CNSocial(null, m.Value, null, m.Type[SocialPrefix.Length..])));
            else im.Add(new CNLabeled<CNInstantMessage>(m.Label is { Length: > 0 } ? ToLabel("", m.Label, LabelKind.Generic) : null, new CNInstantMessage(m.Value, ImService(m.Type))));
        }
        foreach (var s in c.Sip) im.Add(new CNLabeled<CNInstantMessage>(null, new CNInstantMessage(s.Value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ? s.Value[4..] : s.Value, "SIP")));
        var nameless = n.Family is null && n.Given is null && n.Middle is null && org?.Company is { Length: > 0 };
        // A card with no structured name (another program's, or one whose FN is all there is) keeps its display name as the given name.
        var given = n.Given ?? (nameless || n.Family is not null ? null : c.DisplayName);
        return new CNContactData
        {
            Id = "", IsOrganization = nameless, NamePrefix = n.Prefix, GivenName = given, MiddleName = n.Middle, FamilyName = n.Family, NameSuffix = n.Suffix,
            Nickname = c.Nicknames.FirstOrDefault(), PhoneticGivenName = n.PhoneticGiven, PhoneticMiddleName = n.PhoneticMiddle, PhoneticFamilyName = n.PhoneticFamily,
            OrganizationName = org?.Company, DepartmentName = org?.Department, JobTitle = org?.Title,
            Phones = [.. c.Phones.Select(p => new CNLabeled<string>(ToLabel(p.Type, p.Label, LabelKind.Phone), p.Value))],
            Emails = [.. c.Emails.Select(e => new CNLabeled<string>(ToLabel(e.Type, e.Label, LabelKind.Generic), e.Value))],
            Urls = [.. c.Websites.Select(u => new CNLabeled<string>(ToLabel(u.Type, u.Label, LabelKind.Generic), u.Value))],
            Addresses = [.. c.Addresses.Select(a => new CNLabeled<CNAddress>(ToLabel(a.Type, a.Label, LabelKind.Generic),
                new CNAddress(string.Join("\n", new[] { a.Street, a.PoBox }.Where(s => s is { Length: > 0 })) is { Length: > 0 } street ? street : null,
                    a.Neighborhood, a.City, null, a.Region, a.PostalCode, a.Country)))],
            Birthday = birthday, Dates = dates,
            Relations = [.. c.Relations.Select(r => new CNLabeled<string>(ToLabel(r.Type, r.Label, LabelKind.Relation), r.Value))],
            InstantMessages = im, SocialProfiles = social,
            ImageData = c.Photo,
        };
    }

    /// <summary>
    /// A contact's version: SHA-256 of its card without the photo, and of the thumbnail (which iOS makes again whenever the photo or
    /// its crop changes). iOS keeps no per-contact version or modification time; this one changes exactly when the backup's file would.
    /// </summary>
    public static string Version(Contact withoutPhoto, byte[]? thumbnail)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(VCard.Write(withoutPhoto with { Photo = null }));
        hash.AppendData("\0photo:"u8);
        if (thumbnail is { Length: > 0 }) hash.AppendData(SHA256.HashData(thumbnail));
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..32];
    }
}

/// <summary>
/// When each contact's version was first seen (<c>ios-contact-versions.json</c>): its time in the tree, so a changed contact has a new
/// time and size together (the backup reads it) and an unchanged one keeps both (taken from the parent snapshot without reading its
/// photo). Losing the file costs one full read.
/// </summary>
public sealed class ContactVersionStore(string? path, Func<long>? now = null)
{
    private readonly Lock _gate = new();
    private Dictionary<string, (string Version, long Seen)>? _entries;
    private readonly Func<long> _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private Dictionary<string, (string Version, long Seen)> Entries
    {
        get
        {
            if (_entries is not null) return _entries;
            _entries = new(StringComparer.Ordinal);
            if (path is null || !File.Exists(path)) return _entries;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 2)
                        _entries[p.Name] = (p.Value[0].GetString() ?? "", p.Value[1].GetInt64());
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException or FormatException) { }
            return _entries;
        }
    }

    /// <summary>The time the contact got this version (now when it is new or changed).</summary>
    public long Seen(string id, string version)
    {
        lock (_gate)
        {
            if (Entries.TryGetValue(id, out var e) && e.Version == version) return e.Seen;
            var t = _now();
            Entries[id] = (version, t);
            return t;
        }
    }

    /// <summary>The key of a contact: its container and identifier (a listing of some containers forgets only their gone contacts).</summary>
    public static string Key(string containerId, string id) => $"{containerId}|{id}";

    /// <summary>Forgets the contacts of <paramref name="containers"/> that were not <paramref name="listed"/> and writes the file.</summary>
    public void Save(IReadOnlySet<string> listed, IReadOnlySet<string> containers)
    {
        if (path is null) return;
        lock (_gate)
        {
            foreach (var key in Entries.Keys.Where(k => !listed.Contains(k) && containers.Contains(k[..Math.Max(0, k.IndexOf('|'))])).ToList()) Entries.Remove(key);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var buffer = new MemoryStream();
                using (var w = new Utf8JsonWriter(buffer))
                {
                    w.WriteStartObject();
                    foreach (var (id, (version, seen)) in Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        w.WriteStartArray(id);
                        w.WriteStringValue(version);
                        w.WriteNumberValue(seen);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, buffer.ToArray());
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// The iPhone's contacts as an <see cref="IContactsSource"/> (task 24): containers are the accounts, CNContacts the contacts (each in one
/// container, as the framework keeps them: not the unified card the Contacts app shows), CNGroups the CATEGORIES. A contact's version is
/// derived from its content (<see cref="IosContactMapping.Version"/>), so an unchanged contact's photo is never read again.
/// </summary>
public sealed class IosContactsSource(ICNContacts store, string? versionsPath = null) : IContactsSource
{
    private readonly ContactVersionStore _versions = new(versionsPath);
    private readonly Lock _gate = new();
    private Dictionary<string, List<string>>? _groupsByContact;
    private Dictionary<string, List<CNGroupData>>? _groupsByContainer;

    public MediaAccess Access => IosContactMapping.Access(store.Authorization);

    /// <summary>One permission covers reading and adding (also with limited access: new contacts join the shared ones).</summary>
    public bool CanWrite => Access is MediaAccess.Full or MediaAccess.Partial;

    private void Need()
    {
        if (Access is MediaAccess.None or MediaAccess.Unsupported) throw new UnauthorizedAccessException("ASync may not read the contacts on this iPhone.");
    }

    public IReadOnlyList<ContactAccount> Accounts()
    {
        Need();
        return [.. store.Containers().Select(c => IosContactMapping.Account(c, store.ContactIds(c.Id).Count)).OrderBy(a => a.Id, StringComparer.Ordinal)];
    }

    private Dictionary<string, List<string>> GroupsByContact(bool refresh)
    {
        lock (_gate)
        {
            if (_groupsByContact is not null && !refresh) return _groupsByContact;
            var byContact = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var byContainer = new Dictionary<string, List<CNGroupData>>(StringComparer.Ordinal);
            foreach (var g in store.Groups().OrderBy(g => g.Name, StringComparer.Ordinal).ThenBy(g => g.Id, StringComparer.Ordinal))
            {
                (byContainer.TryGetValue(g.ContainerId, out var list) ? list : byContainer[g.ContainerId] = []).Add(g);
                if (g.Name.Trim().Length == 0) continue;
                foreach (var m in g.Members)
                {
                    var titles = byContact.TryGetValue(m, out var t) ? t : byContact[m] = [];
                    if (!titles.Contains(g.Name)) titles.Add(g.Name);
                }
            }
            _groupsByContainer = byContainer;
            return _groupsByContact = byContact;
        }
    }

    public IReadOnlyList<ContactSummary> Contacts(IReadOnlySet<string>? accountIds = null)
    {
        Need();
        var groups = GroupsByContact(refresh: true);
        var containers = store.Containers().Where(c => accountIds is null || accountIds.Contains(c.Id)).ToList();
        var result = new List<ContactSummary>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in containers)
            foreach (var c in store.Contacts(container.Id))
            {
                var data = c with { ContainerId = container.Id };
                var contact = IosContactMapping.ToContact(data, groups.GetValueOrDefault(c.Id) ?? [], withPhoto: false);
                var version = IosContactMapping.Version(contact, c.ThumbnailData);
                var key = ContactVersionStore.Key(container.Id, c.Id);
                listed.Add(key);
                result.Add(new ContactSummary(c.Id, contact.ShortId, container.Id, VCard.FormattedName(contact), version, _versions.Seen(key, version)));
            }
        _versions.Save(listed, containers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal));
        return result;
    }

    public Contact? Read(string id, bool withPhoto = true)
    {
        Need();
        if (store.Read(id, withPhoto) is not { } c) return null;
        return IosContactMapping.ToContact(c, GroupsByContact(refresh: false).GetValueOrDefault(id) ?? [], withPhoto);
    }

    /// <summary>
    /// Adds the contact to the account's container (CNSaveRequest), then puts it into its groups by title, making a group the
    /// container does not have yet (Exchange containers have no groups: their CATEGORIES stay in the file only). Never changes or
    /// deletes another contact.
    /// </summary>
    public Task<string> InsertAsync(ContactAccount account, Contact contact, CancellationToken ct) => Task.Run(() =>
    {
        if (!CanWrite) throw new UnauthorizedAccessException("ASync may not add contacts.");
        var id = store.Add(IosContactMapping.FromContact(contact), account.Id);
        if (contact.Groups.Count > 0 && account.Type != "exchange" && id.Length > 0)
        {
            GroupsByContact(refresh: false);
            foreach (var title in contact.Groups.Where(t => t.Trim().Length > 0).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    string groupId;
                    lock (_gate)
                    {
                        var list = _groupsByContainer!.TryGetValue(account.Id, out var l) ? l : _groupsByContainer[account.Id] = [];
                        if (list.FirstOrDefault(g => string.Equals(g.Name, title, StringComparison.Ordinal)) is { } found) groupId = found.Id;
                        else
                        {
                            groupId = store.AddGroup(title, account.Id);
                            list.Add(new CNGroupData(groupId, title, account.Id, []));
                        }
                    }
                    store.AddMember(groupId, id);
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException)
                {
                    Console.Error.WriteLine($"ASync: the group \"{title}\" of a restored contact: {e.Message}");
                }
            }
        }
        return id;
    }, ct);
}
