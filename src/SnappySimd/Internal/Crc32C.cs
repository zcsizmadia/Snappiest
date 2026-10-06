using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SnappySimd.Internal;

/// <summary>
/// CRC-32C (Castagnoli), as required by the Snappy framing format.
/// </summary>
/// <remarks>
/// On x64 (SSE4.2) and arm64 (CRC32) the hardware instruction is used. Its throughput is about three times its
/// latency, so large inputs are processed as three independent streams whose results are combined with a
/// precomputed GF(2) shift, which roughly triples throughput over a single dependency chain.
/// </remarks>
internal static class Crc32C
{
    // Reflected Castagnoli polynomial
    private const uint Poly = 0x82F63B78;

    // Lane sizes for the 3-way interleaved hardware path
    private const int LongLane = 8192;
    private const int ShortLane = 256;

    private static readonly uint[] s_softwareTable = BuildSoftwareTable();

    // Byte-wise lookup tables that shift a CRC state across LongLane / ShortLane zero bytes
    private static readonly uint[] s_shiftLong = BuildShiftTable(LongLane);
    private static readonly uint[] s_shiftShort = BuildShiftTable(ShortLane);

    internal static bool IsHardwareAccelerated => Sse42.X64.IsSupported || Crc32.Arm64.IsSupported;

    /// <summary>Computes the CRC-32C of <paramref name="source"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> source) => ~Update(~0u, source);

    /// <summary>Continues the CRC-32C <paramref name="crc"/> over <paramref name="source"/>.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> source) => ~Update(~crc, source);

    /// <summary>Applies the Snappy framing format CRC mask.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ApplyMask(uint crc) => ((crc >> 15) | (crc << 17)) + 0xa282ead8;

    /// <summary>Computes the masked CRC-32C used in Snappy framing chunks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ComputeMasked(ReadOnlySpan<byte> source) => ApplyMask(Compute(source));

    // Operates on the raw (non-inverted) CRC register
    internal static uint Update(uint state, ReadOnlySpan<byte> source) =>
        IsHardwareAccelerated ? UpdateHardware(state, source) : UpdateSoftware(state, source);

    internal static uint UpdateHardware(uint state, ReadOnlySpan<byte> source)
    {
        ref byte p = ref MemoryMarshal.GetReference(source);
        nuint length = (nuint)source.Length;

        while (length >= 3 * LongLane)
        {
            state = Interleave3(state, ref p, LongLane, s_shiftLong);
            p = ref Unsafe.Add(ref p, 3 * LongLane);
            length -= 3 * LongLane;
        }

        while (length >= 3 * ShortLane)
        {
            state = Interleave3(state, ref p, ShortLane, s_shiftShort);
            p = ref Unsafe.Add(ref p, 3 * ShortLane);
            length -= 3 * ShortLane;
        }

        while (length >= 8)
        {
            state = Step(state, Unsafe.ReadUnaligned<ulong>(ref p));
            p = ref Unsafe.Add(ref p, 8);
            length -= 8;
        }

        while (length > 0)
        {
            state = Step(state, p);
            p = ref Unsafe.Add(ref p, 1);
            length--;
        }

        return state;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Interleave3(uint state, ref byte p, int lane, uint[] shiftTable)
    {
        uint crc0 = state, crc1 = 0, crc2 = 0;
        ref byte p1 = ref Unsafe.Add(ref p, lane);
        ref byte p2 = ref Unsafe.Add(ref p, 2 * lane);

        for (nint i = 0; i < lane; i += 8)
        {
            crc0 = Step(crc0, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i)));
            crc1 = Step(crc1, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p1, i)));
            crc2 = Step(crc2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p2, i)));
        }

        // state(A || B) = shift(state(A), |B|) ^ state0(B), where state0 starts from zero
        crc0 = Shift(shiftTable, crc0) ^ crc1;
        return Shift(shiftTable, crc0) ^ crc2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Shift(uint[] table, uint crc)
    {
        ref uint t = ref MemoryMarshal.GetArrayDataReference(table);
        return Unsafe.Add(ref t, (nint)(crc & 0xFF))
               ^ Unsafe.Add(ref t, 256 + (nint)((crc >> 8) & 0xFF))
               ^ Unsafe.Add(ref t, 512 + (nint)((crc >> 16) & 0xFF))
               ^ Unsafe.Add(ref t, 768 + (nint)(crc >> 24));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Step(uint crc, ulong data)
    {
        if (Sse42.X64.IsSupported)
        {
            return (uint)Sse42.X64.Crc32(crc, data);
        }

        // Only called when IsHardwareAccelerated, so this is arm64
        return Crc32.Arm64.ComputeCrc32C(crc, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Step(uint crc, byte data)
    {
        if (Sse42.IsSupported)
        {
            return Sse42.Crc32(crc, data);
        }

        // Only called when IsHardwareAccelerated, so this is arm64
        return Crc32.ComputeCrc32C(crc, data);
    }

    // Slicing-by-8
    internal static uint UpdateSoftware(uint state, ReadOnlySpan<byte> source)
    {
        ref uint table = ref MemoryMarshal.GetArrayDataReference(s_softwareTable);

        while (source.Length >= 8)
        {
            uint lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source) ^ state;
            uint hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4));

            state = Unsafe.Add(ref table, (7 * 256) + (nint)(lo & 0xFF))
                    ^ Unsafe.Add(ref table, (6 * 256) + (nint)((lo >> 8) & 0xFF))
                    ^ Unsafe.Add(ref table, (5 * 256) + (nint)((lo >> 16) & 0xFF))
                    ^ Unsafe.Add(ref table, (4 * 256) + (nint)(lo >> 24))
                    ^ Unsafe.Add(ref table, (3 * 256) + (nint)(hi & 0xFF))
                    ^ Unsafe.Add(ref table, (2 * 256) + (nint)((hi >> 8) & 0xFF))
                    ^ Unsafe.Add(ref table, 256 + (nint)((hi >> 16) & 0xFF))
                    ^ Unsafe.Add(ref table, (nint)(hi >> 24));

            source = source.Slice(8);
        }

        foreach (byte b in source)
        {
            state = Unsafe.Add(ref table, (nint)((state ^ b) & 0xFF)) ^ (state >> 8);
        }

        return state;
    }

    private static uint[] BuildSoftwareTable()
    {
        uint[] table = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ Poly : c >> 1;
            }

            table[i] = c;
        }

        for (int i = 0; i < 256; i++)
        {
            for (int s = 1; s < 8; s++)
            {
                uint prev = table[((s - 1) * 256) + i];
                table[(s * 256) + i] = table[prev & 0xFF] ^ (prev >> 8);
            }
        }

        return table;
    }

    // Table[k * 256 + b] = (b << 8k) * x^(8 * zeroBytes) mod P, so a shift is four lookups.
    private static uint[] BuildShiftTable(int zeroBytes)
    {
        uint op = PowerOfX(8L * zeroBytes);
        uint[] table = new uint[4 * 256];
        for (int k = 0; k < 4; k++)
        {
            for (uint b = 0; b < 256; b++)
            {
                table[(k * 256) + (int)b] = MultiplyModP(op, b << (8 * k));
            }
        }

        return table;
    }

    // x^n mod P, in reflected bit order (bit 31 is x^0)
    internal static uint PowerOfX(long n)
    {
        uint result = 1u << 31; // x^0
        uint square = 1u << 30; // x^1
        while (n > 0)
        {
            if ((n & 1) != 0)
            {
                result = MultiplyModP(square, result);
            }

            square = MultiplyModP(square, square);
            n >>= 1;
        }

        return result;
    }

    // a * b mod P, in reflected bit order
    internal static uint MultiplyModP(uint a, uint b)
    {
        uint m = 1u << 31;
        uint p = 0;
        while (m != 0)
        {
            if ((a & m) != 0)
            {
                p ^= b;
            }

            m >>= 1;
            b = (b & 1) != 0 ? (b >> 1) ^ Poly : b >> 1;
        }

        return p;
    }
}
