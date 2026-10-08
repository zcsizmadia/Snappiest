using Snappiest.Internal;
using Snappiest.Tests.Infrastructure;

namespace Snappiest.Tests;

/// <summary>
/// Hand-built blocks that exercise every tag form, including ones our compressor never emits, plus round trips
/// shaped to hit each copy and pattern-extension path in the decoder.
/// </summary>
public class BlockFormatTests
{
    private static byte[] Block(int uncompressedLength, params byte[][] parts)
    {
        var list = new List<byte>();
        uint value = (uint)uncompressedLength;
        while (value >= 0x80)
        {
            list.Add((byte)(value | 0x80));
            value >>= 7;
        }

        list.Add((byte)value);
        foreach (byte[] part in parts)
        {
            list.AddRange(part);
        }

        return [.. list];
    }

    private static byte[] Literal(byte[] data)
    {
        int n = data.Length - 1;
        if (n < 60)
        {
            return [(byte)(n << 2), .. data];
        }

        byte[] lengthBytes = BitConverter.GetBytes((uint)n);
        int count = n < 1 << 8 ? 1 : n < 1 << 16 ? 2 : n < 1 << 24 ? 3 : 4;
        return [(byte)((59 + count) << 2), .. lengthBytes.AsSpan(0, count), .. data];
    }

    private static byte[] Copy1(int offset, int length) => [(byte)(1 | ((length - 4) << 2) | ((offset >> 8) << 5)), (byte)offset];

    private static byte[] Copy2(int offset, int length) => [(byte)(2 | ((length - 1) << 2)), (byte)offset, (byte)(offset >> 8)];

    private static byte[] Copy4(int offset, int length) => [(byte)(3 | ((length - 1) << 2)), .. BitConverter.GetBytes((uint)offset)];

    private static byte[] Bytes(int length, int seed = 1)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Test]
    [Arguments(1)]
    [Arguments(60)]
    [Arguments(61)]
    [Arguments(256)]
    [Arguments(257)]
    [Arguments(65536)]
    [Arguments(65537)]
    [Arguments(1 << 24)]
    [Arguments((1 << 24) + 1)]
    public async Task LongLiterals_AllLengthEncodings(int length)
    {
        byte[] data = Bytes(length);
        byte[] block = Block(length, Literal(data));

        await Assert.That(TestData.Same(data, Snappy.DecompressToArray(block))).IsTrue();
    }

    [Test]
    public async Task Copy4_IsDecoded()
    {
        byte[] data = Bytes(100);
        byte[] block = Block(164, Literal(data), Copy4(100, 64));

        byte[] output = Snappy.DecompressToArray(block);

        await Assert.That(TestData.Same(data, output.AsSpan(0, 100).ToArray())).IsTrue();
        await Assert.That(TestData.Same(data.AsSpan(0, 64).ToArray(), output.AsSpan(100).ToArray())).IsTrue();
    }

    [Test]
    public async Task Copy4_InFastLoopRegion_IsDecoded()
    {
        // Enough data around the copy-4 that the branchless loop runs before and after it
        byte[] data = Bytes(1000);
        byte[] tail = Bytes(1000, 2);
        byte[] block = Block(2064, Literal(data), Copy4(1000, 64), Literal(tail));

        byte[] output = Snappy.DecompressToArray(block);

        await Assert.That(TestData.Same(data, output.AsSpan(0, 1000).ToArray())).IsTrue();
        await Assert.That(TestData.Same(data.AsSpan(0, 64).ToArray(), output.AsSpan(1000, 64).ToArray())).IsTrue();
        await Assert.That(TestData.Same(tail, output.AsSpan(1064).ToArray())).IsTrue();
    }

    public static IEnumerable<(int Offset, int Length)> PatternCases()
    {
        for (int offset = 1; offset <= 70; offset++)
        {
            foreach (int length in new[] { 4, 5, 11, 12, 16, 17, 31, 32, 33, 48, 63, 64 })
            {
                yield return (offset, length);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(PatternCases))]
    public async Task OverlappingCopies_AllOffsets(int offset, int length)
    {
        // A copy in the middle of a long block (fast loop) and one at the very end (slow path)
        byte[] prefix = Bytes(400, offset);
        byte[] suffix = Bytes(400, offset + 1000);
        byte[] copy = Copy2(offset, length);
        byte[] block = Block(400 + length + 400 + length, Literal(prefix), copy, Literal(suffix), copy);

        byte[] expected = new byte[400 + length + 400 + length];
        prefix.CopyTo(expected, 0);
        for (int i = 0; i < length; i++)
        {
            expected[400 + i] = expected[400 - offset + i];
        }

        suffix.CopyTo(expected, 400 + length);
        int end = 800 + length;
        for (int i = 0; i < length; i++)
        {
            expected[end + i] = expected[end - offset + i];
        }

        byte[] output = Snappy.DecompressToArray(block);
        await Assert.That(TestData.Same(expected, output)).IsTrue();

        // The same copy encoded as copy-1 where it fits
        if (offset < 2048 && length <= 11)
        {
            byte[] block1 = Block(expected.Length, Literal(prefix), Copy1(offset, length), Literal(suffix), Copy1(offset, length));
            await Assert.That(TestData.Same(expected, Snappy.DecompressToArray(block1))).IsTrue();
        }
    }

    [Test]
    public async Task OverlappingCopies_NearInputEnd_LargeOutput()
    {
        // The input runs out long before the output, so these copies take the slow path with output slop
        for (int offset = 1; offset <= 20; offset++)
        {
            byte[] prefix = Bytes(400, offset);
            var parts = new List<byte[]> { Literal(prefix) };
            for (int i = 0; i < 10; i++)
            {
                parts.Add(Copy2(offset, 64));
            }

            byte[] expected = new byte[400 + 640];
            prefix.CopyTo(expected, 0);
            for (int i = 400; i < expected.Length; i++)
            {
                expected[i] = expected[i - offset];
            }

            byte[] output = Snappy.DecompressToArray(Block(expected.Length, [.. parts]));
            await Assert.That(TestData.Same(expected, output)).IsTrue();
        }
    }

    [Test]
    public async Task OffsetZero_Throws()
    {
        byte[] data = Bytes(400);
        await Assert.That(() => Snappy.DecompressToArray(Block(404, Literal(data), Copy1(0, 4)))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(410, Literal(data), Copy2(0, 10), Literal(data)))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(4, Literal([1]), Copy2(0, 3)))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task OffsetBeforeStart_Throws()
    {
        byte[] data = Bytes(400);
        await Assert.That(() => Snappy.DecompressToArray(Block(810, Literal(data), Copy2(401, 10), Literal(data)))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(14, Literal(data.AsSpan(0, 4).ToArray()), Copy2(5, 10)))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(68, Literal(data.AsSpan(0, 4).ToArray()), Copy4(5, 64)))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(14, Copy1(1, 4)))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task OutputOverrun_Throws()
    {
        byte[] data = Bytes(400);
        await Assert.That(() => Snappy.DecompressToArray(Block(399, Literal(data)))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(410, Literal(data), Copy2(400, 20)))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(401, Literal(data)))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task TruncatedTags_Throw()
    {
        byte[] data = Bytes(10);
        await Assert.That(() => Snappy.DecompressToArray(Block(14, Literal(data), [0x01]))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(14, Literal(data), [0x02, 0x01]))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(14, Literal(data), [0x03, 0x01, 0x00, 0x00]))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(100, [0xF0]))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(100, [0xFC, 0xFF, 0xFF, 0xFF, 0xFF]))).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.DecompressToArray(Block(20, [0x4C, 1, 2, 3]))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Validate_AgreesWithDecompress_OnCorruptedBlocks()
    {
        var random = new Random(7);
        byte[] source = TestData.Load("html").AsSpan(0, 20000).ToArray();
        byte[] valid = Snappy.CompressToArray(source);
        int header = VarInt.GetByteCount((uint)source.Length);
        int mismatches = 0;

        for (int i = 0; i < 3000; i++)
        {
            byte[] corrupt = (byte[])valid.Clone();
            int flips = random.Next(1, 4);
            for (int f = 0; f < flips; f++)
            {
                corrupt[random.Next(header, corrupt.Length)] = (byte)random.Next(256);
            }

            if (random.Next(4) == 0)
            {
                corrupt = corrupt.AsSpan(0, random.Next(header, corrupt.Length)).ToArray();
            }

            bool valid1 = BlockDecompressor.Validate(corrupt, header, source.Length);
            bool valid2;
            try
            {
                byte[] output = new byte[source.Length];
                BlockDecompressor.Decompress(corrupt, header, output, source.Length);
                valid2 = true;
            }
            catch (InvalidDataException)
            {
                valid2 = false;
            }

            if (valid1 != valid2)
            {
                mismatches++;
            }
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task Decompress_LengthMuchLargerThanInputCouldProduce_Throws()
    {
        // 1 GB claimed from a few bytes: rejected before allocating anything
        byte[] block = [0x80, 0x80, 0x80, 0x80, 0x04, 0x00, 0x61];
        await Assert.That(() => Snappy.DecompressToMemory(block)).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.TryDecompress(block, new byte[10], out _)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Validate_RejectsMalformedTags()
    {
        // Each block is truncated or inconsistent in a different place
        byte[][] blocks =
        [
            Block(70, [0xF0]),                                  // long literal length bytes missing
            Block(70, [0xF0, 0x40, 1, 2]),                     // long literal data missing
            Block(5, [0x00, 1, 0x01]),                         // copy-1 offset byte missing
            Block(5, [0x00, 1, 0x02, 1]),                      // copy-2 offset bytes missing
            Block(5, [0x00, 1, 0x03, 1, 0, 0]),                // copy-4 offset bytes missing
            Block(5, [0x00, 1, 0x02, 2, 0]),                   // offset past the start
            Block(3, [0x00, 1, 0x05, 1]),                      // copy overruns the length
            Block(2, [0x00, 1]),                               // too little output
        ];

        foreach (byte[] block in blocks)
        {
            int length = Snappy.GetUncompressedLength(block);
            int header = VarInt.GetByteCount((uint)length);
            await Assert.That(BlockDecompressor.Validate(block, header, length)).IsFalse();
        }

        byte[] good = Block(5, [0x00, 1, 0x01, 1]);
        await Assert.That(BlockDecompressor.Validate(good, 1, 5)).IsTrue();
        byte[] good4 = Block(5, [0x00, 1, 0x0F, 1, 0, 0, 0]);
        await Assert.That(BlockDecompressor.Validate(good4, 1, 5)).IsTrue();
    }

    [Test]
    public async Task CompressFragment_InvalidArguments_Throw()
    {
        await Assert.That(() => BlockCompressor.CompressFragment(new byte[BlockCompressor.BlockSize + 1], new byte[200000])).Throws<ArgumentException>();
        await Assert.That(() => BlockCompressor.CompressFragment(new byte[100], new byte[100])).Throws<ArgumentException>();
        await Assert.That(BlockCompressor.CompressFragment([], new byte[32])).IsEqualTo(0);
    }
}
