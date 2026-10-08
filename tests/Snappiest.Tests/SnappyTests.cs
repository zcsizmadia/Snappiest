using System.Buffers;
using System.Text;
using Snappiest.Tests.Infrastructure;

namespace Snappiest.Tests;

/// <summary>
/// Raw block API. The first group mirrors Snappier's SnappyTests so behaviour stays identical after migrating.
/// </summary>
public class SnappyTests
{
    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task CompressAndDecompressFile(string filename)
    {
        byte[] input = TestData.Load(filename);

        byte[] compressed = new byte[Snappy.GetMaxCompressedLength(input.Length)];
        int compressedLength = Snappy.Compress(input, compressed);

        byte[] output = new byte[Snappy.GetUncompressedLength(compressed.AsSpan(0, compressedLength))];
        int outputLength = Snappy.Decompress(compressed.AsSpan(0, compressedLength), output);

        await Assert.That(outputLength).IsEqualTo(input.Length);
        await Assert.That(TestData.Same(input, output)).IsTrue();
    }

    [Test]
    public async Task CompressAndDecompressFile_LimitedOutputBuffer()
    {
        // The output is smaller than the maximum compressed length but larger than the actual result
        byte[] input = TestData.Load("alice29.txt").AsSpan(0, 65536).ToArray();

        byte[] compressed = new byte[Snappy.GetMaxCompressedLength(input.Length) - 5];
        int compressedLength = Snappy.Compress(input, compressed);

        byte[] output = new byte[Snappy.GetUncompressedLength(compressed.AsSpan(0, compressedLength))];
        int outputLength = Snappy.Decompress(compressed.AsSpan(0, compressedLength), output);

        await Assert.That(outputLength).IsEqualTo(input.Length);
        await Assert.That(TestData.Same(input, output)).IsTrue();
    }

    [Test]
    public async Task CompressAndDecompress_ExactOutputBuffer()
    {
        // Forces every fragment through the scratch buffer path
        byte[] input = TestData.Load("html_x_4");
        byte[] reference = Snappy.CompressToArray(input);

        byte[] compressed = new byte[reference.Length];
        int compressedLength = Snappy.Compress(input, compressed);

        await Assert.That(compressedLength).IsEqualTo(reference.Length);
        await Assert.That(TestData.Same(reference, compressed)).IsTrue();
    }

    [Test]
    public async Task Compress_InsufficientOutputBuffer()
    {
        byte[] input = TestData.Load("alice29.txt").AsSpan(0, 65536).ToArray();
        byte[] compressed = new byte[1024];

        await Assert.That(() => Snappy.Compress(input, compressed)).Throws<ArgumentException>();
    }

    [Test]
    public async Task TryCompressAndDecompress()
    {
        byte[] input = TestData.Load("alice29.txt").AsSpan(0, 65536).ToArray();

        byte[] compressed = new byte[Snappy.GetMaxCompressedLength(input.Length)];
        bool result = Snappy.TryCompress(input, compressed, out int compressedLength);
        await Assert.That(result).IsTrue();

        byte[] output = new byte[Snappy.GetUncompressedLength(compressed.AsSpan(0, compressedLength))];
        result = Snappy.TryDecompress(compressed.AsSpan(0, compressedLength), output, out int outputLength);

        await Assert.That(result).IsTrue();
        await Assert.That(outputLength).IsEqualTo(input.Length);
        await Assert.That(TestData.Same(input, output)).IsTrue();
    }

    [Test]
    public async Task TryCompress_InsufficientOutputBuffer()
    {
        byte[] input = TestData.Load("alice29.txt").AsSpan(0, 65536).ToArray();
        byte[] compressed = new byte[1024];

        await Assert.That(Snappy.TryCompress(input, compressed, out _)).IsFalse();
    }

    [Test]
    public async Task TryCompress_EmptyOutput_ReturnsFalse()
    {
        await Assert.That(Snappy.TryCompress([1, 2, 3], [], out int written)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryCompress_OutputTooSmallForLengthHeader_ReturnsFalse()
    {
        byte[] input = new byte[300];
        byte[] output = new byte[1];

        await Assert.That(Snappy.TryCompress(input, output, out _)).IsFalse();
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.CorpusFiles))]
    public async Task CompressAndDecompressFile_ViaBufferWriter(string filename)
    {
        byte[] input = TestData.Load(filename);

        var compressed = new ArrayBufferWriter<byte>();
        Snappy.Compress(new ReadOnlySequence<byte>(input), compressed);

        var output = new ArrayBufferWriter<byte>();
        Snappy.Decompress(new ReadOnlySequence<byte>(compressed.WrittenMemory), output);

        await Assert.That(output.WrittenCount).IsEqualTo(input.Length);
        await Assert.That(TestData.Same(input, output.WrittenMemory)).IsTrue();
    }

    [Test]
    [Arguments(1000)]
    [Arguments(16384)]
    [Arguments(32768)]
    [Arguments(65536)]
    [Arguments(100000)]
    public async Task CompressAndDecompressFile_ViaBufferWriter_SplitInput(int maxSegmentSize)
    {
        byte[] input = TestData.Load("alice29.txt");

        var compressed = new ArrayBufferWriter<byte>();
        Snappy.Compress(SequenceHelpers.CreateSequence(input, maxSegmentSize), compressed);

        var output = new ArrayBufferWriter<byte>();
        Snappy.Decompress(SequenceHelpers.CreateSequence(compressed.WrittenMemory, maxSegmentSize), output);

        await Assert.That(output.WrittenCount).IsEqualTo(input.Length);
        await Assert.That(TestData.Same(input, output.WrittenMemory)).IsTrue();

        // Splitting the input must not change the compressed result
        await Assert.That(TestData.Same(Snappy.CompressToArray(input), compressed.WrittenMemory.ToArray())).IsTrue();
    }

    [Test]
    public async Task CompressSequence_Empty()
    {
        var compressed = new ArrayBufferWriter<byte>();
        Snappy.Compress(ReadOnlySequence<byte>.Empty, compressed);

        var output = new ArrayBufferWriter<byte>();
        Snappy.Decompress(new ReadOnlySequence<byte>(compressed.WrittenMemory), output);

        await Assert.That(compressed.WrittenCount).IsEqualTo(1);
        await Assert.That(output.WrittenCount).IsEqualTo(0);
    }

    [Test]
    public async Task CompressSequence_NullWriter_Throws()
    {
        await Assert.That(() => Snappy.Compress(ReadOnlySequence<byte>.Empty, null!)).Throws<ArgumentNullException>();
        await Assert.That(() => Snappy.Decompress(ReadOnlySequence<byte>.Empty, null!)).Throws<ArgumentNullException>();
    }

    public static IEnumerable<string> CompressAndDecompressStringCases() =>
    [
        "",
        "a",
        "ab",
        "abc",
        "aaaaaaa" + new string('b', 16) + "aaaaaabc",
        "aaaaaaa" + new string('b', 256) + "aaaaaabc",
        "aaaaaaa" + new string('b', 2047) + "aaaaaabc",
        "aaaaaaa" + new string('b', 65536) + "aaaaaabc",
        "abcaaaaaaa" + new string('b', 65536) + "aaaaaabc",
    ];

    [Test]
    [MethodDataSource(nameof(CompressAndDecompressStringCases))]
    public async Task CompressAndDecompressString(string str)
    {
        byte[] input = Encoding.UTF8.GetBytes(str);

        byte[] compressed = Snappy.CompressToArray(input);
        byte[] output = Snappy.DecompressToArray(compressed);

        await Assert.That(output.Length).IsEqualTo(input.Length);
        await Assert.That(TestData.Same(input, output)).IsTrue();
    }

    [Test]
    public async Task Compress_OverlappingBuffers_InvalidOperationException()
    {
        byte[] input = new byte[1024];

        await Assert.That(() => Snappy.Compress(input, input.AsSpan(input.Length - 1))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BadData_InsufficentOutputBuffer_ThrowsArgumentException()
    {
        byte[] input = new byte[100000];
        input.AsSpan().Fill((byte)'A');
        byte[] compressed = Snappy.CompressToArray(input);

        await Assert.That(() => Snappy.Decompress(compressed, new byte[100])).Throws<ArgumentException>();
    }

    [Test]
    public async Task TryDecompress_InsufficentOutputBuffer_ReturnsFalseWithPartialOutput()
    {
        byte[] input = TestData.Load("html");
        byte[] compressed = Snappy.CompressToArray(input);

        byte[] output = new byte[100];
        bool result = Snappy.TryDecompress(compressed, output, out int written);

        await Assert.That(result).IsFalse();
        await Assert.That(written).IsEqualTo(100);
        await Assert.That(TestData.Same(input.AsSpan(0, 100).ToArray(), output)).IsTrue();
    }

    [Test]
    public async Task TryDecompress_InsufficentOutputBuffer_CorruptData_Throws()
    {
        byte[] full = Snappy.CompressToArray(TestData.Load("html"));
        byte[] compressed = full.AsSpan(0, full.Length - 1).ToArray();

        await Assert.That(() => Snappy.TryDecompress(compressed, new byte[100], out _)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task BadData_SimpleCorruption_ThrowsInvalidDataException()
    {
        byte[] input = Encoding.UTF8.GetBytes("making sure we don't crash with corrupted input");
        byte[] compressed = Snappy.CompressToArray(input);

        // corrupt the data a bit
        compressed[1]--;
        compressed[3]++;

        await Assert.That(() =>
        {
            int length = Snappy.GetUncompressedLength(compressed);
            byte[] output = new byte[length];
            Snappy.Decompress(compressed, output);
        }).Throws<InvalidDataException>();
    }

    [Test]
    public async Task BadData_LongLength_ThrowsInvalidDataException()
    {
        byte[] input = new byte[1000];
        input.AsSpan().Fill((byte)'A');
        byte[] compressed = Snappy.CompressToArray(input);

        // Set the length header to 16383
        compressed[0] = 255;
        compressed[1] = 127;

        await Assert.That(() => Snappy.Decompress(compressed, new byte[1000])).Throws<InvalidDataException>();
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.BadDataFiles))]
    public async Task BadData_FromFile_ThrowsInvalidDataException(string filename)
    {
        byte[] input = TestData.Load(filename);

        await Assert.That(() =>
        {
            int length = Snappy.GetUncompressedLength(input);
            byte[] output = new byte[length];
            Snappy.Decompress(input, output);
        }).Throws<InvalidDataException>();
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.BadDataFiles))]
    public async Task BadData_TryDecompress_ThrowsInvalidDataException(string filename)
    {
        byte[] input = TestData.Load(filename);

        await Assert.That(() =>
        {
            int length = Snappy.GetUncompressedLength(input);
            byte[] output = new byte[length];
            Snappy.TryDecompress(input, output, out _);
        }).Throws<InvalidDataException>();
    }

    [Test]
    [MethodDataSource(typeof(TestData), nameof(TestData.BadDataFiles))]
    public async Task BadData_DecompressToMemory_ThrowsInvalidDataException(string filename)
    {
        byte[] input = TestData.Load(filename);

        await Assert.That(() => Snappy.DecompressToMemory(input)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task DecompressToMemory()
    {
        byte[] input = TestData.Load("alice29.txt");
        byte[] compressed = Snappy.CompressToArray(input);

        using IMemoryOwner<byte> output = Snappy.DecompressToMemory(compressed);

        await Assert.That(output.Memory.Length).IsEqualTo(input.Length);
        await Assert.That(TestData.Same(input, output.Memory)).IsTrue();
    }

    [Test]
    public async Task DecompressToMemory_FromSequence()
    {
        byte[] input = TestData.Load("alice29.txt");
        byte[] compressed = Snappy.CompressToArray(input);

        using IMemoryOwner<byte> output = Snappy.DecompressToMemory(SequenceHelpers.CreateSequence(compressed, 1024));

        await Assert.That(output.Memory.Length).IsEqualTo(input.Length);
        await Assert.That(TestData.Same(input, output.Memory)).IsTrue();
    }

    [Test]
    public async Task DecompressToMemory_FromSingleSegmentSequence()
    {
        byte[] input = TestData.Load("html");
        byte[] compressed = Snappy.CompressToArray(input);

        using IMemoryOwner<byte> output = Snappy.DecompressToMemory(new ReadOnlySequence<byte>(compressed));

        await Assert.That(TestData.Same(input, output.Memory)).IsTrue();
    }

    [Test]
    public async Task DecompressToBufferWriter_FromSequence()
    {
        byte[] input = TestData.Load("alice29.txt");
        byte[] compressed = Snappy.CompressToArray(input);

        var writer = new ArrayBufferWriter<byte>();
        Snappy.Decompress(SequenceHelpers.CreateSequence(compressed, 1024), writer);

        await Assert.That(writer.WrittenCount).IsEqualTo(input.Length);
        await Assert.That(TestData.Same(input, writer.WrittenMemory)).IsTrue();
    }

    [Test]
    public async Task CompressToMemory_DisposeTwice_ThenMemoryThrows()
    {
        IMemoryOwner<byte> owner = Snappy.CompressToMemory([1, 2, 3]);
        await Assert.That(owner.Memory.Length).IsGreaterThan(0);

        owner.Dispose();
        owner.Dispose();

        await Assert.That(() => owner.Memory).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task RandomData()
    {
        var random = new Random(301);
        int failures = 0;

        for (int i = 0; i < 20000; i++)
        {
            int length = i < 100 ? 65536 + random.Next(0, 65535) : random.Next(0, 4095);
            byte[] buffer = TestData.Generate(random, length, i < 100 ? 8 : random.Next(0, 3));

            using IMemoryOwner<byte> compressed = Snappy.CompressToMemory(buffer);
            using IMemoryOwner<byte> decompressed = Snappy.DecompressToMemory(compressed.Memory.Span);

            if (!TestData.Same(buffer, decompressed.Memory))
            {
                failures++;
            }
        }

        await Assert.That(failures).IsEqualTo(0);
    }

    [Test]
    public async Task GetMaxCompressedLength_IsSufficient()
    {
        // Incompressible data close to and across fragment boundaries
        var random = new Random(42);
        foreach (int length in new[] { 0, 1, 15, 16, 17, 60, 61, 255, 256, 65535, 65536, 65537, 200000 })
        {
            byte[] input = new byte[length];
            random.NextBytes(input);

            byte[] output = new byte[Snappy.GetMaxCompressedLength(length)];
            int written = Snappy.Compress(input, output);

            await Assert.That(written).IsLessThanOrEqualTo(output.Length);
            await Assert.That(TestData.Same(input, Snappy.DecompressToArray(output.AsSpan(0, written)))).IsTrue();
        }
    }

    [Test]
    public async Task GetMaxCompressedLength_InvalidLength_Throws()
    {
        await Assert.That(() => Snappy.GetMaxCompressedLength(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Snappy.GetMaxCompressedLength(int.MaxValue)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GetUncompressedLength_Invalid_Throws()
    {
        await Assert.That(() => Snappy.GetUncompressedLength([])).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.GetUncompressedLength([0x80])).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.GetUncompressedLength([0xFF, 0xFF, 0xFF, 0xFF, 0x0F])).Throws<InvalidDataException>();
        await Assert.That(() => Snappy.GetUncompressedLength([0xFF, 0xFF, 0xFF, 0xFF, 0x10])).Throws<InvalidDataException>();
        await Assert.That(Snappy.GetUncompressedLength([0xFF, 0xFF, 0xFF, 0xFF, 0x07])).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task Decompress_EmptyBlockWithTrailingData_Throws()
    {
        await Assert.That(() => Snappy.DecompressToArray([0x00, 0x00])).Throws<InvalidDataException>();
        await Assert.That(Snappy.DecompressToArray([0x00]).Length).IsEqualTo(0);
    }

    [Test]
    public async Task DecompressSequence_EmptyBlock()
    {
        var writer = new ArrayBufferWriter<byte>();
        Snappy.Decompress(new ReadOnlySequence<byte>(new byte[] { 0 }), writer);

        await Assert.That(writer.WrittenCount).IsEqualTo(0);
    }
}
