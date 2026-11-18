using System.Net;
using System.Net.Http;
using ASync.Client.Core;
using ASync.Client.Core.Documents;
using ASync.Engine.Storage;

namespace ASync.App.iOS.FileProvider;

/// <summary>
/// What went wrong, in the terms the system understands. The extension turns each into an <c>NSError</c>: the
/// <c>NSFileProviderErrorDomain</c> codes for the first six (the Files app shows its own UI for "not signed in", "cannot reach
/// the server", a taken name), Cocoa's file errors for the rest, always with the message as <c>NSLocalizedDescription</c>.
/// </summary>
public enum FileProviderErrorKind
{
    /// <summary><c>NSFileProviderErrorNotAuthenticated</c>: not enrolled, removed from the account, the key not reachable.</summary>
    NotAuthenticated,
    /// <summary><c>NSFileProviderErrorServerUnreachable</c>: no connection, a timeout. The system retries later.</summary>
    ServerUnreachable,
    /// <summary><c>NSFileProviderErrorNoSuchItem</c>: gone on the server, no longer shared, an identifier that is not ours.</summary>
    NoSuchItem,
    /// <summary><c>NSFileProviderErrorFilenameCollision</c>: the name is taken in that directory.</summary>
    FilenameCollision,
    /// <summary><c>NSFileProviderErrorInsufficientQuota</c>: the storage quota is full.</summary>
    InsufficientQuota,
    /// <summary><c>NSFileProviderErrorDirectoryNotEmpty</c>: a non-recursive delete of a directory with items.</summary>
    DirectoryNotEmpty,
    /// <summary><c>NSFileProviderErrorSyncAnchorExpired</c>: the system enumerates again from scratch.</summary>
    SyncAnchorExpired,
    /// <summary>Cocoa's no-permission error: a Mirror folder or a backup (read-only here), a folder renamed only in the web UI.</summary>
    NotPermitted,
    /// <summary>Cocoa's invalid file name.</summary>
    InvalidName,
    /// <summary>Cocoa's user-cancelled: the system cancelled the request's progress.</summary>
    Cancelled,
    /// <summary>Anything else (Cocoa's unknown read or write error, with the message).</summary>
    Other,
}

public sealed record FileProviderError(FileProviderErrorKind Kind, string Message);

/// <summary>An error that already has its kind (the operations throw it where the catalog has no exception of its own).</summary>
public sealed class FileProviderException(FileProviderErrorKind kind, string message) : Exception(message)
{
    public FileProviderErrorKind Kind { get; } = kind;
}

public static class FileProviderErrors
{
    /// <summary>
    /// Maps what the catalog, the gateway or the store threw. <paramref name="account"/> tells an "access denied" because the
    /// device is not signed in (→ not authenticated) from one because the folder is read-only (→ not permitted): the
    /// catalog throws <see cref="UnauthorizedAccessException"/> for both. <paramref name="cancelled"/>: the request's own
    /// token, so a cancellation by the system is told apart from a timeout.
    /// </summary>
    public static FileProviderError Map(Exception e, DocumentsAccount account, bool cancelled = false)
    {
        while (e is AggregateException { InnerException: { } inner }) e = inner;
        return e switch
        {
            FileProviderException f => new(f.Kind, f.Message),
            OperationCanceledException when cancelled => new(FileProviderErrorKind.Cancelled, "Cancelled."),
            DeviceRevokedException => new(FileProviderErrorKind.NotAuthenticated, account.Problem ?? Removed),
            ApiException { Status: HttpStatusCode.Unauthorized } => new(FileProviderErrorKind.NotAuthenticated, account.Problem ?? Removed),
            UnauthorizedAccessException when !account.Enrolled || account.Problem is not null =>
                new(FileProviderErrorKind.NotAuthenticated, account.Problem ?? e.Message),
            UnauthorizedAccessException => new(FileProviderErrorKind.NotPermitted, e.Message),
            System.Security.Cryptography.CryptographicException when IsKeychain(e) => new(FileProviderErrorKind.NotAuthenticated, e.Message),
            HttpRequestException or TimeoutException or TaskCanceledException => new(FileProviderErrorKind.ServerUnreachable, DocumentCatalog.Describe(e)),
            StorageQuotaExceededException => new(FileProviderErrorKind.InsufficientQuota, e.Message),
            ApiException { Status: HttpStatusCode.InsufficientStorage or HttpStatusCode.RequestEntityTooLarge } => new(FileProviderErrorKind.InsufficientQuota, e.Message),
            ApiException { Status: HttpStatusCode.Forbidden or HttpStatusCode.NotFound } => new(FileProviderErrorKind.NoSuchItem, DocumentCatalog.Describe(e)),
            ApiException { Status: >= HttpStatusCode.InternalServerError } => new(FileProviderErrorKind.ServerUnreachable, e.Message),
            FileNotFoundException or DirectoryNotFoundException => new(FileProviderErrorKind.NoSuchItem, e.Message),
            IOException when e.Message.EndsWith("already exists there.", StringComparison.Ordinal) => new(FileProviderErrorKind.FilenameCollision, e.Message),
            ArgumentException => new(FileProviderErrorKind.InvalidName, e.Message),
            _ => new(FileProviderErrorKind.Other, DocumentCatalog.Describe(e)),
        };
    }

    public const string Removed = "This iPhone was removed from the account. Open ASync to enroll it again.";

    /// <summary>The Keychain's own failures (the phone not unlocked since it started, the key missing or not shared).</summary>
    private static bool IsKeychain(Exception e) => e.Message.Contains("Keychain", StringComparison.Ordinal) || e.Message.Contains("unlocked", StringComparison.Ordinal);

    /// <summary>A listing's error as an exception with the kind (the catalog keeps the exception it caught, when there was one).</summary>
    public static FileProviderError FromListing(DocumentListing listing, DocumentsAccount account) =>
        listing.Failure is { } failure ? Map(failure, account)
        : account.Problem is { } problem ? new(FileProviderErrorKind.NotAuthenticated, problem)
        : !account.Enrolled ? new(FileProviderErrorKind.NotAuthenticated, "This iPhone is not enrolled: open ASync.")
        : new(FileProviderErrorKind.Other, listing.Error ?? "The listing failed.");
}
