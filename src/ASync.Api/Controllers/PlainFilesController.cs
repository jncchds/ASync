using System.Text;
using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ASync.Api.Controllers;

/// <summary>
/// The files of a Mirror/TwoWay folder as they are on the storage: browse, download (files, or directories as a ZIP),
/// earlier versions, the trash, and restores. Owner only. See <see cref="PlainFileService"/>.
/// </summary>
[ApiController]
[Route("api/folders/{id:int}/files")]
[Authorize(Roles = Roles.Users)]
public class PlainFilesController : ControllerBase
{
    private readonly IFolderRepository _folders;
    private readonly PlainFileService _files;
    private readonly ILogger<PlainFilesController> _logger;

    public PlainFilesController(IFolderRepository folders, PlainFileService files, ILogger<PlainFilesController> logger)
    {
        _folders = folders;
        _files = files;
        _logger = logger;
    }

    /// <summary>One directory (<paramref name="path"/> "" = the folder root). <c>.async/</c> is not listed.</summary>
    [HttpGet]
    public Task<IActionResult> List(int id, [FromQuery] string? path, CancellationToken ct) =>
        WithFolderAsync(id, User.UserId(), async folder => Ok(await (await _files.OpenAsync(folder, ct)).ListDirectoryAsync(path ?? "", ct)), ct);

    /// <summary>A file's earlier versions, newest first.</summary>
    [HttpGet("history")]
    public Task<IActionResult> History(int id, [FromQuery] string? path, CancellationToken ct) =>
        WithFolderAsync(id, User.UserId(), async folder => Ok(await (await _files.OpenAsync(folder, ct)).HistoryAsync(path ?? "", ct)), ct);

    /// <summary>Every file that has earlier versions.</summary>
    [HttpGet("versions")]
    public Task<IActionResult> Versions(int id, CancellationToken ct) =>
        WithFolderAsync(id, User.UserId(), async folder => Ok(await (await _files.OpenAsync(folder, ct)).VersionedFilesAsync(ct)), ct);

    /// <summary>Deleted files, grouped by the minute they were deleted, newest first.</summary>
    [HttpGet("trash")]
    public Task<IActionResult> Trash(int id, CancellationToken ct) =>
        WithFolderAsync(id, User.UserId(), async folder => Ok(await (await _files.OpenAsync(folder, ct)).TrashAsync(ct)), ct);

    /// <summary>Deletes everything in the trash for good. Needs <c>?confirm=true</c>.</summary>
    [HttpDelete("trash")]
    public Task<IActionResult> EmptyTrash(int id, [FromQuery] bool confirm, CancellationToken ct) =>
        WithFolderAsync(id, User.UserId(), async folder =>
        {
            if (!confirm) return BadRequest(new { message = "Emptying the trash deletes its files for good: confirm it." });
            var deleted = await (await _files.OpenAsync(folder, ct)).Store.EmptyTrashAsync(ct);
            _logger.LogInformation("User {UserId} emptied the trash of folder {FolderId} ({Files} files).", folder.UserId, folder.Id, deleted);
            return Ok(new PlainEmptyTrashResultDto(deleted));
        }, ct);

    /// <summary>
    /// Restores versions and trashed files. 409 while a device is syncing the folder, and (with
    /// <see cref="PlainRestoreConflict.Refuse"/>) when a trashed file's path is taken now.
    /// </summary>
    [HttpPost("restore")]
    public Task<IActionResult> Restore(int id, [FromBody] PlainRestoreRequest req, CancellationToken ct) =>
        WithFolderAsync(id, User.UserId(), async folder =>
        {
            try
            {
                var result = await _files.RestoreAsync(folder, req, ct);
                _logger.LogInformation("User {UserId} restored {Count} file(s) in folder {FolderId}.", folder.UserId, result.RestoredTo.Length, folder.Id);
                return Ok(result);
            }
            catch (FolderBusyException e)
            {
                var device = folder.Devices.FirstOrDefault(d => d.DeviceId == e.Holder)?.Device?.Name;
                return Conflict(new
                {
                    message = device is null
                        ? "Another restore of this folder is running. Try again in a moment."
                        : $"{device} is syncing this folder right now. Try again in a moment.",
                    busy = true,
                });
            }
            catch (PlainRestoreConflictException e)
            {
                return Conflict(new PlainRestoreConflictDto(e.Message, e.Conflicts));
            }
        }, ct);

    /// <summary>Downloads a file (a current one, a version or a trashed one), or a current directory as a ZIP.</summary>
    [HttpGet("download")]
    public Task<IActionResult> Download(int id, [FromQuery] PlainSource source, [FromQuery] string? path, [FromQuery] string? stamp,
        [FromQuery] bool zip, CancellationToken ct) =>
        DownloadAsync(User.UserId(), id, new PlainDownloadLinkRequest(source, path, stamp, zip), ct);

    /// <summary>A link valid for a few minutes that downloads without an Authorization header (see the snapshot browser).</summary>
    [HttpPost("download-link")]
    public Task<IActionResult> DownloadLink(int id, [FromBody] PlainDownloadLinkRequest req, CancellationToken ct) =>
        WithFolderAsync(id, User.UserId(), folder =>
        {
            if (req.Zip && req.Source != PlainSource.Current) return Task.FromResult<IActionResult>(BadRequest(new { message = "Only current directories can be downloaded as a ZIP." }));
            PlainBrowser.StoragePathOf(req.Source, req.Path ?? "", req.Stamp); // validates the path and stamp (400)
            var (ticket, expires) = _files.IssueTicket(new PlainDownloadTicket(folder.UserId, folder.Id, req.Source,
                StoragePath.Normalize(req.Path ?? ""), req.Stamp, req.Zip));
            return Task.FromResult<IActionResult>(Ok(new SnapshotDownloadLinkDto($"/api/file-downloads?t={Uri.EscapeDataString(ticket)}", expires)));
        }, ct);

    /// <summary>Where download links lead. The ticket is the authorization, and the folder's owner is checked again.</summary>
    [HttpGet("/api/file-downloads")]
    [AllowAnonymous]
    public Task<IActionResult> DownloadByTicket([FromQuery(Name = "t")] string? ticket, CancellationToken ct)
    {
        var grant = ticket is null ? null : _files.ReadTicket(ticket);
        if (grant is null)
            return Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status403Forbidden, new { message = "This download link has expired. Start the download again." }));
        return DownloadAsync(grant.UserId, grant.FolderId, new PlainDownloadLinkRequest(grant.Source, grant.Path, grant.Stamp, grant.Zip), ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<IActionResult> DownloadAsync(int userId, int id, PlainDownloadLinkRequest req, CancellationToken ct) =>
        WithFolderAsync(id, userId, async folder =>
        {
            var browser = await _files.OpenAsync(folder, ct);
            var path = StoragePath.Normalize(req.Path ?? "");
            StorageEntry? file = null;
            if (req.Zip)
            {
                if (req.Source != PlainSource.Current) return BadRequest(new { message = "Only current directories can be downloaded as a ZIP." });
            }
            else file = await browser.FindFileAsync(req.Source, path, req.Stamp, ct);

            var name = path.Length == 0 ? SafeFileName(folder.Name) : path[(path.LastIndexOf('/') + 1)..];
            var shown = req.Source switch
            {
                PlainSource.Version when req.Stamp is { Length: >= 13 } s => Suffixed(name, $"version {s[..8]}-{s[9..13]}"),
                PlainSource.Trash when req.Stamp is { Length: >= 13 } s => Suffixed(name, $"deleted {s[..8]}-{s[9..13]}"),
                _ => req.Zip ? $"{name}.zip" : name,
            };
            Response.ContentType = req.Zip ? "application/zip" : "application/octet-stream";
            Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = shown, FileName = Ascii(shown) }.ToString();
            Response.Headers.CacheControl = "no-store";
            try
            {
                if (req.Zip) await browser.WriteZipAsync(path, Response.Body, ct);
                else
                {
                    Response.ContentLength = file!.Size;
                    await browser.CopyFileAsync(file, Response.Body, ct);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException && Response.HasStarted)
            {
                // Too late for an error response: abort, so the browser shows a failed download, not a truncated file.
                _logger.LogWarning(e, "File download of folder {FolderId} failed part-way.", folder.Id);
                HttpContext.Abort();
            }
            return new EmptyResult();
        }, ct);

    /// <summary>Loads the folder (owner only), checks it is a plain folder, and maps storage errors to responses.</summary>
    private async Task<IActionResult> WithFolderAsync(int id, int userId, Func<Folder, Task<IActionResult>> action, CancellationToken ct)
    {
        var folder = await _folders.GetAsync(id);
        if (folder is null || folder.UserId != userId) return NotFound(new { message = "No such folder." });
        if (PlainFileService.CannotBrowse(folder) is { } refusal) return StatusCode(refusal.Status, new { message = refusal.Message });
        try
        {
            return await action(folder);
        }
        catch (FileNotFoundException e) { return NotFound(new { message = e.Message }); }
        catch (StorageNotFoundException) { return NotFound(new { message = "That file no longer exists." }); }
        catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not read the files of folder {FolderId}.", id);
            ct.ThrowIfCancellationRequested();
            if (Response.HasStarted) { HttpContext.Abort(); return new EmptyResult(); }
            return StatusCode(StatusCodes.Status502BadGateway, new { message = $"Could not read the storage: {e.Message}" });
        }
    }

    /// <summary><c>report.pdf</c> → <c>report (version 20261114-1203).pdf</c>.</summary>
    private static string Suffixed(string name, string suffix)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{name[..dot]} ({suffix}){name[dot..]}" : $"{name} ({suffix})";
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c)]).Trim();
        return cleaned.Length == 0 ? "folder" : cleaned;
    }

    private static string Ascii(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_');
        return sb.ToString();
    }
}
