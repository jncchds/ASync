using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ASync.Engine.Crypto;

namespace ASync.Engine.Repository;

/// <summary>
/// Packs group many blobs into one storage object (fewer, larger objects suit S3 and WebDAV far better than
/// one object per chunk). Layout: blob₁ | blob₂ | … | encrypted header (JSON blob list) | header length (4 bytes LE).
/// Each blob is encrypted on its own, so a reader fetches just its byte range. The pack id is the SHA-256 of the
/// whole pack, so storage corruption is detectable.
/// </summary>
internal sealed class PackWriter(RepositoryKeys keys)
{
    private readonly MemoryStream _buffer = new();
    private readonly List<PackBlob> _blobs = [];

    public long Size => _buffer.Length;
    public int Count => _blobs.Count;

    public void Add(BlobId id, BlobType type, byte[] sealedBlob, int rawLength)
    {
        _blobs.Add(new PackBlob(id, type, _buffer.Length, sealedBlob.Length, rawLength));
        _buffer.Write(sealedBlob);
    }

    public (string Id, byte[] Content, PackEntry Entry) Finish()
    {
        var header = keys.Encrypt(JsonSerializer.SerializeToUtf8Bytes(_blobs, EngineJson.Default.ListPackBlob), "async-pack-header"u8);
        _buffer.Write(header);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, header.Length);
        _buffer.Write(length);

        var content = _buffer.ToArray();
        var id = Convert.ToHexStringLower(SHA256.HashData(content));
        return (id, content, new PackEntry(id, content.Length, [.. _blobs]));
    }

    public static string PathOf(string packId) => $"data/{packId[..2]}/{packId}";

    /// <summary>Reads a pack's header (used to rebuild the index and to check packs).</summary>
    public static List<PackBlob> ReadHeader(RepositoryKeys keys, ReadOnlySpan<byte> pack)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(pack[^4..]);
        if (length <= 0 || length > pack.Length - 4) throw new InvalidDataException("Corrupt pack header length.");
        var header = keys.Decrypt(pack.Slice(pack.Length - 4 - length, length), "async-pack-header"u8);
        return JsonSerializer.Deserialize(header, EngineJson.Default.ListPackBlob) ?? [];
    }
}

/// <summary>Seals one blob: optional Brotli compression, then encryption. The plaintext starts with a flag byte.</summary>
internal static class BlobCodec
{
    private const byte Raw = 0;
    private const byte Brotli = 1;

    public static byte[] Seal(RepositoryKeys keys, ReadOnlySpan<byte> plaintext, bool tryCompress, ReadOnlySpan<byte> associatedData = default) =>
        keys.Encrypt(Encode(plaintext, tryCompress), associatedData);

    public static byte[] Open(RepositoryKeys keys, ReadOnlySpan<byte> sealedBlob, int? rawLength, ReadOnlySpan<byte> associatedData = default) =>
        Decode(keys.Decrypt(sealedBlob, associatedData), rawLength);

    private static byte[] Encode(ReadOnlySpan<byte> plaintext, bool tryCompress)
    {
        byte[]? payload = null;
        if (tryCompress && plaintext.Length > 64)
        {
            var compressed = new byte[1 + BrotliEncoder.GetMaxCompressedLength(plaintext.Length)];
            // Quality 3 / window 22: fast enough to keep up with disks, and most of Brotli's gain on text.
            if (BrotliEncoder.TryCompress(plaintext, compressed.AsSpan(1), out var written, quality: 3, window: 22)
                && written < plaintext.Length * 0.95)
            {
                compressed[0] = Brotli;
                payload = compressed[..(written + 1)];
            }
        }
        if (payload is null)
        {
            payload = new byte[plaintext.Length + 1];
            payload[0] = Raw;
            plaintext.CopyTo(payload.AsSpan(1));
        }
        return payload;
    }

    /// <summary><paramref name="rawLength"/> is known for blobs (from the index); whole objects pass null.</summary>
    private static byte[] Decode(byte[] payload, int? rawLength)
    {
        switch (payload[0])
        {
            case Raw:
                return payload[1..];
            case Brotli when rawLength is int length:
                var output = new byte[length];
                if (!BrotliDecoder.TryDecompress(payload.AsSpan(1), output, out var written) || written != length)
                    throw new InvalidDataException("A blob did not decompress to its recorded size.");
                return output;
            case Brotli:
                using (var input = new MemoryStream(payload, 1, payload.Length - 1))
                using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
                using (var buffer = new MemoryStream())
                {
                    brotli.CopyTo(buffer);
                    return buffer.ToArray();
                }
            default:
                throw new InvalidDataException($"Unknown blob encoding {payload[0]}.");
        }
    }
}
