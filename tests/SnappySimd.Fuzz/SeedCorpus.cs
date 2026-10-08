using System.Buffers.Binary;
using System.IO.Compression;

namespace SnappySimd.Fuzz;

/// <summary>
/// Writes the seed corpus: testdata slices compressed as blocks and streams, the testdata .snappy files, and small
/// hand-made edge cases (every tag form, the fast loop's input margin, framing chunk types).
/// </summary>
internal static class SeedCorpus
{
    private static readonly string[] RawFiles =
    [
        "alice29.txt", "asyoulik.txt", "fireworks.jpeg", "geo.protodata", "html", "kppkn.gtb", "lcet10.txt",
        "paper-100k.pdf", "plrabn12.txt", "urls.10K", "json_api.json", "events.ndjson",
    ];

    private static readonly int[] SliceLengths = [16, 100, 131, 1000, 4096, 30_000];

    public static void Write(string testData, string corpus)
    {
        string blockDir = Directory.CreateDirectory(Path.Combine(corpus, "block")).FullName;
        string streamDir = Directory.CreateDirectory(Path.Combine(corpus, "stream")).FullName;
        string compressDir = Directory.CreateDirectory(Path.Combine(corpus, "compress")).FullName;

        foreach (string name in RawFiles)
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(testData, name));
            foreach (int length in SliceLengths)
            {
                byte[] slice = raw.AsSpan(0, Math.Min(length, raw.Length)).ToArray();
                File.WriteAllBytes(Path.Combine(blockDir, $"{name}-{length}"), Snappy.CompressToArray(slice));
                File.WriteAllBytes(Path.Combine(compressDir, $"{name}-{length}"), [0, .. slice]);
            }

            // Multi-chunk streams: whole 64KB chunks and small flushed ones (the parallel decoder batches both)
            byte[] large = raw.AsSpan(0, Math.Min(100_000, raw.Length)).ToArray();
            File.WriteAllBytes(Path.Combine(streamDir, $"{name}-100000"), CompressStream(large, flushEvery: 0));
            File.WriteAllBytes(Path.Combine(streamDir, $"{name}-flushed"), CompressStream(large.AsSpan(0, Math.Min(5000, large.Length)).ToArray(), flushEvery: 500));

            // A short sample repeated past 64KB fragments (see CompressTarget's input format)
            File.WriteAllBytes(Path.Combine(compressDir, $"{name}-repeat"), [3, .. raw.AsSpan(0, Math.Min(777, raw.Length))]);
        }

        foreach (string name in new[] { "alice29.snappy", "html_x_4.snappy", "baddata1.snappy", "baddata2.snappy", "baddata3.snappy" })
        {
            File.Copy(Path.Combine(testData, name), Path.Combine(blockDir, name), overwrite: true);
        }

        foreach ((string name, byte[] data) in Blocks())
        {
            File.WriteAllBytes(Path.Combine(blockDir, "edge-" + name), data);
        }

        foreach ((string name, byte[] data) in Streams())
        {
            File.WriteAllBytes(Path.Combine(streamDir, "edge-" + name), data);
        }

        File.WriteAllBytes(Path.Combine(compressDir, "edge-empty"), []);
        File.WriteAllBytes(Path.Combine(compressDir, "edge-zeros-repeat"), [15, 0, 0, 0, 0]);
        File.WriteAllBytes(Path.Combine(compressDir, "edge-pattern"), [0, .. Enumerable.Range(0, 3000).Select(i => (byte)(i % 251 % 7))]);
    }

    private static IEnumerable<(string, byte[])> Blocks()
    {
        byte[] text = "snappy simd fuzzing: the quick brown fox jumps over the lazy dog, again and again"u8.ToArray();

        yield return ("empty", []);
        yield return ("zero-length", [0]);
        yield return ("zero-length-trailing", [0, 0]);
        yield return ("length-only", [5]);
        yield return ("varint-5-bytes", [0x80, 0x80, 0x80, 0x80, 0x0F]);
        yield return ("varint-too-long", [0x80, 0x80, 0x80, 0x80, 0x80, 0x01]);
        yield return ("literal-1", [1, 0, 0x41]);
        yield return ("literal-61", Block(61, Literal(Fill(61))));
        yield return ("literal-2-byte-length", Block(300, Literal(Fill(300))));
        yield return ("copy1-overlap", Block(40, Literal([0x61]), Copy1(1, 11), Copy1(1, 11), Copy1(1, 11), Copy1(1, 6)));
        yield return ("copy2-pattern", Block(text.Length + 64, Literal(text), Copy2(3, 64)));
        yield return ("copy4", Block(text.Length + 60, Literal(text), Copy4(text.Length, 60)));
        yield return ("offset-zero", Block(20, Literal(Fill(10)), Copy1(0, 10)));
        yield return ("offset-too-far", Block(20, Literal(Fill(10)), Copy2(11, 10)));
        yield return ("overrun", Block(20, Literal(Fill(10)), Copy1(5, 11)));
        yield return ("short", Block(21, Literal(Fill(10)), Copy1(5, 10)));

        // Tag streams around the fast loop's input margin (130 bytes) and output tail (127 bytes)
        foreach (int tags in new[] { 120, 129, 130, 131, 132, 200 })
        {
            var parts = new List<byte[]> { Literal(text) };
            int length = text.Length;
            for (int size = parts[0].Length; size + 2 <= tags; size += 2)
            {
                int copy = 4 + ((size * 7) % 8);
                parts.Add(Copy1(1 + ((size * 13) % text.Length), copy));
                length += copy;
            }

            yield return ($"tags-{tags}", Block(length, [.. parts]));
        }
    }

    private static IEnumerable<(string, byte[])> Streams()
    {
        byte[] header = [0xff, 0x06, 0x00, 0x00, .. "sNaPpY"u8];
        byte[] data = "hello snappy stream"u8.ToArray();

        yield return ("empty", []);
        yield return ("header-only", header);
        yield return ("header-twice", [.. header, .. header]);
        yield return ("uncompressed", [.. header, .. Chunk(0x01, data, data)]);
        yield return ("compressed", [.. header, .. Chunk(0x00, data, Snappy.CompressToArray(data))]);
        yield return ("empty-chunks", [.. header, .. Chunk(0x01, [], []), .. Chunk(0x00, [], [0])]);
        yield return ("padding", [.. header, .. Chunk(0xfe, [], new byte[7]), .. Chunk(0x01, data, data)]);
        yield return ("skippable", [.. header, .. Chunk(0x80, [], [1, 2, 3]), .. Chunk(0x00, data, Snappy.CompressToArray(data))]);
        yield return ("unskippable", [.. header, .. Chunk(0x02, [], [1, 2, 3])]);
        yield return ("bad-crc", [.. header, .. Chunk(0x01, [], data)]);
        yield return ("truncated", [.. header, .. Chunk(0x01, data, data).AsSpan(0, 10)]);
        yield return ("no-header", Chunk(0x01, data, data));

        var many = new List<byte>(header);
        for (int i = 0; i < 40; i++)
        {
            byte[] piece = data.AsSpan(0, 1 + (i % data.Length)).ToArray();
            many.AddRange(i % 2 == 0 ? Chunk(0x00, piece, Snappy.CompressToArray(piece)) : Chunk(0x01, piece, piece));
        }

        yield return ("many-chunks", [.. many]);
    }

    private static byte[] CompressStream(byte[] data, int flushEvery)
    {
        var output = new MemoryStream();
        using (var stream = new SnappyStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            if (flushEvery == 0)
            {
                stream.Write(data);
            }
            else
            {
                for (int i = 0; i < data.Length; i += flushEvery)
                {
                    stream.Write(data, i, Math.Min(flushEvery, data.Length - i));
                    stream.Flush();
                }
            }
        }

        return output.ToArray();
    }

    /// <summary>A framing chunk whose CRC is that of <paramref name="crcOf"/>.</summary>
    private static byte[] Chunk(byte type, byte[] crcOf, byte[] body)
    {
        bool data = type is 0x00 or 0x01;
        int length = body.Length + (data ? 4 : 0);
        byte[] chunk = new byte[4 + length];
        chunk[0] = type;
        chunk[1] = (byte)length;
        chunk[2] = (byte)(length >> 8);
        chunk[3] = (byte)(length >> 16);
        if (data)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), ReferenceFraming.MaskedCrc32C(crcOf));
        }

        body.CopyTo(chunk.AsSpan(data ? 8 : 4));
        return chunk;
    }

    private static byte[] Fill(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)('a' + (i % 26)))];

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

        int count = n < 1 << 8 ? 1 : n < 1 << 16 ? 2 : n < 1 << 24 ? 3 : 4;
        byte[] lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)n);
        return [(byte)((59 + count) << 2), .. lengthBytes.AsSpan(0, count), .. data];
    }

    private static byte[] Copy1(int offset, int length) => [(byte)(1 | ((length - 4) << 2) | ((offset >> 8) << 5)), (byte)offset];

    private static byte[] Copy2(int offset, int length) => [(byte)(2 | ((length - 1) << 2)), (byte)offset, (byte)(offset >> 8)];

    private static byte[] Copy4(int offset, int length)
    {
        byte[] tag = new byte[5];
        tag[0] = (byte)(3 | ((length - 1) << 2));
        BinaryPrimitives.WriteUInt32LittleEndian(tag.AsSpan(1), (uint)offset);
        return tag;
    }
}
