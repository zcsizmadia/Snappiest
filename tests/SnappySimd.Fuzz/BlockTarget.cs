using System.Buffers;
using SnappySimd.Tests.Fuzz;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Fuzz;

/// <summary>
/// Raw block decompression of arbitrary bytes through every public entry point. Each one must agree with
/// <see cref="ReferenceDecoder"/>: decode a block it accepts to the same bytes, and throw
/// <see cref="InvalidDataException"/> for a block it rejects. Input and output live next to inaccessible pages, so an
/// out-of-bounds read or write crashes the process instead of passing silently.
/// </summary>
internal static class BlockTarget
{
    // The decoders allocate the declared length up front: keep random headers from asking for gigabytes
    private const int MaxDeclaredLength = 1 << 22;

    public static void Run(ReadOnlySpan<byte> data)
    {
        byte[] input = data.ToArray();

        int? referenceLength = ReferenceFraming.SpecLength(input);
        int declared;
        try
        {
            declared = Snappy.GetUncompressedLength(input);
        }
        catch (InvalidDataException)
        {
            Check.That(referenceLength is null, "length header rejected, but the spec accepts it");
            ExpectInvalid("DecompressToArray", () => Snappy.DecompressToArray(input));
            ExpectInvalid("TryDecompress", () => Snappy.TryDecompress(input, new byte[16], out _));
            return;
        }

        Check.That(referenceLength == declared, $"length header {declared}, the spec reads {referenceLength}");

        if (declared > MaxDeclaredLength)
        {
            HugeDeclaredLength(input);
            return;
        }

        byte[]? expected = ReferenceDecoder.Decode(input, out string why);

        Verify("DecompressToArray", expected, why, () => Snappy.DecompressToArray(input));
        Verify("Decompress(guarded, end)", expected, why, () => DecompressGuarded(input, declared, alignToEnd: true));
        Verify("Decompress(guarded, start)", expected, why, () => DecompressGuarded(input, declared, alignToEnd: false));
        Verify("Decompress(larger output)", expected, why, () => DecompressIntoLarger(input, declared));
        Verify("DecompressToMemory", expected, why, () =>
        {
            using IMemoryOwner<byte> owner = Snappy.DecompressToMemory(input);
            return owner.Memory.Span.ToArray();
        });
        Verify("Decompress(sequence)", expected, why, () =>
        {
            var writer = new ArrayBufferWriter<byte>();
            Snappy.Decompress(Sequences.Split(input, Math.Max(1, input.Length / 3)), writer);
            return writer.WrittenSpan.ToArray();
        });

        if (declared > 0)
        {
            // Too small an output: a valid block returns false with the prefix written, a corrupt one still throws
            Verify("TryDecompress(short output)", expected?.AsSpan(0, declared - 1).ToArray(), why, () =>
            {
                byte[] output = new byte[declared - 1];
                bool done = Snappy.TryDecompress(input, output, out int written);
                Check.That(!done && written == output.Length, $"TryDecompress(short output) returned {done}, {written}");
                return output;
            });
            ExpectOutcome("Decompress(short output)", expected is null, () => Snappy.Decompress(input, new byte[declared - 1]));
        }
    }

    /// <summary>
    /// A declared length too large to allocate for the referee. Only a corrupt block can declare it at fuzzing input
    /// sizes (every tag produces at most 64 bytes); the validating path must reject it without allocating.
    /// </summary>
    private static void HugeDeclaredLength(byte[] input)
    {
        bool valid;
        try
        {
            valid = !Snappy.TryDecompress(input, [], out _);
        }
        catch (InvalidDataException)
        {
            return;
        }

        Check.That(valid, "TryDecompress into an empty buffer succeeded for a non-empty block");
        byte[]? expected = ReferenceDecoder.Decode(input, out string why);
        Verify("DecompressToArray(huge)", expected, why, () => Snappy.DecompressToArray(input));
    }

    private static unsafe byte[] DecompressGuarded(byte[] input, int declared, bool alignToEnd)
    {
        using GuardedBuffer source = GuardedBuffer.From(input, alignToEnd);
        using var target = new GuardedBuffer(declared, alignToEnd);
        int written = Snappy.Decompress(new ReadOnlySpan<byte>(source.Pointer, source.Length), target.Span);
        Check.That(written == declared, $"Decompress returned {written}, declared {declared}");
        return target.Span.ToArray();
    }

    private static byte[] DecompressIntoLarger(byte[] input, int declared)
    {
        const int Extra = 256;
        byte[] output = new byte[declared + Extra];
        output.AsSpan().Fill(0xA5);
        int written = Snappy.Decompress(input, output);
        Check.That(written == declared, $"Decompress returned {written}, declared {declared}");
        Check.That(!output.AsSpan(declared).ContainsAnyExcept((byte)0xA5), "Decompress wrote past the declared length");
        return output.AsSpan(0, declared).ToArray();
    }

    private static void Verify(string entry, byte[]? expected, string why, Func<byte[]> decode)
    {
        byte[] actual;
        try
        {
            actual = decode();
        }
        catch (InvalidDataException)
        {
            Check.That(expected is null, $"{entry} rejects a block the spec accepts");
            return;
        }

        Check.That(expected is not null, $"{entry} accepts a block the spec rejects ({why})");
        Check.That(expected.AsSpan().SequenceEqual(actual), $"{entry} decodes to different bytes than the spec");
    }

    private static void ExpectInvalid(string entry, Action action) => ExpectOutcome(entry, invalid: true, action);

    /// <summary>
    /// Invalid input must throw <see cref="InvalidDataException"/>; valid input with too small an output must throw
    /// the documented <see cref="ArgumentException"/>.
    /// </summary>
    private static void ExpectOutcome(string entry, bool invalid, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            Check.That(invalid, $"{entry} rejects a block the spec accepts");
            return;
        }
        catch (ArgumentException) when (!invalid)
        {
            return;
        }

        Check.That(false, $"{entry} did not throw");
    }
}
