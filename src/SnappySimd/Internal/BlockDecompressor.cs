using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SnappySimd.Internal;

/// <summary>
/// Decodes a raw Snappy block in a single pass, straight into the destination buffer.
/// </summary>
/// <remarks>
/// The hot loop is a port of <c>DecompressBranchless</c> from google/snappy 1.3.x: tags are decoded through a
/// length-minus-offset table so the common literal/copy split costs no unpredictable branches, and each copy is
/// deferred by one tag so its loads overlap with decoding the next tag. Everything the fast loop cannot handle
/// (long literals, 4-byte offsets, the last ~128 bytes of input or output, corrupt data) falls back to a fully
/// bounds-checked loop.
/// </remarks>
internal static unsafe class BlockDecompressor
{
    // Bytes the fast loop may touch past the current position
    private const int SlopBytes = 64;

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
    internal static bool DecompressTags(byte* ip, byte* ipEnd, byte* opBase, byte* opEnd)
    {
        byte* op = opBase;

        // The fast loop may write SlopBytes past its position, so it must stop SlopBytes - 1 short of the end
        byte* opLimitMinSlop = opEnd - Math.Min(SlopBytes - 1, (nint)(opEnd - opBase));

        while (true)
        {
            DecompressBranchless(ref ip, ipEnd, ref op, opBase, opLimitMinSlop);

            if (ip >= ipEnd)
            {
                break;
            }

            // Slow path: one tag, fully bounds checked
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
                byte* src = op - offset;
                for (nuint i = 0; i < length; i++)
                {
                    op[i] = src[i];
                }
            }

            op += length;
        }

        return op == opEnd;
    }

    /// <summary>
    /// Decodes tags while at least 2 * <see cref="SlopBytes"/> of input and output remain, stopping before any
    /// tag it cannot handle. On return <paramref name="ipRef"/> points at the next undecoded tag.
    /// </summary>
    /// <remarks>
    /// Selects are written as mask arithmetic rather than conditionals so the JIT emits straight-line code, and
    /// the method has no stackalloc so it stays eligible for tiered (PGO) compilation.
    /// </remarks>
    private static void DecompressBranchless(ref byte* ipRef, byte* ipLimit, ref byte* opRef, byte* opBase, byte* opLimitMinSlop)
    {
        byte* ip = ipRef;
        byte* op = opRef;

        opLimitMinSlop -= SlopBytes;
        if (ipLimit - ip <= 2 * (SlopBytes + 1) || op >= opLimitMinSlop)
        {
            return;
        }

        byte* ipLimitMinSlop = ipLimit - (2 * SlopBytes) - 1;
        short* table = s_lengthMinusOffset;

        // The table doubles as a readable dummy source for the "no deferred copy" state, so the deferred copy can
        // be issued unconditionally
        byte* safeSource = (byte*)table;
        byte* deferredSrc = safeSource;
        nuint deferredLength = 0;

        nuint tag = *ip++;

        do
        {
            byte* oldIp = ip;
            nint lengthMinusOffset = table[tag];

            // Advance to the next tag. Both candidates are loaded before selecting, which keeps the ip dependency
            // chain short; there is always enough input slop for the speculative loads.
            nuint tagType = tag & 3;
            nuint literalMask = (nuint)(((nint)tagType - 1) >> 63); // all ones for literals
            nuint literalLength = tag >> 2;
            nuint tagLiteral = ip[1 + literalLength];
            nuint tagCopy = ip[tagType];
            tag = (tagLiteral & literalMask) | (tagCopy & ~literalMask);
            ip += 1 + (((1 + literalLength) & literalMask) | (tagType & ~literalMask));

            // Offset bytes after the tag, masked by type: 0, 0xFF, 0xFFFF, 0 packed as 16 bit lanes
            uint next = Unsafe.ReadUnaligned<uint>(oldIp);
            nint extracted = (nint)(next & (uint)(0x0000FFFF00FF0000UL >> (int)(tagType * 16)) & 0xFFFF);
            nint length = lengthMinusOffset & 0xFF;
            nint lengthMinOffset = lengthMinusOffset - extracted;

            if (lengthMinOffset > 0)
            {
                if ((length & 0x80) != 0)
                {
                    // Long literal or copy-4
                    ip = oldIp;
                    goto Exit;
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
                    goto Exit;
                }

                SimdCopy.Copy64BytesWithPatternExtension(op, offset);
                op += length;
                continue;
            }

            // Copies read from earlier output, literals from the input
            byte* opNext = op + deferredLength;
            byte* copySrc = opNext + lengthMinOffset - length;
            if (copySrc < opBase && tagType != 0)
            {
                ip = oldIp;
                goto Exit;
            }

            byte* from = (byte*)(((nuint)oldIp & literalMask) | ((nuint)copySrc & ~literalMask));
            SimdCopy.MemCopy64(op, deferredSrc, deferredLength);
            op = opNext;
            deferredSrc = from;
            deferredLength = (nuint)length;
        }
        while (ip < ipLimitMinSlop && op + deferredLength < opLimitMinSlop);

    Exit:
        ip--;

        if (deferredLength != 0)
        {
            SimdCopy.MemCopy64(op, deferredSrc, deferredLength);
            op += deferredLength;
        }

        ipRef = ip;
        opRef = op;
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
        short* table = (short*)NativeMemory.AlignedAlloc(256 * sizeof(short), 64);
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
