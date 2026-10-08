using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Snappiest.Internal;

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
                // The table entry of the position being probed; it only becomes a pointer once it matches, so
                // the compare that decides can address [baseIp + index] directly, one add less on its chain
                nuint index;

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
                        if (Probe(table, mask, baseIp, (uint)probe, delta + j, out index))
                        {
                            i = j;
                            goto LiteralThenMatch;
                        }

                        if (Probe(table, mask, baseIp, (uint)(probe >> 8), delta + j + 1, out index))
                        {
                            i = j + 1;
                            goto LiteralThenMatch;
                        }

                        if (Probe(table, mask, baseIp, (uint)(probe >> 16), delta + j + 2, out index))
                        {
                            i = j + 2;
                            goto LiteralThenMatch;
                        }

                        if (Probe(table, mask, baseIp, (uint)(probe >> 24), delta + j + 3, out index))
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

                    index = *entry;
                    *entry = (ushort)(ip - baseIp);
                    if ((uint)data == Unsafe.ReadUnaligned<uint>(baseIp + index))
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
                    // Extend the match, then emit copies until the input right after it no longer matches.
                    // Few values stay live across the extension: it runs in a method that already keeps every
                    // register busy, and a spilled ip or data would put a store forward on the critical chain.
                    byte* candidate = baseIp + index;
                    nuint matched;
                    nuint offset;
                    if (ip + 5 <= ipLimit)
                    {
                        // The common case, a match shorter than 12, is decided by the first 8 bytes after the
                        // known 4 right here, so no flag has to be carried out of a helper.
                        ulong a1 = Unsafe.ReadUnaligned<ulong>(candidate + 4);
                        ulong a2 = Unsafe.ReadUnaligned<ulong>(ip + 4);
                        if (a1 != a2)
                        {
                            matched = 4 + MismatchOffset(a2, Unsafe.ReadUnaligned<ulong>(ip + 8), a1 ^ a2, ref data);
                            offset = (nuint)(ip - candidate);
                            ip += matched;
                            op = EmitCopyLessThan12(op, offset, matched);
                            goto CopyEmitted;
                        }

                        matched = 12 + FindMatchLength(candidate + 12, ip + 12, ipLimit, ref data);
                    }
                    else
                    {
                        TailMatch tail = FindMatchLengthTail(candidate + 4, ip + 4, ipEnd);
                        data = tail.Data;
                        matched = 4 + tail.Length;
                    }

                    offset = (nuint)(ip - candidate);
                    ip += matched;
                    op = EmitCopy(op, offset, matched);

                CopyEmitted:
                    if (ip >= ipLimit)
                    {
                        goto EmitRemainder;
                    }

                    // Also index ip - 1, which improves compression
                    *TableEntry(table, Unsafe.ReadUnaligned<uint>(ip - 1), mask) = (ushort)(ip - baseIp - 1);
                    ushort* entry = TableEntry(table, (uint)data, mask);
                    index = *entry;
                    *entry = (ushort)(ip - baseIp);
                }
                while ((uint)data == Unsafe.ReadUnaligned<uint>(baseIp + index));
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
    private static bool Probe(ushort* table, uint mask, byte* baseIp, uint dword, nint position, out nuint index)
    {
        ushort* entry = TableEntry(table, dword, mask);
        index = *entry;
        *entry = (ushort)position;
        return Unsafe.ReadUnaligned<uint>(baseIp + index) == dword;
    }

    /// <summary>
    /// Returns how many bytes match after the initial 4, and leaves at least 5 valid bytes of the input at the
    /// end of the match in <paramref name="data"/>. The main loop inlines the first 8 byte comparison itself
    /// and calls <see cref="FindMatchLength(byte*, byte*, byte*, ref ulong)"/> or <see cref="FindMatchLengthTail"/>;
    /// this is the same logic in one piece.
    /// </summary>
    internal static nuint FindMatchLength(byte* s1, byte* s2, byte* s2Limit, ref ulong data, out bool lessThan8)
    {
        if (s2 <= s2Limit - 16)
        {
            ulong a1 = Unsafe.ReadUnaligned<ulong>(s1);
            ulong a2 = Unsafe.ReadUnaligned<ulong>(s2);
            if (a1 != a2)
            {
                lessThan8 = true;
                return MismatchOffset(a2, Unsafe.ReadUnaligned<ulong>(s2 + 4), a1 ^ a2, ref data);
            }

            lessThan8 = false;
            return 8 + FindMatchLength(s1 + 8, s2 + 8, s2Limit - InputMarginBytes, ref data);
        }

        TailMatch tail = FindMatchLengthTail(s1, s2, s2Limit);
        data = tail.Data;
        lessThan8 = tail.Length < 8;
        return tail.Length;
    }

    /// <summary>
    /// For 8 byte words that differ: the number of leading equal bytes, leaving the input after them in
    /// <paramref name="data"/>. <paramref name="a3"/> holds the input 4 bytes after <paramref name="a2"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint MismatchOffset(ulong a2, ulong a3, ulong xor, ref ulong data)
    {
        // The next hash needs the input at the end of the match, which depends on the match length and so on
        // the candidate load. Shifting the words loaded from the input (not from the candidate) by the length
        // keeps those loads off that dependency chain; see google/snappy's FindMatchLength.
        int shift = BitOperations.TrailingZeroCount(xor);
        // All ones when the low 4 bytes are equal, so the shift stays below 32. Written as arithmetic
        // because the JIT turns the equivalent ?: into a branch that mispredicts on text.
        ulong fromA3 = (ulong)((long)((xor & 0xFFFFFFFF) - 1) >> 63);
        data = (a2 ^ ((a2 ^ a3) & fromA3)) >> (shift & (3 * 8));
        return (nuint)(shift >> 3);
    }

    /// <summary>
    /// Extends a match whose first 8 bytes from <paramref name="s2"/> - 8 are known to match. The input ends
    /// <see cref="InputMarginBytes"/> after <paramref name="limit"/>, the main loop's limit, which is passed
    /// instead of the end so that the loop keeps one pointer less alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint FindMatchLength(byte* s1, byte* s2, byte* limit, ref ulong data)
    {
        // One moving pointer: the candidate is always diff bytes behind it
        nint diff = (nint)(s1 - s2);
        byte* p = s2;
        nuint n;

        // Up to the vector threshold, the 8 byte compares are unrolled with a single bounds check: markup and
        // text matches mostly end in the first few, where a loop's bookkeeping was a fifth of the work
        if (p <= limit + (InputMarginBytes - (VectorMatchThreshold + 8)))
        {
            if (Mismatch(p, diff, ref data, out n))
            {
                return n;
            }

            if (Mismatch(p + 8, diff, ref data, out n))
            {
                return 8 + n;
            }

            if (Mismatch(p + 16, diff, ref data, out n))
            {
                return 16 + n;
            }

            if (Mismatch(p + 24, diff, ref data, out n))
            {
                return 24 + n;
            }

            if (Mismatch(p + 32, diff, ref data, out n))
            {
                return 32 + n;
            }

            if (Mismatch(p + 40, diff, ref data, out n))
            {
                return 40 + n;
            }

            if (Mismatch(p + 48, diff, ref data, out n))
            {
                return 48 + n;
            }

            p += VectorMatchThreshold - 8;

            // Once a match is long, compare 32 bytes at a time. Doing this from the start costs more than it
            // saves on the medium-length matches typical of text and markup.
            if (Vector256.IsHardwareAccelerated)
            {
                byte* vectorLimit = limit + (InputMarginBytes - 40);
                while (p <= vectorLimit)
                {
                    uint differ = ~Vector256.Equals(Vector256.Load(p + diff), Vector256.Load(p)).ExtractMostSignificantBits();
                    if (differ != 0)
                    {
                        nuint count = (nuint)BitOperations.TrailingZeroCount(differ);
                        data = Unsafe.ReadUnaligned<ulong>(p + count);
                        return (nuint)(p - s2) + count;
                    }

                    p += 32;
                }
            }
        }

        byte* last = limit + (InputMarginBytes - 16);
        while (p <= last)
        {
            if (Mismatch(p, diff, ref data, out n))
            {
                return (nuint)(p - s2) + n;
            }

            p += 8;
        }

        TailMatch tail = FindMatchLengthTail(p + diff, p, limit + InputMarginBytes);
        data = tail.Data;
        return (nuint)(p - s2) + tail.Length;
    }

    /// <summary>Compares 8 bytes at <paramref name="p"/> with those <paramref name="diff"/> bytes before it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Mismatch(byte* p, nint diff, ref ulong data, out nuint matched)
    {
        ulong a1 = Unsafe.ReadUnaligned<ulong>(p + diff);
        ulong a2 = Unsafe.ReadUnaligned<ulong>(p);
        if (a1 == a2)
        {
            matched = 0;
            return false;
        }

        matched = MismatchOffset(a2, Unsafe.ReadUnaligned<ulong>(p + 4), a1 ^ a2, ref data);
        return true;
    }

    /// <summary>
    /// Byte by byte match extension for the last 16 bytes of the input. Not inlined, so it must not write the
    /// caller's data through a ref (that would keep the caller's copy in memory); it returns the input after
    /// the match instead, which is only valid when the match ends at least 8 bytes before the limit. Callers
    /// never read it otherwise, as the match then ends past their main loop limit.
    /// </summary>
    private static TailMatch FindMatchLengthTail(byte* s1, byte* s2, byte* s2Limit)
    {
        nuint matched = 0;
        ulong data = 0;
        while (s2 < s2Limit)
        {
            if (s1[matched] != *s2)
            {
                if (s2 <= s2Limit - 8)
                {
                    data = Unsafe.ReadUnaligned<ulong>(s2);
                }

                break;
            }

            s2++;
            matched++;
        }

        return new TailMatch(matched, data);
    }

    private readonly struct TailMatch(nuint length, ulong data)
    {
        public readonly nuint Length = length;
        public readonly ulong Data = data;
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
        // Branch free choice between copy-1 and copy-2: offset < 2048 is hard to predict. Starting from the
        // copy-2 tag, the mask adds what turns it into copy-1: offset bits 8-10 and the tag and length bias.
        const uint Copy2Bias = unchecked((uint)(Copy2ByteOffset - (1 << 2)));
        const uint Copy1Bias = unchecked((uint)(Copy1ByteOffset - (4 << 2)));
        nint copy1Mask = (nint)(offset - 2048) >> 63;
        uint copy1Delta = (uint)((offset >> 3) & 0xE0) + unchecked(Copy1Bias - Copy2Bias);
        uint u = (uint)((length << 2) + (offset << 8)) + Copy2Bias + (copy1Delta & (uint)copy1Mask);
        Unsafe.WriteUnaligned(op, u);
        return op + 3 + copy1Mask;
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
