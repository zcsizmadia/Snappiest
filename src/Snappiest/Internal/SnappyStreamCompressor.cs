using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Snappiest.Internal;

/// <summary>
/// Emits the Snappy framing format. Input is gathered into 64KB chunks; each chunk is compressed straight into an
/// output buffer that is written to the stream once per <see cref="Write"/> call.
/// </summary>
/// <remarks>
/// With parallel options, input is gathered into a batch of chunks (several per thread) and the chunks are
/// compressed concurrently into a second output buffer by pool workers, each chunk packed into place by whichever
/// worker finds its predecessors done (<see cref="SlotPacker"/>). Meanwhile the calling thread writes the previous
/// batch, then joins the workers. A batch never outlives the Write call that started it. Chunk boundaries are the
/// same as single-threaded (every 64KB since the last flush), so the output is identical.
/// </remarks>
internal sealed class SnappyStreamCompressor : IDisposable
{
    private const int ChunkOverhead = StreamFormat.ChunkHeaderLength + StreamFormat.ChecksumLength;

    // Chunks per thread in a parallel batch. A batch ends with the caller joining the workers, so the last chunks of
    // a batch leave threads idle: 2 per thread (half the buffers) measured 16 MB on 16 threads at 1.8-2.4 ms
    // instead of 1.3 ms; 8 per thread made 12 threads slower than 8 before the write overlapped with compression.
    private const int ChunksPerThread = 4;

    private static readonly int MaxChunkOutput =
        ChunkOverhead + VarInt.MaxLength + BlockCompressor.MaxFragmentLength(StreamFormat.MaxChunkDataLength);

    private readonly int _threads;
    private readonly int _batchChunks;
    private readonly SlotPacker? _packer;

    private byte[]? _input;
    private int _inputLength;

    // Output not yet written: the stream header, sequential chunks and the last joined batch
    private byte[]? _output;
    private int _outputLength;

    // The batch in flight compresses into _back (parallel only); when joined, _back becomes the output buffer
    private byte[]? _back;
    private ParallelWork? _pending;
    private GCHandle _pinnedBack;
    private GCHandle _pinnedInput;

    private bool _headerWritten;
    private bool _disposed;

    public SnappyStreamCompressor(SnappyParallelOptions? parallel = null)
    {
        _threads = parallel?.MaxDegreeOfParallelism ?? 1;
        _batchChunks = _threads > 1 ? _threads * ChunksPerThread : 1;
        _packer = _threads > 1 ? new SlotPacker(_batchChunks) : null;
    }

    private int BatchInputSize => _batchChunks * StreamFormat.MaxChunkDataLength;

    private int BatchOutputSize => _batchChunks * MaxChunkOutput;

    // Room for the stream identifier and two chunks when single-threaded; a parallel buffer holds the identifier,
    // one batch and a chunk compressed after it at a flush. Parallel streams have two such buffers.
    private int OutputBufferSize => StreamFormat.StreamHeader.Length + (_threads > 1 ? BatchOutputSize + MaxChunkOutput : 2 * MaxChunkOutput);

    // Output beyond this is written before the next chunk or batch is compressed
    private int WriteThreshold => StreamFormat.StreamHeader.Length + (_threads > 1 ? 0 : MaxChunkOutput);

    public unsafe void Write(ReadOnlySpan<byte> input, Stream stream)
    {
        EnsureBuffers();
        EnsureHeader();

        // A batch may compress from the caller's buffer while the previous batch is written
        fixed (byte* inputPointer = input)
        {
            try
            {
                bool largeWrite = input.Length >= BatchInputSize;
                while (!input.IsEmpty)
                {
                    int consumed = CompressInputOnce(input, largeWrite);
                    input = input.Slice(consumed);

                    if (_outputLength > WriteThreshold)
                    {
                        WriteOutput(stream);
                    }
                }

                JoinPending();
                WriteOutput(stream);
            }
            catch
            {
                AbandonPending();
                throw;
            }
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, Stream stream, CancellationToken cancellationToken)
    {
        EnsureBuffers();
        EnsureHeader();

        using MemoryHandle pin = input.Pin();
        try
        {
            bool largeWrite = input.Length >= BatchInputSize;
            while (!input.IsEmpty)
            {
                int consumed = CompressInputOnce(input.Span, largeWrite);
                input = input.Slice(consumed);

                if (_outputLength > WriteThreshold)
                {
                    await WriteOutputAsync(stream, cancellationToken).ConfigureAwait(false);
                }
            }

            JoinPending();
            await WriteOutputAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            AbandonPending();
            throw;
        }
    }

    public void Flush(Stream stream)
    {
        EnsureBuffers();
        EnsureHeader();

        try
        {
            // Nothing is pending here (writes join their batches), so this only writes leftovers of a failed write
            JoinPending();
            WriteOutput(stream);

            CompressPending();
            JoinPending();
            WriteOutput(stream);
        }
        catch
        {
            AbandonPending();
            throw;
        }
    }

    public async ValueTask FlushAsync(Stream stream, CancellationToken cancellationToken)
    {
        EnsureBuffers();
        EnsureHeader();

        try
        {
            JoinPending();
            await WriteOutputAsync(stream, cancellationToken).ConfigureAwait(false);

            CompressPending();
            JoinPending();
            await WriteOutputAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            AbandonPending();
            throw;
        }
    }

    /// <summary>Consumes input up to the next batch boundary, compressing the batch if it completes.</summary>
    /// <param name="input">The rest of the caller's write, pinned by the caller.</param>
    /// <param name="largeWrite">The caller's write was at least a batch long.</param>
    private int CompressInputOnce(ReadOnlySpan<byte> input, bool largeWrite)
    {
        int batchInput = BatchInputSize;
        if (_inputLength == 0 && input.Length >= batchInput)
        {
            // Compress directly from the caller's buffer
            CompressChunks(input.Slice(0, batchInput));
            return batchInput;
        }

        if (_inputLength == 0 && largeWrite && input.Length >= StreamFormat.MaxChunkDataLength)
        {
            // The rest of a large write: compress its whole chunks from the caller's buffer as a smaller batch, and
            // buffer only the last partial chunk. Copying a whole partial batch here (up to the batch size minus one
            // chunk) on the calling thread made 24 threads slower than 16.
            int whole = input.Length - (input.Length % StreamFormat.MaxChunkDataLength);
            CompressChunks(input.Slice(0, whole));
            return whole;
        }

        // A batch compressing from the input buffer must finish before it is reused
        if (_pinnedInput.IsAllocated)
        {
            JoinPending();
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
        if (_inputLength > 0)
        {
            CompressChunks(_input.AsSpan(0, _inputLength));
            _inputLength = 0;
        }
    }

    /// <summary>
    /// Compresses consecutive 64KB chunks (the last may be shorter) into the output buffer, or starts a batch of
    /// them on the workers. <paramref name="input"/> must stay pinned until the batch is joined.
    /// </summary>
    private unsafe void CompressChunks(ReadOnlySpan<byte> input)
    {
        int count = (input.Length + StreamFormat.MaxChunkDataLength - 1) / StreamFormat.MaxChunkDataLength;
        if (count == 1 || _threads == 1)
        {
            // After the batch before it, which the output buffer has room for
            JoinPending();
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> chunk = input.Slice(i * StreamFormat.MaxChunkDataLength);
                _outputLength += CompressChunk(chunk.Slice(0, Math.Min(chunk.Length, StreamFormat.MaxChunkDataLength)), _output.AsSpan(_outputLength));
            }

            return;
        }

        JoinPending();

        // The output buffer holds either the batch just joined, which the caller writes while this batch runs, or
        // only the stream header: put that in front of the batch so they are written together
        byte[] back = _back!;
        int start = 0;
        if (_outputLength <= StreamFormat.StreamHeader.Length)
        {
            start = _outputLength;
            _output.AsSpan(0, start).CopyTo(back);
            _outputLength = 0;
        }

        _pinnedBack = GCHandle.Alloc(back, GCHandleType.Pinned);
        if (input.Overlaps(_input))
        {
            _pinnedInput = GCHandle.Alloc(_input, GCHandleType.Pinned);
        }

        SlotPacker packer = _packer!;
        byte* backPointer = (byte*)_pinnedBack.AddrOfPinnedObject();
        packer.Reset(count, backPointer + start, count, MaxChunkOutput, backPointer, back.Length, start);

        // Spans cannot be captured by the worker lambda: pass the pinned address instead
        nint inputAddress;
        fixed (byte* inputPointer = input)
        {
            inputAddress = (nint)inputPointer;
        }

        int inputLength = input.Length;
        _pending = ParallelWork.Start(count, _threads, i =>
        {
            int offset = i * StreamFormat.MaxChunkDataLength;
            var chunk = new ReadOnlySpan<byte>((byte*)inputAddress + offset, Math.Min(StreamFormat.MaxChunkDataLength, inputLength - offset));
            packer.Complete(i, CompressChunk(chunk, packer.GetSlot(i)));
        });
    }

    /// <summary>Works on the pending batch until it is done, then makes its buffer the output buffer.</summary>
    private void JoinPending()
    {
        if (_pending is null)
        {
            return;
        }

        ParallelWork pending = _pending;
        _pending = null;
        try
        {
            pending.Join();
        }
        finally
        {
            Unpin();
        }

        // Every chunk is compressed; whatever the last worker did not pack is packed here
        SlotPacker packer = _packer!;
        packer.Pack();

        // The previous output was written while the batch ran
        Debug.Assert(_outputLength == 0);
        (_output, _back) = (_back, _output);
        _outputLength = packer.Packed;
    }

    /// <summary>Stops the pending batch when its output is no longer wanted (the write failed).</summary>
    private void AbandonPending()
    {
        if (_pending is null)
        {
            return;
        }

        ParallelWork pending = _pending;
        _pending = null;
        try
        {
            pending.Abandon();
        }
        finally
        {
            Unpin();
        }
    }

    private void Unpin()
    {
        _pinnedBack.Free();
        if (_pinnedInput.IsAllocated)
        {
            _pinnedInput.Free();
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
            int length = _outputLength;
            _outputLength = 0;
            stream.Write(_output!, 0, length);
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
        if (_threads > 1)
        {
            _back ??= ArrayPool<byte>.Shared.Rent(OutputBufferSize);
        }
    }

    public void Dispose()
    {
        _disposed = true;

        // A batch is pending only after a failed write; the buffers cannot be returned while it runs
        AbandonPending();

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

        if (_back is not null)
        {
            ArrayPool<byte>.Shared.Return(_back);
            _back = null;
        }
    }
}
