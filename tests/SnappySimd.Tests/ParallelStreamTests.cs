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
    [Arguments(200_000)] // several chunks per read: decoded straight into the caller's buffer
    [Arguments(1 << 20)]
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

    [Test]
    [Arguments(1, 100_000)]
    [Arguments(4, 100_000)]
    [Arguments(4, 1 << 20)] // several chunks per read: decoded straight into the caller's buffer
    public async Task CorruptChunk_ReadingAgainKeepsFailing(int threads, int bufferSize)
    {
        // A caller that catches the error and reads on must not silently skip the bad chunk
        byte[] input = Repeat(TestData.Load("json_api.json"), 1_000_000);
        byte[] compressed = await Compress(input, 1, WritePattern.OneWrite);

        int position = StreamFormatHeaderLength;
        for (int chunk = 0; chunk < 5; chunk++)
        {
            position += 4 + (compressed[position + 1] | (compressed[position + 2] << 8) | (compressed[position + 3] << 16));
        }

        compressed[position + 4] ^= 0xFF;

        using SnappyStream decompressor = threads == 1
            ? new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress)
            : new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress, leaveOpen: false, Options(threads));

        byte[] buffer = new byte[bufferSize];
        int failures = 0;
        long returned = 0;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                int read = decompressor.Read(buffer);
                if (read == 0)
                {
                    break;
                }

                returned += read;
            }
            catch (InvalidDataException)
            {
                failures++;
            }
        }

        // Nothing after the bad chunk is ever returned, and every read after the first failure fails again
        await Assert.That(returned).IsLessThanOrEqualTo(5 * 65536);
        await Assert.That(failures).IsGreaterThan(40);
    }

    [Test]
    [Arguments(0, 1 << 20)]
    [Arguments(0, 3)]
    [Arguments(5, 1 << 20)]
    [Arguments(5, 3)] // the data chunk does not fit: the batch is the empty chunks only
    [Arguments(65536, 1000)]
    public async Task ParallelRead_EmptyDataChunks(int dataLength, int readSize)
    {
        // Found by coverage-guided fuzzing: a parallel batch of chunks that decode to nothing consumed its input but
        // reported no progress, so the reader went on with a stale count of buffered bytes and failed
        static byte[] Chunk(byte type, byte[] data, byte[] body) =>
            [type, (byte)(body.Length + 4), (byte)((body.Length + 4) >> 8), (byte)((body.Length + 4) >> 16),
             .. BitConverter.GetBytes(SnappySimd.Internal.Crc32C.ComputeMasked(data)), .. body];

        byte[] data = Repeat(TestData.Load("alice29.txt"), dataLength);
        byte[] empties = [.. Chunk(0x01, [], []), .. Chunk(0x00, [], [0]), .. Chunk(0x01, [], [])];
        byte[] stream =
        [
            0xff, 0x06, 0x00, 0x00, .. "sNaPpY"u8,
            .. empties,
            .. dataLength > 0 ? Chunk(0x00, data, Snappy.CompressToArray(data)) : [],
            .. empties,
        ];

        await Assert.That(TestData.Same(data, Decompress(stream, 1, readSize))).IsTrue();
        await Assert.That(TestData.Same(data, Decompress(stream, 4, readSize))).IsTrue();
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

    [Test]
    [Arguments(3)]
    [Arguments(16)]
    public async Task Write_SizesAroundBatchBoundaries(int threads)
    {
        // A batch is 4 chunks per thread; its last part is compressed while the previous batch is written
        int batch = threads * 4 * 65536;
        byte[] input = Repeat(TestData.Load("json_api.json"), (3 * batch) + 65536 + 1);
        byte[] expected = await Compress(input, 1, WritePattern.OneWrite);

        foreach (int[] sizes in new[] { new[] { batch }, [batch - 1], [batch + 1], [batch + 65536], [batch, batch + 1, 65535], [65536, batch * 2 + 65537] })
        {
            using var output = new MemoryStream();
            using (var compressor = new SnappyStream(output, CompressionMode.Compress, leaveOpen: true, Options(threads)))
            {
                int position = 0;
                foreach (int size in sizes)
                {
                    compressor.Write(input, position, size);
                    position += size;
                }

                compressor.Write(input, position, input.Length - position);
            }

            await Assert.That(TestData.Same(expected, output.ToArray())).IsTrue();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Write_ThrowingStream_PropagatesAndDisposes(bool async)
    {
        // The stream fails while a batch is compressing in the background: the batch is stopped before the
        // exception leaves, and Dispose returns the buffers without waiting for a flush that cannot succeed
        byte[] input = Repeat(TestData.Load("json_api.json"), 8 << 20);
        var output = new ThrowingStream(1 << 20);
        var compressor = new SnappyStream(output, CompressionMode.Compress, leaveOpen: true, Options(4));

        Exception? first = null;
        Exception? second = null;
        try
        {
            if (async)
            {
                await compressor.WriteAsync(input);
            }
            else
            {
                compressor.Write(input);
            }
        }
        catch (Exception exception)
        {
            first = exception;
        }

        try
        {
            // Nothing was written successfully, so disposing does not flush (and does not fail again)
            if (async)
            {
                await compressor.DisposeAsync();
            }
            else
            {
                compressor.Dispose();
            }
        }
        catch (Exception exception)
        {
            second = exception;
        }

        await Assert.That(first?.GetType()).IsEqualTo(typeof(IOException));
        await Assert.That(second).IsNull();
        await Assert.That(() => compressor.Write(input, 0, 1)).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments(1000)]
    [Arguments(100_000)]
    public async Task ParallelRead_CorruptChunkInBatchDecodedAhead_SameBytesBeforeTheError(int readSize)
    {
        // With 4 threads a batch is 8 chunks: chunk 20 is in a batch decoded in the background while the
        // previous one is served
        byte[] input = Repeat(TestData.Load("json_api.json"), 2_000_000);
        byte[] compressed = await Compress(input, 1, WritePattern.OneWrite);

        int position = StreamFormatHeaderLength;
        for (int chunk = 0; chunk < 20; chunk++)
        {
            position += 4 + (compressed[position + 1] | (compressed[position + 2] << 8) | (compressed[position + 3] << 16));
        }

        compressed[position + 4] ^= 0xFF;

        // Bytes copied by the read that hits the bad chunk are lost with the exception, so allow one read less
        // (the sequential path's reads are aligned differently after its smaller input buffer refills)
        int parallel = ReadUntilError(compressed, 4, readSize);
        await Assert.That(parallel).IsGreaterThanOrEqualTo((20 * 65536) - readSize);
        await Assert.That(parallel).IsLessThanOrEqualTo(20 * 65536);
    }

    [Test]
    public async Task ParallelRead_DisposeWithBatchDecodingAhead()
    {
        byte[] input = Repeat(TestData.Load("json_api.json"), 4_000_000);
        byte[] compressed = await Compress(input, 1, WritePattern.OneWrite);

        // A small read starts a batch in the background; disposing right away must wait for it
        for (int i = 0; i < 20; i++)
        {
            var decompressor = new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress, leaveOpen: false, Options(8));
            byte[] buffer = new byte[1000];
            await Assert.That(decompressor.Read(buffer)).IsEqualTo(1000);
            await Assert.That(TestData.Same(input.AsSpan(0, 1000).ToArray(), buffer)).IsTrue();
            decompressor.Dispose();
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(4096)]
    [Arguments(81920)]
    public async Task ParallelRead_IncompressibleInput_ManyRefills(int readSize)
    {
        // Stored chunks fill the input buffer with few chunks per refill, so the batch decoded ahead drains and
        // restarts many times
        byte[] input = Repeat(TestData.Load("fireworks.jpeg"), 4_000_000);
        byte[] compressed = await Compress(input, 1, WritePattern.OneWrite);

        await Assert.That(TestData.Same(input, Decompress(compressed, 2, readSize))).IsTrue();
        await Assert.That(TestData.Same(input, Decompress(compressed, 3, readSize))).IsTrue();
    }

    /// <summary>Accepts writes up to a limit, then throws.</summary>
    private sealed class ThrowingStream(long limit) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _written += count;
            if (_written > limit)
            {
                throw new IOException("Disk full.");
            }
        }
    }
}
