using System.Buffers;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Tests;

/// <summary>
/// Opt-in parallel compression must produce exactly the single-threaded output.
/// </summary>
public class ParallelTests
{
    public static IEnumerable<(string File, int Length, int Threads)> Cases()
    {
        foreach (string file in new[] { "json_api.json", "html_x_4", "fireworks.jpeg", "urls.10K", "plrabn12.txt" })
        {
            foreach (int length in new[] { 1, 65535, 65536, 65537, 300_000, 1 << 20, (1 << 24) + 12345 })
            {
                foreach (int threads in new[] { 2, 3, 8 })
                {
                    yield return (file, length, threads);
                }
            }
        }
    }

    private static byte[] Repeat(byte[] data, int length)
    {
        byte[] result = new byte[length];
        for (int i = 0; i < length; i += data.Length)
        {
            data.AsSpan(0, Math.Min(data.Length, length - i)).CopyTo(result.AsSpan(i));
        }

        return result;
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Compress_IdenticalToSingleThreaded(string file, int length, int threads)
    {
        byte[] input = Repeat(TestData.Load(file), length);
        var options = new SnappyParallelOptions { MaxDegreeOfParallelism = threads, MinimumParallelLength = 0 };

        byte[] single = Snappy.CompressToArray(input);
        byte[] parallel = Snappy.CompressToArray(input, options);

        await Assert.That(TestData.Same(single, parallel)).IsTrue();
        await Assert.That(TestData.Same(input, Snappier.Snappy.DecompressToArray(parallel))).IsTrue();
    }

    [Test]
    public async Task TryCompress_SpanOverloads()
    {
        byte[] input = Repeat(TestData.Load("html"), 1 << 20);
        byte[] expected = Snappy.CompressToArray(input);
        var options = new SnappyParallelOptions { MaxDegreeOfParallelism = 4, MinimumParallelLength = 0 };

        // Exactly the right size: fragments are copied from scratch buffers, so no slop is needed
        byte[] exact = new byte[expected.Length];
        await Assert.That(Snappy.TryCompress(input, exact, out int written, options)).IsTrue();
        await Assert.That(written).IsEqualTo(expected.Length);
        await Assert.That(TestData.Same(expected, exact)).IsTrue();

        byte[] large = new byte[Snappy.GetMaxCompressedLength(input.Length)];
        await Assert.That(Snappy.Compress(input, large, options)).IsEqualTo(expected.Length);

        using IMemoryOwner<byte> owner = Snappy.CompressToMemory(input, options);
        await Assert.That(TestData.Same(expected, owner.Memory)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(8)]
    public async Task Compress_Repeated_IdenticalEveryTime(int threads)
    {
        // Fragments are packed into place by whichever worker finds them ready; repeat to shake out ordering races
        byte[] input = Repeat(TestData.Load("json_api.json"), (4 << 20) + 999);
        byte[] expected = Snappy.CompressToArray(input);
        var options = new SnappyParallelOptions { MaxDegreeOfParallelism = threads, MinimumParallelLength = 0 };

        byte[] large = new byte[Snappy.GetMaxCompressedLength(input.Length)];
        byte[] exact = new byte[expected.Length];
        for (int i = 0; i < 100; i++)
        {
            await Assert.That(Snappy.Compress(input, large, options)).IsEqualTo(expected.Length);
            await Assert.That(TestData.Same(expected, large.AsSpan(0, expected.Length).ToArray())).IsTrue();

            await Assert.That(Snappy.Compress(input, exact, options)).IsEqualTo(expected.Length);
            await Assert.That(TestData.Same(expected, exact)).IsTrue();
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(5)]
    public async Task TryCompress_ExactOutput_MoreFragmentsThanScratchSlots(int threads)
    {
        // An exact-size output cannot hold the fragments' slots, so they go through a ring of scratch slots that
        // fragments wait for (256 fragments, 4 slots per thread)
        byte[] input = Repeat(TestData.Load("html_x_4"), (16 << 20) + 1);
        byte[] expected = Snappy.CompressToArray(input);
        var options = new SnappyParallelOptions { MaxDegreeOfParallelism = threads, MinimumParallelLength = 0 };

        byte[] exact = new byte[expected.Length];
        for (int i = 0; i < 5; i++)
        {
            await Assert.That(Snappy.TryCompress(input, exact, out int written, options)).IsTrue();
            await Assert.That(written).IsEqualTo(expected.Length);
            await Assert.That(TestData.Same(expected, exact)).IsTrue();
        }

        // Too small by one byte: the last fragment does not fit; by half: packing stops midway and the waiting
        // fragments must not wait forever
        await Assert.That(Snappy.TryCompress(input, new byte[expected.Length - 1], out _, options)).IsFalse();
        await Assert.That(Snappy.TryCompress(input, new byte[expected.Length / 2], out _, options)).IsFalse();
        await Assert.That(Snappy.TryCompress(input, exact, out _, options)).IsTrue();
        await Assert.That(TestData.Same(expected, exact)).IsTrue();
    }

    [Test]
    public async Task TryCompress_OutputTooSmall_ReturnsFalse()
    {
        byte[] input = Repeat(TestData.Load("html"), 1 << 20);
        int needed = Snappy.CompressToArray(input).Length;
        var options = new SnappyParallelOptions { MaxDegreeOfParallelism = 4, MinimumParallelLength = 0 };

        await Assert.That(Snappy.TryCompress(input, new byte[needed - 1], out _, options)).IsFalse();
        await Assert.That(Snappy.TryCompress(input, new byte[2], out _, options)).IsFalse();
        await Assert.That(Snappy.TryCompress(input, [], out _, options)).IsFalse();
        await Assert.That(() => Snappy.Compress(input, new byte[needed - 1], options)).Throws<ArgumentException>();
    }

    [Test]
    public async Task SmallOrSingleThreaded_UsesTheCallingThread()
    {
        // Below the threshold, or with one thread: the plain path, still identical
        byte[] input = Repeat(TestData.Load("json_api.json"), 1 << 20);
        byte[] expected = Snappy.CompressToArray(input);

        await Assert.That(TestData.Same(expected, Snappy.CompressToArray(input, SnappyParallelOptions.Default))).IsTrue();
        await Assert.That(TestData.Same(expected, Snappy.CompressToArray(input, new SnappyParallelOptions { MaxDegreeOfParallelism = 1 }))).IsTrue();
        await Assert.That(TestData.Same(expected, Snappy.CompressToArray(input, new SnappyParallelOptions { MinimumParallelLength = int.MaxValue }))).IsTrue();
        await Assert.That(Snappy.CompressToArray([], SnappyParallelOptions.Default).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Options_Validation()
    {
        await Assert.That(() => new SnappyParallelOptions { MaxDegreeOfParallelism = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SnappyParallelOptions { MinimumParallelLength = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(SnappyParallelOptions.Default.MaxDegreeOfParallelism).IsEqualTo(Environment.ProcessorCount);
        await Assert.That(SnappyParallelOptions.Default.MinimumParallelLength).IsEqualTo(256 * 1024);

        await Assert.That(() => Snappy.TryCompress([1], new byte[10], out _, null!)).Throws<ArgumentNullException>();
        await Assert.That(() => Snappy.CompressToMemory([1], null!)).Throws<ArgumentNullException>();
        byte[] buffer = new byte[100];
        await Assert.That(() => Snappy.TryCompress(buffer.AsSpan(0, 50), buffer.AsSpan(40), out _, SnappyParallelOptions.Default)).Throws<InvalidOperationException>();
    }
}
