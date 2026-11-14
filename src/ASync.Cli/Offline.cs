using System.Text.Json;
using System.Text.Json.Serialization;
using ASync.Contracts;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using ASync.Storage;

namespace ASync.Cli;

/// <summary>
/// Opening a Backup folder's repository straight from its storage, with no server and no enrolled device: for
/// restoring after the server (or its database) is gone. The repository is
/// <list type="bullet">
/// <item><c>--repo &lt;directory&gt;</c>: a local directory (a disk, a mounted share, a copy of the storage), or</item>
/// <item><c>--storage &lt;settings.json&gt; --repo &lt;path&gt;</c>: the folder's directory on an S3, WebDAV, SFTP or SMB
///   back-end, with the connection settings in the same JSON shape the server's API takes
///   (<c>{"type": "S3", "endpoint": …, "bucket": …, …}</c>).</item>
/// </list>
/// It opens with a recovery key (standard or end-to-end folders), an end-to-end passphrase, or a key that the
/// server's <c>secrets export-folder-keys</c> printed.
/// </summary>
internal static class Offline
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        AllowOutOfOrderMetadataProperties = true,
    };

    public static IStorageBackend OpenStorage(string repo, string? storageFile)
    {
        if (storageFile is null)
        {
            var path = Path.GetFullPath(repo);
            if (!Directory.Exists(path)) throw new CliException($"The directory {path} does not exist.");
            return new LocalBackend(path);
        }

        StorageSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<StorageSettings>(File.ReadAllText(storageFile), Json)
                       ?? throw new CliException($"{storageFile} holds no storage settings.");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException)
        {
            throw new CliException($"Cannot read the storage settings in {storageFile}: {e.Message}");
        }
        var backend = StorageBackendFactory.Create(settings);
        string prefix;
        try { prefix = StoragePath.Normalize(repo); }
        catch (ArgumentException e) { throw new CliException($"--repo: {e.Message}"); }
        return prefix.Length == 0 ? backend : new PrefixedBackend(backend, prefix);
    }

    public static async Task<Repository> OpenAsync(IStorageBackend backend, string secret, CancellationToken ct)
    {
        if (!await Repository.ExistsAsync(backend, ct))
            throw new CliException("There is no backup repository there (no `config` file). Point --repo at the folder's directory on the storage.");
        try
        {
            return await Repository.OpenWithSecretAsync(backend, secret, ct);
        }
        catch (WrongKeyException)
        {
            throw new CliException("That key does not open this repository. Use the folder's recovery key (or, for an end-to-end folder, its passphrase).");
        }
    }
}
