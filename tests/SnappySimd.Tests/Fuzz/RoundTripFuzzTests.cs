using System.IO.Compression;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Tests.Fuzz;

/// <summary>
/// Layer 1: generated, realistic inputs must round-trip through blocks and streams, decode with Snappier, never be
/// bigger than Snappier's output, and compress identically in parallel.
/// </summary>
public class RoundTripFuzzTests
{
    [Test]
    public async Task Blocks()
    {
        var harness = new FuzzHarness(nameof(RoundTripFuzzTests) + "." + nameof(Blocks));
        while (harness.Next() is { } random)
        {
            var (name, generate) = FuzzGenerators.All[random.Next(FuzzGenerators.All.Length)];
            byte[] input = generate(random, FuzzGenerators.Length(random));
            try
            {
                byte[] ours = Snappy.CompressToArray(input);
                byte[] theirs = Snappier.Snappy.CompressToArray(input);

                if (!TestData.Same(input, Snappy.DecompressToArray(ours)))
                {
                    harness.Fail($"{name}, {input.Length} B: SnappySimd round trip differs", input);
                }
                else if (!TestData.Same(input, Snappier.Snappy.DecompressToArray(ours)))
                {
                    harness.Fail($"{name}, {input.Length} B: Snappier cannot read SnappySimd output", input);
                }
                else if (!TestData.Same(input, Snappy.DecompressToArray(theirs)))
                {
                    harness.Fail($"{name}, {input.Length} B: SnappySimd cannot read Snappier output", input);
                }
                else if (ours.Length > theirs.Length + (theirs.Length / 100) + 16)
                {
                    harness.Fail($"{name}, {input.Length} B: {ours.Length} B compressed vs Snappier {theirs.Length} B", input);
                }
                else if (ours.Length > Snappy.GetMaxCompressedLength(input.Length))
                {
                    harness.Fail($"{name}, {input.Length} B: exceeds GetMaxCompressedLength", input);
                }
            }
            catch (Exception exception)
            {
                harness.Fail($"{name}, {input.Length} B: {exception.GetType().Name}: {exception.Message}", input);
            }
        }

        await Assert.That(harness.Failures).IsEmpty().Because(harness.Report());
    }

    [Test]
    public async Task Streams_AndParallel()
    {
        var harness = new FuzzHarness(nameof(RoundTripFuzzTests) + "." + nameof(Streams_AndParallel));
        while (harness.Next() is { } random)
        {
            var (name, generate) = FuzzGenerators.All[random.Next(FuzzGenerators.All.Length)];
            byte[] input = generate(random, FuzzGenerators.Length(random) * random.Next(1, 4));
            int threads = random.Next(2, 6);
            var options = new SnappyParallelOptions { MaxDegreeOfParallelism = threads, MinimumParallelLength = 0 };
            try
            {
                // Random write sizes and flushes, single-threaded and parallel: identical bytes expected
                int writeSeed = random.Next();
                byte[] single = WriteStream(input, new Random(writeSeed), null);
                byte[] parallel = WriteStream(input, new Random(writeSeed), options);

                if (!TestData.Same(single, parallel))
                {
                    harness.Fail($"{name}, {input.Length} B, {threads} threads: parallel stream differs", input);
                }
                else if (!TestData.Same(input, ReadStream(single, null, random.Next(1, 100_000))))
                {
                    harness.Fail($"{name}, {input.Length} B: stream round trip differs", input);
                }
                else if (!TestData.Same(input, ReadStream(single, options, random.Next(1, 100_000))))
                {
                    harness.Fail($"{name}, {input.Length} B, {threads} threads: parallel stream read differs", input);
                }
                else if (!TestData.Same(input, ReadSnappier(single)))
                {
                    harness.Fail($"{name}, {input.Length} B: Snappier cannot read the stream", input);
                }
                else if (!TestData.Same(Snappy.CompressToArray(input), Snappy.CompressToArray(input, options)))
                {
                    harness.Fail($"{name}, {input.Length} B, {threads} threads: parallel block differs", input);
                }
            }
            catch (Exception exception)
            {
                harness.Fail($"{name}, {input.Length} B: {exception.GetType().Name}: {exception.Message}", input);
            }
        }

        await Assert.That(harness.Failures).IsEmpty().Because(harness.Report());
    }

    [Test]
    public async Task GuardPages()
    {
        // Input and output against inaccessible pages: an out-of-bounds access crashes the run
        var harness = new FuzzHarness(nameof(RoundTripFuzzTests) + "." + nameof(GuardPages));
        while (harness.Next() is { } random)
        {
            var (name, generate) = FuzzGenerators.All[random.Next(FuzzGenerators.All.Length)];
            byte[] input = generate(random, Math.Min(FuzzGenerators.Length(random), 200_000));
            bool alignToEnd = random.Next(2) == 0;
            try
            {
                using GuardedBuffer source = GuardedBuffer.From(input, alignToEnd);
                byte[] compressed = new byte[Snappy.GetMaxCompressedLength(input.Length)];
                int length = Snappy.Compress(source.Span, compressed);

                using GuardedBuffer packed = GuardedBuffer.From(compressed.AsSpan(0, length), alignToEnd);
                using var output = new GuardedBuffer(input.Length, alignToEnd);
                Snappy.Decompress(packed.Span, output.Span);

                if (!input.AsSpan().SequenceEqual(output.Span))
                {
                    harness.Fail($"{name}, {input.Length} B: guard page round trip differs", input);
                }
            }
            catch (Exception exception)
            {
                harness.Fail($"{name}, {input.Length} B: {exception.GetType().Name}: {exception.Message}", input);
            }
        }

        await Assert.That(harness.Failures).IsEmpty().Because(harness.Report());
    }

    internal static byte[] WriteStream(byte[] input, Random random, SnappyParallelOptions? options)
    {
        using var output = new MemoryStream();
        using (SnappyStream compressor = options is null
                   ? new SnappyStream(output, CompressionMode.Compress, leaveOpen: true)
                   : new SnappyStream(output, CompressionMode.Compress, leaveOpen: true, options))
        {
            for (int i = 0; i < input.Length;)
            {
                int count = Math.Min(random.Next(1, 150_000), input.Length - i);
                compressor.Write(input, i, count);
                if (random.Next(4) == 0)
                {
                    compressor.Flush();
                }

                i += count;
            }
        }

        return output.ToArray();
    }

    internal static byte[] ReadStream(byte[] compressed, SnappyParallelOptions? options, int readSize)
    {
        using SnappyStream decompressor = options is null
            ? new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress)
            : new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress, leaveOpen: false, options);
        using var output = new MemoryStream();
        decompressor.CopyTo(output, readSize);
        return output.ToArray();
    }

    internal static byte[] ReadSnappier(byte[] compressed)
    {
        using var decompressor = new Snappier.SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return output.ToArray();
    }
}
