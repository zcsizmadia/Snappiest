using System.Buffers;

namespace SnappySimd.Internal;

/// <summary>
/// Compresses the 64KB fragments of a block on several threads. Each fragment is compressed exactly as the
/// single-threaded compressor does (fragments never reference each other), so the output is identical.
/// </summary>
internal static unsafe class ParallelBlockCompressor
{
    // Fragments in flight per thread: enough to balance uneven fragments, few enough to bound the scratch memory
    private const int FragmentsPerThread = 4;

    public static bool TryCompress(ReadOnlySpan<byte> input, Span<byte> output, SnappyParallelOptions options, out int bytesWritten)
    {
        int fragments = (int)(((long)input.Length + BlockCompressor.BlockSize - 1) / BlockCompressor.BlockSize);
        int threads = Math.Min(options.MaxDegreeOfParallelism, fragments);
        if (input.Length < options.MinimumParallelLength || threads < 2)
        {
            return BlockCompressor.TryCompress(input, output, out bytesWritten);
        }

        bytesWritten = 0;
        if (output.Length < VarInt.GetByteCount((uint)input.Length))
        {
            return false;
        }

        int written = VarInt.Write(output, (uint)input.Length);

        int batch = Math.Min(fragments, threads * FragmentsPerThread);
        int slot = BlockCompressor.MaxFragmentLength(BlockCompressor.BlockSize);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(batch * slot);
        int[] lengths = ArrayPool<int>.Shared.Rent(batch);
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = threads };

        try
        {
            fixed (byte* inputPointer = input)
            fixed (byte* scratchPointer = scratch)
            {
                // Spans cannot be captured by the worker lambda: pass the pinned addresses instead
                nint inputAddress = (nint)inputPointer;
                nint scratchAddress = (nint)scratchPointer;
                int inputLength = input.Length;

                for (int first = 0; first < fragments; first += batch)
                {
                    int count = Math.Min(batch, fragments - first);
                    int firstFragment = first;

                    ParallelWork.For(count, parallelOptions, i =>
                    {
                        int offset = (firstFragment + i) * BlockCompressor.BlockSize;
                        var source = new ReadOnlySpan<byte>((byte*)inputAddress + offset, Math.Min(BlockCompressor.BlockSize, inputLength - offset));
                        var destination = new Span<byte>((byte*)scratchAddress + ((nint)i * slot), slot);
                        lengths[i] = BlockCompressor.CompressFragment(source, destination);
                    });

                    for (int i = 0; i < count; i++)
                    {
                        if (output.Length - written < lengths[i])
                        {
                            return false;
                        }

                        new ReadOnlySpan<byte>(scratchPointer + ((nint)i * slot), lengths[i]).CopyTo(output.Slice(written));
                        written += lengths[i];
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(lengths);
            ArrayPool<byte>.Shared.Return(scratch);
        }

        bytesWritten = written;
        return true;
    }
}
