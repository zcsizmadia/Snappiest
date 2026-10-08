using System.Buffers.Binary;
using Snappiest.Tests.Fuzz;

namespace Snappiest.Fuzz;

/// <summary>
/// A plain decoder for the framing format, written from google/snappy's framing_format.txt, with data chunks decoded
/// by <see cref="ReferenceDecoder"/> and a bitwise CRC-32C. The referee for the stream target.
/// </summary>
/// <remarks>
/// Two choices follow Snappiest (and Snappier) rather than the letter of the spec: the stream identifier is not
/// required to come first, and a compressed chunk body may not be longer than the largest one a compressor can emit
/// for 64KB (4 + <see cref="Snappy.GetMaxCompressedLength"/>(65536) bytes).
/// </remarks>
internal static class ReferenceFraming
{
    private const int MaxChunkData = 1 << 16;

    private static readonly int MaxDataChunkBody = 4 + Snappy.GetMaxCompressedLength(MaxChunkData);

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Decodes a stream.</summary>
    /// <returns>The bytes of the chunks before the first error (all of them if there is none).</returns>
    public static byte[] Decode(ReadOnlySpan<byte> data, out string? error)
    {
        var output = new MemoryStream();
        int pos = 0;
        while (pos < data.Length)
        {
            if (data.Length - pos < 4)
            {
                error = $"truncated chunk header at {pos}";
                return output.ToArray();
            }

            byte type = data[pos];
            int length = data[pos + 1] | (data[pos + 2] << 8) | (data[pos + 3] << 16);
            int at = pos;
            pos += 4;

            if (type is 0x00 or 0x01 && (length < 4 || length > MaxDataChunkBody))
            {
                error = $"invalid data chunk length {length} at {at}";
                return output.ToArray();
            }

            if (data.Length - pos < length)
            {
                error = $"truncated chunk at {at}";
                return output.ToArray();
            }

            ReadOnlySpan<byte> body = data.Slice(pos, length);
            pos += length;

            switch (type)
            {
                case 0xff:
                    if (!body.SequenceEqual("sNaPpY"u8))
                    {
                        error = $"bad stream identifier at {at}";
                        return output.ToArray();
                    }

                    break;

                case 0x00:
                case 0x01:
                {
                    byte[]? chunk;
                    if (type == 0x01)
                    {
                        chunk = body.Length - 4 <= MaxChunkData ? body.Slice(4).ToArray() : null;
                    }
                    else
                    {
                        int? declared = SpecLength(body.Slice(4));
                        chunk = declared is null or > MaxChunkData ? null : ReferenceDecoder.Decode(body.Slice(4), out _);
                    }

                    if (chunk is null)
                    {
                        error = $"invalid data chunk at {at}";
                        return output.ToArray();
                    }

                    if (MaskedCrc32C(chunk) != BinaryPrimitives.ReadUInt32LittleEndian(body))
                    {
                        error = $"CRC mismatch at {at}";
                        return output.ToArray();
                    }

                    output.Write(chunk);
                    break;
                }

                case < 0x80:
                    error = $"reserved unskippable chunk 0x{type:x2} at {at}";
                    return output.ToArray();

                default:
                    break; // reserved skippable or padding
            }
        }

        error = null;
        return output.ToArray();
    }

    /// <summary>A block's length header as <see cref="ReferenceDecoder"/> reads it, or null if it rejects it.</summary>
    public static int? SpecLength(ReadOnlySpan<byte> block)
    {
        ulong length = 0;
        for (int i = 0, shift = 0; ; i++, shift += 7)
        {
            if (i >= block.Length || shift > 28)
            {
                return null;
            }

            length |= (ulong)(block[i] & 0x7F) << shift;
            if (block[i] < 0x80)
            {
                return length > int.MaxValue ? null : (int)length;
            }
        }
    }

    public static uint MaskedCrc32C(ReadOnlySpan<byte> data)
    {
        uint crc = ~0u;
        foreach (byte b in data)
        {
            crc = CrcTable[(byte)(crc ^ b)] ^ (crc >> 8);
        }

        crc = ~crc;
        return ((crc >> 15) | (crc << 17)) + 0xa282ead8;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0x82F63B78 : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
