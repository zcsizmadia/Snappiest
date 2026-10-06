using System.Buffers;
using System.Runtime.CompilerServices;

namespace SnappySimd.Internal;

/// <summary>
/// Little-endian base-128 encoding of the uncompressed length that prefixes every Snappy block.
/// </summary>
internal static class VarInt
{
    public const int MaxLength = 5;

    public static OperationStatus TryRead(ReadOnlySpan<byte> input, out uint value, out int bytesRead)
    {
        uint result = 0;
        int limit = Math.Min(input.Length, MaxLength);
        for (int i = 0; i < limit; i++)
        {
            uint b = input[i];
            if (i == MaxLength - 1 && b > 0x0F)
            {
                // The fifth byte may only carry the top 4 bits of a 32-bit value
                break;
            }

            result |= (b & 0x7F) << (i * 7);
            if (b < 0x80)
            {
                value = result;
                bytesRead = i + 1;
                return OperationStatus.Done;
            }
        }

        value = 0;
        bytesRead = 0;
        // Fewer than 5 bytes without a terminator could still be completed; 5 bytes without one never can
        return input.Length < MaxLength ? OperationStatus.NeedMoreData : OperationStatus.InvalidData;
    }

    /// <summary>
    /// Reads a block's uncompressed length, throwing <see cref="InvalidDataException"/> if it is malformed or
    /// does not fit in an <see cref="int"/>.
    /// </summary>
    public static int ReadLength(ReadOnlySpan<byte> input, out int bytesRead)
    {
        if (TryRead(input, out uint value, out bytesRead) != OperationStatus.Done || value > int.MaxValue)
        {
            ThrowHelper.ThrowInvalidDataExceptionInvalidLength();
        }

        return (int)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(Span<byte> output, uint value)
    {
        int i = 0;
        while (value >= 0x80)
        {
            output[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        output[i++] = (byte)value;
        return i;
    }

    public static int GetByteCount(uint value) => value switch
    {
        < 1u << 7 => 1,
        < 1u << 14 => 2,
        < 1u << 21 => 3,
        < 1u << 28 => 4,
        _ => 5,
    };
}
