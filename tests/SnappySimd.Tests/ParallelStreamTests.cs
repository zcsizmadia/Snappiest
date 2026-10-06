using System.IO.Compression;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Tests;

/// <summary>
/// Parallel SnappyStream: output identical to single-threaded, and every combination of writer and reader
/// (single-threaded, parallel, Snappier) round-trips.
/// </summary>
public class ParallelStreamTests
{
    public enum WritePattern
    {
        OneWrite,
        Writes4K,
        RandomWritesWithFlushes,
        AsyncWrites,
    }

    public static IEnumerable<(string File, int Length, int Threads, WritePattern Pattern)> Cases()
    {
        foreach (string file in new[] { "json_api.json", "html_x_4", "fireworks.jpeg", "alice29.txt" })
        {
            foreach (int length in new[] { 1, 65536, 300_000, (1 << 22) + 777 })
            {
                foreach (int threads in new[] { 2, 5 })
                {
                    foreach (WritePattern pattern in Enum.GetValues<WritePattern>())
                    {
                        yield return (file, length, threads, pattern);
                    }
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

    private static SnappyParallelOptions Options(int threads) => new() { MaxDegreeOfParallelism = threads, MinimumParallelLength = 0 };

    /// <summary>Compresses with SnappySimd (threads = 1: single-threaded constructor).</summary>
    private static async Task<byte[]> Compress(byte[] input, int threads, WritePattern pattern)
    {
        using var output = new MemoryStream();
        await using (SnappyStream compressor = threads == 1
                         ? new SnappyStream(output, CompressionMode.Compress, leaveOpen: true)
                         : new SnappyStream(output, CompressionMode.Compress, leaveOpen: true, Options(threads)))
        {
            switch (pattern)
            {
                case WritePattern.OneWrite:
                    compressor.Write(input);
                    break;

                case WritePattern.Writes4K:
                    for (int i = 0; i < input.Length; i += 4096)
                    {
                        compressor.Write(input, i, Math.Min(4096, input.Length - i));
                    }

                    break;

                case WritePattern.RandomWritesWithFlushes:
                    var random = new Random(input.Length);
                    for (int i = 0; i < input.Length;)
                    {
                        int count = Math.Min(random.Next(1, 200_000), input.Length - i);
                        compressor.Write(input, i, count);
                        if (random.Next(3) == 0)
                        {
                            compressor.Flush();
                        }

                        i += count;
                    }

                    break;

                case WritePattern.AsyncWrites:
                    for (int i = 0; i < input.Length; i += 100_000)
                    {
                        await compressor.WriteAsync(input.AsMemory(i, Math.Min(100_000, input.Length - i)));
                    }

                    await compressor.FlushAsync();
                    break;
            }
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] compressed, int threads, int readSize = 81920)
    {
        using SnappyStream decompressor = threads == 1
            ? new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress)
            : new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress, leaveOpen: false, Options(threads));
        using var output = new MemoryStream();
        decompressor.CopyTo(output, readSize);
        return output.ToArray();
    }

    private static async Task<byte[]> DecompressAsync(byte[] compressed, int threads)
    {
        await using var decompressor = new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress, leaveOpen: false, Options(threads));
        using var output = new MemoryStream();
        await decompressor.CopyToAsync(output);
        return output.ToArray();
    }

    private static byte[] SnappierCompress(byte[] input, int flushEvery = 0)
    {
        using var output = new MemoryStream();
        using (var compressor = new Snappier.SnappyStream(output, CompressionMode.Compress, true))
        {
            if (flushEvery == 0)
            {
                compressor.Write(input);
            }
            else
            {
                for (int i = 0; i < input.Length; i += flushEvery)
                {
                    compressor.Write(input, i, Math.Min(flushEvery, input.Length - i));
                    compressor.Flush();
                }
            }
        }

        return output.ToArray();
    }

    private static byte[] SnappierDecompress(byte[] compressed)
    {
        using var decompressor = new Snappier.SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return output.ToArray();
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Compress_IdenticalToSingleThreaded(string file, int length, int threads, WritePattern pattern)
    {
        byte[] input = Repeat(TestData.Load(file), length);

        byte[] single = await Compress(input, 1, pattern);
        byte[] parallel = await Compress(input, threads, pattern);

        await Assert.That(TestData.Same(single, parallel)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task AllWriterReaderCombinations_RoundTrip(string file, int length, int threads, WritePattern pattern)
    {
        byte[] input = Repeat(TestData.Load(file), length);
        byte[] fromSingle = await Compress(input, 1, pattern);
        byte[] fromParallel = await Compress(input, threads, pattern);

        // Written single-threaded, read in parallel; written in parallel, read single-threaded and by Snappier
        await Assert.That(TestData.Same(input, Decompress(fromSingle, threads))).IsTrue();
        await Assert.That(TestData.Same(input, Decompress(fromParallel, 1))).IsTrue();
        await Assert.That(TestData.Same(input, Decompress(fromParallel, threads))).IsTrue();
        await Assert.That(TestData.Same(input, SnappierDecompress(fromParallel))).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(7)]
    public async Task SnappierStreams_ReadInParallel(int threads)
    {
        byte[] input = Repeat(TestData.Load("json_api.json"), 3_000_000);

        await Assert.That(TestData.Same(input, Decompress(SnappierCompress(input), threads))).IsTrue();
        await Assert.That(TestData.Same(input, Decompress(SnappierCompress(input, flushEvery: 3000), threads))).IsTrue();
        await Assert.That(TestData.Same(input, await DecompressAsync(SnappierCompress(input), threads))).IsTrue();
    }

    [Test]
    [Arguments(1)]
    [Arguments(1000)]
    [Arguments(65536)]
    [Arguments(1 << 20)]
    public async Task ParallelRead_VariousReadSizes(int readSize)
    {
        byte[] input = Repeat(TestData.Load("html_x_4"), 2_000_000);
        byte[] compressed = await Compress(input, 1, WritePattern.OneWrite);

        await Assert.That(TestData.Same(input, Decompress(compressed, 4, readSize))).IsTrue();
    }

    [Test]
    public async Task ParallelRead_SkippableAndIdentifierChunksBetweenDataChunks()
    {
        byte[] part = Repeat(TestData.Load("html"), 300_000);
        byte[] first = await Compress(part, 1, WritePattern.OneWrite);
        byte[] skippable = [0x80, 0x05, 0x00, 0x00, 1, 2, 3, 4, 5];

        // Two complete streams (each with its own identifier) separated by a skippable chunk
        byte[] combined = [.. first, .. skippable, .. first];

        await Assert.That(TestData.Same([.. part, .. part], Decompress(combined, 4))).IsTrue();
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(1000)]
    public async Task ParallelRead_CorruptChunk_SameBytesBeforeTheError(int readSize)
    {
        byte[] input = Repeat(TestData.Load("json_api.json"), 1_000_000);
        byte[] compressed = await Compress(input, 1, WritePattern.OneWrite);

        // Corrupt the CRC of the 6th chunk: chunks before it are fine, it must fail, nothing after it is returned
        int position = StreamFormatHeaderLength;
        for (int chunk = 0; chunk < 5; chunk++)
        {
            position += 4 + (compressed[position + 1] | (compressed[position + 2] << 8) | (compressed[position + 3] << 16));
        }

        compressed[position + 4] ^= 0xFF;

        int sequential = ReadUntilError(compressed, 1, readSize);
        int parallel = ReadUntilError(compressed, 4, readSize);

        // Bytes copied by the read that hits the bad chunk are lost with the exception, so allow one read less
        await Assert.That(sequential).IsGreaterThanOrEqualTo((5 * 65536) - readSize);
        await Assert.That(sequential).IsLessThanOrEqualTo(5 * 65536);
        await Assert.That(parallel).IsEqualTo(sequential);
    }

    private const int StreamFormatHeaderLength = 10;

    private static int ReadUntilError(byte[] compressed, int threads, int readSize)
    {
        using SnappyStream decompressor = threads == 1
            ? new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress)
            : new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress, leaveOpen: false, Options(threads));
        byte[] buffer = new byte[readSize];
        int total = 0;
        try
        {
            int read;
            while ((read = decompressor.Read(buffer)) > 0)
            {
                total += read;
            }
        }
        catch (InvalidDataException)
        {
            return total;
        }

        return -1;
    }

    [Test]
    public async Task Constructor_NullOptions_Throws()
    {
        await Assert.That(() => new SnappyStream(new MemoryStream(), CompressionMode.Compress, false, null!)).Throws<ArgumentNullException>();
    }
}
