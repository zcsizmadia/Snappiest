using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace SnappySimd.Benchmarks;

/// <summary>
/// 256 different messages of one size, processed round-robin; times are per message. Benchmarks that repeat one
/// block let the CPU's branch predictor learn its whole tag sequence, which flatters branchy decoders (measured:
/// Snappier gets ~2 mispredictions per 1500 tags on a repeated 16KB html block). Distinct messages, as in real
/// traffic, prevent that.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class MessageMixBenchmarks
{
    private const int Messages = 256;

    // Compressible sources: text, markup, JSON, protobuf, tables (html_x_4 repeats html; jpeg/pdf barely compress)
    private static readonly string[] Sources =
    [
        "html", "alice29.txt", "asyoulik.txt", "lcet10.txt", "plrabn12.txt", "urls.10K",
        "json_api.json", "json_indented.json", "events.ndjson", "kppkn.gtb", "geo.protodata",
    ];

    private byte[][] _inputs = [];
    private byte[][] _compressed = [];
    private byte[] _compressBuffer = [];
    private byte[] _decompressBuffer = [];

    [Params(1024, 4096, 16384, 65536)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[][] sources = [.. Sources.Select(Corpus.Load)];
        var random = new Random(42);
        _inputs = [.. Enumerable.Range(0, Messages).Select(i =>
        {
            byte[] source = sources[i % sources.Length];
            return source.AsSpan(random.Next(source.Length - Size), Size).ToArray();
        })];
        _compressed = [.. _inputs.Select(i => Snappier.Snappy.CompressToArray(i))];
        _compressBuffer = new byte[Snappy.GetMaxCompressedLength(Size)];
        _decompressBuffer = new byte[Size];
    }

    [BenchmarkCategory("Compress"), Benchmark(Baseline = true, OperationsPerInvoke = Messages)]
    public int Compress_Snappier()
    {
        int total = 0;
        foreach (byte[] input in _inputs)
        {
            total += Snappier.Snappy.Compress(input, _compressBuffer);
        }

        return total;
    }

    [BenchmarkCategory("Compress"), Benchmark(OperationsPerInvoke = Messages)]
    public int Compress_SnappySimd()
    {
        int total = 0;
        foreach (byte[] input in _inputs)
        {
            total += Snappy.Compress(input, _compressBuffer);
        }

        return total;
    }

    [BenchmarkCategory("Decompress"), Benchmark(Baseline = true, OperationsPerInvoke = Messages)]
    public int Decompress_Snappier()
    {
        int total = 0;
        foreach (byte[] compressed in _compressed)
        {
            total += Snappier.Snappy.Decompress(compressed, _decompressBuffer);
        }

        return total;
    }

    [BenchmarkCategory("Decompress"), Benchmark(OperationsPerInvoke = Messages)]
    public int Decompress_SnappySimd()
    {
        int total = 0;
        foreach (byte[] compressed in _compressed)
        {
            total += Snappy.Decompress(compressed, _decompressBuffer);
        }

        return total;
    }
}
