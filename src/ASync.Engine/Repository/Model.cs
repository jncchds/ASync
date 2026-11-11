using System.Text.Json.Serialization;
using ASync.Engine.Chunking;
using ASync.Engine.Crypto;

namespace ASync.Engine.Repository;

/// <summary><c>config</c>: the only plaintext object besides key files. Identifies the format and the chunker parameters.</summary>
public sealed record RepositoryConfig(int Version, string Id, ChunkerParameters Chunker, DateTimeOffset Created)
{
    public const int CurrentVersion = 1;
}

public enum BlobType { Data, Tree }

/// <summary>Where a blob lives: a byte range of an encrypted pack.</summary>
public sealed record BlobLocation(string Pack, BlobType Type, long Offset, int Length, int RawLength);

/// <summary>One blob entry of a pack header or an index file.</summary>
public sealed record PackBlob(BlobId Id, BlobType Type, long Offset, int Length, int RawLength);

public sealed record PackEntry(string Id, long Size, List<PackBlob> Blobs);

/// <summary><c>index/&lt;id&gt;</c>: which blobs are in which packs. Rebuildable from the pack headers.</summary>
public sealed record IndexFile(List<PackEntry> Packs);

public enum NodeType { File, Directory, Symlink }

/// <summary>
/// One directory of a snapshot. Serialized deterministically (nodes sorted by name), so an unchanged directory
/// produces the same tree blob and is stored once across all snapshots.
/// </summary>
public sealed record Tree(List<TreeNode> Nodes);

public sealed record TreeNode
{
    public required string Name { get; init; }
    public required NodeType Type { get; init; }
    public long Size { get; init; }
    /// <summary>Last write time, Unix milliseconds (UTC).</summary>
    public long Modified { get; init; }
    /// <summary>Unix permission bits where the platform has them.</summary>
    public int? Mode { get; init; }
    public List<BlobId>? Content { get; init; }
    public BlobId? Subtree { get; init; }
    public string? LinkTarget { get; init; }
}

/// <summary><c>snapshots/&lt;id&gt;</c>: the state of a source at a moment.</summary>
public sealed record Snapshot
{
    public required string Id { get; init; }
    public required DateTimeOffset Time { get; init; }
    public required BlobId Tree { get; init; }
    /// <summary>What was backed up (a device's folder, "photos"…). Retention policies apply per source.</summary>
    public required string Source { get; init; }
    public string? DeviceId { get; init; }
    public string? Hostname { get; init; }
    public string? Parent { get; init; }
    public List<string> Tags { get; init; } = [];
    public SnapshotStats Stats { get; init; } = new();
}

public sealed record SnapshotStats
{
    public int FilesNew { get; set; }
    public int FilesChanged { get; set; }
    public int FilesUnmodified { get; set; }
    public int Directories { get; set; }
    public long BytesProcessed { get; set; }
    /// <summary>Bytes of new chunks stored (before compression and encryption).</summary>
    public long BytesAdded { get; set; }
    public long BytesStored { get; set; }
    public List<string> Errors { get; set; } = [];
}

public sealed record LockFile(bool Exclusive, string Owner, DateTimeOffset Created, DateTimeOffset Refreshed);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RepositoryConfig))]
[JsonSerializable(typeof(KeyFile))]
[JsonSerializable(typeof(IndexFile))]
[JsonSerializable(typeof(List<PackBlob>))]
[JsonSerializable(typeof(Tree))]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(LockFile))]
internal partial class EngineJson : JsonSerializerContext;
