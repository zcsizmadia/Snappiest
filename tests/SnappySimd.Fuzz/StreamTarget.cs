using System.IO.Compression;

namespace SnappySimd.Fuzz;

/// <summary>
/// <see cref="SnappyStream"/> decompression of arbitrary bytes, single-threaded and parallel, each with two read
/// patterns (whole input available and large reads; input trickling in and small, odd-sized reads).
/// </summary>
/// <remarks>
/// Every run must agree with <see cref="ReferenceFraming"/> on success or failure, a success must give its bytes, and
/// a failure must have returned a prefix of the bytes of the chunks before the bad one. For the same read pattern,
/// the parallel decoder must return exactly what the single-threaded one does, up to the same failure point.
/// The only exception allowed is <see cref="InvalidDataException"/>.
/// </remarks>
internal static class StreamTarget
{
    private static readonly SnappyParallelOptions Parallel = new() { MaxDegreeOfParallelism = 4, MinimumParallelLength = 0 };

    private static readonly int[] SmallReads = [1, 100, 7, 65536, 4096, 3, 70000, 513];

    private static readonly byte[] ReadBuffer = new byte[1 << 20];

    private readonly record struct Outcome(byte[] Output, bool Failed);

    public static void Run(ReadOnlySpan<byte> data)
    {
        byte[] input = data.ToArray();
        byte[] expected = ReferenceFraming.Decode(input, out string? error);
        bool invalid = error is not null;

        Outcome bulk = Decode(input, options: null, small: false);
        Outcome bulkParallel = Coverage.Untraced(() => Decode(input, Parallel, small: false));
        Outcome small = Decode(input, options: null, small: true);
        Outcome smallParallel = Coverage.Untraced(() => Decode(input, Parallel, small: true));

        foreach ((string name, Outcome outcome) in new[] { ("bulk", bulk), ("bulk parallel", bulkParallel), ("small", small), ("small parallel", smallParallel) })
        {
            Check.That(outcome.Failed == invalid, invalid
                ? $"{name}: accepts a stream the spec rejects ({error})"
                : $"{name}: rejects a stream the spec accepts");
            Check.That(
                invalid ? expected.AsSpan().StartsWith(outcome.Output) : expected.AsSpan().SequenceEqual(outcome.Output),
                $"{name}: returned bytes that differ from the spec decoder ({outcome.Output.Length} vs {expected.Length})");
        }

        Check.That(bulk.Output.AsSpan().SequenceEqual(bulkParallel.Output), "bulk: parallel output differs from single-threaded");
        Check.That(small.Output.AsSpan().SequenceEqual(smallParallel.Output), "small: parallel output differs from single-threaded");
    }

    private static Outcome Decode(byte[] input, SnappyParallelOptions? options, bool small)
    {
        using Stream source = small ? new TrickleStream(input, 3000) : new MemoryStream(input, writable: false);
        using SnappyStream stream = options is null
            ? new SnappyStream(source, CompressionMode.Decompress, leaveOpen: true)
            : new SnappyStream(source, CompressionMode.Decompress, leaveOpen: true, options);

        var output = new MemoryStream();
        byte[] buffer = ReadBuffer;
        try
        {
            for (int call = 0; ; call++)
            {
                int count = small ? SmallReads[call % SmallReads.Length] : buffer.Length;
                int read = stream.Read(buffer, 0, count);
                if (read == 0)
                {
                    return new Outcome(output.ToArray(), false);
                }

                Check.That(read <= count, $"Read returned {read} for a {count} byte request");
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException)
        {
            return new Outcome(output.ToArray(), true);
        }
    }

    /// <summary>A read-only stream that returns at most a few bytes per read.</summary>
    private sealed class TrickleStream(byte[] data, int maxRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(Math.Min(buffer.Length, maxRead), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
