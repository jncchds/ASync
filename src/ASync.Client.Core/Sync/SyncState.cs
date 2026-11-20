using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASync.Client.Core.Sync;

/// <summary>
/// What a file looked like on both sides right after the last sync that touched it. Remote values are whatever
/// the storage reports (its own clock and sizes), used only as change tokens.
/// </summary>
public sealed record BaseEntry(long LocalSize, long LocalModified, long RemoteSize, long RemoteModified);

/// <summary>
/// A device's memory of one Mirror/TwoWay folder (<c>sync-state.json</c> in the folder's state directory). Without
/// it, every difference would look like a conflict; with it, the syncer can tell which side changed.
/// <para>
/// The file is written only when something in it changed (<see cref="Dirty"/>): on a large folder it is the dominant
/// local write of a run, and a run that changed nothing must not wear the device's flash. Change entries with
/// <see cref="Set"/> and <see cref="Remove"/>, not through <see cref="Files"/>. <see cref="LastSync"/> alone does not make
/// the state dirty: it is only informational, kept in memory and written with the next real change.
/// </para>
/// </summary>
public sealed class SyncState
{
    public Dictionary<string, BaseEntry> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>When a run last finished (informational only; does not make the state dirty by itself).</summary>
    public DateTimeOffset? LastSync { get; set; }

    public DateTimeOffset? LastCleanUp
    {
        get;
        set { if (field != value) { field = value; Dirty = true; } }
    }

    /// <summary>The tree's <c>Location</c> this state was made at (null in states from before it was recorded).</summary>
    public string? Location
    {
        get;
        set { if (field != value) { field = value; Dirty = true; } }
    }

    /// <summary>Something changed since the state was loaded or last saved.</summary>
    [JsonIgnore]
    public bool Dirty { get; private set; }

    /// <summary>How many times this instance was written (tests and measurements).</summary>
    [JsonIgnore]
    public int Saves { get; private set; }

    /// <summary>Records a file's entry; the state becomes dirty only when the entry differs from the one it had.</summary>
    public void Set(string path, BaseEntry entry)
    {
        if (Files.TryGetValue(path, out var old) && old == entry) return;
        Files[path] = entry;
        Dirty = true;
    }

    public void Remove(string path)
    {
        if (Files.Remove(path)) Dirty = true;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SyncState Load(string path)
    {
        if (!File.Exists(path)) return new SyncState();
        var state = JsonSerializer.Deserialize<SyncState>(File.ReadAllBytes(path), Json) ?? new SyncState();
        state.Files = new Dictionary<string, BaseEntry>(state.Files, StringComparer.Ordinal);
        state.Dirty = false;
        return state;
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        File.Move(temp, path, overwrite: true);
        Dirty = false;
        Saves++;
    }

    /// <summary>Writes the state when it changed; true when it did.</summary>
    public bool SaveIfDirty(string path)
    {
        if (!Dirty) return false;
        Save(path);
        return true;
    }
}
