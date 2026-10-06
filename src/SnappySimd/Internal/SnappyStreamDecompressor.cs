using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;

namespace SnappySimd.Internal;

/// <summary>
/// Parses the Snappy framing format. Compressed input is buffered until a whole chunk is available, then the chunk
/// is decoded in one pass: directly into the caller's buffer when it fits, otherwise into an internal buffer.
/// </summary>
/// <remarks>
/// With parallel options, a run of complete data chunks in the input buffer (up to two per thread) is decoded
/// concurrently into per-chunk slots, which are then served in order. A failing chunk throws when reading reaches
/// it, as in sequential decoding.
/// </remarks>
internal sealed class SnappyStreamDecompressor : IDisposable
{
    // Holds at least one maximum size chunk plus read-ahead
    private const int SequentialInputBufferSize = 1 << 17;
    private const int ChunksPerThread = 2;

    private readonly ParallelOptions? _parallelOptions;
    private readonly int _batchChunks;

    private byte[]? _input;
    private int _inputStart;
    private int _inputEnd;

    // Decoded output not yet returned: _slotCount slots of up to 64KB, served from slot _slot at _outputStart
    private byte[]? _output;
    private readonly int[] _slotLengths;
    private readonly Exception?[] _slotErrors;
    private int _slotCount;
    private int _slot;
    private int _outputStart;

    private long _skipRemaining;

    // Set when a chunk decoded in a parallel batch failed. The batch's input is already consumed, so unlike the
    // sequential path (which leaves a bad chunk unread) every later read must fail too.
    private Exception? _failure;

    private readonly int[] _batchOffsets;

    public SnappyStreamDecompressor(SnappyParallelOptions? parallel = null)
    {
        int threads = parallel?.MaxDegreeOfParallelism ?? 1;
        _batchChunks = threads > 1 ? threads * ChunksPerThread : 1;
        _parallelOptions = threads > 1 ? new ParallelOptions { MaxDegreeOfParallelism = threads } : null;
        _slotLengths = new int[_batchChunks];
        _slotErrors = new Exception?[_batchChunks];
        _batchOffsets = new int[_batchChunks];

        int inputSize = Math.Max(SequentialInputBufferSize, (_batchChunks + 1) * (StreamFormat.ChunkHeaderLength + StreamFormat.MaxDataChunkLength));
        _input = ArrayPool<byte>.Shared.Rent(inputSize);
    }

    /// <summary>
    /// Decompresses buffered input into <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written. Zero means more input is needed.</returns>
    public int Read(Span<byte> destination)
    {
        if (_failure is not null)
        {
            ExceptionDispatchInfo.Throw(_failure);
        }

        // SnappyStream checks for disposal before calling
        byte[] input = _input!;
        int written = 0;

        while (!destination.IsEmpty)
        {
            if (_slot < _slotCount)
            {
                if (_slotErrors[_slot] is { } error)
                {
                    // The chunk failed when it was decoded in a batch: fail now that reading has reached it, and stay failed
                    _slotCount = _slot = 0;
                    _failure = error;
                    ExceptionDispatchInfo.Throw(error);
                }

                int length = _slotLengths[_slot];
                int count = Math.Min(destination.Length, length - _outputStart);
                _output.AsSpan((_slot * StreamFormat.MaxChunkDataLength) + _outputStart, count).CopyTo(destination);
                _outputStart += count;
                written += count;
                destination = destination.Slice(count);

                if (_outputStart == length)
                {
                    _slot++;
                    _outputStart = 0;
                }

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

            if (_parallelOptions is not null && DecodeBatch(input))
            {
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
                    int length = GetChunkDataLength(chunkType, chunk, out int headerLength);

                    // Decode straight into the caller's buffer when the whole chunk fits
                    if (destination.Length >= length)
                    {
                        DecodeChunk(chunkType, chunk, headerLength, destination.Slice(0, length));
                        written += length;
                        destination = destination.Slice(length);
                    }
                    else
                    {
                        _output ??= ArrayPool<byte>.Shared.Rent(_batchChunks * StreamFormat.MaxChunkDataLength);
                        DecodeChunk(chunkType, chunk, headerLength, _output.AsSpan(0, length));
                        _slotLengths[0] = length;
                        _slotErrors[0] = null;
                        _slotCount = 1;
                        _slot = 0;
                        _outputStart = 0;
                    }

                    _inputStart += StreamFormat.ChunkHeaderLength + chunkLength;
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

    /// <summary>
    /// Decodes a run of at least two complete data chunks at the head of the input concurrently.
    /// </summary>
    /// <returns><c>false</c> if there is no such run; the sequential path then handles the next chunk.</returns>
    private bool DecodeBatch(byte[] input)
    {
        int[] offsets = _batchOffsets;
        int count = 0;
        int position = _inputStart;

        // Stop at the first chunk that is not a complete, plausible data chunk: the sequential path deals with it
        while (count < _batchChunks && _inputEnd - position >= StreamFormat.ChunkHeaderLength)
        {
            uint header = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(position));
            byte chunkType = (byte)header;
            int chunkLength = (int)(header >> 8);
            if ((chunkType != StreamFormat.CompressedData && chunkType != StreamFormat.UncompressedData)
                || chunkLength < StreamFormat.ChecksumLength || chunkLength > StreamFormat.MaxDataChunkLength
                || _inputEnd - position < StreamFormat.ChunkHeaderLength + chunkLength)
            {
                break;
            }

            offsets[count++] = position;
            position += StreamFormat.ChunkHeaderLength + chunkLength;
        }

        if (count < 2)
        {
            return false;
        }

        _output ??= ArrayPool<byte>.Shared.Rent(_batchChunks * StreamFormat.MaxChunkDataLength);
        byte[] output = _output;
        int[] lengths = _slotLengths;
        Exception?[] errors = _slotErrors;

        Parallel.For(0, count, _parallelOptions!, i =>
        {
            int offset = offsets[i];
            uint header = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset));
            ReadOnlySpan<byte> chunk = input.AsSpan(offset + StreamFormat.ChunkHeaderLength, (int)(header >> 8));
            try
            {
                int length = GetChunkDataLength((byte)header, chunk, out int headerLength);
                DecodeChunk((byte)header, chunk, headerLength, output.AsSpan(i * StreamFormat.MaxChunkDataLength, length));
                lengths[i] = length;
                errors[i] = null;
            }
            catch (InvalidDataException exception)
            {
                lengths[i] = 0;
                errors[i] = exception;
            }
        });

        _inputStart = position;
        _slotCount = count;
        _slot = 0;
        _outputStart = 0;
        return true;
    }

    /// <summary>Reads the length of a data chunk's uncompressed data, validating it.</summary>
    private static int GetChunkDataLength(byte chunkType, ReadOnlySpan<byte> chunk, out int headerLength)
    {
        ReadOnlySpan<byte> data = chunk.Slice(StreamFormat.ChecksumLength);

        int length;
        if (chunkType == StreamFormat.CompressedData)
        {
            length = BlockDecompressor.ReadUncompressedLength(data, out headerLength);
        }
        else
        {
            length = data.Length;
            headerLength = 0;
        }

        if (length > StreamFormat.MaxChunkDataLength)
        {
            ThrowHelper.ThrowInvalidDataException("Invalid chunk length.");
        }

        return length;
    }

    /// <summary>Decodes a data chunk into <paramref name="target"/> (exactly its length) and verifies its CRC.</summary>
    private static void DecodeChunk(byte chunkType, ReadOnlySpan<byte> chunk, int headerLength, Span<byte> target)
    {
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        ReadOnlySpan<byte> data = chunk.Slice(StreamFormat.ChecksumLength);

        if (chunkType == StreamFormat.CompressedData)
        {
            BlockDecompressor.Decompress(data, headerLength, target, target.Length);
        }
        else
        {
            data.CopyTo(target);
        }

        if (Crc32C.ComputeMasked(target) != expectedCrc)
        {
            ThrowHelper.ThrowInvalidDataException("Chunk CRC mismatch.");
        }
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
