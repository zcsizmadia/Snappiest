using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SnappySimd.Internal;

/// <summary>
/// Compresses data into raw Snappy blocks.
/// </summary>
/// <remarks>
/// <c>CompressFragment</c> is a port of <c>CompressFragment</c> from google/snappy 1.3.x: the first 16
/// positions after each match are probed without the skip heuristic, literals and short copies are emitted with
/// unconditional over-writes, and match extension reuses the bytes loaded while comparing.
/// </remarks>
internal static unsafe class BlockCompressor
{
    public const int BlockSize = 1 << 16;

    private const int MinHashTableBits = 8;
    // 2^14 entries (32KB): the same compression ratio as Snappier, and a smaller table to clear and keep in L1
    private const int MaxHashTableBits = 14;
    private const int MinHashTableSize = 1 << MinHashTableBits;
    private const int MaxHashTableSize = 1 << MaxHashTableBits;

    // Matches at least this long are extended 32 bytes at a time (benchmarked: 16 and 32 are slower on text)
    private const int VectorMatchThreshold = 64;

    // The main loop reads up to this many bytes past the current position
    private const int InputMarginBytes = 15;

    private const byte Literal = 0;
    private const byte Copy1ByteOffset = 1;
    private const byte Copy2ByteOffset = 2;

    // Per-thread hash table, pinned so the pointer stays valid without a fixed statement
    [ThreadStatic]
    private static ushort[]? t_hashTable;

    /// <summary>The worst case compressed size of a fragment, including the over-write slop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MaxFragmentLength(int fragmentLength) => 32 + fragmentLength + (fragmentLength / 6);

    /// <summary>The worst case compressed size of a whole block, including its length header.</summary>
    public static long MaxCompressedLength(long inputLength)
    {
        long fullBlocks = inputLength / BlockSize;
        int remainder = (int)(inputLength % BlockSize);
        long length = VarInt.MaxLength + (fullBlocks * MaxFragmentLength(BlockSize));
        if (remainder > 0)
        {
            length += MaxFragmentLength(remainder);
        }

        return length;
    }

    /// <summary>
    /// Compresses <paramref name="input"/> as a complete Snappy block, including the length header.
    /// </summary>
    /// <returns><c>false</c> if <paramref name="output"/> is too small.</returns>
    public static bool TryCompress(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten)
    {
        bytesWritten = 0;
        if (output.Length < VarInt.GetByteCount((uint)input.Length))
        {
            return false;
        }

        int written = VarInt.Write(output, (uint)input.Length);

        while (!input.IsEmpty)
        {
            ReadOnlySpan<byte> fragment = input.Slice(0, Math.Min(input.Length, BlockSize));
            Span<byte> remaining = output.Slice(written);

            int fragmentWritten;
            if (remaining.Length >= MaxFragmentLength(fragment.Length))
            {
                fragmentWritten = CompressFragment(fragment, remaining);
            }
            else
            {
                // The output may still be big enough for the actual result: compress to a scratch buffer
                byte[] scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(MaxFragmentLength(fragment.Length));
                try
                {
                    fragmentWritten = CompressFragment(fragment, scratch);
                    if (fragmentWritten > remaining.Length)
                    {
                        return false;
                    }

                    scratch.AsSpan(0, fragmentWritten).CopyTo(remaining);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
                }
            }

            written += fragmentWritten;
            input = input.Slice(fragment.Length);
        }

        bytesWritten = written;
        return true;
    }

    /// <summary>
    /// Compresses up to <see cref="BlockSize"/> bytes without a length header. <paramref name="output"/> must be at
    /// least <see cref="MaxFragmentLength"/> bytes.
    /// </summary>
    public static int CompressFragment(ReadOnlySpan<byte> fragment, Span<byte> output)
    {
        if (fragment.Length > BlockSize || output.Length < MaxFragmentLength(fragment.Length))
        {
            ThrowHelper.ThrowArgumentException("Invalid fragment or output size.", nameof(output));
        }

        if (fragment.IsEmpty)
        {
            return 0;
        }

        ushort* table = GetHashTable(fragment.Length, out int tableSize);

        fixed (byte* ip = fragment)
        fixed (byte* op = output)
        {
            return (int)(CompressFragment(ip, fragment.Length, op, table, tableSize) - op);
        }
    }

    private static ushort* GetHashTable(int fragmentLength, out int tableSize)
    {
        ushort[] table = t_hashTable ??= GC.AllocateUninitializedArray<ushort>(MaxHashTableSize, pinned: true);

        tableSize = fragmentLength >= MaxHashTableSize
            ? MaxHashTableSize
            : fragmentLength <= MinHashTableSize
                ? MinHashTableSize
                : 2 << BitOperations.Log2((uint)fragmentLength - 1);

        ushort* p = (ushort*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(table));
        NativeMemory.Clear(p, (nuint)tableSize * sizeof(ushort));
        return p;
    }

    // Hash of 4 bytes, returned as a byte offset into the table (mask is 2 * (tableSize - 1))
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort* TableEntry(ushort* table, uint bytes, uint mask)
    {
        uint hash;
        if (Sse42.IsSupported)
        {
            hash = Sse42.Crc32(bytes, mask);
        }
        else if (Crc32.IsSupported)
        {
            hash = Crc32.ComputeCrc32C(bytes, mask);
        }
        else
        {
            hash = (bytes * 0x1E35A7BDu) >> (31 - MaxHashTableBits);
        }

        return (ushort*)((byte*)table + (hash & mask));
    }

    private static byte* CompressFragment(byte* input, int inputSize, byte* op, ushort* table, int tableSize)
    {
        byte* ip = input;
        uint mask = (uint)(2 * (tableSize - 1));
        byte* ipEnd = input + inputSize;
        byte* baseIp = input;

        if (inputSize >= InputMarginBytes)
        {
            byte* ipLimit = input + inputSize - InputMarginBytes;

            while (true)
            {
                // Bytes in [nextEmit, ip) will be emitted as a literal
                byte* nextEmit = ip++;
                ulong data = 0;
                byte* candidate;

                // Heuristic match skipping: after 32 misses look at every other byte, after 32 more every third, ...
                uint skip = 32;

                if (ipLimit - ip >= 16)
                {
                    // Probe the next 16 positions back to back, without the skip bookkeeping. Each 8 byte load
                    // supplies four positions by shifting.
                    nint delta = (nint)(ip - baseIp);
                    ulong probe = Unsafe.ReadUnaligned<ulong>(ip);
                    nint i;
                    for (nint j = 0; j < 16; j += 4)
                    {
                        if (Probe(table, mask, baseIp, (uint)probe, delta + j, out candidate))
                        {
                            i = j;
                            goto LiteralThenMatch;
                        }

                        if (Probe(table, mask, baseIp, (uint)(probe >> 8), delta + j + 1, out candidate))
                        {
                            i = j + 1;
                            goto LiteralThenMatch;
                        }

                        if (Probe(table, mask, baseIp, (uint)(probe >> 16), delta + j + 2, out candidate))
                        {
                            i = j + 2;
                            goto LiteralThenMatch;
                        }

                        if (Probe(table, mask, baseIp, (uint)(probe >> 24), delta + j + 3, out candidate))
                        {
                            i = j + 3;
                            goto LiteralThenMatch;
                        }

                        probe = Unsafe.ReadUnaligned<ulong>(ip + j + 4);
                    }

                    ip += 16;
                    skip += 16;
                    goto SkipLoop;

                LiteralThenMatch:
                    // Literal of i + 1 bytes (always <= 16) starting at nextEmit
                    *op = (byte)(Literal | (i << 2));
                    SimdCopy.Copy16(nextEmit, op + 1);
                    ip += i;
                    op = op + i + 2;
                    goto EmitMatch;
                }

            SkipLoop:

                data = Unsafe.ReadUnaligned<uint>(ip);
                while (true)
                {
                    ushort* entry = TableEntry(table, (uint)data, mask);
                    uint bytesBetweenHashLookups = skip >> 5;
                    skip += bytesBetweenHashLookups;
                    byte* nextIp = ip + bytesBetweenHashLookups;
                    if (nextIp > ipLimit)
                    {
                        ip = nextEmit;
                        goto EmitRemainder;
                    }

                    candidate = baseIp + *entry;
                    *entry = (ushort)(ip - baseIp);
                    if ((uint)data == Unsafe.ReadUnaligned<uint>(candidate))
                    {
                        break;
                    }

                    data = Unsafe.ReadUnaligned<uint>(nextIp);
                    ip = nextIp;
                }

                // A 4 byte match: emit the unmatched bytes before it
                op = EmitLiteralFast(op, nextEmit, (nint)(ip - nextEmit));

            EmitMatch:
                do
                {
                    // Extend the match, then emit copies until the input right after it no longer matches
                    byte* matchStart = ip;
                    nuint matched = 4 + FindMatchLength(candidate + 4, ip + 4, ipEnd, ref data, out bool lessThan8);
                    ip += matched;
                    nuint offset = (nuint)(matchStart - candidate);
                    op = lessThan8 ? EmitCopyLessThan12(op, offset, matched) : EmitCopy(op, offset, matched);

                    if (ip >= ipLimit)
                    {
                        goto EmitRemainder;
                    }

                    // Also index ip - 1, which improves compression
                    *TableEntry(table, Unsafe.ReadUnaligned<uint>(ip - 1), mask) = (ushort)(ip - baseIp - 1);
                    ushort* entry = TableEntry(table, (uint)data, mask);
                    candidate = baseIp + *entry;
                    *entry = (ushort)(ip - baseIp);
                }
                while ((uint)data == Unsafe.ReadUnaligned<uint>(candidate));
            }
        }

    EmitRemainder:
        if (ip < ipEnd)
        {
            op = EmitLiteralSlow(op, ip, (nint)(ipEnd - ip));
        }

        return op;
    }

    /// <summary>Looks up and replaces the table entry for one position, reporting whether its 4 bytes match.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Probe(ushort* table, uint mask, byte* baseIp, uint dword, nint position, out byte* candidate)
    {
        ushort* entry = TableEntry(table, dword, mask);
        candidate = baseIp + *entry;
        *entry = (ushort)position;
        return Unsafe.ReadUnaligned<uint>(candidate) == dword;
    }

    /// <summary>
    /// Returns how many bytes match after the initial 4, and leaves at least 5 valid bytes of the input at the
    /// end of the match in <paramref name="data"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static nuint FindMatchLength(byte* s1, byte* s2, byte* s2Limit, ref ulong data, out bool lessThan8)
    {
        nuint matched = 0;

        if (s2 <= s2Limit - 16)
        {
            ulong a1 = Unsafe.ReadUnaligned<ulong>(s1);
            ulong a2 = Unsafe.ReadUnaligned<ulong>(s2);
            if (a1 != a2)
            {
                ulong xor = a1 ^ a2;
                int shift = BitOperations.TrailingZeroCount(xor);
                ulong a3 = Unsafe.ReadUnaligned<ulong>(s2 + 4);
                a2 = (uint)xor == 0 ? a3 : a2;
                data = a2 >> (shift & (3 * 8));
                lessThan8 = true;
                return (nuint)(shift >> 3);
            }

            matched = 8;
            s2 += 8;

        }

        while (s2 <= s2Limit - 16)
        {
            ulong a1 = Unsafe.ReadUnaligned<ulong>(s1 + matched);
            ulong a2 = Unsafe.ReadUnaligned<ulong>(s2);
            if (a1 == a2)
            {
                s2 += 8;
                matched += 8;

                // Once a match is long, compare 32 bytes at a time. Doing this from the start costs more than it
                // saves on the medium-length matches typical of text and markup.
                if (Vector256.IsHardwareAccelerated && matched >= VectorMatchThreshold)
                {
                    while (s2 <= s2Limit - 40)
                    {
                        uint differ = ~Vector256.Equals(Vector256.Load(s1 + matched), Vector256.Load(s2)).ExtractMostSignificantBits();
                        if (differ != 0)
                        {
                            nuint count = (nuint)BitOperations.TrailingZeroCount(differ);
                            data = Unsafe.ReadUnaligned<ulong>(s2 + count);
                            lessThan8 = false;
                            return matched + count;
                        }

                        s2 += 32;
                        matched += 32;
                    }
                }
            }
            else
            {
                ulong xor = a1 ^ a2;
                int shift = BitOperations.TrailingZeroCount(xor);
                ulong a3 = Unsafe.ReadUnaligned<ulong>(s2 + 4);
                a2 = (uint)xor == 0 ? a3 : a2;
                data = a2 >> (shift & (3 * 8));
                lessThan8 = false;
                return matched + (nuint)(shift >> 3);
            }
        }

        while (s2 < s2Limit)
        {
            if (s1[matched] == *s2)
            {
                s2++;
                matched++;
            }
            else
            {
                if (s2 <= s2Limit - 8)
                {
                    data = Unsafe.ReadUnaligned<ulong>(s2);
                }

                break;
            }
        }

        lessThan8 = matched < 8;
        return matched;
    }

    // In the main loop: may read 15 bytes past the literal and write 15 bytes past the output
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* EmitLiteralFast(byte* op, byte* literal, nint length)
    {
        nint n = length - 1;
        if (length <= 16)
        {
            *op++ = (byte)(Literal | (n << 2));
            SimdCopy.Copy16(literal, op);
            return op + length;
        }

        op = EmitLiteralTag(op, n);

        byte* end = op + length;
        do
        {
            SimdCopy.Copy16(literal, op);
            op += 16;
            literal += 16;
        }
        while (op < end);

        return end;
    }

    private static byte* EmitLiteralSlow(byte* op, byte* literal, nint length)
    {
        op = EmitLiteralTag(op, length - 1);
        Buffer.MemoryCopy(literal, op, length, length);
        return op + length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* EmitLiteralTag(byte* op, nint n)
    {
        if (n < 60)
        {
            *op++ = (byte)(Literal | (n << 2));
            return op;
        }

        int count = (BitOperations.Log2((uint)n) >> 3) + 1;
        *op++ = (byte)(Literal | ((59 + count) << 2));

        // Writes 4 bytes, of which only count matter; the literal that follows is at least 61 bytes
        Unsafe.WriteUnaligned(op, (uint)n);
        return op + count;
    }

    // length in [4, 12)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* EmitCopyLessThan12(byte* op, nuint offset, nuint length)
    {
        // Branch free choice between copy-1 and copy-2: offset < 2048 is hard to predict
        uint u = (uint)((length << 2) + (offset << 8));
        uint copy1 = unchecked((uint)(Copy1ByteOffset - (4 << 2)) + (uint)((offset >> 3) & 0xE0));
        uint copy2 = unchecked((uint)(Copy2ByteOffset - (1 << 2)));
        // All ones when offset < 2048
        uint copy1Mask = (uint)((nint)(offset - 2048) >> 63);
        u += (copy1 & copy1Mask) | (copy2 & ~copy1Mask);
        Unsafe.WriteUnaligned(op, u);
        return op + 3 + (nint)(int)copy1Mask;
    }

    // length in [12, 64]: always a copy-2. Writes 4 bytes, of which 3 matter.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* EmitCopy2(byte* op, nuint offset, nuint length)
    {
        Unsafe.WriteUnaligned(op, (uint)(Copy2ByteOffset + ((length - 1) << 2) + (offset << 8)));
        return op + 3;
    }

    // length >= 12
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* EmitCopy(byte* op, nuint offset, nuint length)
    {
        // Emit 64 byte copies, keeping at least 4 bytes for the last one
        while (length >= 68)
        {
            op = EmitCopy2(op, offset, 64);
            length -= 64;
        }

        if (length > 64)
        {
            op = EmitCopy2(op, offset, 60);
            length -= 60;
        }

        return length < 12 ? EmitCopyLessThan12(op, offset, length) : EmitCopy2(op, offset, length);
    }
}
