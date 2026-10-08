using System.Buffers;
using System.IO.Compression;
using Snappiest.Tests.Infrastructure;

namespace Snappiest.Tests;

/// <summary>
/// Edge cases that only arise from unusual callers: huge sequences, base streams closed underneath us, and
/// disposal or reentrancy while an asynchronous operation is in flight.
/// </summary>
public class CoverageEdgeTests
{
    [Test]
    public async Task DecompressSequence_LongerThanAnArray_Throws()
    {
        // More than Array.MaxLength bytes, as a multi-segment sequence sharing one buffer
        byte[] chunk = new byte[1 << 26];
        ReadOnlySequence<byte> sequence = SequenceHelpers.CreateRepeatedSequence(chunk, 33);

        await Assert.That(() => Snappy.DecompressToMemory(sequence)).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.Decompress(sequence, new ArrayBufferWriter<byte>())).Throws<InvalidDataException>();
    }

    [Test]
    public async Task CanReadAndCanWrite_FollowTheBaseStream()
    {
        var writable = new MemoryStream();
        using var compressor = new SnappyStream(writable, CompressionMode.Compress, leaveOpen: true);
        var readable = new MemoryStream();
        using var decompressor = new SnappyStream(readable, CompressionMode.Decompress, leaveOpen: true);

        await Assert.That(compressor.CanWrite).IsTrue();
        await Assert.That(decompressor.CanRead).IsTrue();

        writable.Dispose();
        readable.Dispose();

        await Assert.That(compressor.CanWrite).IsFalse();
        await Assert.That(decompressor.CanRead).IsFalse();
    }

    [Test]
    public async Task Dispose_DuringPendingWrite_CompletesTheWrite()
    {
        var gate = new GateStream();
        var compressor = new SnappyStream(gate, CompressionMode.Compress, leaveOpen: true);

        ValueTask pending = compressor.WriteAsync(new byte[100]);
        await Assert.That(pending.IsCompleted).IsFalse();

        // Buffers in use by the pending write must not be returned to the pool
        compressor.Dispose();
        gate.Release();
        await pending;

        await Assert.That(compressor.CanWrite).IsFalse();
    }

    [Test]
    public async Task Dispose_DuringPendingRead_CompletesTheRead()
    {
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var c = new SnappyStream(output, CompressionMode.Compress, true))
            {
                c.Write(TestData.Load("html"));
            }

            compressed = output.ToArray();
        }

        var gate = new GateStream(compressed);
        var decompressor = new Snappiest.SnappyStream(gate, CompressionMode.Decompress, leaveOpen: true);

        ValueTask<int> pending = decompressor.ReadAsync(new byte[100]);
        decompressor.Dispose();
        gate.Release();

        await Assert.That(async () => await pending).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ConcurrentFlushAsync_Throws()
    {
        var gate = new GateStream();
        await using var compressor = new SnappyStream(gate, CompressionMode.Compress, leaveOpen: true);
        compressor.Write(new byte[10]);

        Task pending = compressor.FlushAsync();
        await Assert.That(async () => await compressor.FlushAsync()).Throws<InvalidOperationException>();
        await Assert.That(async () => await compressor.WriteAsync(new byte[1])).Throws<InvalidOperationException>();

        gate.Release();
        await pending;
    }

    [Test]
    public async Task ReadAsync_WhileReadPending_FromBufferedData_Throws()
    {
        // The second read could be served from buffered data, but must still be rejected while the first is pending
        var gate = new GateStream([0xff]);
        await using var decompressor = new SnappyStream(gate, CompressionMode.Decompress, leaveOpen: true);

        ValueTask<int> pending = decompressor.ReadAsync(new byte[10]);
        await Assert.That(async () => await decompressor.ReadAsync(new byte[10])).Throws<InvalidOperationException>();

        gate.Release();
        await Assert.That(async () => await pending).Throws<InvalidDataException>();
    }

    [Test]
    public async Task ReadAsync_CorruptChunkAlreadyBuffered_ThrowsAndReleasesTheStream()
    {
        // A valid chunk followed by an unskippable one. Reading one byte at a time leaves the bad chunk in the input
        // buffer, so a later ReadAsync fails synchronously, before any await.
        byte[] valid;
        using (var output = new MemoryStream())
        {
            using (var c = new SnappyStream(output, CompressionMode.Compress, true))
            {
                c.Write("hello hello hello"u8);
            }

            valid = output.ToArray();
        }

        byte[] data = [.. valid, 0x02, 0x01, 0x00, 0x00, 0x00];
        await using var decompressor = new SnappyStream(new MemoryStream(data), CompressionMode.Decompress);
        byte[] one = new byte[1];
        for (int i = 0; i < 17; i++)
        {
            await Assert.That(await decompressor.ReadAsync(one)).IsEqualTo(1);
        }

        await Assert.That(async () => await decompressor.ReadAsync(one)).Throws<InvalidDataException>();

        // The failed read must not leave the stream marked as busy
        await Assert.That(async () => await decompressor.ReadAsync(one)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task DisposeAsync_Twice_AndCanReadAfterDispose()
    {
        var decompressor = new SnappyStream(new MemoryStream(), CompressionMode.Decompress);
        await decompressor.DisposeAsync();
        await decompressor.DisposeAsync();

        await Assert.That(decompressor.CanRead).IsFalse();
        await Assert.That(decompressor.CanWrite).IsFalse();
    }

    /// <summary>Async reads, writes and flushes wait until released.</summary>
    private sealed class GateStream : MemoryStream
    {
        public GateStream(byte[]? data = null)
        {
            if (data is not null)
            {
                Write(data);
                Position = 0;
            }
        }

        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanWrite => true;

        public void Release() => _gate.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await _gate.Task;

        public override async Task FlushAsync(CancellationToken cancellationToken) => await _gate.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _gate.Task;
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
