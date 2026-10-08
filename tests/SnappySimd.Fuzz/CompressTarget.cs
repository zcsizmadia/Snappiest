using System.Buffers;
using System.IO.Compression;
using SnappySimd.Tests.Fuzz;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Fuzz;

/// <summary>
/// Compression round trips of arbitrary bytes, as blocks and as streams, single-threaded and parallel.
/// </summary>
/// <remarks>
/// <para>
/// Input format: the first byte's low four bits are a repeat count. With a count of n > 0 the rest of the input is
/// repeated up to n * 20,011 bytes (at most about 300KB), so short inputs still reach multi-fragment and multi-chunk
/// data (and the parallel paths, which need at least two 64KB fragments). With 0 the rest is used as it is.
/// </para>
/// <para>
/// Every way of compressing must give the same bytes (the parallel output is specified to be identical), the result
/// must fit in <see cref="Snappy.GetMaxCompressedLength"/>, and both SnappySimd and the spec decoder must restore
/// the input.
/// </para>
/// </remarks>
internal static class CompressTarget
{
    private const int RepeatUnit = 20_011;

    private static readonly SnappyParallelOptions Parallel = new() { MaxDegreeOfParallelism = 4, MinimumParallelLength = 0 };

    // Write sizes for the piecewise stream writes: odd sizes that straddle the 64KB chunk boundaries
    private static readonly int[] Writes = [1, 1000, 65535, 7, 70000, 4096, 3];

    public static void Run(ReadOnlySpan<byte> data)
    {
        byte[] input = Payload(data);

        byte[] block = Snappy.CompressToArray(input);
        Check.That(block.Length <= Snappy.GetMaxCompressedLength(input.Length), $"{block.Length} bytes is above the maximum compressed length");

        Same("Compress(guarded, max length)", block, CompressGuarded(input, Snappy.GetMaxCompressedLength(input.Length), null));
        Same("Compress(guarded, exact length)", block, CompressGuarded(input, block.Length, null));
        byte[] shortOutput = new byte[block.Length - 1];
        Check.That(!Snappy.TryCompress(input, shortOutput, out _), "TryCompress succeeded with one byte too few");

        var writer = new ArrayBufferWriter<byte>();
        Snappy.Compress(Sequences.Split(input, 30_011), writer);
        Same("Compress(sequence)", block, writer.WrittenSpan.ToArray());

        Same("DecompressToArray", input, Snappy.DecompressToArray(block));
        byte[]? reference = ReferenceDecoder.Decode(block, out string why);
        Check.That(reference is not null, $"the spec decoder rejects the compressed block ({why})");
        Same("ReferenceDecoder", input, reference);

        byte[] stream = CompressStream(input, null, piecewise: false);
        byte[] flushed = CompressStream(input, null, piecewise: true, flush: true);
        Same("SnappyStream(piecewise)", stream, CompressStream(input, null, piecewise: true));
        Same("SnappyStream round trip", input, DecompressStream(stream));
        Same("SnappyStream round trip (flushed)", input, DecompressStream(flushed));
        byte[] referenceStream = ReferenceFraming.Decode(stream, out string? error);
        Check.That(error is null, $"the spec decoder rejects the compressed stream ({error})");
        Same("ReferenceFraming", input, referenceStream);

        // Parallel output must be identical to single-threaded
        Coverage.Untraced(() =>
        {
            Same("CompressToArray(parallel)", block, Snappy.CompressToArray(input, Parallel));
            Same("Compress(parallel, guarded, max length)", block, CompressGuarded(input, Snappy.GetMaxCompressedLength(input.Length), Parallel));
            Same("Compress(parallel, guarded, exact length)", block, CompressGuarded(input, block.Length, Parallel));
            Check.That(!Snappy.TryCompress(input, shortOutput, out _, Parallel), "TryCompress(parallel) succeeded with one byte too few");
            Same("SnappyStream(parallel)", stream, CompressStream(input, Parallel, piecewise: false));
            Same("SnappyStream(parallel, piecewise)", stream, CompressStream(input, Parallel, piecewise: true));
            Same("SnappyStream(parallel, flushed)", flushed, CompressStream(input, Parallel, piecewise: true, flush: true));
            return true;
        });
    }

    private static byte[] Payload(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return [];
        }

        int repeat = data[0] & 0x0F;
        ReadOnlySpan<byte> rest = data.Slice(1);
        if (repeat == 0 || rest.IsEmpty)
        {
            return rest.ToArray();
        }

        byte[] payload = new byte[repeat * RepeatUnit];
        for (int i = 0; i < payload.Length; i += rest.Length)
        {
            rest.Slice(0, Math.Min(rest.Length, payload.Length - i)).CopyTo(payload.AsSpan(i));
        }

        return payload;
    }

    private static unsafe byte[] CompressGuarded(byte[] input, int outputLength, SnappyParallelOptions? options)
    {
        using GuardedBuffer source = GuardedBuffer.From(input);
        using var target = new GuardedBuffer(outputLength);
        var sourceSpan = new ReadOnlySpan<byte>(source.Pointer, source.Length);
        int written = options is null
            ? Snappy.Compress(sourceSpan, target.Span)
            : Snappy.Compress(sourceSpan, target.Span, options);
        return target.Span.Slice(0, written).ToArray();
    }

    private static byte[] CompressStream(byte[] input, SnappyParallelOptions? options, bool piecewise, bool flush = false)
    {
        var output = new MemoryStream();
        using (SnappyStream stream = options is null
            ? new SnappyStream(output, CompressionMode.Compress, leaveOpen: true)
            : new SnappyStream(output, CompressionMode.Compress, leaveOpen: true, options))
        {
            if (!piecewise)
            {
                stream.Write(input);
            }
            else
            {
                // At least one write, as in the whole-input case: writing even nothing emits the stream identifier
                int position = 0, call = 0;
                do
                {
                    int count = Math.Min(Writes[call % Writes.Length], input.Length - position);
                    stream.Write(input, position, count);
                    position += count;
                    if (flush && call % 3 == 1)
                    {
                        stream.Flush();
                    }

                    call++;
                }
                while (position < input.Length);
            }
        }

        return output.ToArray();
    }

    private static byte[] DecompressStream(byte[] compressed)
    {
        using var stream = new SnappyStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
        var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static void Same(string what, byte[] expected, byte[] actual) =>
        Check.That(expected.AsSpan().SequenceEqual(actual), $"{what}: {actual.Length} bytes differ from the expected {expected.Length}");
}
