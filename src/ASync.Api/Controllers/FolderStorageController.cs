using System.Globalization;
using System.Net.Http.Headers;
using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Engine.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>
/// The storage gateway: <see cref="IStorageBackend"/> over HTTP, scoped to one folder's root on its back-end. Devices
/// run the sync engine themselves against it (the client's <c>RemoteStorageBackend</c>), so the engine is the same
/// everywhere, end-to-end encryption happens on the device, and storage credentials never leave the server.
/// Only devices that belong to the folder get in.
/// </summary>
[ApiController]
[Route("api/sync/folders/{folderId:int}/storage")]
[Authorize(Roles = Roles.Device)]
public class FolderStorageController : ControllerBase
{
    /// <summary>Uploads up to this size are buffered in memory; larger ones in a temporary file (back-ends like S3 need the length up front).</summary>
    private const long MemoryBufferLimit = 32 * 1024 * 1024;

    private readonly IFolderRepository _folders;
    private readonly FolderBackendCache _backends;
    private readonly SnapshotService _snapshots;

    public FolderStorageController(IFolderRepository folders, FolderBackendCache backends, SnapshotService snapshots)
    {
        _folders = folders;
        _backends = backends;
        _snapshots = snapshots;
    }

    private async Task<IStorageBackend?> BackendAsync(int folderId, CancellationToken ct)
    {
        if (User.DeviceId() is not Guid deviceId) return null;
        var membership = await _folders.GetMembershipAsync(folderId, deviceId);
        return membership is null ? null : await _backends.GetAsync(membership.Folder!, ct);
    }

    [HttpGet("stat")]
    public async Task<IActionResult> Stat(int folderId, [FromQuery] string path, CancellationToken ct)
    {
        if (await BackendAsync(folderId, ct) is not { } backend) return NotFound();
        return await Guard(async () => await backend.StatAsync(path, ct) is { } e ? Ok(Dto(e)) : NotFound());
    }

    [HttpGet("list")]
    public async Task<IActionResult> List(int folderId, [FromQuery] string? dir, [FromQuery] bool recursive, CancellationToken ct)
    {
        if (await BackendAsync(folderId, ct) is not { } backend) return NotFound();
        return await Guard(async () =>
        {
            var entries = new List<StorageEntryDto>();
            await foreach (var e in backend.ListAsync(dir ?? "", recursive, ct)) entries.Add(Dto(e));
            return Ok(entries);
        });
    }

    /// <summary>Supports a single <c>Range: bytes=a-b</c> (the repository reads blobs this way).</summary>
    [HttpGet("file")]
    public async Task<IActionResult> Read(int folderId, [FromQuery] string path, CancellationToken ct)
    {
        if (await BackendAsync(folderId, ct) is not { } backend) return NotFound();
        return await Guard(async () =>
        {
            long offset = 0;
            long? length = null;
            if (RangeHeaderValue.TryParse(Request.Headers.Range.ToString(), out var range) && range.Ranges.Count == 1
                && range.Ranges.First() is { From: long from } r)
            {
                offset = from;
                length = r.To is long to ? to - from + 1 : null;
                Response.StatusCode = StatusCodes.Status206PartialContent;
            }
            var stream = await backend.OpenReadAsync(path, offset, length, ct);
            if (length is long l) Response.ContentLength = l;
            return new FileStreamResult(stream, "application/octet-stream") { EnableRangeProcessing = false };
        });
    }

    /// <summary><c>mtime</c> = Unix milliseconds to set as the file's modification time.</summary>
    [HttpPut("file")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Write(int folderId, [FromQuery] string path, [FromQuery] long? mtime, CancellationToken ct)
    {
        if (await BackendAsync(folderId, ct) is not { } backend) return NotFound();
        return await Guard(async () =>
        {
            await using var buffer = Request.ContentLength is long len and <= MemoryBufferLimit
                ? (Stream)new MemoryStream((int)len)
                : new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await Request.Body.CopyToAsync(buffer, ct);
            if (Request.ContentLength is long expected && buffer.Length != expected)
                return BadRequest(new { message = "The upload was cut short." });
            buffer.Position = 0;
            await backend.WriteAsync(path, buffer, mtime is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null, ct);
            return NoContent();
        });
    }

    [HttpDelete("file")]
    public async Task<IActionResult> Delete(int folderId, [FromQuery] string path, CancellationToken ct)
    {
        if (await BackendAsync(folderId, ct) is not { } backend) return NotFound();
        return await Guard(async () =>
        {
            await backend.DeleteAsync(path, ct);
            // Only a prune deletes index files and packs: the snapshot browser's cached index is now out of date.
            // (Packs go last, after the new index is complete, so a reload after this point is right.)
            var normalized = StoragePath.Normalize(path);
            if (normalized.StartsWith("index/", StringComparison.Ordinal) || normalized.StartsWith("data/", StringComparison.Ordinal))
                _snapshots.Invalidate(folderId);
            return NoContent();
        });
    }

    [HttpPost("move")]
    public async Task<IActionResult> Move(int folderId, [FromBody] StorageMoveRequest req, CancellationToken ct)
    {
        if (await BackendAsync(folderId, ct) is not { } backend) return NotFound();
        return await Guard(async () =>
        {
            await backend.MoveAsync(req.From, req.To, ct);
            return NoContent();
        });
    }

    private static StorageEntryDto Dto(StorageEntry e) => new(e.Path, e.IsDirectory, e.Size, e.LastModified);

    /// <summary>
    /// Storage errors become answers the client maps back to the same exceptions:
    /// 404 + <c>X-Storage-Error: not-found</c>, 400 for bad paths, 502 for the back-end failing.
    /// </summary>
    private async Task<IActionResult> Guard(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (StorageNotFoundException e)
        {
            Response.Headers["X-Storage-Error"] = "not-found";
            return NotFound(new { message = e.Message });
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { message = e.Message });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = $"Storage error: {e.Message}" });
        }
    }
}
