using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;

namespace Snappiest.Internal;

/// <summary>
/// Parses the Snappy framing format. Compressed input is buffered until a whole chunk is available, then the chunk
/// is decoded in one pass: directly into the caller's buffer when it fits, otherwise into an internal buffer.
/// </summary>
/// <remarks>
/// With parallel options, a run of complete data chunks in the input buffer (a batch of several per thread) is
/// decoded concurrently: straight into the caller's buffer when at least two of them fit, otherwise into per-chunk
/// slots, which are then served in order. While the slots of one batch are served, the next batch is decoded in
/// the background into a second set of slots, so small reads (Stream.CopyTo uses 80KB) overlap copying with
/// decoding; the background batch is joined before a read returns 0, so nothing runs while input is refilled or
/// after Dispose. A failing chunk throws when reading reaches it, as in sequential decoding.
/// </remarks>
internal sealed class SnappyStreamDecompressor : IDisposable
{
    // Holds at least one maximum size chunk plus read-ahead
    private const int SequentialInputBufferSize = 1 << 17;

    // Chunks per thread in a batch. Two sets of slots are decoded alternately, so this is half of the single set
    // used before, keeping the memory the same.
    private const int ChunksPerThread = 2;

    private readonly int _threads;
    private readonly int _batchChunks;

    private byte[]? _input;
    private int _inputStart;
    private int _inputEnd;

    // Decoded output not yet returned: _slots.Count slots of up to 64KB, served from slot _slot at _outputStart.
    // _ahead holds the batch being decoded in the background, if any.
    private SlotSet _slots;
    private SlotSet _ahead;
    private ParallelWork? _aheadWork;
    private int _slot;
    private int _outputStart;

    private long _skipRemaining;

    // Set when a chunk decoded in a parallel batch failed. The batch's input is already consumed, so unlike the
    // sequential path (which leaves a bad chunk unread) every later read must fail too.
    private Exception? _failure;

    private readonly int[] _batchOffsets;

    public SnappyStreamDecompressor(SnappyParallelOptions? parallel = null)
    {
        _threads = parallel?.MaxDegreeOfParallelism ?? 1;
        _batchChunks = _threads > 1 ? _threads * ChunksPerThread : 1;
        _slots = new SlotSet(_batchChunks);
        _ahead = new SlotSet(_batchChunks);
        _batchOffsets = new int[_batchChunks];

        // Room for the batch being served and the one decoded ahead, plus a chunk
        int batches = _threads > 1 ? 2 : 1;
        int inputSize = Math.Max(SequentialInputBufferSize, ((batches * _batchChunks) + 1) * (StreamFormat.ChunkHeaderLength + StreamFormat.MaxDataChunkLength));
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
            SlotSet slots = _slots;
            if (_slot < slots.Count)
            {
                if (slots.Errors[_slot] is { } error)
                {
                    // The chunk failed when it was decoded in a batch: fail now that reading has reached it, and stay failed
                    slots.Count = _slot = 0;
                    _failure = error;
                    ExceptionDispatchInfo.Throw(error);
                }

                int length = slots.Lengths[_slot];
                int count = Math.Min(destination.Length, length - _outputStart);
                slots.Output.AsSpan((_slot * StreamFormat.MaxChunkDataLength) + _outputStart, count).CopyTo(destination);
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

            if (_aheadWork is not null)
            {
                // The batch decoded while these slots were served is next; decode the one after it meanwhile
                JoinAhead();
                StartAhead(input);
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

            if (_threads > 1)
            {
                // Progress is consumed input, not written bytes: a batch of empty chunks writes nothing, and
                // `available` is stale after any batch
                int batchStart = _inputStart;
                int direct = DecodeBatchInto(input, destination);
                if (_inputStart != batchStart)
                {
                    written += direct;
                    destination = destination.Slice(direct);
                    continue;
                }

                if (DecodeBatch(input))
                {
                    StartAhead(input);
                    continue;
                }
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
                        byte[] output = slots.EnsureOutput();
                        DecodeChunk(chunkType, chunk, headerLength, output.AsSpan(0, length));
                        slots.Lengths[0] = length;
                        slots.Errors[0] = null;
                        slots.Count = 1;
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
    /// Decodes a run of complete data chunks at the head of the input concurrently, straight into
    /// <paramref name="destination"/>, when at least two of them fit. Copying decoded slots into the caller's buffer
    /// on the reading thread was the serial bottleneck of parallel decompression.
    /// </summary>
    /// <returns>
    /// The number of bytes written, which is 0 for a batch of empty chunks: callers detect progress by
    /// <c>_inputStart</c> moving. Nothing is consumed if fewer than two chunks fit or the first chunk fails. If a chunk fails,
    /// the chunks before it are returned and its input stays unread, so the sequential path reports the error, as in
    /// sequential decoding.
    /// </returns>
    private int DecodeBatchInto(byte[] input, Span<byte> destination)
    {
        int[] offsets = _batchOffsets;
        int[] lengths = _slots.Lengths;
        int count = 0;
        int total = 0;
        int position = _inputStart;

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

            int length;
            try
            {
                length = GetChunkDataLength(chunkType, input.AsSpan(position + StreamFormat.ChunkHeaderLength, chunkLength), out _);
            }
            catch (InvalidDataException)
            {
                break; // the sequential path reports it when reading reaches it
            }

            if (destination.Length - total < length)
            {
                break;
            }

            offsets[count] = position;
            lengths[count++] = total;
            total += length;
            position += StreamFormat.ChunkHeaderLength + chunkLength;
        }

        if (count < 2)
        {
            return 0;
        }

        // Spans cannot be captured by the worker lambda: pass the pinned address instead
        int failed = count;
        unsafe
        {
            fixed (byte* target = destination)
            {
                nint address = (nint)target;
                int end = total;
                ParallelWork.For(count, _threads, i =>
                {
                    int offset = offsets[i];
                    uint header = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset));
                    ReadOnlySpan<byte> chunk = input.AsSpan(offset + StreamFormat.ChunkHeaderLength, (int)(header >> 8));
                    int start = lengths[i];
                    int length = (i + 1 < count ? lengths[i + 1] : end) - start;
                    try
                    {
                        GetChunkDataLength((byte)header, chunk, out int headerLength);
                        DecodeChunk((byte)header, chunk, headerLength, new Span<byte>((byte*)address + start, length));
                    }
                    catch (InvalidDataException)
                    {
                        // Keep the first failing chunk
                        int seen;
                        while (i < (seen = Volatile.Read(ref failed)) && Interlocked.CompareExchange(ref failed, i, seen) != seen)
                        {
                        }
                    }
                });
            }
        }

        if (failed < count)
        {
            // Return the chunks before the failing one; leave it unread
            _inputStart = offsets[failed];
            return lengths[failed];
        }

        _inputStart = position;
        return total;
    }

    /// <summary>
    /// Finds a run of at least two complete data chunks at the head of the input and consumes it.
    /// </summary>
    /// <returns>The number of chunks, with their offsets in <paramref name="offsets"/>; 0 if there is no such run.</returns>
    private int TakeBatch(byte[] input, int[] offsets)
    {
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
            return 0;
        }

        _inputStart = position;
        return count;
    }

    /// <summary>
    /// Decodes a run of at least two complete data chunks at the head of the input concurrently into the slots.
    /// </summary>
    /// <returns><c>false</c> if there is no such run; the sequential path then handles the next chunk.</returns>
    private bool DecodeBatch(byte[] input)
    {
        SlotSet slots = _slots;
        int count = TakeBatch(input, slots.Offsets);
        if (count == 0)
        {
            return false;
        }

        slots.EnsureOutput();
        slots.Count = count;
        _slot = 0;
        _outputStart = 0;
        ParallelWork.For(count, _threads, slots.Decode(input));
        return true;
    }

    /// <summary>Starts decoding the next run of complete data chunks into the spare slots, if there is one.</summary>
    private void StartAhead(byte[] input)
    {
        SlotSet ahead = _ahead;
        int count = TakeBatch(input, ahead.Offsets);
        if (count == 0)
        {
            return;
        }

        ahead.EnsureOutput();
        ahead.Count = count;
        _aheadWork = ParallelWork.Start(count, _threads, ahead.Decode(input));
    }

    /// <summary>Waits for the batch decoded ahead and makes its slots the ones being served.</summary>
    private void JoinAhead()
    {
        ParallelWork work = _aheadWork!;
        _aheadWork = null;
        try
        {
            work.Join();
        }
        catch (Exception exception)
        {
            // Not a chunk error (those are kept per slot): the batch is lost, so the stream cannot go on
            _failure = exception;
            throw;
        }

        (_slots, _ahead) = (_ahead, _slots);
        _slot = 0;
        _outputStart = 0;
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
        // SnappyStream checks for disposal before calling, and calls only after a read returned 0, so no batch is
        // decoding from the buffer
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
        // Called once, by SnappyStream. A batch is still decoding only after a read threw: wait for it before the
        // buffers go back to the pool
        _aheadWork?.Abandon();
        _aheadWork = null;

        ArrayPool<byte>.Shared.Return(_input!);
        _input = null;
        _slots.Dispose();
        _ahead.Dispose();
    }

    /// <summary>A batch of decoded chunks: one slot of up to 64KB per chunk, with its length or error.</summary>
    private sealed class SlotSet(int capacity)
    {
        public byte[]? Buffer;
        public readonly int[] Lengths = new int[capacity];
        public readonly Exception?[] Errors = new Exception?[capacity];
        public readonly int[] Offsets = new int[capacity];
        public int Count;

        public byte[] Output => Buffer!;

        public byte[] EnsureOutput() => Buffer ??= ArrayPool<byte>.Shared.Rent(capacity * StreamFormat.MaxChunkDataLength);

        /// <summary>The worker body decoding chunk i of <paramref name="input"/> (at Offsets[i]) into slot i.</summary>
        public Action<int> Decode(byte[] input)
        {
            byte[] output = Buffer!;
            int[] offsets = Offsets;
            int[] lengths = Lengths;
            Exception?[] errors = Errors;
            return i =>
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
            };
        }

        public void Dispose()
        {
            if (Buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(Buffer);
                Buffer = null;
            }
        }
    }
}
