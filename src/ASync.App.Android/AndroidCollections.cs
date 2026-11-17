using System.Globalization;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Provider;
using Android.Runtime;
using Android.Util;
using AndroidX.Work;
using ASync.App.Services;
using ASync.Client.Core.Background;
using ASync.Client.Core.Collections;
using ASync.Contracts;
using Java.Util.Concurrent;
using Uri = Android.Net.Uri;

namespace ASync.App.Android;

/// <summary>
/// The permissions for contacts and calendars (task 20): READ_CONTACTS / READ_CALENDAR to back up, WRITE_CONTACTS / WRITE_CALENDAR asked
/// only when a restore needs them. Each request has its own code so <see cref="MainActivity.OnRequestPermissionsResult"/> knows whom to tell.
/// </summary>
public sealed class AndroidCollectionAccess : ICollectionAccess
{
    public static readonly AndroidCollectionAccess Contacts = new(FolderKind.Contacts, Manifest.Permission.ReadContacts, Manifest.Permission.WriteContacts, 4);
    public static readonly AndroidCollectionAccess Calendars = new(FolderKind.Calendars, Manifest.Permission.ReadCalendar, Manifest.Permission.WriteCalendar, 6);

    private readonly string _read, _write;

    private AndroidCollectionAccess(FolderKind kind, string read, string write, int requestCode)
    {
        Kind = kind;
        _read = read;
        _write = write;
        ReadCode = requestCode;
    }

    public FolderKind Kind { get; }
    public int ReadCode { get; }
    public int WriteCode => ReadCode + 1;

    private static Context Context => global::Android.App.Application.Context;
    private static bool Granted(string permission) => Context.CheckSelfPermission(permission) == Permission.Granted;

    public MediaAccess Access => Granted(_read) ? MediaAccess.Full : MediaAccess.None;
    public bool CanWrite => Granted(_write);

    public void RequestRead() => Ask([_read], ReadCode);
    public void RequestWrite() => Ask([_read, _write], WriteCode);

    private static void Ask(string[] permissions, int code)
    {
        if (MainActivity.Current is not { } activity) return;
        activity.RunOnUiThread(() => activity.RequestPermissions(permissions, code));
    }

    public void OpenSettings()
    {
        var intent = new Intent(Settings.ActionApplicationDetailsSettings, Uri.Parse($"package:{Context.PackageName}"));
        intent.AddFlags(ActivityFlags.NewTask);
        Context.StartActivity(intent);
    }

    public event Action? Changed;

    public void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Log.Warn("ASync", $"A {Kind} permission listener failed: {e.Message}"); }
        BackgroundWork.EnsureCollectionTriggers();
    }

    /// <summary>Tells the right one about a permission dialog's answer.</summary>
    public static void OnResult(int requestCode)
    {
        foreach (var a in new[] { Contacts, Calendars })
            if (requestCode == a.ReadCode || requestCode == a.WriteCode) a.NotifyChanged();
    }
}

internal static class Cursors
{
    public static string? Str(this ICursor c, int i) => i < 0 || c.IsNull(i) ? null : c.GetString(i);
    public static long? Long(this ICursor c, int i) => i < 0 || c.IsNull(i) ? null : c.GetLong(i);
    public static int? Int(this ICursor c, int i) => i < 0 || c.IsNull(i) ? null : c.GetInt(i);

    public static string S(long v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Android's contacts (ContactsContract) as an <see cref="IContactsSource"/>. A contact is a raw contact (one account's record, not the
/// merged contact the Contacts app shows), so each account's contacts are backed up and restored on their own. Accounts are the
/// (account_type, account_name) pairs of the raw contacts; contacts in no account are "Phone" (id <c>local</c>). Ids are
/// <c>android:raw:&lt;_id&gt;</c>; the raw contact's VERSION (bumped by the provider on any change, its photo included) is the version
/// that lets a backup skip unchanged contacts. Data rows map to vCard fields both ways (types to TYPE, custom labels to X-ABLABEL).
/// </summary>
public sealed class AndroidContactsSource(Context context) : IContactsSource
{
    public const string Source = "android-contacts";
    private const string Local = "local";

    private ContentResolver Resolver => context.ContentResolver!;

    public MediaAccess Access => AndroidCollectionAccess.Contacts.Access;
    public bool CanWrite => AndroidCollectionAccess.Contacts.CanWrite;

    private void Need()
    {
        if (Access is MediaAccess.None) throw new UnauthorizedAccessException("ASync may not read the contacts on this phone.");
    }

    private static string AccountId(string? type, string? name) => type is null && name is null ? Local : $"{type}:{name}";

    private static string TypeLabel(string? type) => type switch
    {
        null => "Phone",
        "com.google" => "Google",
        "com.android.exchange" or "com.google.android.gm.exchange" => "Exchange",
        "com.android.localphone" or "vnd.sec.contact.phone" or "com.local.contacts" => "Phone",
        var t when t.Contains("sim", StringComparison.OrdinalIgnoreCase) => "SIM",
        "com.whatsapp" => "WhatsApp",
        "org.telegram.messenger" => "Telegram",
        "com.viber.voip" => "Viber",
        "com.skype.raider" or "com.skype.contacts.sync" => "Skype",
        var t => t[(t.LastIndexOf('.') + 1)..] is { Length: > 0 } last ? char.ToUpperInvariant(last[0]) + last[1..] : t,
    };

    /// <summary>Messaging apps keep their own read-only copies: they take no new contacts.</summary>
    private static bool ReadOnlyType(string? type) =>
        type is "com.whatsapp" or "org.telegram.messenger" or "com.viber.voip" or "com.skype.raider" or "com.skype.contacts.sync" or "com.facebook.auth.login"
            or "com.linkedin.android" or "com.discord";

    public IReadOnlyList<ContactAccount> Accounts()
    {
        Need();
        var counts = new Dictionary<(string? Type, string? Name), int>();
        using (var c = Resolver.Query(ContactsContract.RawContacts.ContentUri!, ["account_type", "account_name"], "deleted=0", null, null))
            while (c is not null && c.MoveToNext())
            {
                var key = (c.Str(0), c.Str(1));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        // Accounts that hold no contacts (any more) can still take restored ones: their sync adapters keep groups (Google's "My
        // Contacts", "Starred in Android") although every contact was deleted; and the Google accounts the app may see.
        using (var g = Resolver.Query(ContactsContract.Groups.ContentUri!, ["account_type", "account_name"], "deleted=0", null, null))
            while (g is not null && g.MoveToNext())
                if (g.Str(0) is not null || g.Str(1) is not null) counts.TryAdd((g.Str(0), g.Str(1)), 0);
        try
        {
            foreach (var a in global::Android.Accounts.AccountManager.Get(context)!.GetAccounts())
                if (a.Type == "com.google") counts.TryAdd((a.Type, a.Name), 0);
        }
        catch (Java.Lang.SecurityException) { }
        counts.TryAdd((null, null), 0); // the phone itself always takes contacts
        return [.. counts.Select(kv =>
        {
            var id = AccountId(kv.Key.Type, kv.Key.Name);
            var label = TypeLabel(kv.Key.Type);
            var name = kv.Key.Name is { Length: > 0 } n ? n : label;
            return new ContactAccount(id, CollectionLayout.HashId(id), name, kv.Key.Type ?? "", label, kv.Value, ReadOnlyType(kv.Key.Type));
        }).OrderBy(a => a.Id, StringComparer.Ordinal)];
    }

    public IReadOnlyList<ContactSummary> Contacts(IReadOnlySet<string>? accountIds = null)
    {
        Need();
        var updated = new Dictionary<long, long>();
        using (var c = Resolver.Query(ContactsContract.Contacts.ContentUri!, ["_id", "contact_last_updated_timestamp"], null, null, null))
            while (c is not null && c.MoveToNext()) updated[c.GetLong(0)] = c.Long(1) ?? 0;
        var result = new List<ContactSummary>();
        using var r = Resolver.Query(ContactsContract.RawContacts.ContentUri!,
            ["_id", "account_type", "account_name", "version", "contact_id", "display_name"], "deleted=0", null, "_id");
        while (r is not null && r.MoveToNext())
        {
            var account = AccountId(r.Str(1), r.Str(2));
            if (accountIds is not null && !accountIds.Contains(account)) continue;
            var id = r.GetLong(0);
            result.Add(new ContactSummary($"android:raw:{Cursors.S(id)}", Cursors.S(id), account, r.Str(5), Cursors.S(r.Long(3) ?? 0),
                r.Long(4) is { } contact ? updated.GetValueOrDefault(contact) : 0));
        }
        return result;
    }

    private static long? RawId(string id) =>
        id.StartsWith("android:raw:", StringComparison.Ordinal) && long.TryParse(id["android:raw:".Length..], CultureInfo.InvariantCulture, out var v) ? v : null;

    // ── Android's data kinds ↔ vCard ────────────────────────────────────────────

    private static readonly Dictionary<int, (string Type, string? Label)> PhoneTypes = new()
    {
        [1] = ("home", null), [2] = ("cell", null), [3] = ("work", null), [4] = ("work,fax", null), [5] = ("home,fax", null), [6] = ("pager", null),
        [7] = ("", null), [8] = ("", "Callback"), [9] = ("", "Car"), [10] = ("work", "Company main"), [11] = ("", "ISDN"), [12] = ("", "Main"),
        [13] = ("fax", null), [14] = ("", "Radio"), [15] = ("", "Telex"), [16] = ("textphone", null), [17] = ("work,cell", null),
        [18] = ("work,pager", null), [19] = ("", "Assistant"), [20] = ("text", null),
    };

    private static readonly Dictionary<int, (string Type, string? Label)> EmailTypes = new() { [1] = ("home", null), [2] = ("work", null), [3] = ("", null), [4] = ("", "Mobile") };
    private static readonly Dictionary<int, (string Type, string? Label)> PostalTypes = new() { [1] = ("home", null), [2] = ("work", null), [3] = ("", null) };
    private static readonly Dictionary<int, (string Type, string? Label)> WebTypes = new()
    {
        [1] = ("", "Homepage"), [2] = ("", "Blog"), [3] = ("", "Profile"), [4] = ("home", null), [5] = ("work", null), [6] = ("", "FTP"), [7] = ("", null),
    };
    private static readonly Dictionary<int, (string Type, string? Label)> RelationTypes = new()
    {
        [1] = ("", "Assistant"), [2] = ("", "Brother"), [3] = ("child", null), [4] = ("", "Domestic partner"), [5] = ("", "Father"), [6] = ("friend", null),
        [7] = ("", "Manager"), [8] = ("", "Mother"), [9] = ("parent", null), [10] = ("", "Partner"), [11] = ("", "Referred by"), [12] = ("kin", null),
        [13] = ("", "Sister"), [14] = ("spouse", null),
    };
    private static readonly Dictionary<int, string> ImSchemes = new()
    {
        [0] = "aim", [1] = "msnim", [2] = "ymsgr", [3] = "skype", [4] = "qq", [5] = "xmpp", [6] = "icq", [7] = "xmpp", [8] = "x-netmeeting",
    };

    private static LabeledValue Labeled(string value, int? type, string? custom, Dictionary<int, (string Type, string? Label)> types, bool primary) =>
        type is 0 or null ? new LabeledValue(value, "", custom is { Length: > 0 } ? custom : null, primary)
        : types.TryGetValue(type.Value, out var t) ? new LabeledValue(value, t.Type, t.Label, primary)
        : new LabeledValue(value, "", null, primary);

    /// <summary>The Android type for a vCard TYPE and label (0 = custom, with the label).</summary>
    private static (int Type, string? Label) AndroidType(string type, string? label, Dictionary<int, (string Type, string? Label)> types, int other)
    {
        var norm = string.Join(',', type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal));
        foreach (var (k, v) in types)
        {
            var vn = string.Join(',', v.Type.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
            if (vn == norm && string.Equals(v.Label, label, StringComparison.OrdinalIgnoreCase)) return (k, null);
        }
        if (label is { Length: > 0 }) return (0, label);
        foreach (var (k, v) in types)
            if (string.Join(',', v.Type.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)) == norm && v.Label is null) return (k, null);
        return (other, null);
    }

    public Contact? Read(string id, bool withPhoto = true)
    {
        Need();
        if (RawId(id) is not { } raw) return null;
        string? accountType = null, accountName = null, sourceId = null;
        var starred = false;
        using (var r = Resolver.Query(ContactsContract.RawContacts.ContentUri!, ["account_type", "account_name", "sourceid", "starred", "deleted"],
                   "_id=?", [Cursors.S(raw)], null))
        {
            if (r is null || !r.MoveToFirst() || r.GetInt(4) != 0) return null;
            (accountType, accountName, sourceId, starred) = (r.Str(0), r.Str(1), r.Str(2), r.GetInt(3) != 0);
        }
        var groups = new Dictionary<long, string>();
        using (var g = Resolver.Query(ContactsContract.Groups.ContentUri!, ["_id", "title", "system_id"], "deleted=0", null, null))
            while (g is not null && g.MoveToNext())
                if (g.Str(2) is null && g.Str(1) is { Length: > 0 } title) groups[g.GetLong(0)] = title; // not "My Contacts" and the like

        StructuredName? name = null;
        string? displayName = null, note = null;
        byte[]? photo = null;
        long? photoFile = null;
        List<string> nicks = [], groupTitles = [];
        List<LabeledValue> phones = [], emails = [], webs = [], ims = [], sips = [], relations = [];
        List<PostalAddress> addresses = [];
        List<Organization> orgs = [];
        List<ContactDate> dates = [];
        string[] projection = ["mimetype", "data1", "data2", "data3", "data4", "data5", "data6", "data7", "data8", "data9", "data10", "data14", "is_primary", "_id"];
        using (var d = Resolver.Query(ContactsContract.Data.ContentUri!, [.. projection, "data15"], "raw_contact_id=?", [Cursors.S(raw)], "_id"))
        {
            while (d is not null && d.MoveToNext())
            {
                string? S(int i) => d.Str(i);
                var primary = d.GetInt(12) != 0;
                switch (S(0))
                {
                    case "vnd.android.cursor.item/name":
                        displayName = S(1);
                        name = new StructuredName(S(3), S(2), S(5), S(4), S(6), S(9), S(7), S(8));
                        break;
                    case "vnd.android.cursor.item/nickname": if (S(1) is { Length: > 0 } nick) nicks.Add(nick); break;
                    case "vnd.android.cursor.item/phone_v2": if (S(1) is { Length: > 0 } number) phones.Add(Labeled(number, d.Int(2), S(3), PhoneTypes, primary)); break;
                    case "vnd.android.cursor.item/email_v2": if (S(1) is { Length: > 0 } email) emails.Add(Labeled(email, d.Int(2), S(3), EmailTypes, primary)); break;
                    case "vnd.android.cursor.item/website": if (S(1) is { Length: > 0 } url) webs.Add(Labeled(url, d.Int(2), S(3), WebTypes, primary)); break;
                    case "vnd.android.cursor.item/sip_address": if (S(1) is { Length: > 0 } sip) sips.Add(Labeled(sip, d.Int(2), S(3), EmailTypes, primary)); break;
                    case "vnd.android.cursor.item/relation": if (S(1) is { Length: > 0 } rel) relations.Add(Labeled(rel, d.Int(2), S(3), RelationTypes, primary)); break;
                    case "vnd.android.cursor.item/im":
                        if (S(1) is not { Length: > 0 } handle) break;
                        var protocol = d.Int(5);
                        var scheme = protocol is { } p && ImSchemes.TryGetValue(p, out var known) ? known
                            : S(6) is { Length: > 0 } customProtocol ? new string(customProtocol.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()) : "x-im";
                        ims.Add(new LabeledValue(handle, scheme.Length > 0 ? scheme : "x-im", d.Int(2) == 0 ? S(3) : null, primary));
                        break;
                    case "vnd.android.cursor.item/postal-address_v2":
                        var t = Labeled("", d.Int(2), S(3), PostalTypes, primary);
                        addresses.Add(new PostalAddress(S(4), S(5), S(6), S(7), S(8), S(9), S(10), S(1), t.Type, t.Label, primary));
                        break;
                    case "vnd.android.cursor.item/organization":
                        var ot = d.Int(2) switch { 1 => ("work", (string?)null), 0 => ("", S(3)), _ => ("", null) };
                        orgs.Add(new Organization(S(1), S(5), S(4), ot.Item1, ot.Item2));
                        break;
                    case "vnd.android.cursor.item/contact_event":
                        if (S(1) is not { Length: > 0 } date) break;
                        dates.Add(d.Int(2) switch
                        {
                            3 => new ContactDate(date, "birthday"),
                            1 => new ContactDate(date, "anniversary"),
                            0 => new ContactDate(date, "other", S(3)),
                            _ => new ContactDate(date, "other", "Other"),
                        });
                        break;
                    case "vnd.android.cursor.item/note": if (S(1) is { Length: > 0 } text) note = text; break;
                    case "vnd.android.cursor.item/group_membership":
                        if (long.TryParse(S(1), CultureInfo.InvariantCulture, out var gid) && groups.TryGetValue(gid, out var gt)) groupTitles.Add(gt);
                        break;
                    case "vnd.android.cursor.item/photo":
                        if (!withPhoto) break;
                        photoFile = d.Long(11);
                        if (!d.IsNull(14)) photo = d.GetBlob(14);
                        break;
                }
            }
        }
        if (withPhoto && photoFile is { } file)
        {
            try
            {
                using var fd = Resolver.OpenAssetFileDescriptor(Uri.WithAppendedPath(ContactsContract.DisplayPhoto.ContentUri, Cursors.S(file))!, "r");
                using var input = fd?.CreateInputStream();
                if (input is not null)
                {
                    using var buffer = new MemoryStream();
                    input.CopyTo(buffer);
                    if (buffer.Length > 0) photo = buffer.ToArray();
                }
            }
            catch (Exception e) when (e is Java.IO.IOException or IOException or Java.Lang.SecurityException) { } // the thumbnail then
        }
        return new Contact
        {
            Id = id, ShortId = Cursors.S(raw), AccountId = AccountId(accountType, accountName), DisplayName = displayName, Name = name,
            Nicknames = nicks, Phones = phones, Emails = emails, Addresses = addresses, Organizations = orgs, Websites = webs, Messaging = ims, Sip = sips,
            Relations = relations, Dates = dates, Note = note, Photo = photo is { Length: > 0 } ? photo : null, Groups = groupTitles, Starred = starred,
            SourceId = sourceId,
        };
    }

    public Task<string> InsertAsync(ContactAccount account, Contact c, CancellationToken ct) => Task.Run(() =>
    {
        if (!CanWrite) throw new UnauthorizedAccessException("ASync may not add contacts.");
        var ops = new List<ContentProviderOperation>();
        var rawValues = new ContentValues();
        if (account.Id == Local)
        {
            rawValues.PutNull("account_type");
            rawValues.PutNull("account_name");
        }
        else
        {
            rawValues.Put("account_type", account.Type);
            rawValues.Put("account_name", account.Name);
        }
        if (c.Starred) rawValues.Put("starred", 1);
        ops.Add(ContentProviderOperation.NewInsert(ContactsContract.RawContacts.ContentUri!)!.WithValues(rawValues)!.Build()!);
        void Row(string mime, Action<ContentValues> fill)
        {
            var v = new ContentValues();
            v.Put("mimetype", mime);
            fill(v);
            ops.Add(ContentProviderOperation.NewInsert(ContactsContract.Data.ContentUri!)!.WithValueBackReference("raw_contact_id", 0)!.WithValues(v)!.Build()!);
        }
        void Put(ContentValues v, string key, string? value) { if (value is { Length: > 0 }) v.Put(key, value); }
        var n = c.Name ?? new StructuredName();
        Row("vnd.android.cursor.item/name", v =>
        {
            Put(v, "data1", c.DisplayName ?? VCard.FormattedName(c));
            Put(v, "data2", n.Given); Put(v, "data3", n.Family); Put(v, "data4", n.Prefix); Put(v, "data5", n.Middle); Put(v, "data6", n.Suffix);
            Put(v, "data7", n.PhoneticGiven); Put(v, "data8", n.PhoneticMiddle); Put(v, "data9", n.PhoneticFamily);
        });
        foreach (var nick in c.Nicknames) Row("vnd.android.cursor.item/nickname", v => v.Put("data1", nick));
        void Typed(string mime, LabeledValue lv, Dictionary<int, (string Type, string? Label)> types, int other)
        {
            var (type, label) = AndroidType(lv.Type, lv.Label, types, other);
            Row(mime, v =>
            {
                v.Put("data1", lv.Value);
                v.Put("data2", type);
                Put(v, "data3", label);
                if (lv.Preferred) v.Put("is_primary", 1);
            });
        }
        foreach (var p in c.Phones) Typed("vnd.android.cursor.item/phone_v2", p, PhoneTypes, 7);
        foreach (var e in c.Emails) Typed("vnd.android.cursor.item/email_v2", e, EmailTypes, 3);
        foreach (var w in c.Websites) Typed("vnd.android.cursor.item/website", w, WebTypes, 7);
        foreach (var s in c.Sip) Typed("vnd.android.cursor.item/sip_address", s with { Value = s.Value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ? s.Value[4..] : s.Value }, EmailTypes, 3);
        foreach (var r in c.Relations) Typed("vnd.android.cursor.item/relation", r, RelationTypes, 0);
        foreach (var im in c.Messaging)
            Row("vnd.android.cursor.item/im", v =>
            {
                v.Put("data1", im.Value);
                v.Put("data2", im.Label is { Length: > 0 } ? 0 : 3);
                Put(v, "data3", im.Label);
                var known = ImSchemes.Where(kv => kv.Value == im.Type).Select(kv => (int?)kv.Key).FirstOrDefault();
                if (im.Type == "xmpp") known = 7;
                v.Put("data5", known ?? -1);
                if (known is null) v.Put("data6", im.Type);
            });
        foreach (var a in c.Addresses)
            Row("vnd.android.cursor.item/postal-address_v2", v =>
            {
                var (type, label) = AndroidType(a.Type, a.Label, PostalTypes, 3);
                v.Put("data2", type);
                Put(v, "data3", label);
                Put(v, "data1", a.Formatted); Put(v, "data4", a.Street); Put(v, "data5", a.PoBox); Put(v, "data6", a.Neighborhood); Put(v, "data7", a.City);
                Put(v, "data8", a.Region); Put(v, "data9", a.PostalCode); Put(v, "data10", a.Country);
                if (a.Preferred) v.Put("is_primary", 1);
            });
        foreach (var o in c.Organizations)
            Row("vnd.android.cursor.item/organization", v =>
            {
                v.Put("data2", o.Label is { Length: > 0 } ? 0 : o.Type.Contains("work", StringComparison.Ordinal) || o.Type.Length == 0 ? 1 : 2);
                Put(v, "data3", o.Label); Put(v, "data1", o.Company); Put(v, "data4", o.Title); Put(v, "data5", o.Department);
            });
        foreach (var d in c.Dates)
            Row("vnd.android.cursor.item/contact_event", v =>
            {
                v.Put("data1", d.Date);
                v.Put("data2", d.Kind switch { "birthday" => 3, "anniversary" => 1, _ => d.Label is { Length: > 0 } ? 0 : 2 });
                if (d.Kind == "other") Put(v, "data3", d.Label);
            });
        if (c.Note is { Length: > 0 } note) Row("vnd.android.cursor.item/note", v => v.Put("data1", note));
        if (c.Photo is { Length: > 0 } photo) Row("vnd.android.cursor.item/photo", v => v.Put("data15", photo));
        var results = Resolver.ApplyBatch(ContactsContract.Authority, ops);
        var uri = results.FirstOrDefault()?.Uri;
        return uri is null ? "" : $"android:raw:{uri.LastPathSegment}";
    }, ct);
}

/// <summary>
/// Android's calendars (CalendarContract) as an <see cref="ICalendarSource"/>. Events are Events rows; a recurring event's exceptions are
/// the rows whose ORIGINAL_ID is its _id (changed or cancelled occurrences), written with it. Reminders and attendees are their own
/// tables. Android keeps no modification time for events, so a backup always generates them (they are small, and an unchanged one
/// deduplicates). "Events since": LAST_DATE (the end of the last occurrence; null for a never-ending series).
/// </summary>
public sealed class AndroidCalendarSource(Context context) : ICalendarSource
{
    public const string Source = "android-calendar";
    private ContentResolver Resolver => context.ContentResolver!;

    public MediaAccess Access => AndroidCollectionAccess.Calendars.Access;
    public bool CanWrite => AndroidCollectionAccess.Calendars.CanWrite;

    private void Need()
    {
        if (Access is MediaAccess.None) throw new UnauthorizedAccessException("ASync may not read the calendars on this phone.");
    }

    private static string? Color(int? argb) => argb is { } c && c != 0 ? $"#{c & 0xFFFFFF:x6}" : null;

    public IReadOnlyList<CalendarInfo> Calendars()
    {
        Need();
        var counts = new Dictionary<long, int>();
        using (var e = Resolver.Query(CalendarContract.Events.ContentUri!, ["calendar_id"], "deleted=0 AND original_id IS NULL", null, null))
            while (e is not null && e.MoveToNext()) counts[e.GetLong(0)] = counts.GetValueOrDefault(e.GetLong(0)) + 1;
        var list = new List<CalendarInfo>();
        using var c = Resolver.Query(CalendarContract.Calendars.ContentUri!,
            ["_id", "calendar_displayName", "account_name", "account_type", "calendar_color", "calendar_timezone", "calendar_access_level"], "deleted=0", null, "_id");
        while (c is not null && c.MoveToNext())
        {
            var id = c.GetLong(0);
            list.Add(new CalendarInfo(Cursors.S(id), Cursors.S(id), c.Str(1) ?? "Calendar", c.Str(2) ?? "", c.Str(3) ?? "", Color(c.Int(4)),
                counts.GetValueOrDefault(id), c.Str(5), (c.Int(6) ?? 0) < 500)); // below CAL_ACCESS_CONTRIBUTOR: holidays, birthdays, read-only shares
        }
        return list;
    }

    private static readonly string[] EventColumns =
    [
        "_id", "calendar_id", "title", "description", "eventLocation", "dtstart", "dtend", "duration", "allDay", "eventTimezone", "eventEndTimezone",
        "rrule", "rdate", "exrule", "exdate", "eventStatus", "availability", "accessLevel", "organizer", "eventColor", "original_id",
        "originalInstanceTime", "lastDate", "uid2445",
    ];

    private static CalendarEvent Row(ICursor c, Dictionary<long, List<Alarm>> alarms, Dictionary<long, List<Attendee>> attendees)
    {
        var id = c.GetLong(0);
        return new CalendarEvent
        {
            Id = $"android:event:{Cursors.S(id)}", ShortId = Cursors.S(id), CalendarId = Cursors.S(c.GetLong(1)), Uid = c.Str(23),
            Title = c.Str(2), Description = c.Str(3), Location = c.Str(4), Start = c.Long(5) ?? 0, End = c.Long(6), Duration = c.Str(7),
            AllDay = c.Int(8) == 1, TimeZone = c.Str(9), EndTimeZone = c.Str(10), RRule = c.Str(11), RDate = c.Str(12), ExRule = c.Str(13), ExDate = c.Str(14),
            Status = c.Int(15) switch { 0 => "TENTATIVE", 1 => "CONFIRMED", 2 => "CANCELLED", _ => null },
            Transparency = c.Int(16) switch { 0 => "OPAQUE", 1 => "TRANSPARENT", _ => null },
            Class = c.Int(17) switch { 1 => "CONFIDENTIAL", 2 => "PRIVATE", 3 => "PUBLIC", _ => null },
            Organizer = c.Str(18), Color = Color(c.Int(19)), OriginalStart = c.Long(21), LastEnd = c.Long(22),
            Alarms = alarms.GetValueOrDefault(id) ?? [], Attendees = attendees.GetValueOrDefault(id) ?? [],
        };
    }

    public IReadOnlyList<CalendarEvent> Events(IReadOnlySet<string>? calendarIds = null, long? since = null)
    {
        Need();
        var alarms = new Dictionary<long, List<Alarm>>();
        using (var r = Resolver.Query(CalendarContract.Reminders.ContentUri!, ["event_id", "minutes", "method"], null, null, "Reminders._id"))
            while (r is not null && r.MoveToNext())
            {
                var method = r.Int(2) switch { 2 => "email", 3 => "sms", 4 => "alarm", _ => "display" };
                (alarms.TryGetValue(r.GetLong(0), out var l) ? l : alarms[r.GetLong(0)] = []).Add(new Alarm(r.Int(1) ?? 0, method));
            }
        var attendees = new Dictionary<long, List<Attendee>>();
        using (var a = Resolver.Query(CalendarContract.Attendees.ContentUri!,
                   ["event_id", "attendeeName", "attendeeEmail", "attendeeType", "attendeeStatus"], null, null, "Attendees._id"))
            while (a is not null && a.MoveToNext())
            {
                var type = a.Int(3);
                var attendee = new Attendee(a.Str(2), a.Str(1),
                    type switch { 1 => "REQ-PARTICIPANT", 2 => "OPT-PARTICIPANT", _ => "NON-PARTICIPANT" },
                    a.Int(4) switch { 1 => "ACCEPTED", 2 => "DECLINED", 4 => "TENTATIVE", _ => "NEEDS-ACTION" },
                    type == 3 ? "RESOURCE" : "INDIVIDUAL");
                (attendees.TryGetValue(a.GetLong(0), out var l) ? l : attendees[a.GetLong(0)] = []).Add(attendee);
            }
        var where = "deleted=0 AND original_id IS NULL" + (since is { } s ? $" AND (lastDate IS NULL OR lastDate >= {Cursors.S(s)})" : "");
        var masters = new List<CalendarEvent>();
        using (var c = Resolver.Query(CalendarContract.Events.ContentUri!, EventColumns, where, null, "_id"))
            while (c is not null && c.MoveToNext())
            {
                var e = Row(c, alarms, attendees);
                if (calendarIds is null || calendarIds.Contains(e.CalendarId)) masters.Add(e);
            }
        var exceptions = new Dictionary<string, List<CalendarEvent>>(StringComparer.Ordinal);
        using (var x = Resolver.Query(CalendarContract.Events.ContentUri!, EventColumns, "deleted=0 AND original_id IS NOT NULL", null, "_id"))
            while (x is not null && x.MoveToNext())
            {
                var e = Row(x, alarms, attendees);
                var master = $"android:event:{Cursors.S(x.GetLong(20))}";
                (exceptions.TryGetValue(master, out var l) ? l : exceptions[master] = []).Add(e);
            }
        return [.. masters.Select(m => exceptions.TryGetValue(m.Id, out var ex) ? m with { Exceptions = ex } : m)];
    }

    private static ContentValues Values(CalendarEvent e, string calendarId, bool exception)
    {
        var v = new ContentValues();
        void Put(string key, string? value) { if (value is { Length: > 0 }) v.Put(key, value); }
        if (!exception) v.Put("calendar_id", long.Parse(calendarId, CultureInfo.InvariantCulture));
        Put("title", e.Title);
        Put("description", e.Description);
        Put("eventLocation", e.Location);
        v.Put("dtstart", e.Start);
        var tz = e.AllDay ? "UTC" : e.TimeZone ?? "UTC";
        v.Put("eventTimezone", tz);
        if (!exception)
        {
            Put("eventEndTimezone", e.AllDay ? null : e.EndTimeZone);
            v.Put("allDay", e.AllDay ? 1 : 0);
        }
        if (!exception && e.Recurring)
        {
            var seconds = e.End is { } end ? (end - e.Start) / 1000 : ICalendar.ParseDuration(e.Duration) ?? (e.AllDay ? 86400 : 3600);
            v.Put("duration", e.AllDay && seconds % 86400 == 0 ? $"P{seconds / 86400}D" : $"P{seconds}S");
            Put("rrule", e.RRule);
            Put("rdate", e.RDate);
            Put("exrule", e.ExRule);
            Put("exdate", e.ExDate);
        }
        else if (exception)
        {
            // The provider refuses DTEND on an exception ("Exceptions can't overwrite dtend"): its length goes in as a duration.
            var seconds = e.End is { } end ? (end - e.Start) / 1000 : ICalendar.ParseDuration(e.Duration) ?? (e.AllDay ? 86400 : 0);
            v.Put("duration", e.AllDay && seconds % 86400 == 0 ? $"P{seconds / 86400}D" : $"P{seconds}S");
        }
        else v.Put("dtend", e.End ?? e.Start + (ICalendar.ParseDuration(e.Duration) ?? (e.AllDay ? 86400 : 0)) * 1000);
        if (e.Status is { } status) v.Put("eventStatus", status switch { "TENTATIVE" => 0, "CANCELLED" => 2, _ => 1 });
        // An exception takes the rest from its series (the provider copies it); only what an occurrence changes goes in.
        if (exception) return v;
        if (e.Transparency is { } transp) v.Put("availability", transp == "TRANSPARENT" ? 1 : 0);
        if (e.Class is { } cls) v.Put("accessLevel", cls switch { "CONFIDENTIAL" => 1, "PRIVATE" => 2, "PUBLIC" => 3, _ => 0 });
        Put("uid2445", e.Uid);
        if (e.Alarms.Count > 0) v.Put("hasAlarm", 1);
        return v;
    }

    public Task<string> InsertAsync(CalendarInfo calendar, CalendarEvent e, CancellationToken ct) => Task.Run(() =>
    {
        if (!CanWrite) throw new UnauthorizedAccessException("ASync may not add events.");
        var uri = Resolver.Insert(CalendarContract.Events.ContentUri!, Values(e, calendar.Id, false))
                  ?? throw new IOException("The calendar did not take the event.");
        var id = long.Parse(uri.LastPathSegment!, CultureInfo.InvariantCulture);
        Children(id, e);
        var failed = new List<string>();
        foreach (var x in e.Exceptions.Where(x => x.OriginalStart is not null))
        {
            ct.ThrowIfCancellationRequested();
            var v = Values(x, calendar.Id, true);
            v.Put("originalInstanceTime", x.OriginalStart!.Value);
            try
            {
                var exUri = Resolver.Insert(Uri.WithAppendedPath(CalendarContract.Events.ContentExceptionUri, Cursors.S(id))!, v);
                if (exUri?.LastPathSegment is { } exId && long.TryParse(exId, CultureInfo.InvariantCulture, out var exRow)) Children(exRow, x);
            }
            catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.Lang.UnsupportedOperationException or Java.Lang.IllegalStateException)
            {
                Log.Warn("ASync", $"An exception of \"{e.Title}\" was not restored: {ex.Message}");
                failed.Add(ex.Message);
            }
        }
        // The series is in; say what of it is not (the restore lists it, and nothing is deleted again).
        if (failed.Count > 0) throw new IOException($"{failed.Count} changed occurrence(s) were not restored: {failed[0]}");
        return $"android:event:{Cursors.S(id)}";
    }, ct);

    private void Children(long eventId, CalendarEvent e)
    {
        foreach (var a in e.Alarms)
        {
            var v = new ContentValues();
            v.Put("event_id", eventId);
            v.Put("minutes", a.MinutesBefore);
            v.Put("method", a.Method switch { "email" => 2, "sms" => 3, "alarm" => 4, _ => 1 });
            Resolver.Insert(CalendarContract.Reminders.ContentUri!, v);
        }
        foreach (var a in e.Attendees)
        {
            var v = new ContentValues();
            v.Put("event_id", eventId);
            if (a.Name is { Length: > 0 } name) v.Put("attendeeName", name);
            if (a.Email is { Length: > 0 } email) v.Put("attendeeEmail", email);
            v.Put("attendeeType", a.Type == "RESOURCE" ? 3 : a.Role switch { "REQ-PARTICIPANT" => 1, "OPT-PARTICIPANT" => 2, _ => 0 });
            v.Put("attendeeStatus", a.Status switch { "ACCEPTED" => 1, "DECLINED" => 2, "TENTATIVE" => 4, _ => 3 });
            v.Put("attendeeRelationship", 1);
            Resolver.Insert(CalendarContract.Attendees.ContentUri!, v);
        }
    }
}

/// <summary>
/// Changed contacts or events start a backup (task 20), like the photos' <see cref="MediaTriggerWorker"/>: a one-time WorkManager job with
/// a content-URI trigger on the provider (ContactsContract's or CalendarContract's root, descendants included), a 10-second update delay so
/// an edit (or a sync bringing many) is one run, at most 2 minutes, and the network. It runs every unpaused folder of its kind as an
/// automatic run (their run conditions apply), then schedules the next trigger (a content trigger fires once). Note: a backup only reads,
/// and a restore's inserts trip it once more, which finds them unchanged.
/// </summary>
[Register("ua.pp.chds.async.CollectionTriggerWorker")]
public sealed class CollectionTriggerWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    public static string Name(FolderKind kind) => kind == FolderKind.Contacts ? "contacts-trigger" : "calendars-trigger";
    private readonly CancellationTokenSource _stop = new();

    public static OneTimeWorkRequest Build(FolderKind kind)
    {
        var uri = kind == FolderKind.Contacts ? ContactsContract.AuthorityUri! : CalendarContract.ContentUri!;
        var constraints = new Constraints.Builder()
            .AddContentUriTrigger(uri, true)
            .SetTriggerContentUpdateDelay(10, TimeUnit.Seconds!)
            .SetTriggerContentMaxDelay(120, TimeUnit.Seconds!)
            .SetRequiredNetworkType(NetworkType.Connected!)
            .Build();
        var data = new Data.Builder().PutString("kind", kind.ToString())!.Build();
        return (OneTimeWorkRequest)new OneTimeWorkRequest.Builder(typeof(CollectionTriggerWorker))
            .SetConstraints(constraints).SetInputData(data).AddTag(BackgroundWork.Tag).AddTag(Name(kind)).Build();
    }

    public override Result DoWork()
    {
        var kind = Enum.TryParse<FolderKind>(InputData?.GetString("kind"), out var k) ? k : FolderKind.Contacts;
        var context = ApplicationContext!;
        AndroidAgent.EnsureStarted(context);
        var runtime = AndroidAgent.Runtime;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!runtime.FoldersLoaded && DateTime.UtcNow < until && !_stop.IsCancellationRequested) Thread.Sleep(250);
            var ids = BackgroundPlanner.FoldersOfKind(runtime.Folders.Select(f => f.Folder), kind);
            Log.Info("ASync", $"{kind} changed: backing up {ids.Count} {kind} folder(s).");
            foreach (var id in ids)
            {
                if (_stop.IsCancellationRequested) break;
                var result = runtime.RunAutomaticAsync(id, TimeSpan.FromSeconds(90), _stop.Token).GetAwaiter().GetResult();
                Log.Info("ASync", $"Background run of \"{result.Name}\" ({kind} changed): {result.Message}");
            }
        }
        catch (Exception e) when (e is Agent.Control.ControlException or System.OperationCanceledException)
        {
            Log.Info("ASync", $"{kind} changed: no run ({e.Message}).");
        }
        finally
        {
            BackgroundWork.EnsureCollectionTrigger(kind, next: true);
        }
        return Result.InvokeSuccess()!;
    }

    public override void OnStopped()
    {
        _stop.Cancel();
        base.OnStopped();
    }
}
