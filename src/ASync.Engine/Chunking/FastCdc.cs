using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace ASync.Engine.Chunking;

/// <summary>
/// Content-defined chunking (FastCDC, Xia et al. 2016, with normalized chunking). Cut points depend on the data
/// itself, so inserting a byte near the start of a file only changes one or two chunks instead of every chunk
/// after it — the basis of deduplication across versions, files and devices.
/// <para>
/// The gear table is derived from SHA-256 of fixed text, so every platform and version cuts identically. Changing
/// the table or the parameters changes every chunk id: treat them as part of the repository format.
/// </para>
/// </summary>
public sealed class FastCdc
{
    public static readonly ChunkerParameters Default = new(MinSize: 512 * 1024, AverageSize: 1024 * 1024, MaxSize: 8 * 1024 * 1024);

    private static readonly ulong[] Gear = BuildGear();

    private readonly ChunkerParameters _p;
    private readonly ulong _maskSmall; // stricter: used before the average size, so chunks rarely end early
    private readonly ulong _maskLarge; // looser: used after the average size, so chunks rarely run to the maximum

    public FastCdc(ChunkerParameters? parameters = null)
    {
        _p = parameters ?? Default;
        _p.Validate();
        var bits = BitOperations.Log2((uint)_p.AverageSize);
        // Top bits of the gear hash depend on the last 64 bytes, giving a 64-byte rolling window.
        _maskSmall = Mask(bits + 2);
        _maskLarge = Mask(bits - 2);
    }

    public ChunkerParameters Parameters => _p;

    private static ulong Mask(int bits) => bits >= 64 ? ulong.MaxValue : ((1UL << bits) - 1) << (64 - bits);

    private static ulong[] BuildGear()
    {
        var table = new ulong[256];
        Span<byte> seed = stackalloc byte[32];
        for (var i = 0; i < 256; i += 4)
        {
            SHA256.HashData(System.Text.Encoding.ASCII.GetBytes($"async-fastcdc-gear-v1:{i / 4}"), seed);
            for (var j = 0; j < 4; j++) table[i + j] = BinaryPrimitives.ReadUInt64LittleEndian(seed[(j * 8)..]);
        }
        return table;
    }

    /// <summary>Length of the first chunk in <paramref name="data"/>. When <paramref name="data"/> is shorter than the maximum size, the caller must pass all remaining input.</summary>
    public int NextCut(ReadOnlySpan<byte> data)
    {
        var n = data.Length;
        if (n <= _p.MinSize) return n;
        if (n > _p.MaxSize) n = _p.MaxSize;
        var normal = Math.Min(_p.AverageSize, n);

        ulong fp = 0;
        var i = _p.MinSize;
        for (; i < normal; i++)
        {
            fp = (fp << 1) + Gear[data[i]];
            if ((fp & _maskSmall) == 0) return i + 1;
        }
        for (; i < n; i++)
        {
            fp = (fp << 1) + Gear[data[i]];
            if ((fp & _maskLarge) == 0) return i + 1;
        }
        return n;
    }

    /// <summary>
    /// Splits a stream into chunks. Each chunk's memory is only valid until the next one is requested
    /// (the buffer is reused), so copy it if it must live longer.
    /// </summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SplitAsync(Stream input,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[_p.MaxSize * 2];
        int start = 0, end = 0;
        var eof = false;
        while (true)
        {
            // Keep at least MaxSize bytes buffered (or everything up to EOF) so cut points are decided on full data.
            if (!eof && end - start < _p.MaxSize)
            {
                if (start > 0)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }
                while (end < buffer.Length)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(end), ct);
                    if (read == 0) { eof = true; break; }
                    end += read;
                }
            }
            if (end == start) yield break;

            var cut = NextCut(buffer.AsSpan(start, end - start));
            yield return buffer.AsMemory(start, cut);
            start += cut;
        }
    }
}

public record ChunkerParameters(int MinSize, int AverageSize, int MaxSize)
{
    public void Validate()
    {
        if (MinSize < 64 || !BitOperations.IsPow2(AverageSize) || MinSize >= AverageSize || AverageSize >= MaxSize)
            throw new ArgumentException("Chunker parameters need 64 ≤ min < average < max, with a power-of-two average.");
    }
}
