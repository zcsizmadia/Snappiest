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
