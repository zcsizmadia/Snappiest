using System.Buffers;
using System.Buffers.Binary;

namespace SnappySimd.Internal;

/// <summary>
/// Emits the Snappy framing format. Input is gathered into 64KB chunks; each chunk is compressed straight into an
/// output buffer that is written to the stream once per <see cref="Write"/> call.
/// </summary>
/// <remarks>
/// With parallel options, input is gathered into a batch of chunks (two per thread) and the chunks are compressed
/// concurrently. Chunk boundaries are the same as single-threaded (every 64KB since the last flush), so the output
/// is identical.
/// </remarks>
internal sealed class SnappyStreamCompressor : IDisposable
{
    private const int ChunkOverhead = StreamFormat.ChunkHeaderLength + StreamFormat.ChecksumLength;
    private const int ChunksPerThread = 2;

    private static readonly int MaxChunkOutput =
        ChunkOverhead + VarInt.MaxLength + BlockCompressor.MaxFragmentLength(StreamFormat.MaxChunkDataLength);

    private readonly int _threads;
    private readonly int _batchChunks;
    private readonly ParallelOptions? _parallelOptions;

    private byte[]? _input;
    private int _inputLength;

    private byte[]? _output;
    private int _outputLength;

    private bool _headerWritten;
    private bool _disposed;

    public SnappyStreamCompressor(SnappyParallelOptions? parallel = null)
    {
        _threads = parallel?.MaxDegreeOfParallelism ?? 1;
        _batchChunks = _threads > 1 ? _threads * ChunksPerThread : 1;
        _parallelOptions = _threads > 1 ? new ParallelOptions { MaxDegreeOfParallelism = _threads } : null;
    }

    private int BatchInputSize => _batchChunks * StreamFormat.MaxChunkDataLength;

    private int BatchOutputSize => _batchChunks * MaxChunkOutput;

    // Room for the stream identifier and two batches
    private int OutputBufferSize => StreamFormat.StreamHeader.Length + (2 * BatchOutputSize);

    public void Write(ReadOnlySpan<byte> input, Stream stream)
    {
        CompressInput(input, stream);
        WriteOutput(stream);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, Stream stream, CancellationToken cancellationToken)
    {
        while (!input.IsEmpty)
        {
            // Bound the buffered output so it can be written between batches
            int consumed = CompressInputOnce(input.Span);
            input = input.Slice(consumed);

            if (_outputLength > OutputBufferSize - BatchOutputSize)
            {
                await WriteOutputAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }

        EnsureBuffers();
        EnsureHeader();
        await WriteOutputAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public void Flush(Stream stream)
    {
        CompressPending();
        WriteOutput(stream);
    }

    public ValueTask FlushAsync(Stream stream, CancellationToken cancellationToken)
    {
        CompressPending();
        return WriteOutputAsync(stream, cancellationToken);
    }

    private void CompressInput(ReadOnlySpan<byte> input, Stream stream)
    {
        EnsureBuffers();
        EnsureHeader();

        while (!input.IsEmpty)
        {
            int consumed = CompressInputOnce(input);
            input = input.Slice(consumed);

            if (_outputLength > OutputBufferSize - BatchOutputSize)
            {
                WriteOutput(stream);
            }
        }
    }

    /// <summary>Consumes input up to the next batch boundary, compressing the batch if it completes.</summary>
    private int CompressInputOnce(ReadOnlySpan<byte> input)
    {
        EnsureBuffers();
        EnsureHeader();

        int batchInput = BatchInputSize;
        if (_inputLength == 0 && input.Length >= batchInput)
        {
            // Compress directly from the caller's buffer
            CompressChunks(input.Slice(0, batchInput));
            return batchInput;
        }

        int append = Math.Min(input.Length, batchInput - _inputLength);
        input.Slice(0, append).CopyTo(_input.AsSpan(_inputLength));
        _inputLength += append;

        if (_inputLength == batchInput)
        {
            CompressChunks(_input.AsSpan(0, _inputLength));
            _inputLength = 0;
        }

        return append;
    }

    private void CompressPending()
    {
        EnsureBuffers();
        EnsureHeader();

        if (_inputLength > 0)
        {
            CompressChunks(_input.AsSpan(0, _inputLength));
            _inputLength = 0;
        }
    }

    /// <summary>Compresses consecutive 64KB chunks (the last may be shorter) into the output buffer.</summary>
    private unsafe void CompressChunks(ReadOnlySpan<byte> input)
    {
        int count = (input.Length + StreamFormat.MaxChunkDataLength - 1) / StreamFormat.MaxChunkDataLength;
        if (count == 1 || _parallelOptions is null)
        {
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> chunk = input.Slice(i * StreamFormat.MaxChunkDataLength);
                _outputLength += CompressChunk(chunk.Slice(0, Math.Min(chunk.Length, StreamFormat.MaxChunkDataLength)), _output.AsSpan(_outputLength));
            }

            return;
        }

        // Each chunk goes into its own slot, then the slots are packed in order
        int[] lengths = ArrayPool<int>.Shared.Rent(count);
        try
        {
            fixed (byte* inputPointer = input)
            fixed (byte* outputPointer = &_output![_outputLength])
            {
                nint inputAddress = (nint)inputPointer;
                nint outputAddress = (nint)outputPointer;
                int inputLength = input.Length;
                int slot = MaxChunkOutput;

                Parallel.For(0, count, _parallelOptions, i =>
                {
                    int offset = i * StreamFormat.MaxChunkDataLength;
                    var chunk = new ReadOnlySpan<byte>((byte*)inputAddress + offset, Math.Min(StreamFormat.MaxChunkDataLength, inputLength - offset));
                    lengths[i] = CompressChunk(chunk, new Span<byte>((byte*)outputAddress + ((nint)i * slot), slot));
                });

                // Slot i starts at or after the end of the packed chunks before it, so moving forward is safe
                int packed = lengths[0];
                for (int i = 1; i < count; i++)
                {
                    Buffer.MemoryCopy(outputPointer + ((nint)i * slot), outputPointer + packed, lengths[i], lengths[i]);
                    packed += lengths[i];
                }

                _outputLength += packed;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(lengths);
        }
    }

    /// <summary>Writes one chunk (header, checksum, data) to <paramref name="destination"/>.</summary>
    /// <returns>The chunk's length.</returns>
    private static int CompressChunk(ReadOnlySpan<byte> input, Span<byte> destination)
    {
        Span<byte> body = destination.Slice(ChunkOverhead);

        uint crc = Crc32C.ComputeMasked(input);

        // Cannot fail: the destination always has room for a maximum size chunk
        BlockCompressor.TryCompress(input, body, out int compressedLength);

        byte chunkType;
        int bodyLength;
        if (compressedLength < input.Length)
        {
            chunkType = StreamFormat.CompressedData;
            bodyLength = compressedLength;
        }
        else
        {
            // Compression did not help: store the data, overwriting the compressed attempt
            chunkType = StreamFormat.UncompressedData;
            bodyLength = input.Length;
            input.CopyTo(body);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)((bodyLength + StreamFormat.ChecksumLength) << 8) | chunkType);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(StreamFormat.ChunkHeaderLength), crc);

        return ChunkOverhead + bodyLength;
    }

    private void WriteOutput(Stream stream)
    {
        if (_outputLength > 0)
        {
            stream.Write(_output!, 0, _outputLength);
            _outputLength = 0;
        }
    }

    private async ValueTask WriteOutputAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (_outputLength > 0)
        {
            int length = _outputLength;
            _outputLength = 0;
            await stream.WriteAsync(_output.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureHeader()
    {
        if (!_headerWritten)
        {
            StreamFormat.StreamHeader.CopyTo(_output.AsSpan(_outputLength));
            _outputLength += StreamFormat.StreamHeader.Length;
            _headerWritten = true;
        }
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_input), nameof(_output))]
    private void EnsureBuffers()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _input ??= ArrayPool<byte>.Shared.Rent(BatchInputSize);
        _output ??= ArrayPool<byte>.Shared.Rent(OutputBufferSize);
    }

    public void Dispose()
    {
        _disposed = true;

        if (_input is not null)
        {
            ArrayPool<byte>.Shared.Return(_input);
            _input = null;
        }

        if (_output is not null)
        {
            ArrayPool<byte>.Shared.Return(_output);
            _output = null;
        }
    }
}
