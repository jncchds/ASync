using System.Globalization;
using System.Text;
using System.Text.Json;
using ASync.Contracts;

namespace ASync.Client.Core.Media;

/// <summary>
/// Where an asset's files go inside a Photos snapshot. <b>Part of the permanent format</b> (AGENTS.md "Extra data sources"): snapshots
/// made with it are restored with it, so it changes only together with <see cref="MediaSidecar.Format"/> and a reader for the old one.
/// <code>
/// &lt;yyyy&gt;/&lt;MM&gt;/&lt;stem&gt;~&lt;short id&gt;.&lt;ext&gt;          the original, its bytes as they are
/// &lt;yyyy&gt;/&lt;MM&gt;/&lt;stem&gt;~&lt;short id&gt;.&lt;role&gt;.&lt;ext&gt;   further resources of the asset (iOS: "live", "edited", "raw")
/// &lt;yyyy&gt;/&lt;MM&gt;/&lt;stem&gt;~&lt;short id&gt;.json           the sidecar (<see cref="MediaSidecar"/>)
/// </code>
/// Year and month of the date taken (else modified), in UTC, so a phone changing time zones never moves a file. The stem is the
/// original file name without its extension, made safe for every file system; the short id makes two IMG_0001.JPG distinct. Albums are
/// not directories: an asset is stored once however many albums it is in, and the sidecar lists them.
/// </summary>
public static class MediaLayout
{
    private const int MaxStem = 80;

    /// <summary>The asset's directory: <c>2026/11</c>.</summary>
    public static string Directory(MediaAsset asset)
    {
        var when = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(asset.When, 0, 253402300799999)).UtcDateTime;
        return when.ToString("yyyy'/'MM", CultureInfo.InvariantCulture);
    }

    /// <summary>The name every file of the asset starts with: <c>IMG_1234~1000000033</c>.</summary>
    public static string BaseName(MediaAsset asset) => $"{Safe(Path.GetFileNameWithoutExtension(asset.FileName), MaxStem, "asset")}~{SafeId(asset.ShortId)}";

    public static string ResourceName(MediaAsset asset, MediaResource resource)
    {
        var ext = Extension(resource.FileName);
        return resource.Role == MediaResource.Original
            ? BaseName(asset) + ext
            : $"{BaseName(asset)}.{Safe(resource.Role.ToLowerInvariant(), 20, "extra")}{ext}";
    }

    public static string SidecarName(MediaAsset asset) => BaseName(asset) + ".json";

    /// <summary>".jpg", ".heic", "" (lower case, at most 10 characters, only letters and digits).</summary>
    private static string Extension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length is < 2 or > 11 || !ext[1..].All(char.IsAsciiLetterOrDigit)) return "";
        return ext.ToLowerInvariant();
    }

    /// <summary>Ids keep letters, digits and '-'; everything else becomes '_' (<c>8A4D…/L0/001</c> → <c>8A4D…_L0_001</c>).</summary>
    public static string SafeId(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id) sb.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_');
        return sb.Length == 0 ? "0" : sb.Length > 80 ? sb.ToString(0, 80) : sb.ToString();
    }

    /// <summary>A name safe on Windows, macOS, Linux, FAT and Android's providers: no separators, reserved characters, control characters or trailing dots.</summary>
    public static string Safe(string? name, int max, string fallback)
    {
        var sb = new StringBuilder();
        foreach (var c in name ?? "")
            sb.Append(c < 32 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '~' ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.').Trim();
        if (s.Length > max) s = s[..max].TrimEnd();
        return s.Length == 0 || s is "." or ".." ? fallback : s;
    }
}

/// <summary>
/// The JSON beside every asset in a Photos snapshot: what the library knew about it. <b>Permanent format</b> (version
/// <see cref="Format"/>); readers ignore fields they do not know, so fields can be added without a new version. Written with a fixed
/// field order and invariant formats, so an unchanged asset gives byte-identical JSON (stored once).
/// </summary>
public sealed record MediaSidecar(
    string Id, MediaKind Kind, string FileName, string Mime, long Size, long Modified, long? Taken,
    IReadOnlyList<MediaAlbumRef> Albums, IReadOnlyList<(string Role, string File, long Size, string Mime)> Resources,
    int? Width = null, int? Height = null, long? DurationMs = null, double? Latitude = null, double? Longitude = null,
    bool Favourite = false, bool Screenshot = false, string? RelativePath = null, string Source = "")
{
    public const int Format = 1;

    public static MediaSidecar For(MediaAsset asset, IReadOnlyDictionary<string, string> albumNames, MediaDetails details, string source) => new(
        asset.Id, asset.Kind, asset.FileName, asset.OriginalResource.Mime, asset.Size, asset.Modified, asset.Taken,
        [.. asset.AlbumIds.Select(id => new MediaAlbumRef(id, albumNames.TryGetValue(id, out var n) ? n : ""))],
        [.. asset.Resources.Select(r => (r.Role, MediaLayout.ResourceName(asset, r), r.Size, r.Mime))],
        asset.Width, asset.Height, asset.DurationMs, details.Latitude, details.Longitude, asset.Favourite, asset.Screenshot, asset.RelativePath, source);

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("format", Format);
            w.WriteString("source", Source);
            w.WriteString("id", Id);
            w.WriteString("kind", Kind == MediaKind.Video ? "video" : "photo");
            w.WriteString("fileName", FileName);
            w.WriteString("mime", Mime);
            w.WriteNumber("size", Size);
            if (Taken is { } taken) w.WriteString("taken", Iso(taken));
            w.WriteString("modified", Iso(Modified));
            if (Width is { } width) w.WriteNumber("width", width);
            if (Height is { } height) w.WriteNumber("height", height);
            if (DurationMs is { } duration) w.WriteNumber("durationMs", duration);
            if (Latitude is { } lat && Longitude is { } lon)
            {
                w.WriteStartObject("location");
                w.WriteNumber("latitude", Math.Round(lat, 7));
                w.WriteNumber("longitude", Math.Round(lon, 7));
                w.WriteEndObject();
            }
            w.WriteBoolean("favourite", Favourite);
            w.WriteBoolean("screenshot", Screenshot);
            if (RelativePath is { Length: > 0 } rel) w.WriteString("relativePath", rel);
            w.WriteStartArray("albums");
            foreach (var a in Albums)
            {
                w.WriteStartObject();
                w.WriteString("id", a.Id);
                w.WriteString("name", a.Name);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("resources");
            foreach (var r in Resources)
            {
                w.WriteStartObject();
                w.WriteString("role", r.Role);
                w.WriteString("file", r.File);
                w.WriteNumber("size", r.Size);
                w.WriteString("mime", r.Mime);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Reads a sidecar of any known format; throws <see cref="InvalidDataException"/> for one it cannot use.</summary>
    public static MediaSidecar Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var r = doc.RootElement;
            var format = r.GetProperty("format").GetInt32();
            if (format > Format) throw new InvalidDataException($"A sidecar of format {format} needs a newer ASync.");
            static long Ms(JsonElement e) => DateTimeOffset.Parse(e.GetString()!, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? Num(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
            double? lat = null, lon = null;
            if (r.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
                (lat, lon) = (loc.GetProperty("latitude").GetDouble(), loc.GetProperty("longitude").GetDouble());
            return new MediaSidecar(
                Str("id") ?? throw new InvalidDataException("A sidecar without an id."),
                Str("kind") == "video" ? MediaKind.Video : MediaKind.Photo,
                Str("fileName") ?? "", Str("mime") ?? "application/octet-stream", Num("size") ?? 0,
                r.TryGetProperty("modified", out var m) ? Ms(m) : 0,
                r.TryGetProperty("taken", out var t) ? Ms(t) : null,
                r.TryGetProperty("albums", out var albums)
                    ? [.. albums.EnumerateArray().Select(a => new MediaAlbumRef(a.GetProperty("id").GetString() ?? "", a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))]
                    : [],
                [.. r.GetProperty("resources").EnumerateArray().Select(x => (
                    x.GetProperty("role").GetString() ?? MediaResource.Original, x.GetProperty("file").GetString() ?? "",
                    x.GetProperty("size").GetInt64(), x.TryGetProperty("mime", out var mm) ? mm.GetString() ?? "" : ""))],
                (int?)Num("width"), (int?)Num("height"), Num("durationMs"), lat, lon,
                r.TryGetProperty("favourite", out var f) && f.ValueKind == JsonValueKind.True,
                r.TryGetProperty("screenshot", out var s) && s.ValueKind == JsonValueKind.True,
                Str("relativePath"), Str("source") ?? "");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"Not a valid photo sidecar: {e.Message}", e);
        }
    }
}

/// <summary>Which assets a <see cref="MediaSelectionDto"/> takes.</summary>
public static class MediaFilter
{
    public static bool Includes(MediaSelectionDto selection, MediaAsset asset)
    {
        if (asset.Kind == MediaKind.Video && !selection.IncludeVideos) return false;
        if (asset.Screenshot && !selection.IncludeScreenshots) return false;
        if (selection.Since is { } since && asset.When < new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc)).ToUnixTimeMilliseconds()) return false;
        if (selection.All) return true;
        var albums = selection.Albums ?? [];
        return asset.AlbumIds.Any(id => albums.Any(a => a.Id == id));
    }
}
