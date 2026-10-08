using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Snappiest.Internal;

/// <summary>
/// Over-copying vector primitives used by the compressor and decompressor hot loops. Callers guarantee the
/// slop space these routines read and write beyond the requested length.
/// </summary>
internal static unsafe class SimdCopy
{
    /// <summary>True when a variable byte shuffle is available (SSSE3 PSHUFB or arm64 TBL).</summary>
    public static bool HasByteShuffle
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Ssse3.IsSupported || AdvSimd.Arm64.IsSupported;
    }

    // Two 16x16 tables: masks that expand a 1..16 byte pattern into 16 bytes, followed by masks that rotate an
    // expanded pattern forward by 16 bytes. Allocated once and never freed, so the pointer stays valid.
    private static readonly byte* s_patternMasks = BuildPatternMasks();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy16(byte* src, byte* dst) => Vector128.Store(Vector128.Load(src), dst);

    /// <summary>Copies 32 bytes, loading everything before storing (memmove semantics).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy32(byte* src, byte* dst)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256.Store(Vector256.Load(src), dst);
        }
        else
        {
            Vector128<byte> a = Vector128.Load(src);
            Vector128<byte> b = Vector128.Load(src + 16);
            Vector128.Store(a, dst);
            Vector128.Store(b, dst + 16);
        }
    }

    /// <summary>Copies 64 bytes, loading everything before storing (memmove semantics).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy64(byte* src, byte* dst)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<byte> a = Vector256.Load(src);
            Vector256<byte> b = Vector256.Load(src + 32);
            Vector256.Store(a, dst);
            Vector256.Store(b, dst + 32);
        }
        else
        {
            Vector128<byte> a = Vector128.Load(src);
            Vector128<byte> b = Vector128.Load(src + 16);
            Vector128<byte> c = Vector128.Load(src + 32);
            Vector128<byte> d = Vector128.Load(src + 48);
            Vector128.Store(a, dst);
            Vector128.Store(b, dst + 16);
            Vector128.Store(c, dst + 32);
            Vector128.Store(d, dst + 48);
        }
    }

    /// <summary>
    /// Copies at least <paramref name="size"/> (at most 64) bytes, writing up to 64 bytes. The source and destination
    /// must not overlap within <paramref name="size"/> bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MemCopy64(byte* dst, byte* src, nuint size)
    {
        Copy32(src, dst);
        if (size > 32)
        {
            Copy32(src + 32, dst + 32);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuffle(Vector128<byte> vector, Vector128<byte> indices)
    {
        if (Ssse3.IsSupported)
        {
            return Ssse3.Shuffle(vector, indices);
        }

        // Only called when HasByteShuffle, so this is arm64
        return AdvSimd.Arm64.VectorTableLookup(vector, indices);
    }

    /// <summary>
    /// Writes 64 bytes at <paramref name="dst"/> that repeat the <paramref name="offset"/> bytes preceding it, as a
    /// Snappy copy with an overlapping source does. Requires offset >= 1 and [dst - offset, dst + 64) to be addressable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy64BytesWithPatternExtension(byte* dst, nuint offset)
    {
        if (HasByteShuffle)
        {
            if (offset - 1 < 16)
            {
                if (offset == 1)
                {
                    Vector128<byte> fill = Vector128.Create(dst[-1]);
                    Vector128.Store(fill, dst);
                    Vector128.Store(fill, dst + 16);
                    Vector128.Store(fill, dst + 32);
                    Vector128.Store(fill, dst + 48);
                    return;
                }

                byte* masks = s_patternMasks + ((offset - 1) * 16);
                Vector128<byte> pattern = Shuffle(Vector128.Load(dst - offset), Vector128.Load(masks));

                if ((offset & (offset - 1)) == 0)
                {
                    // 2, 4, 8, 16: the expanded pattern is already aligned with every 16 byte block
                    Vector128.Store(pattern, dst);
                    Vector128.Store(pattern, dst + 16);
                    Vector128.Store(pattern, dst + 32);
                    Vector128.Store(pattern, dst + 48);
                    return;
                }

                Vector128<byte> reshuffle = Vector128.Load(masks + 256);
                Vector128.Store(pattern, dst);
                pattern = Shuffle(pattern, reshuffle);
                Vector128.Store(pattern, dst + 16);
                pattern = Shuffle(pattern, reshuffle);
                Vector128.Store(pattern, dst + 32);
                pattern = Shuffle(pattern, reshuffle);
                Vector128.Store(pattern, dst + 48);
                return;
            }
        }
        else if (offset - 1 < 15)
        {
            // Expand the pattern to 16 bytes one byte at a time, then copy from a whole multiple of the pattern back
            byte* src = dst - offset;
            for (int i = 0; i < 16; i++)
            {
                dst[i] = src[i];
            }

            nuint stride = ((16 / offset) + 1) * offset;
            Copy16(dst + 16 - stride, dst + 16);
            Copy16(dst + 32 - stride, dst + 32);
            Copy16(dst + 48 - stride, dst + 48);
            return;
        }

        // offset >= 16: every 16 byte block reads only bytes that are already final
        Copy16(dst - offset, dst);
        Copy16(dst + 16 - offset, dst + 16);
        Copy16(dst + 32 - offset, dst + 32);
        Copy16(dst + 48 - offset, dst + 48);
    }

    private static byte* BuildPatternMasks()
    {
        byte* table = (byte*)NativeMemory.AlignedAlloc(512, 64);
        for (int size = 1; size <= 16; size++)
        {
            for (int i = 0; i < 16; i++)
            {
                table[((size - 1) * 16) + i] = (byte)(i % size);
                table[256 + ((size - 1) * 16) + i] = (byte)((16 + i) % size);
            }
        }

        return table;
    }
}
