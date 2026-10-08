using System.Buffers;
using Snappiest.Internal;

namespace Snappiest;

/// <summary>
/// Routines for performing Snappy compression and decompression on raw data blocks using <see cref="Span{T}"/>.
/// These routines do not read or write any Snappy framing.
/// </summary>
/// <remarks>
/// The members match <c>Snappier.Snappy</c>, so code written against Snappier only needs its <c>using</c>
/// directive changed to <c>Snappiest</c>.
/// </remarks>
public static class Snappy
{
    /// <summary>
    /// For a given amount of input data, calculate the maximum potential size of the compressed output.
    /// </summary>
    /// <param name="inputLength">Length of the input data, in bytes.</param>
    /// <returns>The maximum potential size of the compressed output.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative, or too large for the
    /// result to fit in an <see cref="int"/>.</exception>
    /// <remarks>
    /// This is useful for allocating a sufficient output buffer before calling <see cref="Compress(ReadOnlySpan{byte}, Span{byte})"/>.
    /// A buffer of this size lets compression write directly to the output.
    /// </remarks>
    public static int GetMaxCompressedLength(int inputLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);

        long length = BlockCompressor.MaxCompressedLength(inputLength);
        if (length > Array.MaxLength)
        {
            ThrowHelper.ThrowArgumentOutOfRangeException(nameof(inputLength), "Input is too large.");
        }

        return (int)length;
    }

    /// <summary>
    /// Compress a block of Snappy data.
    /// </summary>
    /// <param name="input">Data to compress.</param>
    /// <param name="output">Buffer to receive the compressed data.</param>
    /// <returns>Number of bytes written to <paramref name="output"/>.</returns>
    /// <exception cref="ArgumentException">Output buffer is too small.</exception>
    /// <exception cref="InvalidOperationException">Input and output spans must not overlap.</exception>
    /// <remarks>
    /// The output buffer must be large enough to contain the compressed output.
    /// </remarks>
    public static int Compress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (!TryCompress(input, output, out int bytesWritten))
        {
            ThrowHelper.ThrowArgumentExceptionInsufficientOutputBuffer(nameof(output));
        }

        return bytesWritten;
    }

    /// <summary>
    /// Attempt to compress the input data into the output buffer.
    /// </summary>
    /// <param name="input">Data to compress.</param>
    /// <param name="output">Buffer to receive the compressed data.</param>
    /// <param name="bytesWritten">Number of bytes written to the <paramref name="output"/>.</param>
    /// <exception cref="InvalidOperationException">Input and output spans must not overlap.</exception>
    /// <returns><c>true</c> if the compression was successful, <c>false</c> if the output buffer is too small.</returns>
    public static bool TryCompress(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten)
    {
        if (output.IsEmpty)
        {
            // Minimum of 1 byte is required to store a zero-length block, short circuit.
            bytesWritten = 0;
            return false;
        }

        if (input.Overlaps(output))
        {
            ThrowHelper.ThrowInvalidOperationException("Input and output spans must not overlap.");
        }

        return BlockCompressor.TryCompress(input, output, out bytesWritten);
    }

    /// <summary>
    /// Compress a block of Snappy data.
    /// </summary>
    /// <param name="input">Data to compress.</param>
    /// <param name="output">Buffer writer to receive the compressed data.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> is larger than the maximum of 4,294,967,295 bytes.</exception>
    /// <remarks>
    ///     <para>
    ///     For the best performance, sequences with more than one segment should be comprised of segments some multiple of 64KB
    ///     in size (i.e. 64KB or 128KB or 256KB each) with only the final segment varying.
    ///     </para>
    /// </remarks>
    public static void Compress(ReadOnlySequence<byte> input, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (input.Length > uint.MaxValue)
        {
            ThrowHelper.ThrowArgumentException("Input is too large.", nameof(input));
        }

        Span<byte> header = output.GetSpan(VarInt.MaxLength);
        output.Advance(VarInt.Write(header, (uint)input.Length));

        byte[]? scratch = null;
        try
        {
            while (!input.IsEmpty)
            {
                int fragmentLength = (int)Math.Min(input.Length, BlockCompressor.BlockSize);

                ReadOnlySpan<byte> fragment;
                if (input.FirstSpan.Length >= fragmentLength)
                {
                    fragment = input.FirstSpan.Slice(0, fragmentLength);
                }
                else
                {
                    scratch ??= ArrayPool<byte>.Shared.Rent(BlockCompressor.BlockSize);
                    input.Slice(0, fragmentLength).CopyTo(scratch);
                    fragment = scratch.AsSpan(0, fragmentLength);
                }

                Span<byte> destination = output.GetSpan(BlockCompressor.MaxFragmentLength(fragmentLength));
                output.Advance(BlockCompressor.CompressFragment(fragment, destination));

                input = input.Slice(fragmentLength);
            }
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }

    /// <summary>
    /// Compress a block of Snappy data.
    /// </summary>
    /// <param name="input">Data to compress.</param>
    /// <returns>An <see cref="IMemoryOwner{T}"/> with the compressed data. The caller is responsible for disposing this object.</returns>
    /// <remarks>
    /// Failing to dispose of the returned <see cref="IMemoryOwner{T}"/> may result in performance loss.
    /// </remarks>
    public static IMemoryOwner<byte> CompressToMemory(ReadOnlySpan<byte> input)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(GetMaxCompressedLength(input.Length));

        // Cannot fail: the buffer has the maximum compressed length
        BlockCompressor.TryCompress(input, buffer, out int length);

        return new PooledMemoryOwner(buffer, length);
    }

    /// <summary>
    /// Compress a block of Snappy data.
    /// </summary>
    /// <param name="input">Data to compress.</param>
    /// <remarks>
    /// The resulting byte array is allocated on the heap. If possible, <see cref="CompressToMemory(ReadOnlySpan{byte})"/> should
    /// be used instead since it uses a shared buffer pool.
    /// </remarks>
    public static byte[] CompressToArray(ReadOnlySpan<byte> input)
    {
        using IMemoryOwner<byte> buffer = CompressToMemory(input);
        return buffer.Memory.Span.ToArray();
    }

    /// <summary>
    /// Compress a block of Snappy data, using several threads for large inputs.
    /// </summary>
    /// <param name="input">Data to compress.</param>
    /// <param name="output">Buffer to receive the compressed data.</param>
    /// <param name="options">Threading options.</param>
    /// <returns>Number of bytes written to <paramref name="output"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Output buffer is too small.</exception>
    /// <exception cref="InvalidOperationException">Input and output spans must not overlap.</exception>
    /// <remarks>
    /// The output is identical to <see cref="Compress(ReadOnlySpan{byte}, Span{byte})"/>: 64KB fragments are
    /// compressed independently on up to <see cref="SnappyParallelOptions.MaxDegreeOfParallelism"/> threads.
    /// </remarks>
    public static int Compress(ReadOnlySpan<byte> input, Span<byte> output, SnappyParallelOptions options)
    {
        if (!TryCompress(input, output, out int bytesWritten, options))
        {
            ThrowHelper.ThrowArgumentExceptionInsufficientOutputBuffer(nameof(output));
        }

        return bytesWritten;
    }

    /// <summary>
    /// Attempt to compress the input data into the output buffer, using several threads for large inputs.
    /// </summary>
    /// <param name="input">Data to compress.</param>
    /// <param name="output">Buffer to receive the compressed data.</param>
    /// <param name="bytesWritten">Number of bytes written to the <paramref name="output"/>.</param>
    /// <param name="options">Threading options.</param>
    /// <returns><c>true</c> if the compression was successful, <c>false</c> if the output buffer is too small.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Input and output spans must not overlap.</exception>
    public static bool TryCompress(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten, SnappyParallelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (output.IsEmpty)
        {
            bytesWritten = 0;
            return false;
        }

        if (input.Overlaps(output))
        {
            ThrowHelper.ThrowInvalidOperationException("Input and output spans must not overlap.");
        }

        return ParallelBlockCompressor.TryCompress(input, output, options, out bytesWritten);
    }

    /// <summary>
    /// Compress a block of Snappy data, using several threads for large inputs.
    /// </summary>
    /// <param name="input">Data to compress.</param>
    /// <param name="options">Threading options.</param>
    /// <returns>An <see cref="IMemoryOwner{T}"/> with the compressed data. The caller is responsible for disposing this object.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static IMemoryOwner<byte> CompressToMemory(ReadOnlySpan<byte> input, SnappyParallelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(GetMaxCompressedLength(input.Length));

        // Cannot fail: the buffer has the maximum compressed length
        ParallelBlockCompressor.TryCompress(input, buffer, options, out int length);

        return new PooledMemoryOwner(buffer, length);
    }

    /// <summary>
    /// Compress a block of Snappy data, using several threads for large inputs.
    /// </summary>
    /// <param name="input">Data to compress.</param>
    /// <param name="options">Threading options.</param>
    /// <returns>The compressed data.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static byte[] CompressToArray(ReadOnlySpan<byte> input, SnappyParallelOptions options)
    {
        using IMemoryOwner<byte> buffer = CompressToMemory(input, options);
        return buffer.Memory.Span.ToArray();
    }

    /// <summary>
    /// Get the uncompressed data length from a compressed Snappy block.
    /// </summary>
    /// <param name="input">Compressed snappy block.</param>
    /// <returns>The length of the uncompressed data in the block.</returns>
    /// <exception cref="InvalidDataException">The data in <paramref name="input"/> has an invalid length.</exception>
    /// <remarks>
    /// This is useful for allocating a sufficient output buffer before calling <see cref="Decompress(ReadOnlySpan{byte}, Span{byte})"/>.
    /// </remarks>
    public static int GetUncompressedLength(ReadOnlySpan<byte> input) =>
        VarInt.ReadLength(input, out _);

    /// <summary>
    /// Decompress a block of Snappy data. This must be an entire block.
    /// </summary>
    /// <param name="input">Data to decompress.</param>
    /// <param name="output">Buffer to receive the decompressed data.</param>
    /// <returns>Number of bytes written to <paramref name="output"/>.</returns>
    /// <exception cref="InvalidDataException">Invalid Snappy block.</exception>
    /// <exception cref="ArgumentException">Output buffer is too small.</exception>
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (!TryDecompress(input, output, out int bytesWritten))
        {
            ThrowHelper.ThrowArgumentExceptionInsufficientOutputBuffer(nameof(output));
        }

        return bytesWritten;
    }

    /// <summary>
    /// Decompress a block of Snappy data. This must be an entire block.
    /// </summary>
    /// <param name="input">Data to decompress.</param>
    /// <param name="output">Buffer to receive the decompressed data.</param>
    /// <param name="bytesWritten">Number of bytes written to the <paramref name="output"/>.</param>
    /// <returns><c>true</c> if the decompression was successful, <c>false</c> if the output buffer is too small.</returns>
    /// <exception cref="InvalidDataException">Invalid Snappy block.</exception>
    /// <remarks>
    /// The block is always validated: corrupt input throws even when <paramref name="output"/> is too small. When it
    /// is too small, as much of the decompressed data as fits is written.
    /// </remarks>
    public static bool TryDecompress(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten)
    {
        int length = BlockDecompressor.ReadUncompressedLength(input, out int headerLength);

        if (output.Length >= length)
        {
            BlockDecompressor.Decompress(input, headerLength, output, length);
            bytesWritten = length;
            return true;
        }

        // Uncommon: validate without allocating, then decompress to a temporary buffer for the partial result
        if (!BlockDecompressor.Validate(input, headerLength, length))
        {
            ThrowHelper.ThrowInvalidDataExceptionCorruptBlock();
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            BlockDecompressor.Decompress(input, headerLength, buffer, length);
            buffer.AsSpan(0, output.Length).CopyTo(output);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        bytesWritten = output.Length;
        return false;
    }

    /// <summary>
    /// Decompress a block of Snappy data. This must be an entire block.
    /// </summary>
    /// <param name="input">Data to decompress.</param>
    /// <param name="output">Buffer writer to receive the decompressed data.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="InvalidDataException">Invalid Snappy block.</exception>
    public static void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        using ContiguousInput contiguous = new(input);
        ReadOnlySpan<byte> span = contiguous.Span;

        int length = BlockDecompressor.ReadUncompressedLength(span, out int headerLength);
        if (length == 0)
        {
            BlockDecompressor.Decompress(span, headerLength, default, 0);
            return;
        }

        BlockDecompressor.Decompress(span, headerLength, output.GetSpan(length), length);
        output.Advance(length);
    }

    /// <summary>
    /// Decompress a block of Snappy data to a new memory buffer. This must be an entire block.
    /// </summary>
    /// <param name="input">Data to decompress.</param>
    /// <returns>An <see cref="IMemoryOwner{T}"/> with the decompressed data. The caller is responsible for disposing this object.</returns>
    /// <exception cref="InvalidDataException">Invalid Snappy block.</exception>
    /// <remarks>
    /// Failing to dispose of the returned <see cref="IMemoryOwner{T}"/> may result in performance loss.
    /// </remarks>
    public static IMemoryOwner<byte> DecompressToMemory(ReadOnlySpan<byte> input)
    {
        int length = BlockDecompressor.ReadUncompressedLength(input, out int headerLength);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            BlockDecompressor.Decompress(input, headerLength, buffer, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return new PooledMemoryOwner(buffer, length);
    }

    /// <summary>
    /// Decompress a block of Snappy data to a new memory buffer. This must be an entire block.
    /// </summary>
    /// <param name="input">Data to decompress.</param>
    /// <returns>An <see cref="IMemoryOwner{T}"/> with the decompressed data. The caller is responsible for disposing this object.</returns>
    /// <exception cref="InvalidDataException">Invalid Snappy block.</exception>
    /// <remarks>
    /// Failing to dispose of the returned <see cref="IMemoryOwner{T}"/> may result in performance loss.
    /// </remarks>
    public static IMemoryOwner<byte> DecompressToMemory(ReadOnlySequence<byte> input)
    {
        using ContiguousInput contiguous = new(input);
        return DecompressToMemory(contiguous.Span);
    }

    /// <summary>
    /// Decompress a block of Snappy to a new byte array. This must be an entire block.
    /// </summary>
    /// <param name="input">Data to decompress.</param>
    /// <returns>The decompressed data.</returns>
    /// <exception cref="InvalidDataException">Invalid Snappy block.</exception>
    /// <remarks>
    /// The resulting byte array is allocated on the heap. If possible, <see cref="DecompressToMemory(ReadOnlySpan{byte})"/> should
    /// be used instead since it uses a shared buffer pool.
    /// </remarks>
    public static byte[] DecompressToArray(ReadOnlySpan<byte> input)
    {
        int length = BlockDecompressor.ReadUncompressedLength(input, out int headerLength);

        byte[] result = length == 0 ? [] : GC.AllocateUninitializedArray<byte>(length);
        BlockDecompressor.Decompress(input, headerLength, result, length);
        return result;
    }

    /// <summary>
    /// A contiguous view of a sequence, copying to a pooled buffer only when it has more than one segment.
    /// </summary>
    private readonly ref struct ContiguousInput
    {
        private readonly byte[]? _rented;

        public ContiguousInput(ReadOnlySequence<byte> sequence)
        {
            if (sequence.IsSingleSegment)
            {
                Span = sequence.FirstSpan;
            }
            else
            {
                if (sequence.Length > Array.MaxLength)
                {
                    ThrowHelper.ThrowInvalidDataExceptionCorruptBlock();
                }

                int length = (int)sequence.Length;
                _rented = ArrayPool<byte>.Shared.Rent(length);
                sequence.CopyTo(_rented);
                Span = _rented.AsSpan(0, length);
            }
        }

        public ReadOnlySpan<byte> Span { get; }

        public void Dispose()
        {
            if (_rented is not null)
            {
                ArrayPool<byte>.Shared.Return(_rented);
            }
        }
    }
}
