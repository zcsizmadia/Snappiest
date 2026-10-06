using System.Buffers;
using System.Text;
using SnappySimd.Internal;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Tests;

public class Crc32CTests
{
    [Test]
    [Arguments("", 0x00000000u)]
    [Arguments("a", 0xC1D04330u)]
    [Arguments("123456789", 0xE3069283u)]
    [Arguments("The quick brown fox jumps over the lazy dog", 0x22620404u)]
    public async Task KnownValues(string text, uint expected)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);

        await Assert.That(Crc32C.Compute(data)).IsEqualTo(expected);
        await Assert.That(~Crc32C.UpdateSoftware(~0u, data)).IsEqualTo(expected);
    }

    [Test]
    public async Task HardwareAndSoftware_Agree_AllLengths()
    {
        byte[] data = new byte[3 * 8192 * 2 + 3 * 256 * 3 + 50];
        new Random(1).NextBytes(data);
        int mismatches = 0;

        foreach (int length in Enumerable.Range(0, 1100).Concat([8191, 8192, 24575, 24576, 24577, 49152 + 768, data.Length]))
        {
            ReadOnlySpan<byte> slice = data.AsSpan(0, length);
            uint software = Crc32C.UpdateSoftware(0x12345678, slice);
            if (Crc32C.Update(0x12345678, slice) != software)
            {
                mismatches++;
            }

            if (Crc32C.IsHardwareAccelerated && Crc32C.UpdateHardware(0x12345678, slice) != software)
            {
                mismatches++;
            }
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task Folding_MatchesSoftware_AllLengthsAndStates()
    {
        if (!Crc32C.IsFoldingAccelerated)
        {
            return;
        }

        byte[] data = new byte[70000];
        new Random(2).NextBytes(data);
        int mismatches = 0;

        // Every length around the 128 byte threshold and the 64/16 byte loop boundaries, plus chunk sizes
        IEnumerable<int> lengths = Enumerable.Range(0, 600).Concat([1023, 1024, 1025, 4095, 4096, 65535, 65536, 65537, data.Length]);
        foreach (int length in lengths)
        {
            foreach (uint state in new[] { 0u, ~0u, 0x12345678u })
            {
                ReadOnlySpan<byte> slice = data.AsSpan(3, Math.Min(length, data.Length - 3));
                if (Crc32C.UpdateFolding(state, slice) != Crc32C.UpdateSoftware(state, slice))
                {
                    mismatches++;
                }
            }
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }

#if NET10_0_OR_GREATER
    [Test]
    public async Task Folding256_MatchesSoftware()
    {
        if (!System.Runtime.Intrinsics.X86.Pclmulqdq.V256.IsSupported || !Crc32C.IsFoldingAccelerated)
        {
            return;
        }

        byte[] data = new byte[70000];
        new Random(4).NextBytes(data);
        int mismatches = 0;
        foreach (int length in Enumerable.Range(0, 800).Concat([4095, 4096, 65535, 65536, 65537, data.Length]))
        {
            ReadOnlySpan<byte> slice = data.AsSpan(0, length);
            if (Crc32C.UpdateFolding256(0x9abcdef0, slice) != Crc32C.UpdateSoftware(0x9abcdef0, slice))
            {
                mismatches++;
            }
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }
#endif

    [Test]
    public async Task Append_EqualsWhole()
    {
        byte[] data = TestData.Load("html");
        uint whole = Crc32C.Compute(data);
        uint appended = Crc32C.Append(Crc32C.Compute(data.AsSpan(0, 12345)), data.AsSpan(12345));

        await Assert.That(appended).IsEqualTo(whole);
    }

    [Test]
    public async Task Mask_MatchesFramingSpec()
    {
        // From the framing format: mask = rotate right by 15, plus 0xa282ead8
        await Assert.That(Crc32C.ApplyMask(0)).IsEqualTo(0xa282ead8u);
        await Assert.That(Crc32C.ApplyMask(0x00008000u)).IsEqualTo(0xa282ead9u);
    }

    [Test]
    public async Task PolynomialArithmetic()
    {
        await Assert.That(Crc32C.PowerOfX(0)).IsEqualTo(1u << 31);
        await Assert.That(Crc32C.PowerOfX(1)).IsEqualTo(1u << 30);
        await Assert.That(Crc32C.MultiplyModP(1u << 31, 0xDEADBEEF)).IsEqualTo(0xDEADBEEFu);
    }
}

public class VarIntTests
{
    [Test]
    [Arguments(0u, 1)]
    [Arguments(127u, 1)]
    [Arguments(128u, 2)]
    [Arguments(16383u, 2)]
    [Arguments(16384u, 3)]
    [Arguments(2097151u, 3)]
    [Arguments(2097152u, 4)]
    [Arguments(268435455u, 4)]
    [Arguments(268435456u, 5)]
    [Arguments(uint.MaxValue, 5)]
    public async Task RoundTrip(uint value, int expectedLength)
    {
        byte[] buffer = new byte[VarInt.MaxLength];
        int written = VarInt.Write(buffer, value);

        OperationStatus status = VarInt.TryRead(buffer.AsSpan(0, written), out uint read, out int consumed);

        await Assert.That(written).IsEqualTo(expectedLength);
        await Assert.That(VarInt.GetByteCount(value)).IsEqualTo(expectedLength);
        await Assert.That(status).IsEqualTo(OperationStatus.Done);
        await Assert.That(read).IsEqualTo(value);
        await Assert.That(consumed).IsEqualTo(written);
    }

    [Test]
    public async Task Incomplete_And_Invalid()
    {
        await Assert.That(VarInt.TryRead([], out _, out _)).IsEqualTo(OperationStatus.NeedMoreData);
        await Assert.That(VarInt.TryRead([0x80, 0x80], out _, out _)).IsEqualTo(OperationStatus.NeedMoreData);
        await Assert.That(VarInt.TryRead([0x80, 0x80, 0x80, 0x80, 0x80], out _, out _)).IsEqualTo(OperationStatus.InvalidData);
        await Assert.That(VarInt.TryRead([0x80, 0x80, 0x80, 0x80, 0x10], out _, out _)).IsEqualTo(OperationStatus.InvalidData);
        await Assert.That(VarInt.TryRead([0x80, 0x80, 0x80, 0x80, 0x80, 0x01], out _, out _)).IsEqualTo(OperationStatus.InvalidData);
    }
}

/// <summary>
/// Runs the codecs on buffers that sit right against inaccessible pages, so any read or write past either end
/// of the input or output crashes instead of passing silently.
/// </summary>
public class GuardPageTests
{
    public static IEnumerable<int> Lengths() => [0, 1, 15, 16, 17, 100, 129, 130, 131, 1000, 4095, 4096, 65535, 65536, 65537, 150000];

    [Test]
    [MethodDataSource(nameof(Lengths))]
    public async Task Compress_DoesNotTouchOutsideInput(int length)
    {
        byte[] data = TestData.GenerateText(new Random(length), length);

        foreach (bool alignToEnd in new[] { true, false })
        {
            using GuardedBuffer input = GuardedBuffer.From(data, alignToEnd);
            byte[] output = new byte[Snappy.GetMaxCompressedLength(length)];
            int written = Snappy.Compress(input.Span, output);

            await Assert.That(TestData.Same(data, Snappy.DecompressToArray(output.AsSpan(0, written)))).IsTrue();
        }
    }

    [Test]
    [MethodDataSource(nameof(Lengths))]
    public async Task Compress_ExactOutput_DoesNotTouchOutsideOutput(int length)
    {
        byte[] data = TestData.GenerateText(new Random(length), length);
        byte[] reference = Snappy.CompressToArray(data);

        using var output = new GuardedBuffer(reference.Length);
        int written = Snappy.Compress(data, output.Span);

        await Assert.That(TestData.Same(reference, output.Span.Slice(0, written).ToArray())).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(Lengths))]
    public async Task Decompress_DoesNotTouchOutsideBuffers(int length)
    {
        foreach (byte[] data in new[] { TestData.GenerateText(new Random(length), length), TestData.Generate(new Random(length), length, 2) })
        {
            byte[] compressed = Snappy.CompressToArray(data);

            foreach (bool alignToEnd in new[] { true, false })
            {
                using GuardedBuffer input = GuardedBuffer.From(compressed, alignToEnd);
                using var output = new GuardedBuffer(length, alignToEnd);
                Snappy.Decompress(input.Span, output.Span);

                await Assert.That(TestData.Same(data, output.Span.ToArray())).IsTrue();
            }
        }
    }

    [Test]
    public async Task Decompress_CorruptInput_DoesNotTouchOutsideBuffers()
    {
        var random = new Random(3);
        byte[] data = TestData.Load("html").AsSpan(0, 30000).ToArray();
        byte[] valid = Snappy.CompressToArray(data);
        int failures = 0;

        for (int i = 0; i < 500; i++)
        {
            byte[] corrupt = (byte[])valid.Clone();
            for (int f = 0; f < 4; f++)
            {
                corrupt[random.Next(3, corrupt.Length)] = (byte)random.Next(256);
            }

            corrupt = corrupt.AsSpan(0, random.Next(3, corrupt.Length + 1)).ToArray();

            using GuardedBuffer input = GuardedBuffer.From(corrupt);
            using var output = new GuardedBuffer(data.Length);
            try
            {
                Snappy.Decompress(input.Span, output.Span);
            }
            catch (InvalidDataException)
            {
                failures++;
            }
        }

        await Assert.That(failures).IsGreaterThan(0);
    }
}
