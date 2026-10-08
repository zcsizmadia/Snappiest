using System.Buffers;

namespace SnappySimd.Internal;

/// <summary>
/// Compresses the 64KB fragments of a block on several threads. Each fragment is compressed exactly as the
/// single-threaded compressor does (fragments never reference each other), so the output is identical.
/// </summary>
/// <remarks>
/// Fragments are handed out one at a time over the whole input (no batches, so no barriers). Each is compressed
/// into its own slot: in the output itself when the output has room for the maximum compressed length, otherwise
/// in a ring of scratch slots. The <see cref="SlotPacker"/> moves fragments to their final positions as their
/// predecessors complete, so packing overlaps with compression instead of running on the calling thread after
/// each batch (measured, 16 MB json: 2 threads 7.6 -> 5.8 ms, 16 threads 1.48 -> 0.87 ms).
/// </remarks>
internal static unsafe class ParallelBlockCompressor
{
    // Scratch slots per thread when the output is too small for direct compression: a fragment waits for its slot
    // only when packing lags this many fragments per thread behind compression
    private const int SlotsPerThread = 4;

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
        int slot = BlockCompressor.MaxFragmentLength(BlockCompressor.BlockSize);

        // Slots in the output when every fragment's slot fits (the last fragment may be short)
        int last = input.Length - ((fragments - 1) * BlockCompressor.BlockSize);
        bool direct = (long)output.Length - written >= ((long)(fragments - 1) * slot) + BlockCompressor.MaxFragmentLength(last);

        byte[]? scratch = direct ? null : ArrayPool<byte>.Shared.Rent(threads * SlotsPerThread * slot);
        try
        {
            fixed (byte* inputPointer = input)
            fixed (byte* outputPointer = output)
            fixed (byte* scratchPointer = scratch)
            {
                var packer = new SlotPacker(fragments);
                packer.Reset(fragments, direct ? outputPointer + written : scratchPointer, direct ? fragments : threads * SlotsPerThread, slot,
                    outputPointer, output.Length, written);

                // Spans cannot be captured by the worker lambda: pass the pinned address instead
                nint inputAddress = (nint)inputPointer;
                int inputLength = input.Length;

                ParallelWork.For(fragments, threads, i =>
                {
                    if (!packer.WaitForSlot(i))
                    {
                        return;
                    }

                    int offset = i * BlockCompressor.BlockSize;
                    var source = new ReadOnlySpan<byte>((byte*)inputAddress + offset, Math.Min(BlockCompressor.BlockSize, inputLength - offset));
                    packer.Complete(i, BlockCompressor.CompressFragment(source, packer.GetSlot(i)));
                });

                // Every fragment is compressed; whatever the last worker did not pack is packed here
                packer.Pack();
                if (packer.Failed)
                {
                    return false;
                }

                bytesWritten = packer.Packed;
                return true;
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
}
