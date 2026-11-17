using System.Globalization;
using System.Text.Json;
using Android.Content;
using Android.Provider;
using ASync.Agent;
using ASync.App.ViewModels;
using ASync.Contracts;
using Uri = Android.Net.Uri;

namespace ASync.App.Android;

/// <summary>
/// Tests only (<c>scripts/smoke-android-collections.sh</c>, through <see cref="Automation"/>): contacts and calendars (task 20).
/// UI: <c>pim-dialog &lt;folder&gt;</c>, <c>pim-state</c>, <c>pim-allow</c>, <c>pim-choose all|&lt;name&gt;,…</c>, <c>pim-save</c>,
/// <c>pim-restore-choose &lt;folder&gt;</c>, <c>pim-restore-confirm &lt;folder&gt; [target]</c>, <c>pim-restore-cancel &lt;folder&gt;</c>. Without the UI: <c>bg-pim contacts|calendars</c> (the
/// list, the folders and the restore), <c>bg-pim-set &lt;folder&gt; all|&lt;id&gt;,… [days|all]</c>, <c>bg-pim-restore &lt;folder&gt; &lt;snapshot|-&gt;
/// &lt;target|-&gt;</c>, <c>bg-pim-trigger</c>, and the seed helpers that write the providers directly (not through ASync's own import):
/// <c>bg-pim-seed</c> (two contact accounts, two local calendars, fixed content), <c>bg-pim-edit</c> (Carol's number changes),
/// <c>bg-pim-wipe contacts &lt;account id&gt;|calendar &lt;id&gt;</c>, <c>bg-pim-dump contacts &lt;account id&gt;|calendar &lt;id&gt;</c>.
/// </summary>
internal static class CollectionsAutomation
{
    public const string SmokeAccountType = "ua.pp.chds.async.smoke";
    public const string SmokeAccountName = "smoke@example.com";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── UI ────────────────────────────────────────────────────────────────────

    public static string State(ShellViewModel shell) => shell.CollectionsDialog is not { } d ? "closed" : JsonSerializer.Serialize(new
    {
        kind = d.Kind.ToString(), access = d.Access.ToString(), d.NeedsPermission, d.BackUpAll, past = d.SelectedPast.Days, d.ListText, d.Error,
        choices = d.Choices.Select(c => new { c.Id, c.Name, c.Details, c.IsChecked }),
    }, Json);

    public static async Task<string?> RunAsync(ShellViewModel shell, Func<string, FolderViewModel> folder, string op, string arg)
    {
        CollectionsViewModel Dialog() => shell.CollectionsDialog ?? throw new InvalidOperationException("The Contacts / Calendars screen is not open.");
        switch (op)
        {
            case "pim-dialog":
                await folder(arg).ChooseCollectionsCommand.ExecuteAsync(null);
                return State(shell);
            case "pim-state":
                return State(shell);
            case "pim-allow":
                Dialog().AllowCommand.Execute(null);
                return "asked";
            case "pim-choose":
            {
                var d = Dialog();
                if (arg == "all") d.BackUpAll = true;
                else
                {
                    d.ChooseSome = true;
                    var names = arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    foreach (var c in d.Choices) c.IsChecked = names.Contains(c.Name);
                }
                return State(shell);
            }
            case "pim-save":
                await Dialog().SaveCommand.ExecuteAsync(null);
                return shell.CollectionsDialog is null ? "saved" : State(shell);
            case "pim-restore-choose":
            {
                var card = folder(arg);
                await card.RestorePhotosCommand.ExecuteAsync(null);
                return JsonSerializer.Serialize(new { card.ChoosingRestore, choices = card.RestoreChoices.Select(c => c.Label), targets = card.RestoreTargets.Select(t => t.Label) }, Json);
            }
            case "pim-restore-confirm":
            {
                // pim-restore-confirm <folder> [target index]: Restore, which asks for WRITE first (not awaited: the system's dialog waits for a tap).
                var a = arg.Split(' ');
                var card = folder(a[0]);
                if (a.Length > 1) card.SelectedTarget = card.RestoreTargets[int.Parse(a[1], CultureInfo.InvariantCulture)];
                _ = card.ConfirmRestoreCommand.ExecuteAsync(null);
                return "started";
            }
            case "pim-restore-cancel":
                folder(arg).CancelRestoreCommand.Execute(null);
                return "ok";
            default:
                return null;
        }
    }

    // ── Background ────────────────────────────────────────────────────────────

    public static async Task<string?> BackgroundAsync(Context context, AgentRuntime runtime, string[] parts)
    {
        int Folder() => int.Parse(parts[1], CultureInfo.InvariantCulture);
        switch (parts[0])
        {
            case "bg-pim":
            {
                var kind = parts.Length > 1 && parts[1].StartsWith("cal", StringComparison.Ordinal) ? FolderKind.Calendars : FolderKind.Contacts;
                var list = await runtime.CollectionListAsync(kind, CancellationToken.None);
                var status = await runtime.StatusAsync(CancellationToken.None);
                return JsonSerializer.Serialize(new
                {
                    access = list.Access.ToString(), list.CanWrite, list.Items, list.Problem, collections = list.Collections,
                    restore = status.CollectionRestore,
                    folders = status.Folders.Where(f => f.Kind == kind)
                        .Select(f => new { id = f.FolderId, f.Name, selection = f.Collections?.Describe(kind), counts = f.CollectionCounts, last = f.LastResult?.Message, at = f.LastResult?.At }),
                }, Json);
            }
            case "bg-pim-set":
            {
                var folderId = Folder();
                var kind = (await runtime.StatusAsync(CancellationToken.None)).Folders.Single(f => f.FolderId == folderId).Kind;
                int? days = parts.Length > 3 ? parts[3] == "all" ? null : int.Parse(parts[3], CultureInfo.InvariantCulture) : CollectionLimits.DefaultPastDays;
                if (parts[2] == "all") return await runtime.SetCollectionsAsync(folderId, new CollectionSelectionDto(PastDays: days), CancellationToken.None);
                var list = await runtime.CollectionListAsync(kind, CancellationToken.None);
                var ids = parts[2].Split(',');
                return await runtime.SetCollectionsAsync(folderId, new CollectionSelectionDto(false,
                    [.. list.Collections.Where(c => ids.Contains(c.Id)).Select(c => new CollectionRef(c.Id, c.Name))], days), CancellationToken.None);
            }
            case "bg-pim-restore":
                return await runtime.RestoreCollectionsAsync(Folder(), parts[2] == "-" ? null : parts[2], parts[3] == "-" ? null : parts[3], CancellationToken.None);
            case "bg-pim-trigger":
                BackgroundWork.EnsureCollectionTriggers();
                return "ensured";
            case "bg-pim-seed":
                return Seed(context);
            case "bg-pim-edit":
                return Edit(context);
            case "bg-pim-wipe":
                return Wipe(context, parts[1], parts[2]);
            case "bg-pim-dump":
                return Dump(context, parts[1], parts[2]);
            default:
                return null;
        }
    }

    private static Uri AsSyncAdapter(Uri uri, string name, string type) => uri.BuildUpon()!
        .AppendQueryParameter("caller_is_syncadapter", "true")!.AppendQueryParameter("account_name", name)!.AppendQueryParameter("account_type", type)!.Build()!;

    private static long Insert(ContentResolver r, Uri uri, ContentValues v) =>
        long.Parse((r.Insert(uri, v) ?? throw new IOException($"{uri} took nothing")).LastPathSegment!, CultureInfo.InvariantCulture);

    private static long RawContact(ContentResolver r, string? type, string? name, string given, string family, string phone, string? email, byte[]? photo)
    {
        var raw = new ContentValues();
        if (type is null) { raw.PutNull("account_type"); raw.PutNull("account_name"); }
        else { raw.Put("account_type", type); raw.Put("account_name", name); }
        var id = Insert(r, ContactsContract.RawContacts.ContentUri!, raw);
        void Data(string mime, Action<ContentValues> fill)
        {
            var v = new ContentValues();
            v.Put("raw_contact_id", id);
            v.Put("mimetype", mime);
            fill(v);
            Insert(r, ContactsContract.Data.ContentUri!, v);
        }
        Data("vnd.android.cursor.item/name", v => { v.Put("data1", $"{given} {family}"); v.Put("data2", given); v.Put("data3", family); });
        Data("vnd.android.cursor.item/phone_v2", v => { v.Put("data1", phone); v.Put("data2", 2); });
        if (email is not null) Data("vnd.android.cursor.item/email_v2", v => { v.Put("data1", email); v.Put("data2", 2); });
        if (photo is not null) Data("vnd.android.cursor.item/photo", v => v.Put("data15", photo));
        return id;
    }

    /// <summary>A small JPEG (the photo of a seeded contact): 8×8 pixels, made by Android itself so the provider takes it.</summary>
    private static byte[] Jpeg()
    {
        using var bitmap = global::Android.Graphics.Bitmap.CreateBitmap(8, 8, global::Android.Graphics.Bitmap.Config.Argb8888!)!;
        bitmap.EraseColor(unchecked((int)0xFF8A2BE2));
        using var stream = new MemoryStream();
        bitmap.Compress(global::Android.Graphics.Bitmap.CompressFormat.Jpeg!, 90, stream);
        return stream.ToArray();
    }

    private static long Calendar(ContentResolver r, string name, int color)
    {
        var v = new ContentValues();
        v.Put("account_name", SmokeAccountName);
        v.Put("account_type", CalendarContract.AccountTypeLocal);
        v.Put("name", name);
        v.Put("calendar_displayName", name);
        v.Put("calendar_color", color);
        v.Put("calendar_access_level", 700); // owner
        v.Put("ownerAccount", SmokeAccountName);
        v.Put("visible", 1);
        v.Put("sync_events", 1);
        v.Put("calendar_timezone", "UTC");
        return Insert(r, AsSyncAdapter(CalendarContract.Calendars.ContentUri!, SmokeAccountName, CalendarContract.AccountTypeLocal), v);
    }

    private static long Event(ContentResolver r, long calendar, string title, long start, long? end, string? duration, string tz, bool allDay = false,
        string? rrule = null, string? description = null)
    {
        var v = new ContentValues();
        v.Put("calendar_id", calendar);
        v.Put("title", title);
        if (description is not null) v.Put("description", description);
        v.Put("dtstart", start);
        if (end is { } e) v.Put("dtend", e);
        if (duration is not null) v.Put("duration", duration);
        v.Put("eventTimezone", tz);
        v.Put("allDay", allDay ? 1 : 0);
        if (rrule is not null) v.Put("rrule", rrule);
        return Insert(r, CalendarContract.Events.ContentUri!, v);
    }

    private static long Ms(int y, int mo, int d, int h = 0, int mi = 0) => new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>Fixed contacts in two accounts and events in two local calendars (what the smoke test expects, byte for byte).</summary>
    private static string Seed(Context context)
    {
        var r = context.ContentResolver!;
        // An earlier run's data first (the emulator keeps the providers' data across installs).
        var rawAsAdapter = ContactsContract.RawContacts.ContentUri!.BuildUpon()!.AppendQueryParameter("caller_is_syncadapter", "true")!.Build()!;
        r.Delete(rawAsAdapter, "account_type=?", [SmokeAccountType]);
        r.Delete(rawAsAdapter, "account_type IS NULL AND display_name IN ('Ann Local','Bob Local')", null);
        r.Delete(ContactsContract.Groups.ContentUri!.BuildUpon()!.AppendQueryParameter("caller_is_syncadapter", "true")!.Build()!, "account_type=?", [SmokeAccountType]);
        r.Delete(AsSyncAdapter(CalendarContract.Calendars.ContentUri!, SmokeAccountName, CalendarContract.AccountTypeLocal),
            "account_name=? AND account_type=?", [SmokeAccountName, CalendarContract.AccountTypeLocal]);
        // The test account's own group, as a sync adapter keeps one (Google's "My Contacts"): the account stays listed when wiped.
        var group = new ContentValues();
        group.Put("account_type", SmokeAccountType);
        group.Put("account_name", SmokeAccountName);
        group.Put("title", "Smoke group");
        group.Put("group_visible", 1);
        Insert(r, ContactsContract.Groups.ContentUri!.BuildUpon()!.AppendQueryParameter("caller_is_syncadapter", "true")!.Build()!, group);
        var ann = RawContact(r, null, null, "Ann", "Local", "+1 555 0100", null, null);
        var bob = RawContact(r, null, null, "Bob", "Local", "+1 555 0101", "bob@example.com", null);
        var carol = RawContact(r, SmokeAccountType, SmokeAccountName, "Carol", "Smoke", "+1 555 0200", "carol@example.com", Jpeg());
        var dmytro = RawContact(r, SmokeAccountType, SmokeAccountName, "Дмитро", "Коваль", "+380 44 000 0000", null, null);
        var work = Calendar(r, "Smoke Work", unchecked((int)0xFF3366CC));
        var home = Calendar(r, "Smoke Home", unchecked((int)0xFF33AA55));
        var standup = Event(r, work, "Standup", Ms(2026, 11, 2, 8), null, "P900S", "Europe/Kyiv", rrule: "FREQ=WEEKLY;BYDAY=MO;COUNT=10", description: "Weekly, in Kyiv time");
        var review = Event(r, work, "Review", Ms(2026, 11, 20, 14), Ms(2026, 11, 20, 15), null, "UTC");
        var holiday = Event(r, home, "Holiday", Ms(2026, 12, 25), Ms(2026, 12, 26), null, "UTC", allDay: true);
        // A reminder and a moved occurrence of the standup (an exception).
        var reminder = new ContentValues();
        reminder.Put("event_id", standup);
        reminder.Put("minutes", 10);
        reminder.Put("method", 1);
        Insert(r, CalendarContract.Reminders.ContentUri!, reminder);
        var moved = new ContentValues();
        moved.Put("originalInstanceTime", Ms(2026, 11, 9, 8));
        moved.Put("dtstart", Ms(2026, 11, 9, 9));
        moved.Put("duration", "P900S"); // an exception may not set dtend: its length is a duration
        moved.Put("title", "Standup (moved)");
        moved.Put("eventTimezone", "Europe/Kyiv");
        r.Insert(Uri.WithAppendedPath(CalendarContract.Events.ContentExceptionUri, standup.ToString(CultureInfo.InvariantCulture))!, moved);
        return JsonSerializer.Serialize(new { ann, bob, carol, dmytro, work, home, standup, review, holiday }, Json);
    }

    private static string Edit(Context context)
    {
        var r = context.ContentResolver!;
        var v = new ContentValues();
        v.Put("data1", "+1 555 0299");
        var n = r.Update(ContactsContract.Data.ContentUri!, v, "mimetype='vnd.android.cursor.item/phone_v2' AND data1='+1 555 0200'", null);
        return $"updated {n}";
    }

    private static string Wipe(Context context, string what, string id)
    {
        var r = context.ContentResolver!;
        if (what == "contacts")
        {
            var local = id == "local";
            var type = local ? null : id[..id.IndexOf(':')];
            var name = local ? null : id[(id.IndexOf(':') + 1)..];
            var where = local ? "account_type IS NULL AND account_name IS NULL" : "account_type=? AND account_name=?";
            var uri = ContactsContract.RawContacts.ContentUri!.BuildUpon()!.AppendQueryParameter("caller_is_syncadapter", "true")!.Build()!;
            return $"deleted {r.Delete(uri, where, local ? null : [type!, name!])}";
        }
        var events = AsSyncAdapter(CalendarContract.Events.ContentUri!, SmokeAccountName, CalendarContract.AccountTypeLocal);
        return $"deleted {r.Delete(events, "calendar_id=?", [id])}";
    }

    private static string Dump(Context context, string what, string id)
    {
        if (what == "contacts")
        {
            var source = new AndroidContactsSource(context);
            var contacts = source.Contacts(new HashSet<string>([id])).Select(s => source.Read(s.Id)).Where(c => c is not null)
                .Select(c => new { c!.Id, name = c.DisplayName, phones = c.Phones.Select(p => p.Value), emails = c.Emails.Select(e => e.Value), photo = c.Photo?.Length ?? 0 });
            return JsonSerializer.Serialize(contacts.OrderBy(c => c.name, StringComparer.Ordinal), Json);
        }
        var calendar = new AndroidCalendarSource(context);
        var events = calendar.Events(new HashSet<string>([id])).Select(e => new
        {
            e.Id, e.Uid, e.Title, e.Start, e.End, e.Duration, e.AllDay, e.TimeZone, e.RRule, alarms = e.Alarms.Count,
            exceptions = e.Exceptions.Select(x => new { x.Title, x.Start, x.OriginalStart }),
        });
        return JsonSerializer.Serialize(events.OrderBy(e => e.Title, StringComparer.Ordinal), Json);
    }
}
