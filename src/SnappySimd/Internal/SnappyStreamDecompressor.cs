using System.Buffers;
using System.Buffers.Binary;

namespace SnappySimd.Internal;

/// <summary>
/// Parses the Snappy framing format. Compressed input is buffered until a whole chunk is available, then the chunk
/// is decoded in one pass: directly into the caller's buffer when it fits, otherwise into an internal buffer.
/// </summary>
internal sealed class SnappyStreamDecompressor : IDisposable
{
    // Holds at least one maximum size chunk plus read-ahead
    private const int InputBufferSize = 1 << 17;

    private byte[]? _input = ArrayPool<byte>.Shared.Rent(InputBufferSize);
    private int _inputStart;
    private int _inputEnd;

    private byte[]? _output;
    private int _outputStart;
    private int _outputEnd;

    private long _skipRemaining;

    /// <summary>
    /// Decompresses buffered input into <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written. Zero means more input is needed.</returns>
    public int Read(Span<byte> destination)
    {
        // SnappyStream checks for disposal before calling
        byte[] input = _input!;
        int written = 0;

        while (!destination.IsEmpty)
        {
            if (_outputStart < _outputEnd)
            {
                int count = Math.Min(destination.Length, _outputEnd - _outputStart);
                _output.AsSpan(_outputStart, count).CopyTo(destination);
                _outputStart += count;
                written += count;
                destination = destination.Slice(count);
                continue;
            }

            int available = _inputEnd - _inputStart;

            if (_skipRemaining > 0)
            {
                int count = (int)Math.Min(available, _skipRemaining);
                _inputStart += count;
                _skipRemaining -= count;
                if (_skipRemaining > 0)
                {
                    break;
                }

                continue;
            }

            if (available < StreamFormat.ChunkHeaderLength)
            {
                break;
            }

            uint header = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(_inputStart));
            byte chunkType = (byte)header;
            int chunkLength = (int)(header >> 8);

            switch (chunkType)
            {
                case StreamFormat.CompressedData:
                case StreamFormat.UncompressedData:
                {
                    if (chunkLength < StreamFormat.ChecksumLength || chunkLength > StreamFormat.MaxDataChunkLength)
                    {
                        ThrowHelper.ThrowInvalidDataException("Invalid chunk length.");
                    }

                    if (available < StreamFormat.ChunkHeaderLength + chunkLength)
                    {
                        return written;
                    }

                    ReadOnlySpan<byte> chunk = input.AsSpan(_inputStart + StreamFormat.ChunkHeaderLength, chunkLength);
                    int length = DecodeChunk(chunkType, chunk, destination, out bool direct);
                    _inputStart += StreamFormat.ChunkHeaderLength + chunkLength;

                    if (direct)
                    {
                        written += length;
                        destination = destination.Slice(length);
                    }

                    break;
                }

                case StreamFormat.StreamIdentifier:
                    if (chunkLength != StreamFormat.StreamHeader.Length - StreamFormat.ChunkHeaderLength)
                    {
                        ThrowHelper.ThrowInvalidDataException("Invalid stream identifier.");
                    }

                    if (available < StreamFormat.StreamHeader.Length)
                    {
                        return written;
                    }

                    if (!input.AsSpan(_inputStart, StreamFormat.StreamHeader.Length).SequenceEqual(StreamFormat.StreamHeader))
                    {
                        ThrowHelper.ThrowInvalidDataException("Invalid stream identifier.");
                    }

                    _inputStart += StreamFormat.StreamHeader.Length;
                    break;

                case < StreamFormat.MinSkippable:
                    throw new InvalidDataException($"Unknown chunk type {chunkType:x}");

                default:
                    // Reserved skippable chunk (0x80-0xfe), including padding
                    _inputStart += StreamFormat.ChunkHeaderLength;
                    _skipRemaining = chunkLength;
                    break;
            }
        }

        return written;
    }

    private int DecodeChunk(byte chunkType, ReadOnlySpan<byte> chunk, Span<byte> destination, out bool direct)
    {
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        ReadOnlySpan<byte> data = chunk.Slice(StreamFormat.ChecksumLength);

        int length;
        int headerLength = 0;
        if (chunkType == StreamFormat.CompressedData)
        {
            length = BlockDecompressor.ReadUncompressedLength(data, out headerLength);
            if (length > StreamFormat.MaxChunkDataLength)
            {
                ThrowHelper.ThrowInvalidDataException("Invalid chunk length.");
            }
        }
        else
        {
            length = data.Length;
            if (length > StreamFormat.MaxChunkDataLength)
            {
                ThrowHelper.ThrowInvalidDataException("Invalid chunk length.");
            }
        }

        // Decode straight into the caller's buffer when the whole chunk fits
        direct = destination.Length >= length;
        Span<byte> target;
        if (direct)
        {
            target = destination.Slice(0, length);
        }
        else
        {
            _output ??= ArrayPool<byte>.Shared.Rent(StreamFormat.MaxChunkDataLength);
            target = _output.AsSpan(0, length);
        }

        if (chunkType == StreamFormat.CompressedData)
        {
            BlockDecompressor.Decompress(data, headerLength, target, length);
        }
        else
        {
            data.CopyTo(target);
        }

        if (Crc32C.ComputeMasked(target) != expectedCrc)
        {
            ThrowHelper.ThrowInvalidDataException("Chunk CRC mismatch.");
        }

        if (!direct)
        {
            _outputStart = 0;
            _outputEnd = length;
        }

        return length;
    }

    /// <summary>Returns the free space to read more input into, compacting the buffer first if needed.</summary>
    public Memory<byte> GetInputBuffer()
    {
        // SnappyStream checks for disposal before calling
        byte[] input = _input!;

        if (_inputStart == _inputEnd)
        {
            _inputStart = _inputEnd = 0;
        }
        else if (_inputStart > 0 && input.Length - _inputEnd < StreamFormat.ChunkHeaderLength + StreamFormat.MaxDataChunkLength)
        {
            input.AsSpan(_inputStart, _inputEnd - _inputStart).CopyTo(input);
            _inputEnd -= _inputStart;
            _inputStart = 0;
        }

        return input.AsMemory(_inputEnd);
    }

    /// <summary>Records <paramref name="count"/> bytes read into the buffer from <see cref="GetInputBuffer"/>.</summary>
    public void CommitInput(int count)
    {
        if ((uint)count > (uint)(_input!.Length - _inputEnd))
        {
            ThrowHelper.ThrowInvalidDataException("Insufficient buffer");
        }

        _inputEnd += count;
    }

    /// <summary>Called when the underlying stream has ended; throws if it ended inside a chunk.</summary>
    public void Complete()
    {
        if (_inputEnd > _inputStart || _skipRemaining > 0)
        {
            ThrowHelper.ThrowInvalidDataException("Unexpected end of stream.");
        }
    }

    public void Dispose()
    {
        // Called once, by SnappyStream
        ArrayPool<byte>.Shared.Return(_input!);
        _input = null;

        if (_output is not null)
        {
            ArrayPool<byte>.Shared.Return(_output);
            _output = null;
        }
    }
}
