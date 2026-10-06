namespace SnappySimd.Internal;

/// <summary>
/// Constants of the Snappy framing format (https://github.com/google/snappy/blob/main/framing_format.txt).
/// </summary>
internal static class StreamFormat
{
    public const byte CompressedData = 0x00;
    public const byte UncompressedData = 0x01;
    public const byte MinSkippable = 0x80;
    public const byte StreamIdentifier = 0xff;

    public const int ChunkHeaderLength = 4;
    public const int ChecksumLength = 4;

    /// <summary>Maximum uncompressed bytes in a data chunk.</summary>
    public const int MaxChunkDataLength = 1 << 16;

    /// <summary>Largest compressed or uncompressed data chunk body we accept (checksum + payload).</summary>
    public static readonly int MaxDataChunkLength =
        ChecksumLength + Math.Max(MaxChunkDataLength, VarInt.MaxLength + BlockCompressor.MaxFragmentLength(MaxChunkDataLength));

    /// <summary>The stream identifier chunk, including its header.</summary>
    public static ReadOnlySpan<byte> StreamHeader => [0xff, 0x06, 0x00, 0x00, 0x73, 0x4e, 0x61, 0x50, 0x70, 0x59];
}
