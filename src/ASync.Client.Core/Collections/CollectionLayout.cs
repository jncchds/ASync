using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASync.Client.Core.Media;

namespace ASync.Client.Core.Collections;

/// <summary>
/// Where contacts and events go inside a Contacts or Calendars snapshot. <b>Part of the permanent format</b> (AGENTS.md "Extra data
/// sources"): it changes only together with <see cref="CollectionInfoFile.Format"/> and a reader for the old one.
/// <code>
/// Contacts:   &lt;account name&gt;~&lt;account short id&gt;/account.json
///             &lt;account name&gt;~&lt;account short id&gt;/&lt;name&gt;~&lt;contact short id&gt;.vcf      vCard 4.0, photo embedded
/// Calendars:  &lt;calendar name&gt;~&lt;calendar short id&gt;/calendar.json
///             &lt;calendar name&gt;~&lt;calendar short id&gt;/&lt;title&gt;~&lt;event short id&gt;.ics     iCalendar, exceptions inside
/// </code>
/// Names are made safe like a Photos snapshot's (<see cref="MediaLayout.Safe"/>, at most 80 characters); the short ids keep two
/// "John Smith" or two "Lunch" apart and keep a renamed contact's or event's identity in its name. The JSON beside them says what the
/// account or calendar was, so a snapshot can be restored, or imported elsewhere, without the server.
/// </summary>
public static class CollectionLayout
{
    public const string AccountFile = "account.json";
    public const string CalendarFile = "calendar.json";

    public static string AccountDirectory(ContactAccount a) => $"{MediaLayout.Safe(a.Name, 80, "account")}~{MediaLayout.SafeId(a.ShortId)}";

    public static string ContactFile(Contact c) => ContactFile(VCard.FormattedName(c), c.ShortId);

    public static string ContactFile(string? name, string shortId) => $"{MediaLayout.Safe(name, 80, "contact")}~{MediaLayout.SafeId(shortId)}.vcf";

    public static string CalendarDirectory(CalendarInfo c) => $"{MediaLayout.Safe(c.Name, 80, "calendar")}~{MediaLayout.SafeId(c.ShortId)}";

    public static string EventFile(CalendarEvent e) => $"{MediaLayout.Safe(e.Title, 80, "event")}~{MediaLayout.SafeId(e.ShortId)}.ics";

    /// <summary>A short id for something whose id does not fit a file name (an account's <c>com.google:me@gmail.com</c>): 8 hex digits of its SHA-256.</summary>
    public static string HashId(string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8];
}

/// <summary>
/// <c>account.json</c> / <c>calendar.json</c>: what a snapshot's directory was on the phone. <b>Permanent format</b> (version
/// <see cref="Format"/>): UTF-8 JSON, indented, fixed field order; readers ignore fields they do not know and refuse a higher format.
/// Accounts: <c>format, source, kind ("contacts"), id, name, type, typeLabel, readOnly</c>. Calendars: <c>format, source, kind
/// ("calendar"), id, name, account, accountType, color, timeZone, readOnly</c>.
/// </summary>
public sealed record CollectionInfoFile(string Kind, string Id, string Name, string Source = "", string? Type = null, string? TypeLabel = null,
    string? Account = null, string? AccountType = null, string? Color = null, string? TimeZone = null, bool ReadOnly = false)
{
    public const int Format = 1;

    public static CollectionInfoFile For(ContactAccount a, string source) =>
        new("contacts", a.Id, a.Name, source, Type: a.Type, TypeLabel: a.TypeLabel, ReadOnly: a.ReadOnly);

    public static CollectionInfoFile For(CalendarInfo c, string source) =>
        new("calendar", c.Id, c.Name, source, Account: c.AccountName, AccountType: c.AccountType, Color: c.Color, TimeZone: c.TimeZone, ReadOnly: c.ReadOnly);

    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("format", Format);
            w.WriteString("source", Source);
            w.WriteString("kind", Kind);
            w.WriteString("id", Id);
            w.WriteString("name", Name);
            if (Kind == "contacts")
            {
                w.WriteString("type", Type ?? "");
                w.WriteString("typeLabel", TypeLabel ?? "");
            }
            else
            {
                w.WriteString("account", Account ?? "");
                w.WriteString("accountType", AccountType ?? "");
                if (Color is { Length: > 0 } color) w.WriteString("color", color);
                if (TimeZone is { Length: > 0 } tz) w.WriteString("timeZone", tz);
            }
            w.WriteBoolean("readOnly", ReadOnly);
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static CollectionInfoFile Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var r = doc.RootElement;
            var format = r.GetProperty("format").GetInt32();
            if (format > Format) throw new InvalidDataException($"An {r.GetProperty("kind").GetString()} file of format {format} needs a newer ASync.");
            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new CollectionInfoFile(Str("kind") ?? "", Str("id") ?? throw new InvalidDataException("No id."), Str("name") ?? "", Str("source") ?? "",
                Str("type"), Str("typeLabel"), Str("account"), Str("accountType"), Str("color"), Str("timeZone"),
                r.TryGetProperty("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"Not a valid account or calendar file: {e.Message}", e);
        }
    }
}

/// <summary>
/// The sizes of generated files by path and version, kept between runs in the folder's state directory: a contact whose version is
/// unchanged is listed with its last size and its modification time, so the backup takes it from the parent snapshot without
/// generating it (and reading its photo) again. Losing the file costs one full read, nothing else.
/// </summary>
public sealed class GeneratedSizeCache(string? path)
{
    private Dictionary<string, (string Version, long Size)>? _entries;
    private readonly Lock _gate = new();

    private Dictionary<string, (string Version, long Size)> Entries
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
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException) { }
            return _entries;
        }
    }

    public long? Get(string file, string version)
    {
        lock (_gate) return Entries.TryGetValue(file, out var e) && e.Version == version ? e.Size : null;
    }

    public void Set(string file, string version, long size)
    {
        lock (_gate) Entries[file] = (version, size);
    }

    /// <summary>Keeps only <paramref name="keep"/> and writes the cache (failures only cost the next run a full read).</summary>
    public void Save(IEnumerable<string> keep)
    {
        if (path is null) return;
        lock (_gate)
        {
            var wanted = keep.ToHashSet(StringComparer.Ordinal);
            foreach (var stale in Entries.Keys.Where(k => !wanted.Contains(k)).ToList()) Entries.Remove(stale);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var buffer = new MemoryStream();
                using (var w = new Utf8JsonWriter(buffer))
                {
                    w.WriteStartObject();
                    foreach (var (k, v) in Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        w.WriteStartArray(k);
                        w.WriteStringValue(v.Version);
                        w.WriteNumberValue(v.Size);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, buffer.ToArray());
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string FileName(string kind) => string.Create(CultureInfo.InvariantCulture, $"{kind}-sizes.json");
}
