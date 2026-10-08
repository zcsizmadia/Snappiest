using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Snappiest.Tests.Infrastructure;

namespace Snappiest.Tests;

/// <summary>
/// JSON is one of the most common Snappy payloads (Kafka, Cassandra, RPC, caches). The test files are event records
/// in three shapes: a minified array (API response), the same records indented, and newline-delimited (event log).
/// </summary>
public class JsonTests
{
    public static IEnumerable<string> Files() => ["json_api.json", "json_indented.json", "events.ndjson"];

    public static IEnumerable<(string File, int Length)> Prefixes()
    {
        foreach (string file in Files())
        {
            foreach (int length in new[] { 100, 1 << 10, 1 << 12, 1 << 16, 1 << 20 })
            {
                yield return (file, length);
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

    [Test]
    public async Task TestFiles_AreValidJson()
    {
        using JsonDocument api = JsonDocument.Parse(TestData.Load("json_api.json"));
        using JsonDocument indented = JsonDocument.Parse(TestData.Load("json_indented.json"));
        await Assert.That(api.RootElement.GetArrayLength()).IsGreaterThan(1000);
        await Assert.That(indented.RootElement.GetArrayLength()).IsGreaterThan(1000);

        string[] lines = Encoding.UTF8.GetString(TestData.Load("events.ndjson")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines.Take(100))
        {
            using JsonDocument record = JsonDocument.Parse(line);
        }

        await Assert.That(lines.Length).IsGreaterThan(1000);
    }

    [Test]
    [MethodDataSource(nameof(Prefixes))]
    public async Task Block_RoundTrip_AndInterop(string file, int length)
    {
        byte[] data = TestData.Load(file);
        byte[] json = data.AsSpan(0, Math.Min(length, data.Length)).ToArray();

        byte[] ours = Snappy.CompressToArray(json);
        byte[] theirs = Snappier.Snappy.CompressToArray(json);

        await Assert.That(TestData.Same(json, Snappy.DecompressToArray(ours))).IsTrue();
        await Assert.That(TestData.Same(json, Snappier.Snappy.DecompressToArray(ours))).IsTrue();
        await Assert.That(TestData.Same(json, Snappy.DecompressToArray(theirs))).IsTrue();

        // Never worse than Snappier, and well under half the size once there are enough records to repeat
        await Assert.That((double)ours.Length).IsLessThanOrEqualTo(theirs.Length * 1.01);
        if (json.Length >= 1 << 16)
        {
            await Assert.That(ours.Length).IsLessThan(json.Length / 2);
        }
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Block_16MB_ManyFragments(string file)
    {
        byte[] json = Repeat(TestData.Load(file), 1 << 24);

        byte[] ours = Snappy.CompressToArray(json);

        await Assert.That(TestData.Same(json, Snappy.DecompressToArray(ours))).IsTrue();
        await Assert.That(TestData.Same(json, Snappier.Snappy.DecompressToArray(ours))).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Stream_RoundTrip_AndInterop(string file)
    {
        byte[] json = Repeat(TestData.Load(file), 1 << 22);

        // Written the way serializers write: many small writes
        using var compressed = new MemoryStream();
        using (var compressor = new SnappyStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            for (int i = 0; i < json.Length; i += 4096)
            {
                compressor.Write(json, i, Math.Min(4096, json.Length - i));
            }
        }

        compressed.Position = 0;
        using var ours = new MemoryStream();
        using (var decompressor = new SnappyStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            decompressor.CopyTo(ours);
        }

        compressed.Position = 0;
        using var theirs = new MemoryStream();
        using (var decompressor = new Snappier.SnappyStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            decompressor.CopyTo(theirs);
        }

        await Assert.That(TestData.Same(json, ours.ToArray())).IsTrue();
        await Assert.That(TestData.Same(json, theirs.ToArray())).IsTrue();
    }

    [Test]
    public async Task Stream_DeserializeDirectlyFromSnappyStream()
    {
        // The typical consumer: System.Text.Json reading straight from the decompressing stream
        byte[] json = TestData.Load("json_api.json");
        using var compressed = new MemoryStream();
        using (var compressor = new SnappyStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            compressor.Write(json);
        }

        compressed.Position = 0;
        await using var decompressor = new SnappyStream(compressed, CompressionMode.Decompress);
        using JsonDocument document = await JsonDocument.ParseAsync(decompressor);

        using JsonDocument expected = JsonDocument.Parse(json);
        await Assert.That(document.RootElement.GetArrayLength()).IsEqualTo(expected.RootElement.GetArrayLength());
    }

    [Test]
    public async Task Stream_SerializeDirectlyToSnappyStream()
    {
        // The typical producer: System.Text.Json writing straight into the compressing stream
        using JsonDocument source = JsonDocument.Parse(TestData.Load("json_api.json"));
        using var compressed = new MemoryStream();
        await using (var compressor = new SnappyStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(compressor, source.RootElement);
        }

        compressed.Position = 0;
        await using var decompressor = new SnappyStream(compressed, CompressionMode.Decompress);
        using JsonDocument roundTripped = await JsonDocument.ParseAsync(decompressor);

        await Assert.That(roundTripped.RootElement.GetArrayLength()).IsEqualTo(source.RootElement.GetArrayLength());
    }

    [Test]
    public async Task SmallMessages_ManyIndependentBlocks()
    {
        // Message-queue style: thousands of individually compressed small records, also read back by Snappier
        string[] lines = Encoding.UTF8.GetString(TestData.Load("events.ndjson")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int failures = 0;
        byte[] buffer = new byte[Snappy.GetMaxCompressedLength(64 * 1024)];
        byte[] output = new byte[64 * 1024];

        foreach (string line in lines)
        {
            byte[] record = Encoding.UTF8.GetBytes(line);
            int written = Snappy.Compress(record, buffer);
            int read = Snappy.Decompress(buffer.AsSpan(0, written), output);
            if (!record.AsSpan().SequenceEqual(output.AsSpan(0, read))
                || !record.AsSpan().SequenceEqual(Snappier.Snappy.DecompressToArray(buffer.AsSpan(0, written))))
            {
                failures++;
            }
        }

        await Assert.That(lines.Length).IsGreaterThan(1000);
        await Assert.That(failures).IsEqualTo(0);
    }
}
