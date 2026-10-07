using System.IO.Compression;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Tests;

/// <summary>
/// Data produced by SnappySimd must be readable by Snappier and vice versa, in both the raw block and the framed
/// stream formats. This is what makes migration a drop-in change.
/// </summary>
public class InteropTests
{
    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task Block_SnappySimdToSnappier(string filename)
    {
        byte[] input = TestData.Load(filename);
        byte[] compressed = Snappy.CompressToArray(input);

        await Assert.That(TestData.Same(input, Snappier.Snappy.DecompressToArray(compressed))).IsTrue();
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task Block_SnappierToSnappySimd(string filename)
    {
        byte[] input = TestData.Load(filename);
        byte[] compressed = Snappier.Snappy.CompressToArray(input);

        await Assert.That(TestData.Same(input, Snappy.DecompressToArray(compressed))).IsTrue();
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task Block_CompressionRatio_NotWorseThanSnappier(string filename)
    {
        byte[] input = TestData.Load(filename);
        int ours = Snappy.CompressToArray(input).Length;
        int theirs = Snappier.Snappy.CompressToArray(input).Length;

        // Same algorithm family; allow a small tolerance for the different hash table size
        await Assert.That((double)ours).IsLessThanOrEqualTo(theirs * 1.01);
    }

    // Byte-identical output is not part of the contract (only "valid Snappy, never larger than Snappier"), but it
    // holds today because both use google/snappy's CompressFragment; these tests notice if that changes on any CI
    // platform, so the README statement can be kept accurate.
    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task Block_IdenticalToSnappier(string filename)
    {
        byte[] input = TestData.Load(filename);

        await Assert.That(TestData.Same(Snappier.Snappy.CompressToArray(input), Snappy.CompressToArray(input))).IsTrue();
    }

    [Test]
    public async Task Block_IdenticalToSnappier_RandomSlices()
    {
        byte[][] corpus = [.. TestData.CorpusFiles().Select(TestData.Load)];
        var random = new Random(11);
        int different = 0;
        for (int i = 0; i < 500; i++)
        {
            byte[] file = corpus[random.Next(corpus.Length)];
            int length = Math.Min(file.Length, random.Next(4) switch
            {
                0 => random.Next(0, 300),
                1 => random.Next(300, 5000),
                2 => random.Next(5000, 70_000),
                _ => random.Next(70_000, 300_000),
            });
            byte[] input = file.AsSpan(random.Next(file.Length - length + 1), length).ToArray();
            if (!TestData.Same(Snappier.Snappy.CompressToArray(input), Snappy.CompressToArray(input)))
            {
                different++;
            }
        }

        await Assert.That(different).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task Stream_IdenticalToSnappier(string filename)
    {
        byte[] input = TestData.Load(filename);

        foreach (string pattern in new[] { "one", "4k", "random+flush" })
        {
            byte[] ours = WriteStream(output => new SnappyStream(output, CompressionMode.Compress, true), input, pattern);
            byte[] theirs = WriteStream(output => new Snappier.SnappyStream(output, CompressionMode.Compress, true), input, pattern);

            await Assert.That(TestData.Same(theirs, ours)).IsTrue().Because(pattern);
        }
    }

    private static byte[] WriteStream(Func<Stream, Stream> create, byte[] input, string pattern)
    {
        using var output = new MemoryStream();
        using (Stream compressor = create(output))
        {
            var random = new Random(5);
            for (int i = 0; i < input.Length;)
            {
                int count = pattern switch
                {
                    "one" => input.Length,
                    "4k" => 4096,
                    _ => random.Next(1, 150_000),
                };
                count = Math.Min(count, input.Length - i);
                compressor.Write(input, i, count);
                i += count;
                if (pattern == "random+flush" && random.Next(4) == 0)
                {
                    compressor.Flush();
                }
            }
        }

        return output.ToArray();
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task Stream_SnappySimdToSnappier(string filename)
    {
        byte[] input = TestData.Load(filename);

        using var compressed = new MemoryStream();
        using (var compressor = new SnappyStream(compressed, CompressionMode.Compress, true))
        {
            compressor.Write(input);
        }

        compressed.Position = 0;
        using var decompressor = new Snappier.SnappyStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);

        await Assert.That(TestData.Same(input, output.ToArray())).IsTrue();
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task Stream_SnappierToSnappySimd(string filename)
    {
        byte[] input = TestData.Load(filename);

        using var compressed = new MemoryStream();
        using (var compressor = new Snappier.SnappyStream(compressed, CompressionMode.Compress, true))
        {
            compressor.Write(input);
        }

        compressed.Position = 0;
        using var decompressor = new SnappyStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);

        await Assert.That(TestData.Same(input, output.ToArray())).IsTrue();
    }

    [Test]
    public async Task Stream_SnappierSmallFlushes_ToSnappySimd()
    {
        byte[] input = TestData.Load("alice29.txt");
        var random = new Random(5);

        using var compressed = new MemoryStream();
        using (var compressor = new Snappier.SnappyStream(compressed, CompressionMode.Compress, true))
        {
            int position = 0;
            while (position < input.Length)
            {
                int count = Math.Min(random.Next(1, 3000), input.Length - position);
                compressor.Write(input, position, count);
                compressor.Flush();
                position += count;
            }
        }

        compressed.Position = 0;
        using var decompressor = new SnappyStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output, 777);

        await Assert.That(TestData.Same(input, output.ToArray())).IsTrue();
    }

    [Test]
    [Arguments("alice29.snappy", "alice29.txt")]
    [Arguments("html_x_4.snappy", "html_x_4")]
    public async Task Stream_ReferenceFiles(string compressedFile, string originalFile)
    {
        using var compressed = new MemoryStream(TestData.Load(compressedFile));
        using var decompressor = new SnappyStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);

        await Assert.That(TestData.Same(TestData.Load(originalFile), output.ToArray())).IsTrue();
    }

    [Test]
    public async Task Block_RandomData_BothDirections()
    {
        var random = new Random(99);
        int failures = 0;
        for (int i = 0; i < 2000; i++)
        {
            byte[] input = i % 2 == 0
                ? TestData.Generate(random, random.Next(0, 70000), random.Next(1, 9))
                : TestData.GenerateText(random, random.Next(0, 70000));

            if (!TestData.Same(input, Snappier.Snappy.DecompressToArray(Snappy.CompressToArray(input)))
                || !TestData.Same(input, Snappy.DecompressToArray(Snappier.Snappy.CompressToArray(input))))
            {
                failures++;
            }
        }

        await Assert.That(failures).IsEqualTo(0);
    }
}
