using System.Text.Json;

namespace ASync.Client.Core.Sync;

/// <summary>
/// What a file looked like on both sides right after the last sync that touched it. Remote values are whatever
/// the storage reports (its own clock and sizes), used only as change tokens.
/// </summary>
public sealed record BaseEntry(long LocalSize, long LocalModified, long RemoteSize, long RemoteModified);

/// <summary>
/// A device's memory of one Mirror/TwoWay folder (<c>sync-state.json</c> in the folder's state directory). Without
/// it, every difference would look like a conflict; with it, the syncer can tell which side changed.
/// </summary>
public sealed class SyncState
{
    public Dictionary<string, BaseEntry> Files { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset? LastSync { get; set; }
    public DateTimeOffset? LastCleanUp { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SyncState Load(string path)
    {
        if (!File.Exists(path)) return new SyncState();
        var state = JsonSerializer.Deserialize<SyncState>(File.ReadAllBytes(path), Json) ?? new SyncState();
        state.Files = new Dictionary<string, BaseEntry>(state.Files, StringComparer.Ordinal);
        return state;
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
