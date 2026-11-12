using System.Security.Cryptography;
using System.Text;
using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ASync.Api.Controllers;

/// <summary>
/// Browsing and downloading a Backup folder's snapshots in the web UI, for Transport-encrypted folders (the server
/// opens them with its key). End-to-end folders answer 409: restore them with a device. See <see cref="SnapshotService"/>.
/// </summary>
[ApiController]
[Route("api/folders/{id:int}/snapshots")]
[Authorize(Roles = Roles.Users)]
public class SnapshotsController : ControllerBase
{
    private readonly IFolderRepository _folders;
    private readonly IDeviceRepository _devices;
    private readonly FolderService _folderService;
    private readonly SnapshotService _snapshots;
    private readonly ILogger<SnapshotsController> _logger;

    public SnapshotsController(IFolderRepository folders, IDeviceRepository devices, FolderService folderService, SnapshotService snapshots,
        ILogger<SnapshotsController> logger)
    {
        _folders = folders;
        _devices = devices;
        _folderService = folderService;
        _snapshots = snapshots;
        _logger = logger;
    }

    /// <summary>The folder's snapshots, newest first.</summary>
    [HttpGet]
    public Task<IActionResult> List(int id, CancellationToken ct) => WithFolderAsync(id, User.UserId(), null, async folder =>
    {
        var list = await _snapshots.ReadAsync(folder, () => _folderService.ServerKey(folder), null, b => b.Repository.ListSnapshotsAsync(ct), ct);
        var names = (await _devices.GetForUserAsync(folder.UserId)).ToDictionary(d => d.Id, d => d.Name);
        return Ok(list.OrderByDescending(s => s.Time).Select(s =>
        {
            Guid? deviceId = Guid.TryParse(s.DeviceId ?? s.Source, out var g) ? g : null;
            return new SnapshotDto(s.Id, s.Time, s.Source, deviceId, deviceId is Guid d && names.TryGetValue(d, out var n) ? n : "", s.Hostname,
                [.. s.Tags], s.Stats.FilesNew, s.Stats.FilesChanged, s.Stats.FilesUnmodified, s.Stats.BytesProcessed, s.Stats.BytesAdded, s.Stats.Errors.Count);
        }).ToList());
    }, ct);

    /// <summary>One directory of a snapshot (<paramref name="path"/> "" = its root).</summary>
    [HttpGet("{snapshotId}/tree")]
    public Task<IActionResult> Tree(int id, string snapshotId, [FromQuery] string? path, CancellationToken ct) =>
        WithFolderAsync(id, User.UserId(), snapshotId, async folder => Ok(await _snapshots.ReadAsync(folder, () => _folderService.ServerKey(folder), snapshotId,
            async b => await b.ListDirectoryAsync(await b.LoadSnapshotAsync(snapshotId, ct), path ?? "", ct), ct)), ct);

    /// <summary>Downloads one file of a snapshot.</summary>
    [HttpGet("{snapshotId}/file")]
    public Task<IActionResult> File(int id, string snapshotId, [FromQuery] string path, CancellationToken ct) =>
        DownloadAsync(User.UserId(), id, snapshotId, path ?? "", zip: false, ct);

    /// <summary>Downloads a directory of a snapshot as a ZIP (<paramref name="path"/> "" = everything).</summary>
    [HttpGet("{snapshotId}/zip")]
    public Task<IActionResult> Zip(int id, string snapshotId, [FromQuery] string? path, CancellationToken ct) =>
        DownloadAsync(User.UserId(), id, snapshotId, path ?? "", zip: true, ct);

    /// <summary>
    /// A link valid for a few minutes that downloads without an Authorization header, so the browser streams the
    /// download to disk itself. It grants only that file or directory.
    /// </summary>
    [HttpPost("{snapshotId}/download-link")]
    public Task<IActionResult> DownloadLink(int id, string snapshotId, [FromBody] DownloadLinkRequest req, CancellationToken ct) =>
        WithFolderAsync(id, User.UserId(), snapshotId, folder =>
        {
            if (!SnapshotBrowser.IsSnapshotId(snapshotId)) return Task.FromResult<IActionResult>(NotFound(new { message = "No such snapshot." }));
            var path = StoragePath.Normalize(req.Path ?? "");
            var (ticket, expires) = _snapshots.IssueTicket(new SnapshotDownloadTicket(folder.UserId, folder.Id, snapshotId, path, req.Zip));
            return Task.FromResult<IActionResult>(Ok(new SnapshotDownloadLinkDto($"/api/snapshot-downloads?t={Uri.EscapeDataString(ticket)}", expires)));
        }, ct);

    public record DownloadLinkRequest(string? Path, bool Zip = false);

    /// <summary>Where download links lead. The ticket is the authorization (see <see cref="DownloadLink"/>).</summary>
    [HttpGet("/api/snapshot-downloads")]
    [AllowAnonymous]
    public Task<IActionResult> Download([FromQuery(Name = "t")] string? ticket, CancellationToken ct)
    {
        var grant = ticket is null ? null : _snapshots.ReadTicket(ticket);
        if (grant is null)
            return Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status403Forbidden, new { message = "This download link has expired. Start the download again." }));
        return DownloadAsync(grant.UserId, grant.FolderId, grant.SnapshotId, grant.Path, grant.Zip, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<IActionResult> DownloadAsync(int userId, int id, string snapshotId, string path, bool zip, CancellationToken ct) =>
        WithFolderAsync(id, userId, snapshotId, async folder =>
        {
            var serverKey = () => _folderService.ServerKey(folder);
            var (browser, snapshot, node) = await _snapshots.ReadAsync(folder, serverKey, snapshotId, async b =>
            {
                var s = await b.LoadSnapshotAsync(snapshotId, ct);
                return (b, s, await b.FindAsync(s, path, ct));
            }, ct);

            if (zip ? node.Type != NodeType.Directory : node.Type != NodeType.File)
                return BadRequest(new { message = zip ? $"\"{path}\" is not a directory." : $"\"{path}\" is not a file." });
            if (!zip && !browser.CanRead(node))
                await browser.Repository.LoadIndexAsync(ct); // written after the index was loaded, e.g. by a repack

            // A shared lock, so a prune does not delete the data while it is being sent.
            await using var repoLock = await browser.Repository.LockAsync(exclusive: false, $"download by user {userId} (web)", ct);
            var name = node.Name.Length > 0 ? node.Name : SafeFileName(folder.Name);
            Response.ContentType = zip ? "application/zip" : "application/octet-stream";
            Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = zip ? $"{name}-{snapshot.Time.UtcDateTime:yyyyMMdd-HHmm}.zip" : name,
                FileName = Ascii(zip ? $"{name}.zip" : name),
            }.ToString();
            Response.Headers.CacheControl = "no-store";
            try
            {
                if (zip)
                {
                    var lastRefresh = DateTime.UtcNow;
                    await browser.WriteZipAsync(node, Response.Body, async () =>
                    {
                        if (DateTime.UtcNow - lastRefresh < TimeSpan.FromMinutes(5)) return;
                        lastRefresh = DateTime.UtcNow;
                        await repoLock.RefreshAsync(ct);
                    }, ct);
                }
                else
                {
                    Response.ContentLength = node.Size;
                    await browser.CopyFileAsync(node, Response.Body, ct);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException && Response.HasStarted)
            {
                // Too late for an error response: abort, so the browser shows a failed download, not a truncated file.
                _logger.LogWarning(e, "Snapshot download of folder {FolderId} failed part-way.", folder.Id);
                HttpContext.Abort();
            }
            return new EmptyResult();
        }, ct);

    /// <summary>Loads the folder (owner only), checks it can be browsed, and maps engine and storage errors to responses.</summary>
    private async Task<IActionResult> WithFolderAsync(int id, int userId, string? snapshotId, Func<Folder, Task<IActionResult>> action, CancellationToken ct)
    {
        var folder = await _folders.GetAsync(id);
        if (folder is null || folder.UserId != userId) return NotFound(new { message = "No such folder." });
        if (SnapshotService.CannotBrowse(folder) is { } refusal) return StatusCode(refusal.Status, new { message = refusal.Message });
        try
        {
            return await action(folder);
        }
        catch (FileNotFoundException e) { return NotFound(new { message = e.Message }); }
        catch (StorageNotFoundException) when (snapshotId is not null) { return NotFound(new { message = "No such snapshot." }); }
        catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
        catch (RepositoryLockedException e) { return Conflict(new { message = e.Message }); }
        catch (Exception e) when (e is IOException or InvalidDataException or CryptographicException or HttpRequestException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not read the snapshots of folder {FolderId}.", id);
            ct.ThrowIfCancellationRequested();
            return StatusCode(StatusCodes.Status502BadGateway, new { message = $"Could not read the backup repository: {e.Message}" });
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c)]).Trim();
        return cleaned.Length == 0 ? "snapshot" : cleaned;
    }

    private static string Ascii(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_');
        return sb.ToString();
    }
}
