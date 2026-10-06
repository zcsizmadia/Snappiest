using System.Buffers;
using System.Text;
using SnappySimd.Internal;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Tests;

/// <summary>
/// Cases ported from google/snappy's snappy_unittest.cc, using its exact test vectors where it has them.
/// </summary>
public class GoogleSnappyTests
{
    private static void AppendVarint(List<byte> dst, uint value)
    {
        while (value >= 0x80)
        {
            dst.Add((byte)(value | 0x80));
            value >>= 7;
        }

        dst.Add((byte)value);
    }

    private static void AppendLiteral(List<byte> dst, byte[] literal)
    {
        int n = literal.Length - 1;
        if (n < 60)
        {
            dst.Add((byte)(n << 2));
        }
        else
        {
            int count = 0;
            var lengthBytes = new List<byte>();
            while (n > 0)
            {
                lengthBytes.Add((byte)n);
                n >>= 8;
                count++;
            }

            dst.Add((byte)((59 + count) << 2));
            dst.AddRange(lengthBytes);
        }

        dst.AddRange(literal);
    }

    // Same splitting as Google's AppendCopy
    private static void AppendCopy(List<byte> dst, int offset, int length)
    {
        while (length > 0)
        {
            int toCopy = length >= 68 ? 64 : length > 64 ? 60 : length;
            length -= toCopy;

            if (toCopy >= 4 && toCopy < 12 && offset < 2048)
            {
                dst.Add((byte)(1 | ((toCopy - 4) << 2) | ((offset >> 8) << 5)));
                dst.Add((byte)offset);
            }
            else if (offset < 65536)
            {
                dst.Add((byte)(2 | ((toCopy - 1) << 2)));
                dst.Add((byte)offset);
                dst.Add((byte)(offset >> 8));
            }
            else
            {
                dst.Add((byte)(3 | ((toCopy - 1) << 2)));
                dst.Add((byte)offset);
                dst.Add((byte)(offset >> 8));
                dst.Add((byte)(offset >> 16));
                dst.Add((byte)(offset >> 24));
            }
        }
    }

    /// <summary>Round trip through SnappySimd, also checking the result decodes with Snappier.</summary>
    private static bool Verify(byte[] input)
    {
        byte[] compressed = Snappy.CompressToArray(input);
        if (compressed.Length > Snappy.GetMaxCompressedLength(input.Length))
        {
            return false;
        }

        return TestData.Same(input, Snappy.DecompressToArray(compressed))
               && TestData.Same(input, Snappier.Snappy.DecompressToArray(compressed));
    }

    private static bool IsInvalid(byte[] compressed)
    {
        try
        {
            int length = Snappy.GetUncompressedLength(compressed);
            Snappy.Decompress(compressed, new byte[length]);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    [Test]
    public async Task AppendSelfPatternExtensionEdgeCases()
    {
        foreach (string s in new[]
                 {
                     "abcabcabcabcabcabcab",
                     "abcabcabcabcabcabcab0123456789ABCDEF",
                     "abcabcabcabcabcabcabcabcabcabcabcabc",
                     "abcabcabcabcabcabcabcabcabcabcabcabc0123456789ABCDEF",
                 })
        {
            await Assert.That(Verify(Encoding.ASCII.GetBytes(s))).IsTrue();
        }
    }

    [Test]
    public async Task AppendSelfPatternExtensionEdgeCasesExhaustive()
    {
        var random = new Random(1);
        int failures = 0;

        for (int patternSize = 1; patternSize <= 18; patternSize++)
        {
            for (int length = 1; length <= 64; length++)
            {
                foreach (int extra in new[] { 0, 1, 15, 16, 128 })
                {
                    byte[] input = new byte[patternSize + length + extra];
                    for (int i = 0; i < patternSize; i++)
                    {
                        input[i] = (byte)('a' + i);
                    }

                    for (int i = 0; i < length; i++)
                    {
                        input[patternSize + i] = input[i];
                    }

                    random.NextBytes(input.AsSpan(patternSize + length));

                    if (!Verify(input))
                    {
                        failures++;
                    }
                }
            }
        }

        await Assert.That(failures).IsEqualTo(0);
    }

    [Test]
    public async Task MaxBlowup()
    {
        // Random bytes followed by their own 4 byte groups in reverse: the worst case for output growth
        var random = new Random(2);
        var input = new List<byte>(160000);
        for (int i = 0; i < 80000; i++)
        {
            input.Add((byte)random.Next(256));
        }

        for (int i = 0; i < 80000; i += 4)
        {
            int start = input.Count - i - 4;
            input.AddRange(input.GetRange(start, 4));
        }

        byte[] data = [.. input];
        await Assert.That(Verify(data)).IsTrue();

        // The bound must also hold when compressing straight into a buffer of exactly that size
        byte[] output = new byte[Snappy.GetMaxCompressedLength(data.Length)];
        await Assert.That(Snappy.TryCompress(data, output, out _)).IsTrue();
    }

    [Test]
    public async Task RefusesInputLongerThanTheFormatCanExpress()
    {
        // 2^32 bytes without allocating them: every segment shares one buffer
        byte[] chunk = new byte[1 << 26];
        ReadOnlySequence<byte> sequence = SequenceHelpers.CreateRepeatedSequence(chunk, 64);
        await Assert.That(sequence.Length).IsEqualTo(1L << 32);

        var writer = new ArrayBufferWriter<byte>();
        await Assert.That(() => Snappy.Compress(sequence, writer)).Throws<ArgumentException>();
        await Assert.That(writer.WrittenCount).IsEqualTo(0);
    }

    [Test]
    public async Task ReadPastEndOfBuffer()
    {
        var compressed = new List<byte>();
        AppendVarint(compressed, 1);
        AppendLiteral(compressed, "x"u8.ToArray());

        using GuardedBuffer input = GuardedBuffer.From([.. compressed]);
        byte[] output = new byte[1];
        Snappy.Decompress(input.Span, output);

        await Assert.That(output[0]).IsEqualTo((byte)'x');
    }

    [Test]
    public async Task ZeroOffsetCopy()
    {
        byte[] compressed = [0x40, 0x12, 0x00, 0x00];

        await Assert.That(() => Snappy.Decompress(compressed, new byte[100])).Throws<InvalidDataException>();
    }

    [Test]
    public async Task ZeroOffsetCopyValidation()
    {
        byte[] compressed = [0x05, 0x12, 0x00, 0x00];

        await Assert.That(BlockDecompressor.Validate(compressed, 1, 5)).IsFalse();
        await Assert.That(IsInvalid(compressed)).IsTrue();
    }

    [Test]
    public async Task LiteralLengthU32Overflow()
    {
        var compressed = new List<byte>();
        AppendVarint(compressed, 65536);
        AppendLiteral(compressed, "D"u8.ToArray());
        for (int i = 0; i < 260; i++)
        {
            AppendCopy(compressed, 1, 64);
        }

        AppendLiteral(compressed, "F"u8.ToArray());

        // Literal tag with 4 length bytes of 0xffffffff: the real length 2^32 must not wrap to 0
        compressed.AddRange([0xfc, 0xff, 0xff, 0xff, 0xff]);

        // Copies that would complete the output if the literal were treated as empty
        for (int i = 0; i < 763; i++)
        {
            AppendCopy(compressed, 1, 64);
        }

        AppendCopy(compressed, 1, 62);

        byte[] block = [.. compressed];
        await Assert.That(IsInvalid(block)).IsTrue();
        await Assert.That(BlockDecompressor.Validate(block, 3, 65536)).IsFalse();
    }

    [Test]
    public async Task TruncatedVarint()
    {
        await Assert.That(() => Snappy.GetUncompressedLength([0xf0])).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray([0xf0])).Throws<InvalidDataException>();
    }

    [Test]
    public async Task UnterminatedVarint()
    {
        byte[] compressed = [0x80, 0x80, 0x80, 0x80, 0x80, 10];

        await Assert.That(() => Snappy.GetUncompressedLength(compressed)).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(compressed)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task OverflowingVarint()
    {
        byte[] compressed = [0xfb, 0xff, 0xff, 0xff, 0x7f];

        await Assert.That(() => Snappy.GetUncompressedLength(compressed)).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(compressed)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task FourByteOffset()
    {
        // A copy-4 reaching back 100KB, across a fragment boundary
        byte[] fragment1 = Encoding.ASCII.GetBytes("012345689abcdefghijklmnopqrstuvwxyz");
        byte[] fragment2 = Encoding.ASCII.GetBytes("some other string");
        const int n1 = 2;
        const int n2 = 100000 / 17;
        const int length = (n1 * 35) + (n2 * 17) + 35;

        var compressed = new List<byte>();
        AppendVarint(compressed, length);
        var expected = new List<byte>();
        for (int i = 0; i < n1; i++)
        {
            AppendLiteral(compressed, fragment1);
            expected.AddRange(fragment1);
        }

        for (int i = 0; i < n2; i++)
        {
            AppendLiteral(compressed, fragment2);
            expected.AddRange(fragment2);
        }

        AppendCopy(compressed, expected.Count, 35);
        expected.AddRange(fragment1);

        await Assert.That(TestData.Same([.. expected], Snappy.DecompressToArray([.. compressed]))).IsTrue();
    }

    private static unsafe nuint FindMatchLength(string s1, string s2, int length)
    {
        using GuardedBuffer a = GuardedBuffer.From(Encoding.ASCII.GetBytes(s1));
        using GuardedBuffer b = GuardedBuffer.From(Encoding.ASCII.GetBytes(s2));
        ulong data = 0;
        nuint matched = BlockCompressor.FindMatchLength(a.Pointer, b.Pointer, b.Pointer + length, ref data, out bool lessThan8);
        if (lessThan8 != matched < 8)
        {
            throw new InvalidOperationException("lessThan8 disagrees with the match length");
        }

        return matched;
    }

    [Test]
    [Arguments("012345", "012345", 6, 6)]
    [Arguments("01234567abc", "01234567abc", 11, 11)]
    [Arguments("01234567abc", "01234567axc", 9, 9)]
    [Arguments("01234567abc!", "01234567abc!", 11, 11)]
    [Arguments("01234567abc!", "01234567abc?", 11, 11)]
    [Arguments("01234567xxxxxxxx", "?1234567xxxxxxxx", 16, 0)]
    [Arguments("01234567xxxxxxxx", "0?234567xxxxxxxx", 16, 1)]
    [Arguments("01234567xxxxxxxx", "01237654xxxxxxxx", 16, 4)]
    [Arguments("01234567xxxxxxxx", "0123456?xxxxxxxx", 16, 7)]
    [Arguments("abcdefgh01234567xxxxxxxx", "abcdefgh?1234567xxxxxxxx", 24, 8)]
    [Arguments("abcdefgh01234567xxxxxxxx", "abcdefgh0?234567xxxxxxxx", 24, 9)]
    [Arguments("abcdefgh01234567xxxxxxxx", "abcdefgh01237654xxxxxxxx", 24, 12)]
    [Arguments("abcdefgh01234567xxxxxxxx", "abcdefgh0123456?xxxxxxxx", 24, 15)]
    [Arguments("01234567", "?1234567", 8, 0)]
    [Arguments("01234567", "0?234567", 8, 1)]
    [Arguments("01234567", "01?34567", 8, 2)]
    [Arguments("01234567", "012?4567", 8, 3)]
    [Arguments("01234567", "0123?567", 8, 4)]
    [Arguments("01234567", "01234?67", 8, 5)]
    [Arguments("01234567", "012345?7", 8, 6)]
    [Arguments("01234567", "0123456?", 8, 7)]
    [Arguments("01234567", "0123456?", 7, 7)]
    [Arguments("01234567!", "0123456??", 7, 7)]
    [Arguments("xxxxxxabcd", "xxxxxxabcd", 10, 10)]
    [Arguments("xxxxxxabcd?", "xxxxxxabcd?", 10, 10)]
    [Arguments("xxxxxxabcdef", "xxxxxxabcdef", 12, 12)]
    [Arguments("xxxxxx0123abc!", "xxxxxx0123abc!", 12, 12)]
    [Arguments("xxxxxx0123abc!", "xxxxxx0123abc?", 12, 12)]
    [Arguments("xxxxxx0123abc", "xxxxxx0123axc", 13, 11)]
    [Arguments("xxxxxx0123xxxxxxxx", "xxxxxx?123xxxxxxxx", 18, 6)]
    [Arguments("xxxxxx0123xxxxxxxx", "xxxxxx0?23xxxxxxxx", 18, 7)]
    [Arguments("xxxxxx0123xxxxxxxx", "xxxxxx0132xxxxxxxx", 18, 8)]
    [Arguments("xxxxxx0123xxxxxxxx", "xxxxxx012?xxxxxxxx", 18, 9)]
    [Arguments("xxxxxx0123", "xxxxxx?123", 10, 6)]
    [Arguments("xxxxxx0123", "xxxxxx0?23", 10, 7)]
    [Arguments("xxxxxx0123", "xxxxxx0132", 10, 8)]
    [Arguments("xxxxxx0123", "xxxxxx012?", 10, 9)]
    [Arguments("xxxxxxabcd0123xx", "xxxxxxabcd?123xx", 16, 10)]
    [Arguments("xxxxxxabcd0123xx", "xxxxxxabcd0?23xx", 16, 11)]
    [Arguments("xxxxxxabcd0123xx", "xxxxxxabcd0132xx", 16, 12)]
    [Arguments("xxxxxxabcd0123xx", "xxxxxxabcd012?xx", 16, 13)]
    [Arguments("xxxxxxabcd0123", "xxxxxxabcd?123", 14, 10)]
    [Arguments("xxxxxxabcd0123", "xxxxxxabcd0?23", 14, 11)]
    [Arguments("xxxxxxabcd0123", "xxxxxxabcd0132", 14, 12)]
    [Arguments("xxxxxxabcd0123", "xxxxxxabcd012?", 14, 13)]
    public async Task FindMatchLength_GoogleVectors(string s1, string s2, int length, int expected)
    {
        await Assert.That(FindMatchLength(s1, s2, length)).IsEqualTo((nuint)expected);
    }

    private static unsafe nuint FindMatchLength(byte[] s, byte[] t)
    {
        using GuardedBuffer u = GuardedBuffer.From(s);
        using GuardedBuffer v = GuardedBuffer.From(t);
        ulong data = 0;
        return BlockCompressor.FindMatchLength(u.Pointer, v.Pointer, v.Pointer + t.Length, ref data, out _);
    }

    [Test]
    public async Task FindMatchLength_Random()
    {
        // Two-letter alphabets give long, irregular matches; lengths go past the 64 byte vector threshold
        var random = new Random(3);
        int failures = 0;

        for (int trial = 0; trial < 10000; trial++)
        {
            byte a = (byte)random.Next(256), b = (byte)random.Next(256);
            int length = trial % 10 == 0 ? random.Next(0, 400) : random.Next(0, 40);
            byte[] s = new byte[length], t = new byte[length];
            for (int i = 0; i < length; i++)
            {
                s[i] = random.Next(2) == 0 ? a : b;
                t[i] = random.Next(8) == 0 ? (random.Next(2) == 0 ? a : b) : s[i];
            }

            nuint matched = FindMatchLength(s, t);

            int expected = 0;
            while (expected < length && s[expected] == t[expected])
            {
                expected++;
            }

            if (matched != (nuint)expected)
            {
                failures++;
            }
        }

        await Assert.That(failures).IsEqualTo(0);
    }

    [Test]
    public async Task VerifyTagTable()
    {
        // Expected entries derived from the tag definitions in format_description.txt
        int mismatches = 0;
        for (int tag = 0; tag < 256; tag++)
        {
            int expected = (tag & 3) switch
            {
                0 => (tag >> 2) < 60 ? (tag >> 2) + 1 - 256 : 0xFF,
                1 => 4 + ((tag >> 2) & 7) - ((tag >> 5) << 8),
                2 => (tag >> 2) + 1,
                _ => 0xFF,
            };

            if (BlockDecompressor.LengthMinusOffset(tag) != expected)
            {
                mismatches++;
            }
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task VerifyCorrupted_LongRun()
    {
        // Google's VerifyCorrupted second half: 100000 'A's with a corrupted length header
        byte[] source = new byte[100000];
        source.AsSpan().Fill((byte)'A');
        byte[] compressed = Snappy.CompressToArray(source);

        compressed[0] = compressed[1] = compressed[2] = compressed[3] = 0xff;
        await Assert.That(IsInvalid(compressed)).IsTrue();

        compressed[0] = compressed[1] = compressed[2] = compressed[3] = 0x7f;
        await Assert.That(IsInvalid(compressed)).IsTrue();
    }
}
