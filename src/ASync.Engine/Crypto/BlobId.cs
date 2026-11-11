using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASync.Engine;

/// <summary>A 256-bit content id (HMAC-SHA256 of the plaintext). Compact and hashable for large in-memory indexes; text form is lowercase hex.</summary>
[JsonConverter(typeof(BlobIdJsonConverter))]
public readonly record struct BlobId(UInt128 High, UInt128 Low) : IComparable<BlobId>
{
    public static BlobId FromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 32
            ? new(BinaryPrimitives.ReadUInt128BigEndian(bytes), BinaryPrimitives.ReadUInt128BigEndian(bytes[16..]))
            : throw new ArgumentException("A blob id is 32 bytes.");

    public static BlobId Parse(string hex) => FromBytes(Convert.FromHexString(hex));

    public static bool TryParse(string? hex, out BlobId id)
    {
        id = default;
        if (hex is not { Length: 64 }) return false;
        try { id = Parse(hex); return true; } catch (FormatException) { return false; }
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, High);
        BinaryPrimitives.WriteUInt128BigEndian(bytes.AsSpan(16), Low);
        return bytes;
    }

    public override string ToString() => Convert.ToHexStringLower(ToBytes());

    /// <summary>First 8 hex digits, for logs.</summary>
    public string Short => ToString()[..8];

    public int CompareTo(BlobId other) => High != other.High ? High.CompareTo(other.High) : Low.CompareTo(other.Low);
}

internal sealed class BlobIdJsonConverter : JsonConverter<BlobId>
{
    public override BlobId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        BlobId.Parse(reader.GetString() ?? throw new JsonException("Blob id expected."));

    public override void Write(Utf8JsonWriter writer, BlobId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
