using System.Net;
using ASync.Contracts;

namespace ASync.Client.Core.Sync;

/// <summary>
/// What changed on the storage since a cursor (task 26): the paths, or that the storage must be listed in full and why
/// (<see cref="FullReason"/>). <see cref="Cursor"/>: what to keep once a run has handled them (for a full listing: where the
/// log was before it); null: no cursor (a server without the log).
/// </summary>
public sealed record RemoteChanges(IReadOnlySet<string>? Paths, long? Cursor, string? FullReason)
{
    public static RemoteChanges Full(string reason, long? cursor) => new(null, cursor, reason);
}

/// <summary>Where a run learns what changed on the storage (the server's change log; tests fake it).</summary>
public interface IRemoteChangeFeed
{
    /// <summary>The changes after <paramref name="since"/>; null: no cursor yet (only the cursor to keep after a full listing).</summary>
    Task<RemoteChanges> ReadAsync(long? since, CancellationToken ct);
}

/// <summary>
/// The server's change log of a plain folder (<c>GET /api/sync/folders/{id}/changes</c>), paged. More than
/// <see cref="Limit"/> paths since the cursor, a trimmed log, or a server without the log: list in full.
/// </summary>
public sealed class GatewayChangeFeed(AsyncApiClient api, int folderId) : IRemoteChangeFeed
{
    /// <summary>More changed paths than this: listing the storage costs less than stat'ing each one through the gateway.</summary>
    public const int Limit = 5000;

    public async Task<RemoteChanges> ReadAsync(long? since, CancellationToken ct)
    {
        try
        {
            if (since is not long from)
            {
                var now = await api.GetChangesAsync(folderId, null, ct);
                return RemoteChanges.Full("no change-log cursor yet", now.Cursor);
            }
            var paths = new HashSet<string>(StringComparer.Ordinal);
            for (var next = from; ;)
            {
                var page = await api.GetChangesAsync(folderId, next, ct);
                if (page.Trimmed) return RemoteChanges.Full("the change log no longer reaches back to the last run", page.Cursor);
                foreach (var c in page.Changes) paths.Add(c.Path);
                if (paths.Count > Limit)
                    return RemoteChanges.Full($"more than {Limit} changes on the storage", (await api.GetChangesAsync(folderId, null, ct)).Cursor);
                if (page.Complete) return new RemoteChanges(paths, page.Cursor, null);
                if (page.Next <= next) throw new IOException("The server's change log did not move on.");
                next = page.Next;
            }
        }
        catch (ApiException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // A server from before the change log (404 for the route), or a folder that has none.
            return RemoteChanges.Full("the server has no change log", null);
        }
        catch (Exception e) when (e is not OperationCanceledException and not DeviceRevokedException
                                      and (ApiException or HttpRequestException or IOException or System.Text.Json.JsonException))
        {
            // The listing that follows tells whether the server is reachable at all; without a cursor the next run lists again.
            return RemoteChanges.Full($"the change log could not be read ({e.Message})", null);
        }
    }
}
