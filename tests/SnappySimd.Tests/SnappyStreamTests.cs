using System.IO.Compression;
using System.Text;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Tests;

/// <summary>
/// Framed stream format. The first group mirrors Snappier's SnappyStreamTests.
/// </summary>
public class SnappyStreamTests
{
    private static byte[] CompressStream(byte[] input)
    {
        using var output = new MemoryStream();
        using (var compressor = new SnappyStream(output, CompressionMode.Compress, true))
        {
            compressor.Write(input);
        }

        return output.ToArray();
    }

    private static byte[] DecompressStream(byte[] compressed, int bufferSize = 81920)
    {
        using var decompressor = new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output, bufferSize);
        return output.ToArray();
    }

    private static byte[] Chunk(byte type, ReadOnlySpan<byte> body)
    {
        byte[] chunk = new byte[4 + body.Length];
        chunk[0] = type;
        chunk[1] = (byte)body.Length;
        chunk[2] = (byte)(body.Length >> 8);
        chunk[3] = (byte)(body.Length >> 16);
        body.CopyTo(chunk.AsSpan(4));
        return chunk;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static readonly byte[] StreamHeader = [0xff, 0x06, 0x00, 0x00, 0x73, 0x4e, 0x61, 0x50, 0x70, 0x59];

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task CompressAndDecompress(string filename)
    {
        byte[] input = TestData.Load(filename);

        using var output = new MemoryStream();
        using (var compressor = new SnappyStream(output, CompressionMode.Compress, true))
        {
            using var resource = new MemoryStream(input);
            resource.CopyTo(compressor);
        }

        output.Position = 0;
        using var decompressor = new SnappyStream(output, CompressionMode.Decompress, true);
        using var streamReader = new StreamReader(decompressor, Encoding.UTF8);
        string decompressedText = streamReader.ReadToEnd();

        string sourceText = new StreamReader(new MemoryStream(input), Encoding.UTF8).ReadToEnd();
        await Assert.That(decompressedText).IsEqualTo(sourceText);
    }

    [Test]
    public async Task CompressAndDecompress_SingleByte()
    {
        byte[] inBuffer = TestData.Load("alice29.txt").AsSpan(0, 128).ToArray();

        using var output = new MemoryStream();
        using (var compressor = new SnappyStream(output, CompressionMode.Compress, true))
        {
            foreach (byte b in inBuffer)
            {
                compressor.WriteByte(b);
            }
        }

        output.Position = 0;
        using var decompressor = new SnappyStream(output, CompressionMode.Decompress, true);

        byte[] outBuffer = new byte[128];
        for (int i = 0; i < outBuffer.Length; i++)
        {
            outBuffer[i] = (byte)decompressor.ReadByte();
        }

        await Assert.That(TestData.Same(inBuffer, outBuffer)).IsTrue();
        await Assert.That(decompressor.ReadByte()).IsEqualTo(-1);
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task CompressAndDecompressAsync(string filename)
    {
        byte[] input = TestData.Load(filename);

        using var output = new MemoryStream();
        await using (var compressor = new SnappyStream(output, CompressionMode.Compress, true))
        {
            await new MemoryStream(input).CopyToAsync(compressor);
        }

        output.Position = 0;
        await using var decompressor = new SnappyStream(output, CompressionMode.Decompress, true);
        using var decompressed = new MemoryStream();
        await decompressor.CopyToAsync(decompressed);

        await Assert.That(TestData.Same(input, decompressed.ToArray())).IsTrue();
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task CompressAndDecompressChunkStressTest(string filename)
    {
        // Lots of small randomly sized chunks, flushed after every write
        byte[] originalBytes = TestData.Load(filename);
        var rand = new Random(123);

        using var compressed = new MemoryStream();
        using (var inputStream = new MemoryStream(originalBytes))
        using (var compressor = new SnappyStream(compressed, CompressionMode.Compress, true))
        {
            byte[] buffer = new byte[100];
            int requestedSize = rand.Next(1, buffer.Length);
            int n;
            while ((n = inputStream.Read(buffer, 0, requestedSize)) != 0)
            {
                compressor.Write(buffer, 0, n);
                compressor.Flush();
            }
        }

        compressed.Position = 0;
        using var decompressed = new MemoryStream();
        using (var decompressor = new SnappyStream(compressed, CompressionMode.Decompress, true))
        {
            decompressor.CopyTo(decompressed);
        }

        await Assert.That(TestData.Same(originalBytes, decompressed.ToArray())).IsTrue();
    }

    [Test]
    public async Task Known8192ByteChunkStressTest()
    {
        string hex = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "streamerrorsequence.txt")).Trim();
        byte[] originalBytes = Convert.FromHexString(hex);

        using var compressed = new MemoryStream();
        using SnappyStream compressor = new(compressed, CompressionMode.Compress);
        compressor.Write(originalBytes, 0, originalBytes.Length);
        compressor.Flush();

        compressed.Position = 0;
        using SnappyStream decompressor = new(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        decompressor.CopyTo(decompressed);

        await Assert.That(TestData.Same(originalBytes, decompressed.ToArray())).IsTrue();
    }

    [Test]
    public async Task UncompressedBlock()
    {
        byte[] originalBytes = [.. Enumerable.Range(0, 256).Select(p => (byte)p)];

        using var compressed = new MemoryStream();
        using SnappyStream compressor = new(compressed, CompressionMode.Compress);
        compressor.Write(originalBytes, 0, originalBytes.Length);
        compressor.Flush();

        // Snappy header + block header + uncompressed data
        await Assert.That(compressed.Length).IsEqualTo(10 + 8 + originalBytes.Length);

        await Assert.That(TestData.Same(originalBytes, DecompressStream(compressed.ToArray()))).IsTrue();
    }

    // https://github.com/brantburnett/Snappier/security/advisories/GHSA-pggp-6c3x-2xmx
    [Test]
    [Timeout(5000)]
    public async Task MalformedFrameInput(CancellationToken cancellationToken)
    {
        byte[] data = [0x00, 0x04, 0x00, 0x00, 0x64, 0x4e, 0x6c, 0x71, 0x79, 0x20, 0x77, 0x6f, 0x72, 0x6c, 0x64];

        await Assert.That(() => DecompressStream(data)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(1000)]
    [Arguments(65535)]
    [Arguments(65536)]
    [Arguments(65537)]
    [Arguments(200000)]
    public async Task Decompress_VariousReadSizes(int readSize)
    {
        byte[] input = TestData.Load("html_x_4");
        byte[] compressed = CompressStream(input);

        await Assert.That(TestData.Same(input, DecompressStream(compressed, readSize))).IsTrue();
    }

    [Test]
    public async Task Decompress_BaseStreamReturnsOneByteAtATime()
    {
        byte[] input = TestData.Load("alice29.txt");
        byte[] compressed = CompressStream(input);

        using var decompressor = new SnappyStream(new TrickleStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);

        await Assert.That(TestData.Same(input, output.ToArray())).IsTrue();
    }

    [Test]
    public async Task DecompressAsync_BaseStreamReturnsOneByteAtATime()
    {
        byte[] input = TestData.Load("html");
        byte[] compressed = CompressStream(input);

        await using var decompressor = new SnappyStream(new TrickleStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        await decompressor.CopyToAsync(output);

        await Assert.That(TestData.Same(input, output.ToArray())).IsTrue();
    }

    [Test]
    public async Task ReadAsync_ArrayOverload()
    {
        byte[] input = TestData.Load("html");
        byte[] compressed = CompressStream(input);

        await using var decompressor = new SnappyStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[5000];
        int read;
        while ((read = await decompressor.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        await Assert.That(TestData.Same(input, output.ToArray())).IsTrue();
        await Assert.That(await decompressor.ReadAsync(Memory<byte>.Empty)).IsEqualTo(0);
    }

    [Test]
    public async Task WriteAsync_ArrayOverload_AndFlushAsync()
    {
        byte[] input = TestData.Load("lcet10.txt");

        using var compressed = new MemoryStream();
        await using (var compressor = new SnappyStream(compressed, CompressionMode.Compress, true))
        {
            for (int i = 0; i < input.Length; i += 30000)
            {
                await compressor.WriteAsync(input, i, Math.Min(30000, input.Length - i));
                await compressor.FlushAsync();
            }

            await compressor.WriteAsync(ReadOnlyMemory<byte>.Empty);
        }

        await Assert.That(TestData.Same(input, DecompressStream(compressed.ToArray()))).IsTrue();
    }

    [Test]
    public async Task EmptyStream_WritesNothing()
    {
        using var compressed = new MemoryStream();
        using (new SnappyStream(compressed, CompressionMode.Compress, true))
        {
        }

        await Assert.That(compressed.Length).IsEqualTo(0);
        await Assert.That(DecompressStream([]).Length).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyWrite_WritesStreamIdentifierOnly()
    {
        using var compressed = new MemoryStream();
        using (var compressor = new SnappyStream(compressed, CompressionMode.Compress, true))
        {
            compressor.Write([]);
        }

        await Assert.That(TestData.Same(StreamHeader, compressed.ToArray())).IsTrue();
        await Assert.That(DecompressStream(compressed.ToArray()).Length).IsEqualTo(0);
    }

    [Test]
    public async Task SkippableAndPaddingChunks_AreSkipped()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hello hello hello hello");
        byte[] framed = CompressStream(payload);

        byte[] withSkippable = Concat(
            StreamHeader,
            Chunk(0xfe, new byte[100]),
            Chunk(0x80, new byte[70000]),
            framed.AsSpan(StreamHeader.Length).ToArray(),
            Chunk(0xfe, []));

        await Assert.That(TestData.Same(payload, DecompressStream(withSkippable))).IsTrue();
        await Assert.That(TestData.Same(payload, DecompressStream(withSkippable, 3))).IsTrue();

        using var trickle = new SnappyStream(new TrickleStream(withSkippable), CompressionMode.Decompress);
        using var output = new MemoryStream();
        trickle.CopyTo(output);
        await Assert.That(TestData.Same(payload, output.ToArray())).IsTrue();
    }

    [Test]
    public async Task RepeatedStreamIdentifier_IsAccepted()
    {
        byte[] payload = Encoding.ASCII.GetBytes("abcabcabc");
        byte[] framed = CompressStream(payload);

        await Assert.That(TestData.Same(Concat(payload, payload), DecompressStream(Concat(framed, framed)))).IsTrue();
    }

    [Test]
    public async Task UnskippableChunk_Throws()
    {
        byte[] data = Concat(StreamHeader, Chunk(0x02, [1, 2, 3]));

        await Assert.That(() => DecompressStream(data)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task InvalidStreamIdentifier_Throws()
    {
        await Assert.That(() => DecompressStream(Chunk(0xff, "sNaPpX"u8))).Throws<InvalidDataException>();
        await Assert.That(() => DecompressStream(Chunk(0xff, "sNaPpYY"u8))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task CrcMismatch_Throws()
    {
        byte[] compressed = CompressStream(TestData.Load("html"));
        compressed[StreamHeader.Length + 4] ^= 1;

        await Assert.That(() => DecompressStream(compressed)).Throws<InvalidDataException>();

        byte[] uncompressed = CompressStream([.. Enumerable.Range(0, 256).Select(p => (byte)p)]);
        uncompressed[^1] ^= 1;

        await Assert.That(() => DecompressStream(uncompressed)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task InvalidChunkLengths_Throw()
    {
        // Too short for a checksum, too long for a 64KB chunk, and data chunks claiming more than 64KB
        await Assert.That(() => DecompressStream(Concat(StreamHeader, Chunk(0x00, [1, 2, 3])))).Throws<InvalidDataException>();
        await Assert.That(() => DecompressStream(Concat(StreamHeader, Chunk(0x01, new byte[100000])))).Throws<InvalidDataException>();
        await Assert.That(() => DecompressStream(Concat(StreamHeader, Chunk(0x01, new byte[4 + 65537])))).Throws<InvalidDataException>();

        byte[] bigBlock = Snappy.CompressToArray(new byte[65537]);
        await Assert.That(() => DecompressStream(Concat(StreamHeader, Chunk(0x00, [0, 0, 0, 0, .. bigBlock])))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task TruncatedStream_Throws()
    {
        byte[] compressed = CompressStream(TestData.Load("html"));

        await Assert.That(() => DecompressStream(compressed.AsSpan(0, compressed.Length - 1).ToArray())).Throws<InvalidDataException>();
        await Assert.That(() => DecompressStream(compressed.AsSpan(0, StreamHeader.Length + 2).ToArray())).Throws<InvalidDataException>();
        await Assert.That(() => DecompressStream(Concat(StreamHeader, Chunk(0x80, new byte[10])).AsSpan(0, 20).ToArray())).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Properties_AndUnsupportedMembers()
    {
        using var compressor = new SnappyStream(new MemoryStream(), CompressionMode.Compress);
        using var decompressor = new SnappyStream(new MemoryStream(), CompressionMode.Decompress);

        await Assert.That(compressor.CanWrite).IsTrue();
        await Assert.That(compressor.CanRead).IsFalse();
        await Assert.That(compressor.CanSeek).IsFalse();
        await Assert.That(decompressor.CanRead).IsTrue();
        await Assert.That(decompressor.CanWrite).IsFalse();
        await Assert.That(compressor.BaseStream).IsNotNull();

        await Assert.That(() => compressor.Length).Throws<NotSupportedException>();
        await Assert.That(() => compressor.Position).Throws<NotSupportedException>();
        await Assert.That(() => compressor.Position = 1).Throws<NotSupportedException>();
        await Assert.That(() => compressor.Seek(0, SeekOrigin.Begin)).Throws<NotSupportedException>();
        await Assert.That(() => compressor.SetLength(0)).Throws<NotSupportedException>();
        await Assert.That(() => compressor.Read(new byte[1], 0, 1)).Throws<NotSupportedException>();
        await Assert.That(() => decompressor.Write(new byte[1], 0, 1)).Throws<NotSupportedException>();
        await Assert.That(async () => await compressor.ReadAsync(new byte[1])).Throws<NotSupportedException>();
        await Assert.That(async () => await decompressor.WriteAsync(new byte[1])).Throws<NotSupportedException>();
        await Assert.That(decompressor.Read([], 0, 0)).IsEqualTo(0);

        // Flushing a decompression stream, or a compression stream that has not been written to, is a no-op
        decompressor.Flush();
        await decompressor.FlushAsync();
        compressor.Flush();
        await compressor.FlushAsync();
    }

    [Test]
    public async Task Constructor_InvalidArguments_Throw()
    {
        await Assert.That(() => new SnappyStream(null!, CompressionMode.Compress)).Throws<ArgumentNullException>();
        await Assert.That(() => new SnappyStream(new MemoryStream([], false), CompressionMode.Compress)).Throws<ArgumentException>();
        await Assert.That(() => new SnappyStream(new WriteOnlyStream(), CompressionMode.Decompress)).Throws<ArgumentException>();
        await Assert.That(() => new SnappyStream(new MemoryStream(), (CompressionMode)42)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Dispose_ClosesBaseStreamUnlessLeaveOpen()
    {
        var closed = new MemoryStream();
        new SnappyStream(closed, CompressionMode.Compress).Dispose();
        await Assert.That(closed.CanWrite).IsFalse();

        var open = new MemoryStream();
        new SnappyStream(open, CompressionMode.Compress, leaveOpen: true).Dispose();
        await Assert.That(open.CanWrite).IsTrue();

        var closedAsync = new MemoryStream();
        await new SnappyStream(closedAsync, CompressionMode.Decompress).DisposeAsync();
        await Assert.That(closedAsync.CanRead).IsFalse();

        var openAsync = new MemoryStream();
        await new SnappyStream(openAsync, CompressionMode.Decompress, leaveOpen: true).DisposeAsync();
        await Assert.That(openAsync.CanRead).IsTrue();
    }

    [Test]
    public async Task UseAfterDispose_Throws()
    {
        var stream = new SnappyStream(new MemoryStream(), CompressionMode.Compress);
        stream.Dispose();
        stream.Dispose();

        await Assert.That(stream.CanWrite).IsFalse();
        await Assert.That(stream.CanRead).IsFalse();
        await Assert.That(() => stream.BaseStream).Throws<ObjectDisposedException>();
        await Assert.That(() => stream.Write(new byte[1])).Throws<ObjectDisposedException>();
        await Assert.That(() => stream.Flush()).Throws<ObjectDisposedException>();
        await Assert.That(async () => await stream.FlushAsync()).Throws<ObjectDisposedException>();
        await Assert.That(async () => await stream.WriteAsync(new byte[1])).Throws<ObjectDisposedException>();

        var reader = new SnappyStream(new MemoryStream(), CompressionMode.Decompress);
        await reader.DisposeAsync();
        await Assert.That(() => reader.Read(new byte[1])).Throws<ObjectDisposedException>();
        await Assert.That(async () => await reader.ReadAsync(new byte[1])).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task CancelledToken_ReturnsCancelledTask()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var compressor = new SnappyStream(new MemoryStream(), CompressionMode.Compress);
        using var decompressor = new SnappyStream(new MemoryStream(), CompressionMode.Decompress);

        await Assert.That(async () => await compressor.WriteAsync(new byte[1], cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await compressor.FlushAsync(cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await decompressor.ReadAsync(new byte[1], cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ConcurrentAsyncOperation_Throws()
    {
        var gate = new GateStream();
        await using var compressor = new SnappyStream(gate, CompressionMode.Compress, leaveOpen: true);

        ValueTask pending = compressor.WriteAsync(new byte[10]);
        await Assert.That(pending.IsCompleted).IsFalse();

        await Assert.That(async () => await compressor.WriteAsync(new byte[1])).Throws<InvalidOperationException>();
        await Assert.That(async () => await compressor.FlushAsync()).Throws<InvalidOperationException>();

        gate.Release();
        await pending;

        var readGate = new GateStream(CompressStream([1, 2, 3]));
        await using var decompressor = new SnappyStream(readGate, CompressionMode.Decompress, leaveOpen: true);
        ValueTask<int> pendingRead = decompressor.ReadAsync(new byte[10]);
        await Assert.That(async () => await decompressor.ReadAsync(new byte[1])).Throws<InvalidOperationException>();
        readGate.Release();
        await Assert.That(await pendingRead).IsEqualTo(3);
    }

    [Test]
    public async Task BaseStreamReturningTooManyBytes_Throws()
    {
        using var decompressor = new SnappyStream(new LyingStream(), CompressionMode.Decompress);

        await Assert.That(() => decompressor.Read(new byte[10])).Throws<InvalidDataException>();
    }

    [Test]
    public async Task LargeWrites_CompressDirectlyFromCallerBuffer()
    {
        byte[] input = TestData.Load("html_x_4");
        using var compressed = new MemoryStream();
        using (var compressor = new SnappyStream(compressed, CompressionMode.Compress, true))
        {
            compressor.Write(input.AsSpan(0, 10));
            compressor.Write(input.AsSpan(10));
        }

        await Assert.That(TestData.Same(input, DecompressStream(compressed.ToArray()))).IsTrue();
    }

    /// <summary>Returns at most one byte per read.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer.Slice(0, Math.Min(1, buffer.Length)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer.Slice(0, Math.Min(1, buffer.Length)), cancellationToken);
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }

    /// <summary>Claims to have read more bytes than the buffer holds.</summary>
    private sealed class LyingStream : MemoryStream
    {
        public override int Read(Span<byte> buffer) => buffer.Length + 1;
    }

    /// <summary>Async reads and writes wait until released.</summary>
    private sealed class GateStream(byte[]? data = null) : MemoryStream(data ?? [])
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanWrite => true;

        public void Release() => _gate.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _gate.Task;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _gate.Task;
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
