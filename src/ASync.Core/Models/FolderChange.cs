namespace ASync.Core.Models;

/// <summary>
/// One entry of a plain folder's change log (task 26): a path the storage gateway wrote, deleted or moved (a device's sync, the
/// Android documents provider, the iOS File Provider, a restore in the web UI). <see cref="Id"/> is the sequence devices ask
/// "changes since" with: it grows over all folders, so it also grows within each. Old entries are trimmed (see
/// <see cref="Folder.ChangesTrimmedThrough"/>); writes made straight to the storage, outside ASync, are not in it.
/// </summary>
public class FolderChange
{
    public long Id { get; set; }
    public int FolderId { get; set; }
    /// <summary>The path in the folder (a file, or a directory that was moved).</summary>
    public string Path { get; set; } = "";
    /// <summary>The device whose request it was (null: the server, e.g. a restore in the web UI).</summary>
    public Guid? DeviceId { get; set; }
    public DateTime At { get; set; }
}
