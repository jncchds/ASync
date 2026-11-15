using System.Buffers;
using System.Security.Cryptography;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Storage;
using Microsoft.EntityFrameworkCore;

namespace ASync.Api.Services;

/// <summary>The upload is unknown, expired, finished, or belongs to someone else: the device starts again.</summary>
public class UploadGoneException(string message = "This upload no longer exists; start it again.") : Exception(message);

/// <summary>The upload would take its owner past a quota (the gateway answers 507 with <c>X-Storage-Error: quota</c>).</summary>
public class QuotaExceededException(string message) : Exception(message);

/// <summary>
/// Server-wide settings and limits of resumable uploads (<c>Uploads:*</c>). Parts are held in memory while they are
/// checked and stored, so at most <see cref="MaxParallelParts"/> × <see cref="PartSize"/> bytes are in use at once; no
/// part or file is written to the server's disk.
/// </summary>
public sealed class UploadLimits(IConfiguration configuration)
{
    /// <summary><c>Uploads:PartSizeMB</c>, 5–64 (S3 needs 5 MiB parts), default 16.</summary>
    public int PartSize { get; } = Math.Clamp(configuration.GetValue("Uploads:PartSizeMB", 16), 5, 64) * 1024 * 1024;

    /// <summary><c>Uploads:MaxParallelParts</c>, default 4: further parts wait their turn.</summary>
    public int MaxParallelParts { get; } = Math.Max(1, configuration.GetValue("Uploads:MaxParallelParts", 4));

    /// <summary><c>Uploads:AbandonAfterHours</c>, default 24: an upload without a new part for this long is aborted.</summary>
    public TimeSpan AbandonAfter { get; } = TimeSpan.FromHours(Math.Max(1, configuration.GetValue("Uploads:AbandonAfterHours", 24.0)));

    /// <summary>
    /// <c>Uploads:MaxSinglePutMB</c>, default 256: the largest plain PUT (which the server buffers, in memory up to 32 MB,
    /// otherwise in a temporary file). Larger files go in parts; clients send parts above 32 MB.
    /// </summary>
    public long MaxSinglePut { get; } = Math.Max(1, configuration.GetValue("Uploads:MaxSinglePutMB", 256L)) * 1024 * 1024;

    public SemaphoreSlim Parallel { get; } = new(Math.Max(1, configuration.GetValue("Uploads:MaxParallelParts", 4)));
}

/// <summary>
/// Resumable uploads through the storage gateway (see <see cref="UploadSession"/>). Each part goes straight to the
/// folder's back-end (<see cref="IPartUploadBackend"/>: an S3 multipart upload, or a hidden file written at offsets) and
/// is acknowledged only once stored and matching its SHA-256; completing makes the file appear at once.
/// </summary>
/// <remarks>
/// A new upload is held to its owner's quota when it starts (its whole size, counted as used from then on, see
/// <see cref="UsageService"/>); continuing an unfinished one is never refused.
/// </remarks>
public class UploadService(IUploadSessionRepository sessions, UploadLimits limits, FolderBackendCache backends, TimeProvider clock,
    ILogger<UploadService> logger, UsageService usage)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static UploadSessionDto Dto(UploadSession s) =>
        new(s.Id, s.PartSize, s.PartCount, [.. s.Parts.Select(p => p.Number).Order()]);

    /// <summary>Starts an upload, or returns the device's unfinished one of the same file (same path, size and mtime).</summary>
    public async Task<UploadSession> StartAsync(Folder folder, Guid deviceId, IStorageBackend backend, StartUploadRequest req, CancellationToken ct)
    {
        var path = StoragePath.Normalize(req.Path);
        if (path.Length == 0) throw new ArgumentException("An upload needs a file path.");
        if (req.Size < 0) throw new ArgumentException("The size cannot be negative.");
        var version = folder.StorageBackend?.UpdatedAt ?? default;

        if (await sessions.FindAsync(folder.Id, deviceId, path) is { } existing)
        {
            if (existing.Size == req.Size && existing.LastModified == req.Mtime && existing.BackendVersion == version)
                return existing;
            // A different file (or the back-end was changed): what was sent so far is of no use.
            await AbortQuietlyAsync(backend, existing);
            await sessions.DeleteAsync(existing.Id);
        }

        if (await usage.CheckUploadAsync(folder, deviceId, path, req.Size) is { } refusal)
            throw new QuotaExceededException(refusal);
        var mtime = req.Mtime is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
        var handle = await PartUploads.For(backend).BeginPartsAsync(path, mtime, ct);
        var session = new UploadSession
        {
            Id = Guid.CreateVersion7(), FolderId = folder.Id, DeviceId = deviceId, Path = path, Size = req.Size, LastModified = req.Mtime,
            PartSize = limits.PartSize, Handle = handle, BackendVersion = version, CreatedAt = Now, UpdatedAt = Now,
        };
        try
        {
            await sessions.AddAsync(session);
        }
        catch (DbUpdateException)
        {
            // The same device started the same upload twice at once: keep the other one.
            await PartUploads.For(backend).AbortPartsAsync(path, handle, CancellationToken.None);
            if (await sessions.FindAsync(folder.Id, deviceId, path) is { } other) return other;
            throw;
        }
        // Counted when accepted, not when complete: parallel uploads cannot each slip under the quota.
        await usage.RecordAcceptedAsync(folder.Id, req.Size);
        logger.LogInformation("Upload {Id} started: {Path} ({Size} bytes in {Parts} parts) for folder {Folder}",
            session.Id, path, req.Size, session.PartCount, folder.Id);
        return session;
    }

    /// <summary>
    /// Stores part <paramref name="number"/> from <paramref name="body"/> (exactly its length), checked against
    /// <paramref name="sha256"/> when given. Only a stored, matching part is recorded.
    /// </summary>
    public async Task WritePartAsync(Folder folder, Guid deviceId, IStorageBackend backend, Guid id, int number, Stream body, string? sha256,
        CancellationToken ct)
    {
        var session = await OwnedAsync(folder, deviceId, id);
        if (number < 0 || number >= session.PartCount) throw new ArgumentException($"There is no part {number} (0–{session.PartCount - 1}).");
        var length = (int)session.PartLength(number);

        await limits.Parallel.WaitAsync(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            var read = 0;
            while (read < length)
            {
                var n = await body.ReadAsync(buffer.AsMemory(read, length - read), ct);
                if (n == 0) break;
                read += n;
            }
            if (read != length || await body.ReadAsync(new byte[1], ct) != 0)
                throw new ArgumentException($"Part {number} must be exactly {length} bytes.");
            var hash = Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, length)));
            if (sha256 is not null && !string.Equals(hash, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Part {number} arrived damaged (SHA-256 mismatch); send it again.");

            string? token;
            try
            {
                token = await PartUploads.For(backend).WritePartAsync(session.Path, session.Handle, number, (long)number * session.PartSize,
                    new MemoryStream(buffer, 0, length, writable: false), length, ct);
            }
            catch (StorageNotFoundException)
            {
                // The back-end no longer knows the upload (expired, aborted, its staging file removed).
                await sessions.DeleteAsync(session.Id);
                throw new UploadGoneException();
            }
            await sessions.SavePartAsync(new UploadSessionPart
            {
                SessionId = session.Id, Number = number, Size = length, Sha256 = hash, Token = token, CreatedAt = Now,
            }, Now);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            limits.Parallel.Release();
        }
    }

    /// <summary>Puts the file in place (atomically) once every part is there, and forgets the session.</summary>
    public async Task CompleteAsync(Folder folder, Guid deviceId, IStorageBackend backend, Guid id, CancellationToken ct)
    {
        var session = await OwnedAsync(folder, deviceId, id);
        var parts = session.Parts.OrderBy(p => p.Number).ToList();
        var missing = Enumerable.Range(0, session.PartCount).Except(parts.Select(p => p.Number)).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"{missing.Count} of {session.PartCount} parts are missing (the first is {missing[0]}).");
        var mtime = session.LastModified is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
        try
        {
            await PartUploads.For(backend).CompletePartsAsync(session.Path, session.Handle,
                [.. parts.Select(p => new UploadedPart(p.Number, (long)p.Number * session.PartSize, p.Size, p.Token))], session.Size, mtime, ct);
        }
        catch (StorageNotFoundException)
        {
            await sessions.DeleteAsync(session.Id);
            throw new UploadGoneException();
        }
        await sessions.DeleteAsync(session.Id);
        logger.LogInformation("Upload {Id} completed: {Path} ({Size} bytes)", session.Id, session.Path, session.Size);
    }

    public async Task AbortAsync(Folder folder, Guid deviceId, IStorageBackend backend, Guid id)
    {
        var session = await OwnedAsync(folder, deviceId, id);
        await AbortQuietlyAsync(backend, session);
        await sessions.DeleteAsync(session.Id);
    }

    /// <summary>Aborts uploads without a new part for <see cref="UploadLimits.AbandonAfter"/> (on the back-end too). Returns how many.</summary>
    public async Task<int> AbortIdleAsync(CancellationToken ct)
    {
        var idle = await sessions.GetIdleAsync(Now - limits.AbandonAfter);
        foreach (var session in idle)
        {
            ct.ThrowIfCancellationRequested();
            if (session.Folder?.StorageBackend is not null)
            {
                try
                {
                    await using var backend = await backends.OpenDedicatedAsync(session.Folder);
                    await AbortQuietlyAsync(backend, session);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning("Could not open the storage of folder {Folder} to abort upload {Id}: {Message}", session.FolderId, session.Id, e.Message);
                }
            }
            await sessions.DeleteAsync(session.Id);
        }
        return idle.Count;
    }

    private async Task<UploadSession> OwnedAsync(Folder folder, Guid deviceId, Guid id) =>
        await sessions.GetAsync(id) is { } s && s.FolderId == folder.Id && s.DeviceId == deviceId
            ? s.BackendVersion == (folder.StorageBackend?.UpdatedAt ?? default) ? s : throw new UploadGoneException("The folder's storage was changed; start the upload again.")
            : throw new UploadGoneException();

    private async Task AbortQuietlyAsync(IStorageBackend backend, UploadSession session)
    {
        try
        {
            await PartUploads.For(backend).AbortPartsAsync(session.Path, session.Handle, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // What is left is found by PlainStore.CleanUpAsync (files) or AbortStalePartsAsync (S3) later.
            logger.LogWarning("Could not abort upload {Id} on the storage: {Message}", session.Id, e.Message);
        }
    }
}
