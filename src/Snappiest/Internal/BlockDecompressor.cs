using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Snappiest.Internal;

/// <summary>
/// Decodes a raw Snappy block in a single pass, straight into the destination buffer.
/// </summary>
/// <remarks>
/// The hot loop is a port of <c>DecompressBranchless</c> from google/snappy 1.3.x: tags are decoded through a
/// length-minus-offset table so the common literal/copy split costs no unpredictable branches, and each copy is
/// deferred by one tag so its loads overlap with decoding the next tag. The loop needs slop past its position, so
/// the input tail is decoded from a padded copy and the output tail into a padded scratch buffer; everything else
/// it cannot handle (long literals, 4-byte offsets, corrupt data) falls back to a fully bounds-checked loop.
/// </remarks>
internal static unsafe class BlockDecompressor
{
    // Bytes the fast loop may touch past the current position
    private const int SlopBytes = 64;

    // Byte offset of the copy-offset masks after the 256 entry tag table
    private const int OffsetMasks = 256 * sizeof(short);

    // Input the fast loop needs before it can run (two tags plus their slop)
    private const int BranchlessInputMargin = 2 * (SlopBytes + 1);

    // The last BranchlessInputMargin input bytes (which the fast loop stops short of) are decoded from a copy followed
    // by 0xFF sentinel tags (copy-4, which the fast loop treats as exceptional), so the fast loop stops exactly at
    // the end of the input instead of leaving the tail to the bounds-checked loop. The padding covers the loop's
    // speculative reads past a tag. Copying a longer input tail (256 bytes) was slower on repeated small blocks.
    private const int InputScratchSize = BranchlessInputMargin + (2 * SlopBytes);

    // The last TailCapacity output bytes (at least the 2 * SlopBytes - 1 the fast loop's over-writes would run past
    // the end of) are decoded into a scratch buffer behind a copy of the SlopBytes of output that precede them, so
    // copies from just before the tail and the overlapping-copy path read from the scratch; copies from further back
    // are redirected to the real output. After the tail: room for the over-writes of a pair of tags and the final
    // deferred copy. A bigger tail (256 B to 1 KB, decoding small blocks through the scratch from the start) measured
    // slower: the scratch loop keeps two more values live and the copy-out grows.
    private const int TailPrefix = SlopBytes;
    private const int TailCapacity = 2 * SlopBytes;
    private const int TailScratchSize = TailPrefix + TailCapacity + (3 * SlopBytes);

    // Input scratch followed by the output tail scratch, in the caller's frame
    private const int ScratchSize = InputScratchSize + TailScratchSize;

    // Maximum ratio between uncompressed and compressed size: a 3 byte copy-2 tag expands to 64 bytes.
    private const int MaxExpansion = 22;

    // Per tag: length - (offset >> 8 << 8) for copy-1, length for copy-2, length - 256 for short literals (a
    // spurious offset that keeps literals off the overlapping-copy path), and 0xFF (exceptional) for long
    // literals and copy-4.
    private static readonly short* s_lengthMinusOffset = BuildLengthMinusOffset();

    // For tests
    internal static short LengthMinusOffset(int tag) => s_lengthMinusOffset[(byte)tag];

    /// <summary>
    /// Reads the uncompressed length and validates it against the size of the compressed data.
    /// </summary>
    /// <returns>The uncompressed length, and the number of header bytes.</returns>
    public static int ReadUncompressedLength(ReadOnlySpan<byte> input, out int headerLength)
    {
        int length = VarInt.ReadLength(input, out headerLength);
        if (length > (long)(input.Length - headerLength) * MaxExpansion)
        {
            ThrowHelper.ThrowInvalidDataExceptionCorruptBlock();
        }

        return length;
    }

    /// <summary>
    /// Decompresses a complete block (including its length header) into <paramref name="output"/>, which must be
    /// at least <paramref name="uncompressedLength"/> bytes long.
    /// </summary>
    /// <exception cref="InvalidDataException">The block is corrupt.</exception>
    public static void Decompress(ReadOnlySpan<byte> input, int headerLength, Span<byte> output, int uncompressedLength)
    {
        DebugAssert(output.Length >= uncompressedLength);

        ReadOnlySpan<byte> tags = input.Slice(headerLength);
        if (uncompressedLength == 0)
        {
            if (!tags.IsEmpty)
            {
                ThrowHelper.ThrowInvalidDataExceptionCorruptBlock();
            }

            return;
        }

        fixed (byte* ip = tags)
        fixed (byte* op = output)
        {
            if (!DecompressTags(ip, ip + tags.Length, op, op + uncompressedLength))
            {
                ThrowHelper.ThrowInvalidDataExceptionCorruptBlock();
            }
        }
    }

    /// <summary>
    /// Walks the tags of a block without writing output, checking that it decodes to exactly
    /// <paramref name="uncompressedLength"/> bytes. Used to validate a block whose output does not fit.
    /// </summary>
    public static bool Validate(ReadOnlySpan<byte> input, int headerLength, int uncompressedLength)
    {
        ReadOnlySpan<byte> tags = input.Slice(headerLength);
        long produced = 0;
        int i = 0;
        while (i < tags.Length)
        {
            uint tag = tags[i++];
            long length;
            long offset;
            switch (tag & 3)
            {
                case 0:
                    length = (tag >> 2) + 1;
                    if (length > 60)
                    {
                        int count = (int)length - 60;
                        if (tags.Length - i < count)
                        {
                            return false;
                        }

                        length = ReadLittleEndian(ref MemoryMarshal.GetReference(tags.Slice(i)), count) + 1L;
                        i += count;
                    }

                    if (tags.Length - i < length)
                    {
                        return false;
                    }

                    i += (int)length;
                    produced += length;
                    continue;

                case 1:
                    if (i >= tags.Length)
                    {
                        return false;
                    }

                    length = ((tag >> 2) & 7) + 4;
                    offset = ((tag & 0xE0) << 3) | tags[i];
                    i += 1;
                    break;

                case 2:
                    if (tags.Length - i < 2)
                    {
                        return false;
                    }

                    length = (tag >> 2) + 1;
                    offset = tags[i] | (tags[i + 1] << 8);
                    i += 2;
                    break;

                default:
                    if (tags.Length - i < 4)
                    {
                        return false;
                    }

                    length = (tag >> 2) + 1;
                    offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tags.Slice(i));
                    i += 4;
                    break;
            }

            if (offset == 0 || offset > produced)
            {
                return false;
            }

            produced += length;
            if (produced > uncompressedLength)
            {
                return false;
            }
        }

        return produced == uncompressedLength;
    }

    /// <returns><c>true</c> if the tags decoded to exactly [opBase, opEnd).</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool DecompressTags(byte* ip, byte* ipEnd, byte* opBase, byte* opEnd)
    {
        // Blocks too small for the fast loop get their own method: with dynamic PGO, a method's profile decides how
        // its code is laid out, and a stream of tiny blocks would otherwise mark the fast loop as cold here
        if (ipEnd - ip <= BranchlessInputMargin)
        {
            return DecompressSmall(ip, ipEnd, opBase, opEnd);
        }

        // The scratch lives in this frame, not in DecompressCore's: a method with a fixed-size buffer gets a stack
        // guard check and keeps copies of its pointer parameters on the stack, which slowed the loops
        Scratch scratch;
        return DecompressCore(ip, ipEnd, opBase, opEnd, scratch.Data);
    }

    /// <summary>Decodes tags into [opBase, opEnd); <paramref name="scratch"/> is <see cref="ScratchSize"/> bytes.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool DecompressCore(byte* ip, byte* ipEnd, byte* opBase, byte* opEnd, byte* scratch)
    {
        byte* op = opBase;

        // The fast loop may write SlopBytes past its position, so it must stop SlopBytes - 1 short of the end
        byte* opLimitMinSlop = opEnd - Math.Min(SlopBytes - 1, (nint)(opEnd - opBase));

        // Where the output tail starts in its scratch, after the prefix
        byte* tailScratch = scratch + InputScratchSize + TailPrefix;

        while (true)
        {
            nint remaining = (nint)(ipEnd - ip);
            if (remaining >= 2 && op < opEnd)
            {
                byte* fastIp = ip;
                byte* fastLimit = ipEnd;
                if (remaining <= BranchlessInputMargin)
                {
                    // Input tail: decode from a padded copy. The sentinel tags after it stop the loop at the end of
                    // the input; a tag that runs past the end reads sentinel bytes and leaves ip beyond it.
                    CopyInputTail(scratch, ip, remaining);
                    fastIp = scratch;
                    fastLimit = scratch + remaining + BranchlessInputMargin + 1;
                }

                if (opEnd - op > TailCapacity)
                {
                    // Far from the end of the output (so the loop will decode at least one pair of tags: with
                    // dynamic PGO, calls that return at once would teach the JIT that the loop body is cold, which
                    // slows every later large input)
                    Positions after = DecompressBranchless<Direct>(fastIp, fastLimit, op, opBase, opLimitMinSlop, null, 0);
                    ip += after.Ip - fastIp;
                    op = after.Op;
                }
                else
                {
                    // Output tail: decode into the scratch behind a copy of the preceding output, with the limit
                    // set so the loop runs while op + deferred length <= the end of the tail, then copy out
                    nint prefix = Math.Min(TailPrefix, (nint)(op - opBase));
                    CopyTailPrefix(tailScratch - prefix, op - prefix, prefix);
                    nint delta = (nint)(op - tailScratch);
                    Positions after = DecompressBranchless<Redirected>(fastIp, fastLimit, tailScratch, opBase - delta,
                        tailScratch + (opEnd - op) + 1 + SlopBytes, tailScratch - prefix, delta);
                    ip += after.Ip - fastIp;
                    nint produced = (nint)(after.Op - tailScratch);
                    if (produced > opEnd - op)
                    {
                        return false;
                    }

                    CopySmall(op, tailScratch, produced);
                    op += produced;
                }

                if (ip > ipEnd)
                {
                    return false;
                }
            }

            if (ip >= ipEnd)
            {
                break;
            }

            // Slow path: one tag, fully bounds checked. Written out here rather than calling DecodeTagChecked: with
            // ip and op passed by reference to both it and the fast loop, the JIT sometimes stops keeping them in
            // registers (measured 40% slower on large blocks).
            nuint tag = *ip++;
            nuint length;
            nuint offset;
            switch (tag & 3)
            {
                case 0:
                    length = (tag >> 2) + 1;
                    if (length > 60)
                    {
                        int count = (int)length - 60;
                        if (ipEnd - ip < count)
                        {
                            return false;
                        }

                        ulong longLength = ReadLittleEndian(ref *ip, count) + 1UL;
                        ip += count;
                        if (longLength > (ulong)(ipEnd - ip))
                        {
                            return false;
                        }

                        length = (nuint)longLength;
                    }

                    if ((nuint)(ipEnd - ip) < length || (nuint)(opEnd - op) < length)
                    {
                        return false;
                    }

                    Buffer.MemoryCopy(ip, op, length, length);
                    ip += length;
                    op += length;
                    continue;

                case 1:
                    if (ip >= ipEnd)
                    {
                        return false;
                    }

                    length = ((tag >> 2) & 7) + 4;
                    offset = ((tag & 0xE0) << 3) | *ip;
                    ip += 1;
                    break;

                case 2:
                    if (ipEnd - ip < 2)
                    {
                        return false;
                    }

                    length = (tag >> 2) + 1;
                    offset = Unsafe.ReadUnaligned<ushort>(ip);
                    ip += 2;
                    break;

                default:
                    if (ipEnd - ip < 4)
                    {
                        return false;
                    }

                    length = (tag >> 2) + 1;
                    offset = Unsafe.ReadUnaligned<uint>(ip);
                    ip += 4;
                    break;
            }

            if (offset - 1 >= (nuint)(op - opBase) || length > (nuint)(opEnd - op))
            {
                return false;
            }

            CopyWithinOutput(op, offset, length, opLimitMinSlop);
            op += length;
        }

        return op == opEnd;
    }

    private struct Scratch
    {
        public fixed byte Data[ScratchSize];
    }

    private interface ITailMode
    {
        static abstract bool Redirect { get; }
    }

    private readonly struct Direct : ITailMode
    {
        public static bool Redirect => false;
    }

    private readonly struct Redirected : ITailMode
    {
        public static bool Redirect => true;
    }

    /// <summary>Copies exactly <paramref name="count"/> (at most about a kilobyte) non-overlapping bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopySmall(byte* dst, byte* src, nint count)
    {
        if (count >= 32)
        {
            // Whole 32 byte blocks, then the last 32 bytes (overlapping the previous block)
            byte* s = src;
            byte* d = dst;
            for (nint n = count; n > 32; n -= 32, s += 32, d += 32)
            {
                SimdCopy.Copy32(s, d);
            }

            SimdCopy.Copy32(src + count - 32, dst + count - 32);
        }
        else if (count >= 16)
        {
            SimdCopy.Copy16(src, dst);
            SimdCopy.Copy16(src + count - 16, dst + count - 16);
        }
        else
        {
            for (nint i = 0; i < count; i++)
            {
                dst[i] = src[i];
            }
        }
    }

    /// <summary>
    /// Copies the last <paramref name="remaining"/> (2 to <see cref="BranchlessInputMargin"/>) input bytes to
    /// <paramref name="tail"/> and follows them with sentinel tags.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyInputTail(byte* tail, byte* ip, nint remaining)
    {
        CopySmall(tail, ip, remaining);
        Unsafe.InitBlockUnaligned(tail + remaining, 0xFF, 2 * SlopBytes);
    }

    /// <summary>Copies the <paramref name="count"/> (at most <see cref="TailPrefix"/>) output bytes before the tail.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyTailPrefix(byte* dst, byte* src, nint count)
    {
        if (count == TailPrefix)
        {
            SimdCopy.Copy64(src, dst);
        }
        else
        {
            for (nint i = 0; i < count; i++)
            {
                dst[i] = src[i];
            }
        }
    }

    /// <summary>Copies <paramref name="length"/> bytes from <paramref name="offset"/> back, over-copying only when there is room.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyWithinOutput(byte* op, nuint offset, nuint length, byte* opLimitMinSlop)
    {
        if (op < opLimitMinSlop)
        {
            // Room for a 64 byte over-copy
            if (offset >= length)
            {
                SimdCopy.Copy64(op - offset, op);
            }
            else
            {
                SimdCopy.Copy64BytesWithPatternExtension(op, offset);
            }
        }
        else
        {
            // Near the end of the output: no over-writing. Whole 16 byte blocks are safe when the source is at
            // least 16 bytes back (each block reads only finished output); the rest goes byte by byte.
            byte* src = op - offset;
            byte* end = op + length;
            byte* dst = op;
            if (offset >= 16)
            {
                for (; end - dst >= 16; dst += 16, src += 16)
                {
                    SimdCopy.Copy16(src, dst);
                }
            }

            for (; dst < end; dst++, src++)
            {
                *dst = *src;
            }
        }
    }

    /// <summary>Decodes a block whose tags are too short for the fast loop, one bounds-checked tag at a time.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool DecompressSmall(byte* ip, byte* ipEnd, byte* opBase, byte* opEnd)
    {
        byte* op = opBase;
        byte* opLimitMinSlop = opEnd - Math.Min(SlopBytes - 1, (nint)(opEnd - opBase));

        while (ip < ipEnd)
        {
            if (!DecodeTagChecked(ref ip, ipEnd, ref op, opBase, opEnd, opLimitMinSlop))
            {
                return false;
            }
        }

        return op == opEnd;
    }

    /// <summary>Decodes one tag with full bounds checks.</summary>
    /// <returns><c>false</c> if the input is corrupt.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DecodeTagChecked(ref byte* ip, byte* ipEnd, ref byte* op, byte* opBase, byte* opEnd, byte* opLimitMinSlop)
    {
        nuint tag = *ip++;
        nuint length;
        nuint offset;
        switch (tag & 3)
        {
            case 0:
                length = (tag >> 2) + 1;
                if (length > 60)
                {
                    int count = (int)length - 60;
                    if (ipEnd - ip < count)
                    {
                        return false;
                    }

                    ulong longLength = ReadLittleEndian(ref *ip, count) + 1UL;
                    ip += count;
                    if (longLength > (ulong)(ipEnd - ip))
                    {
                        return false;
                    }

                    length = (nuint)longLength;
                }

                if ((nuint)(ipEnd - ip) < length || (nuint)(opEnd - op) < length)
                {
                    return false;
                }

                Buffer.MemoryCopy(ip, op, length, length);
                ip += length;
                op += length;
                return true;

            case 1:
                if (ip >= ipEnd)
                {
                    return false;
                }

                length = ((tag >> 2) & 7) + 4;
                offset = ((tag & 0xE0) << 3) | *ip;
                ip += 1;
                break;

            case 2:
                if (ipEnd - ip < 2)
                {
                    return false;
                }

                length = (tag >> 2) + 1;
                offset = Unsafe.ReadUnaligned<ushort>(ip);
                ip += 2;
                break;

            default:
                if (ipEnd - ip < 4)
                {
                    return false;
                }

                length = (tag >> 2) + 1;
                offset = Unsafe.ReadUnaligned<uint>(ip);
                ip += 4;
                break;
        }

        if (offset - 1 >= (nuint)(op - opBase) || length > (nuint)(opEnd - op))
        {
            return false;
        }

        CopyWithinOutput(op, offset, length, opLimitMinSlop);
        op += length;
        return true;
    }

    /// <summary>
    /// Decodes tags while at least 2 * <see cref="SlopBytes"/> of input and output remain, stopping before any
    /// tag it cannot handle. The returned <see cref="Positions.Ip"/> points at the next undecoded tag.
    /// </summary>
    /// <remarks>
    /// Selects are written as mask arithmetic rather than conditionals so the JIT emits straight-line code, and
    /// the method has no stackalloc so it stays eligible for tiered (PGO) compilation.
    /// The positions are passed by value and returned, not passed by reference: if the JIT ever left this method as
    /// a call, a by-reference ip/op would be address-exposed in the caller and its slow path would keep them in
    /// memory (measured on .NET 11: 1KB html blocks 31-60% slower, 4KB 8-18%). Inlining is forced because the
    /// profile-driven decision depended on the warmup history (after tiny blocks only, the tail-mode instance stayed
    /// a call and 256 byte blocks took 54 instead of 33 ns); it costs nothing when the profile would inline anyway.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Positions DecompressBranchless<TMode>(byte* ip, byte* ipLimit, byte* op, byte* opBase, byte* opLimitMinSlop,
        byte* redirectBound, nint delta)
        where TMode : struct, ITailMode
    {
        // The caller checked that ipLimit - ip > BranchlessInputMargin and op < opLimitMinSlop - SlopBytes
        opLimitMinSlop -= SlopBytes;

        byte* ipLimitMinSlop = ipLimit - (2 * SlopBytes) - 1;
        short* table = s_lengthMinusOffset;

        // The table doubles as a readable dummy source for the "no deferred copy" state, so the deferred copy can
        // be issued unconditionally
        byte* safeSource = (byte*)table;
        byte* deferredSrc = safeSource;
        nuint deferredLength = 0;

        nuint tag = *ip++;

        // Two tags per iteration: the limits leave room for both, and checking them once per pair halves the loop
        // overhead (as in google/snappy).
        do
        {
            if (!DecodeTag<TMode>(ref ip, ref tag, ref op, ref deferredSrc, ref deferredLength, opBase, table, safeSource, redirectBound, delta)
                || !DecodeTag<TMode>(ref ip, ref tag, ref op, ref deferredSrc, ref deferredLength, opBase, table, safeSource, redirectBound, delta))
            {
                break;
            }
        }
        while (ip < ipLimitMinSlop && op + deferredLength < opLimitMinSlop);

        ip--;

        if (deferredLength != 0)
        {
            SimdCopy.MemCopy64(op, deferredSrc, deferredLength);
            op += deferredLength;
        }

        return new Positions(ip, op);
    }

    private readonly struct Positions(byte* ip, byte* op)
    {
        public readonly byte* Ip = ip;
        public readonly byte* Op = op;
    }

    /// <summary>
    /// Decodes one tag in the branchless loop. On entry <paramref name="ip"/> points just past <paramref name="tag"/>.
    /// </summary>
    /// <returns><c>false</c>, with <paramref name="ip"/> just past the tag, if the tag needs the slow path.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DecodeTag<TMode>(ref byte* ip, ref nuint tag, ref byte* op, ref byte* deferredSrc, ref nuint deferredLength,
        byte* opBase, short* table, byte* safeSource, byte* redirectBound, nint delta)
        where TMode : struct, ITailMode
    {
        byte* oldIp = ip;
        nint lengthMinusOffset = table[tag];

        // Advance to the next tag. Both candidates are loaded before selecting, which keeps the ip dependency
        // chain short; there is always enough input slop for the speculative loads.
        nuint tagType = tag & 3;
        nuint literalMask = (nuint)(((nint)tagType - 1) >> 63); // all ones for literals
        nuint literalLength = tag >> 2;
        nuint tagLiteral = oldIp[1 + literalLength];
        nuint tagCopy = oldIp[tagType];
        tag = (tagLiteral & literalMask) | (tagCopy & ~literalMask);
        ip = oldIp + 1 + (((1 + literalLength) & literalMask) | (tagType & ~literalMask));

        // Offset bytes after the tag, masked by type (0, 0xFF, 0xFFFF, 0) with a table lookup like google's x86
        // ExtractOffset: 3 instructions per tag fewer than shifting a packed constant (measured 2-6% faster)
        uint next = Unsafe.ReadUnaligned<ushort>(oldIp);
        nint extracted = (nint)(next & *(ushort*)((byte*)table + OffsetMasks + (tagType * 2)));
        nint length = lengthMinusOffset & 0xFF;
        nint lengthMinOffset = lengthMinusOffset - extracted;

        if (lengthMinOffset > 0)
        {
            if ((length & 0x80) != 0)
            {
                // Long literal or copy-4
                ip = oldIp;
                return false;
            }

            // A copy whose source overlaps its destination (offset < length)
            SimdCopy.MemCopy64(op, deferredSrc, deferredLength);
            op += deferredLength;
            deferredSrc = safeSource;
            deferredLength = 0;

            nuint offset = (nuint)(length - lengthMinOffset);
            if (offset - 1 >= (nuint)(op - opBase))
            {
                // Zero offset, or before the start of the output
                ip = oldIp;
                return false;
            }

            SimdCopy.Copy64BytesWithPatternExtension(op, offset);
            op += length;
            return true;
        }

        // Copies read from earlier output, literals from the input
        byte* opNext = op + deferredLength;
        byte* copySrc = opNext + lengthMinOffset - length;
        if (copySrc < opBase && tagType != 0)
        {
            ip = oldIp;
            return false;
        }

        if (TMode.Redirect)
        {
            // Output tail in its scratch: a source before the scratch's prefix is in the real output
            copySrc += delta & ((nint)(copySrc - redirectBound) >> 63);
        }

        byte* from = (byte*)(((nuint)oldIp & literalMask) | ((nuint)copySrc & ~literalMask));
        SimdCopy.MemCopy64(op, deferredSrc, deferredLength);
        op = opNext;
        deferredSrc = from;
        deferredLength = (nuint)length;
        return true;
    }

    // Reads a 1..4 byte little-endian value without touching bytes past it
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadLittleEndian(ref byte p, int count)
    {
        uint value = p;
        for (int i = 1; i < count; i++)
        {
            value |= (uint)Unsafe.Add(ref p, i) << (8 * i);
        }

        return value;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugAssert(bool condition) => System.Diagnostics.Debug.Assert(condition);

    private static short* BuildLengthMinusOffset()
    {
        // The JIT folds SimdCopy's table pointer into a constant only if that class is initialized when the decoder
        // reaches Tier1; a run without overlapping copies would otherwise leave a class-init helper call on that path
        RuntimeHelpers.RunClassConstructor(typeof(SimdCopy).TypeHandle);

        // 256 tag entries, then the four copy-offset masks in the same allocation
        short* table = (short*)NativeMemory.AlignedAlloc(OffsetMasks + 64, 64);
        ushort* masks = (ushort*)((byte*)table + OffsetMasks);
        masks[0] = 0;
        masks[1] = 0xFF;
        masks[2] = 0xFFFF;
        masks[3] = 0;
        for (int tag = 0; tag < 256; tag++)
        {
            int data = tag >> 2;
            table[tag] = (tag & 3) switch
            {
                3 => 0xFF,
                2 => (short)(data + 1),
                1 => (short)((data & 7) + 4 - ((data >> 3) << 8)),
                _ => data < 60 ? (short)(data + 1 - 256) : (short)0xFF,
            };
        }

        return table;
    }
}
