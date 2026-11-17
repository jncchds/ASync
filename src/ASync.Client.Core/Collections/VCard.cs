using System.Globalization;
using System.Text;

namespace ASync.Client.Core.Collections;

/// <summary>
/// Contacts as vCard 4.0 (RFC 6350), one card per file. <b>Part of the permanent format</b> (AGENTS.md "Extra data sources"): the
/// properties, their order and how values are written are what snapshots contain. Output is deterministic (fixed property order,
/// the provider's order inside each, invariant formats, no REV or other "made at" stamp), so an unchanged contact gives identical
/// bytes and is stored once. Custom labels use the widely read Apple convention (<c>item1.TEL:…</c> + <c>item1.X-ABLABEL:…</c>).
/// <see cref="Parse"/> reads what <see cref="Write"/> writes, and the usual vCard 3/4 of other programs well enough to import.
/// </summary>
public static class VCard
{
    public const string ProdId = "-//ASync//Contacts 1//EN";

    // ── Writing ──────────────────────────────────────────────────────────────

    public static byte[] Write(Contact c)
    {
        var w = new ContentWriter();
        var group = 0;
        string Group() => $"item{++group}.";
        void Labeled(string name, LabeledValue v, string value, string? extraParams = null)
        {
            var g = v.Label is { Length: > 0 } ? Group() : "";
            w.Line(g + name + TypeParam(v.Type) + (v.Preferred ? ";PREF=1" : "") + (extraParams ?? ""), value);
            if (g.Length > 0) w.Line(g + "X-ABLABEL", Escape(v.Label!));
        }

        w.Line("BEGIN", "VCARD");
        w.Line("VERSION", "4.0");
        w.Line("PRODID", ProdId);
        w.Line("UID", Escape(c.Id));
        w.Line("FN", Escape(FormattedName(c)));
        var n = c.Name ?? new StructuredName();
        w.Line("N", string.Join(';', new[] { n.Family, n.Given, n.Middle, n.Prefix, n.Suffix }.Select(Escape)));
        if (n.PhoneticFamily is { Length: > 0 } pf) w.Line("X-PHONETIC-LAST-NAME", Escape(pf));
        if (n.PhoneticGiven is { Length: > 0 } pg) w.Line("X-PHONETIC-FIRST-NAME", Escape(pg));
        if (n.PhoneticMiddle is { Length: > 0 } pm) w.Line("X-PHONETIC-MIDDLE-NAME", Escape(pm));
        foreach (var nick in c.Nicknames.Where(s => s.Length > 0)) w.Line("NICKNAME", Escape(nick));
        foreach (var d in c.Dates)
        {
            var value = DateValue(d.Date);
            if (value is null) continue;
            switch (d.Kind)
            {
                case "birthday" when d.Label is null: w.Line("BDAY", value); break;
                case "anniversary" when d.Label is null: w.Line("ANNIVERSARY", value); break;
                default:
                    var g = Group();
                    w.Line(g + "X-ABDATE", value);
                    w.Line(g + "X-ABLABEL", Escape(d.Label ?? d.Kind));
                    break;
            }
        }
        foreach (var p in c.Phones) Labeled("TEL", p, Escape(p.Value));
        foreach (var e in c.Emails) Labeled("EMAIL", e, Escape(e.Value));
        foreach (var a in c.Addresses)
        {
            var g = a.Label is { Length: > 0 } ? Group() : "";
            var label = a.Formatted is { Length: > 0 } f ? $";LABEL={Param(f)}" : "";
            w.Line(g + "ADR" + TypeParam(a.Type) + (a.Preferred ? ";PREF=1" : "") + label,
                string.Join(';', new[] { a.PoBox, a.Neighborhood, a.Street, a.City, a.Region, a.PostalCode, a.Country }.Select(Escape)));
            if (g.Length > 0) w.Line(g + "X-ABLABEL", Escape(a.Label!));
        }
        var orgGroups = c.Organizations.Count > 1;
        foreach (var o in c.Organizations)
        {
            var g = orgGroups || o.Label is { Length: > 0 } ? Group() : "";
            if (o.Company is { Length: > 0 } || o.Department is { Length: > 0 })
                w.Line(g + "ORG" + TypeParam(o.Type), o.Department is { Length: > 0 } dep ? $"{Escape(o.Company)};{Escape(dep)}" : Escape(o.Company));
            if (o.Title is { Length: > 0 } t) w.Line(g + "TITLE", Escape(t));
            if (g.Length > 0 && o.Label is { Length: > 0 } ol) w.Line(g + "X-ABLABEL", Escape(ol));
        }
        foreach (var u in c.Websites) Labeled("URL", u, Escape(u.Value));
        foreach (var im in c.Messaging) Labeled("IMPP", im with { Type = "" }, ImUri(im.Type, im.Value));
        foreach (var s in c.Sip) Labeled("IMPP", s, s.Value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ? s.Value : "sip:" + s.Value);
        foreach (var r in c.Relations) Labeled("RELATED", r, Escape(r.Value), ";VALUE=text");
        if (c.Note is { Length: > 0 } note) w.Line("NOTE", Escape(note));
        if (c.Groups.Count > 0) w.Line("CATEGORIES", string.Join(',', c.Groups.Select(Escape)));
        if (c.Starred) w.Line("X-ASYNC-STARRED", "TRUE");
        if (c.SourceId is { Length: > 0 } sid) w.Line("X-ASYNC-SOURCE-ID", Escape(sid));
        if (c.Photo is { Length: > 0 } photo) w.Line("PHOTO", $"data:{ImageMime(photo)};base64,{Convert.ToBase64String(photo)}");
        w.Line("END", "VCARD");
        return w.ToArray();
    }

    /// <summary>The name a card shows: the provider's display name, else one made of the parts, else a phone or email, else "Unnamed".</summary>
    public static string FormattedName(Contact c)
    {
        if (c.DisplayName is { Length: > 0 } dn) return dn;
        var n = c.Name;
        var parts = new[] { n?.Prefix, n?.Given, n?.Middle, n?.Family, n?.Suffix }.Where(s => s is { Length: > 0 });
        var joined = string.Join(' ', parts);
        if (joined.Length > 0) return joined;
        return c.Organizations.FirstOrDefault()?.Company ?? c.Phones.FirstOrDefault()?.Value ?? c.Emails.FirstOrDefault()?.Value ?? "Unnamed";
    }

    private static string TypeParam(string type)
    {
        var tokens = type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => new string(t.ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').ToArray()))
            .Where(t => t.Length > 0).ToList();
        return tokens.Count == 0 ? "" : ";TYPE=" + string.Join(',', tokens);
    }

    private static string ImUri(string protocol, string handle)
    {
        var scheme = new string(protocol.ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '+' or '.').ToArray());
        if (scheme.Length == 0) scheme = "x-im";
        return handle.StartsWith(scheme + ":", StringComparison.OrdinalIgnoreCase) ? handle : $"{scheme}:{handle}";
    }

    /// <summary>ISO 8601 extended (<c>1980-01-15</c>, <c>--01-15</c>) to vCard 4's basic form (<c>19800115</c>, <c>--0115</c>); other text is kept.</summary>
    private static string? DateValue(string date)
    {
        var d = date.Trim();
        if (d.Length == 0) return null;
        if (d.Length == 10 && d[4] == '-' && d[7] == '-' && d.Replace("-", "").All(char.IsAsciiDigit)) return d.Replace("-", "");
        if (d.Length == 7 && d.StartsWith("--") && d[4] == '-') return "--" + d[2..4] + d[5..7];
        if (d.All(ch => char.IsAsciiDigit(ch) || ch == '-' || ch == 'T' || ch == ':' || ch == 'Z')) return d;
        return Escape(d); // free text: the provider allowed it (vCard 4: VALUE=text would be exact; readers take it as is)
    }

    public static string ImageMime(byte[] b) =>
        b.Length > 3 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G' ? "image/png"
        : b.Length > 2 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' ? "image/gif"
        : b.Length > 11 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? "image/webp"
        : "image/jpeg";

    /// <summary>A TEXT value escaped for a content line (backslash, comma, semicolon, newline).</summary>
    public static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case ',': sb.Append(@"\,"); break;
                case ';': sb.Append(@"\;"); break;
                case '\r': if (i + 1 < s.Length && s[i + 1] == '\n') i++; sb.Append(@"\n"); break;
                case '\n': sb.Append(@"\n"); break;
                default: if (ch >= 32 || ch == '\t') sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>A parameter value: quoted when it has to be (RFC 6350 has no escape for a quote: it becomes an apostrophe).</summary>
    internal static string Param(string s)
    {
        var clean = new string(s.Replace("\r\n", "\n").Select(ch => ch == '"' ? '\'' : ch == '\n' ? ' ' : ch).Where(ch => ch >= 32 || ch == '\t').ToArray());
        return clean.IndexOfAny([':', ';', ',']) >= 0 || clean.Contains(' ') ? $"\"{clean}\"" : clean;
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>Every card in a .vcf (one or many). Throws <see cref="InvalidDataException"/> when there is none.</summary>
    public static List<Contact> Parse(ReadOnlySpan<byte> data)
    {
        var cards = new List<Contact>();
        List<ContentLine>? current = null;
        foreach (var line in ContentLine.ReadAll(data))
        {
            if (line.Is("BEGIN") && line.Value.Equals("VCARD", StringComparison.OrdinalIgnoreCase)) current = [];
            else if (line.Is("END") && line.Value.Equals("VCARD", StringComparison.OrdinalIgnoreCase) && current is not null)
            {
                cards.Add(FromLines(current, cards.Count));
                current = null;
            }
            else current?.Add(line);
        }
        if (cards.Count == 0) throw new InvalidDataException("No vCard in the file.");
        return cards;
    }

    private static Contact FromLines(List<ContentLine> lines, int index)
    {
        var labels = lines.Where(l => l.Is("X-ABLABEL") && l.Group is not null)
            .GroupBy(l => l.Group!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => Unescape(g.First().Value), StringComparer.OrdinalIgnoreCase);
        string? Label(ContentLine l) => l.Group is { } g && labels.TryGetValue(g, out var s) ? s : null;
        LabeledValue Lv(ContentLine l, string value) => new(value, l.Types, Label(l), l.Preferred);

        string? id = null, fn = null, note = null, sourceId = null;
        StructuredName name = new();
        bool starred = false;
        byte[]? photo = null;
        List<string> nicks = [], groups = [];
        List<LabeledValue> phones = [], emails = [], urls = [], ims = [], sips = [], relations = [];
        List<PostalAddress> addresses = [];
        List<Organization> orgs = [];
        List<ContactDate> dates = [];
        var orgLines = new List<ContentLine>();
        foreach (var l in lines)
        {
            switch (l.Name)
            {
                case "UID": id = Unescape(l.Value); break;
                case "FN": fn = Unescape(l.Value); break;
                case "N":
                    var p = SplitStructured(l.Value);
                    name = name with { Family = At(p, 0), Given = At(p, 1), Middle = At(p, 2), Prefix = At(p, 3), Suffix = At(p, 4) };
                    break;
                case "X-PHONETIC-LAST-NAME": name = name with { PhoneticFamily = Unescape(l.Value) }; break;
                case "X-PHONETIC-FIRST-NAME": name = name with { PhoneticGiven = Unescape(l.Value) }; break;
                case "X-PHONETIC-MIDDLE-NAME": name = name with { PhoneticMiddle = Unescape(l.Value) }; break;
                case "NICKNAME": nicks.AddRange(SplitList(l.Value)); break;
                case "BDAY": dates.Add(new ContactDate(IsoDate(l.Value), "birthday")); break;
                case "ANNIVERSARY": dates.Add(new ContactDate(IsoDate(l.Value), "anniversary")); break;
                case "X-ABDATE":
                    var label = Label(l);
                    var kind = label?.ToLowerInvariant() switch { "birthday" => "birthday", "anniversary" or "_$!<anniversary>!$_" => "anniversary", _ => "other" };
                    dates.Add(new ContactDate(IsoDate(l.Value), kind, kind == "other" || !string.Equals(label, kind, StringComparison.OrdinalIgnoreCase) ? label : null));
                    break;
                case "TEL": phones.Add(Lv(l, Unescape(l.Value).Replace("tel:", "", StringComparison.OrdinalIgnoreCase))); break;
                case "EMAIL": emails.Add(Lv(l, Unescape(l.Value))); break;
                case "URL": urls.Add(Lv(l, Unescape(l.Value))); break;
                case "IMPP":
                    var v = Unescape(l.Value);
                    var colon = v.IndexOf(':');
                    if (colon > 0 && v[..colon].Equals("sip", StringComparison.OrdinalIgnoreCase)) sips.Add(Lv(l, v));
                    else ims.Add(new LabeledValue(colon > 0 ? v[(colon + 1)..] : v, colon > 0 ? v[..colon].ToLowerInvariant() : "", Label(l), l.Preferred));
                    break;
                case "X-SIP": sips.Add(Lv(l, Unescape(l.Value))); break;
                case "RELATED": relations.Add(Lv(l, Unescape(l.Value))); break;
                case "ADR":
                    var a = SplitStructured(l.Value);
                    addresses.Add(new PostalAddress(At(a, 2), At(a, 0), At(a, 1), At(a, 3), At(a, 4), At(a, 5), At(a, 6), l.Param("LABEL"), l.Types, Label(l), l.Preferred));
                    break;
                case "ORG" or "TITLE": orgLines.Add(l); break;
                case "NOTE": note = Unescape(l.Value); break;
                case "CATEGORIES": groups.AddRange(SplitList(l.Value)); break;
                case "X-ASYNC-STARRED": starred = l.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || l.Value == "1"; break;
                case "X-ASYNC-SOURCE-ID": sourceId = Unescape(l.Value); break;
                case "PHOTO": photo = PhotoBytes(l); break;
            }
        }
        // ORG and TITLE belong together by group (item1.ORG + item1.TITLE), else in order.
        var ungroupedTitles = new Queue<string>(orgLines.Where(x => x.Is("TITLE") && x.Group is null).Select(x => Unescape(x.Value)));
        foreach (var l in orgLines)
        {
            if (l.Is("ORG"))
            {
                var o = SplitStructured(l.Value);
                var title = l.Group is { } g
                    ? orgLines.Where(x => x.Is("TITLE") && x.Group == g).Select(x => Unescape(x.Value)).FirstOrDefault()
                    : ungroupedTitles.TryDequeue(out var t) ? t : null;
                orgs.Add(new Organization(At(o, 0), At(o, 1), title, l.Types, Label(l)));
            }
            else if (l.Group is { } g && !orgLines.Any(x => x.Is("ORG") && x.Group == g))
                orgs.Add(new Organization(Title: Unescape(l.Value), Label: Label(l)));
        }
        foreach (var t in ungroupedTitles) orgs.Add(new Organization(Title: t));

        var uid = id is { Length: > 0 } ? id : $"vcard:{index}";
        return new Contact
        {
            Id = uid, ShortId = "", AccountId = "", DisplayName = fn, Name = name, Nicknames = nicks, Phones = phones, Emails = emails,
            Addresses = addresses, Organizations = orgs, Websites = urls, Messaging = ims, Sip = sips, Relations = relations, Dates = dates,
            Note = note, Photo = photo, Groups = groups, Starred = starred, SourceId = sourceId,
        };
    }

    private static byte[]? PhotoBytes(ContentLine l)
    {
        try
        {
            var v = l.Value.Trim();
            if (v.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = v.IndexOf(',');
                return comma > 0 && v[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ? Convert.FromBase64String(v[(comma + 1)..]) : null;
            }
            var encoding = l.Param("ENCODING");
            return encoding is not null && (encoding.Equals("b", StringComparison.OrdinalIgnoreCase) || encoding.Equals("BASE64", StringComparison.OrdinalIgnoreCase))
                ? Convert.FromBase64String(v) : null;
        }
        catch (FormatException) { return null; }
    }

    private static string IsoDate(string value)
    {
        var v = Unescape(value).Trim();
        if (v.Length == 8 && v.All(char.IsAsciiDigit)) return $"{v[..4]}-{v[4..6]}-{v[6..8]}";
        if (v.Length == 6 && v.StartsWith("--") && v[2..].All(char.IsAsciiDigit)) return $"--{v[2..4]}-{v[4..6]}";
        if (v.Length > 8 && v[8] == 'T' && v[..8].All(char.IsAsciiDigit)) return $"{v[..4]}-{v[4..6]}-{v[6..8]}";
        return v;
    }

    private static string? At(List<string> parts, int i) => i < parts.Count && parts[i].Length > 0 ? parts[i] : null;

    /// <summary>Splits a structured value (N, ADR, ORG) at unescaped semicolons and unescapes each part.</summary>
    internal static List<string> SplitStructured(string value) => SplitUnescaped(value, ';');

    internal static List<string> SplitList(string value) => SplitUnescaped(value, ',').Where(s => s.Length > 0).ToList();

    private static List<string> SplitUnescaped(string value, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\') { i++; continue; }
            if (value[i] == separator)
            {
                parts.Add(Unescape(value[start..i]));
                start = i + 1;
            }
        }
        parts.Add(Unescape(value[start..]));
        return parts;
    }

    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var next = s[++i];
                sb.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// What makes two contacts "the same" for a restore that finds one already there under another id: the name, the phone numbers
    /// (digits only) and the email addresses (lower case), in sorted order.
    /// </summary>
    public static string ContentKey(Contact c)
    {
        static string Digits(string s) => new(s.Where(ch => char.IsAsciiDigit(ch) || ch == '+').ToArray());
        return string.Join('\u001f',
            FormattedName(c).Trim().ToLowerInvariant(),
            string.Join(',', c.Phones.Select(p => Digits(p.Value)).Where(p => p.Length > 0).Order(StringComparer.Ordinal)),
            string.Join(',', c.Emails.Select(e => e.Value.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal)));
    }
}

/// <summary>Writes RFC 5545 / RFC 6350 content lines: CRLF endings, folded at 75 octets without splitting a UTF-8 sequence.</summary>
internal sealed class ContentWriter
{
    private readonly MemoryStream _out = new();

    public void Line(string nameAndParams, string value)
    {
        var bytes = Encoding.UTF8.GetBytes($"{nameAndParams}:{value}");
        var lineStart = 0;
        var limit = 75;
        var i = 0;
        while (bytes.Length - lineStart > limit)
        {
            i = lineStart + limit;
            while (i > lineStart && (bytes[i] & 0xC0) == 0x80) i--; // a continuation byte: back to the sequence's start
            _out.Write(bytes, lineStart, i - lineStart);
            _out.Write("\r\n "u8);
            lineStart = i;
            limit = 74; // the leading space counts
        }
        _out.Write(bytes, lineStart, bytes.Length - lineStart);
        _out.Write("\r\n"u8);
    }

    public byte[] ToArray() => _out.ToArray();
}

/// <summary>One unfolded content line: <c>group.NAME;PARAM=value:value</c>, the name and parameter names in upper case.</summary>
internal sealed record ContentLine(string? Group, string Name, IReadOnlyList<KeyValuePair<string, string>> Params, string Value)
{
    public bool Is(string name) => Name == name;

    public string? Param(string name) => Params.FirstOrDefault(p => p.Key == name).Value;

    /// <summary>The TYPE parameter(s) in lower case and comma-separated (<c>work,fax</c>), without <c>pref</c> and <c>voice</c>/<c>internet</c> defaults.</summary>
    public string Types => string.Join(',', Params.Where(p => p.Key == "TYPE").SelectMany(p => p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(t => t.ToLowerInvariant()).Where(t => t is not ("pref" or "internet" or "voice" or "x400")).Distinct());

    public bool Preferred => Params.Any(p => p.Key == "PREF" || (p.Key == "TYPE" && p.Value.Split(',').Any(t => t.Trim().Equals("pref", StringComparison.OrdinalIgnoreCase))));

    public static IEnumerable<ContentLine> ReadAll(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? data[3..] : data);
        var physical = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<string>();
        foreach (var raw in physical)
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && lines.Count > 0) lines[^1] += raw[1..];
            else if (raw.Length > 0) lines.Add(raw);
        }
        var result = new List<ContentLine>(lines.Count);
        foreach (var line in lines) if (Parse(line) is { } parsed) result.Add(parsed);
        return result;
    }

    private static ContentLine? Parse(string line)
    {
        // name and parameters end at the first ':' outside quotes
        var inQuotes = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ':' && !inQuotes) { colon = i; break; }
        }
        if (colon <= 0) return null;
        var head = line[..colon];
        var value = line[(colon + 1)..];
        var segments = new List<string>();
        var start = 0;
        inQuotes = false;
        for (var i = 0; i < head.Length; i++)
        {
            if (head[i] == '"') inQuotes = !inQuotes;
            else if (head[i] == ';' && !inQuotes) { segments.Add(head[start..i]); start = i + 1; }
        }
        segments.Add(head[start..]);
        var fullName = segments[0];
        var dot = fullName.IndexOf('.');
        var group = dot > 0 ? fullName[..dot] : null;
        var name = (dot > 0 ? fullName[(dot + 1)..] : fullName).ToUpperInvariant();
        var ps = new List<KeyValuePair<string, string>>();
        foreach (var seg in segments.Skip(1))
        {
            var eq = seg.IndexOf('=');
            if (eq < 0) ps.Add(new("TYPE", seg)); // vCard 2.1: a bare type
            else ps.Add(new(seg[..eq].ToUpperInvariant(), seg[(eq + 1)..].Trim('"')));
        }
        return new ContentLine(group, name, ps, value);
    }
}

internal static class Invariant
{
    public static string S(long v) => v.ToString(CultureInfo.InvariantCulture);
}
