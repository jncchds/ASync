using System.Globalization;
using System.Net.Http.Headers;
using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
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
    private readonly UploadService _uploads;
    private readonly UploadLimits _limits;

    public FolderStorageController(IFolderRepository folders, FolderBackendCache backends, SnapshotService snapshots, UploadService uploads,
        UploadLimits limits)
    {
        _folders = folders;
        _backends = backends;
        _snapshots = snapshots;
        _uploads = uploads;
        _limits = limits;
    }

    private async Task<IStorageBackend?> BackendAsync(int folderId, CancellationToken ct) =>
        await MemberAsync(folderId, ct) is { } member ? member.Backend : null;

    private async Task<(Folder Folder, Guid DeviceId, IStorageBackend Backend)?> MemberAsync(int folderId, CancellationToken ct)
    {
        if (User.DeviceId() is not Guid deviceId) return null;
        var membership = await _folders.GetMembershipAsync(folderId, deviceId);
        return membership is null ? null : (membership.Folder!, deviceId, await _backends.GetAsync(membership.Folder!, ct));
    }

    // ── Resumable uploads (UploadService; protocol in ASync.Contracts next to StartUploadRequest) ──

    [HttpPost("uploads")]
    public async Task<IActionResult> StartUpload(int folderId, [FromBody] StartUploadRequest req, CancellationToken ct)
    {
        if (await MemberAsync(folderId, ct) is not var (folder, deviceId, backend)) return NotFound();
        return await Guard(async () => Ok(UploadService.Dto(await _uploads.StartAsync(folder, deviceId, backend, req, ct))));
    }

    [HttpPut("uploads/{uploadId:guid}/parts/{number:int}")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> WritePart(int folderId, Guid uploadId, int number, CancellationToken ct)
    {
        if (await MemberAsync(folderId, ct) is not var (folder, deviceId, backend)) return NotFound();
        if (Request.ContentLength is long length && length > _limits.PartSize)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { message = $"A part is at most {_limits.PartSize} bytes." });
        var sha256 = Request.Headers["X-Content-SHA256"].FirstOrDefault();
        return await Guard(async () =>
        {
            await _uploads.WritePartAsync(folder, deviceId, backend, uploadId, number, Request.Body, sha256, ct);
            return NoContent();
        });
    }

    [HttpPost("uploads/{uploadId:guid}/complete")]
    public async Task<IActionResult> CompleteUpload(int folderId, Guid uploadId, CancellationToken ct)
    {
        if (await MemberAsync(folderId, ct) is not var (folder, deviceId, backend)) return NotFound();
        return await Guard(async () =>
        {
            await _uploads.CompleteAsync(folder, deviceId, backend, uploadId, ct);
            return NoContent();
        });
    }

    [HttpDelete("uploads/{uploadId:guid}")]
    public async Task<IActionResult> AbortUpload(int folderId, Guid uploadId, CancellationToken ct)
    {
        if (await MemberAsync(folderId, ct) is not var (folder, deviceId, backend)) return NotFound();
        return await Guard(async () =>
        {
            await _uploads.AbortAsync(folder, deviceId, backend, uploadId);
            return NoContent();
        });
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
        if (Request.ContentLength is not long size || size > _limits.MaxSinglePut)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                message = $"A single upload is limited to {_limits.MaxSinglePut / 1024 / 1024} MB and needs a Content-Length; send larger files in parts (POST uploads).",
            });
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
        catch (UploadGoneException e)
        {
            Response.Headers["X-Upload-Error"] = "gone";
            return NotFound(new { message = e.Message });
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or Amazon.Runtime.AmazonServiceException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = $"Storage error: {e.Message}" });
        }
    }
}
