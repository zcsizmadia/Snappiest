using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace SnappySimd.Benchmarks;

/// <summary>Raw block compression and decompression of whole corpus files, Snappier vs SnappySimd.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class BlockBenchmarks
{
    private byte[] _input = [];
    private byte[] _compressed = [];
    private byte[] _compressBuffer = [];
    private byte[] _decompressBuffer = [];

    public static IEnumerable<string> Files => Corpus.Files();

    [ParamsSource(nameof(Files))]
    public string File { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _input = Corpus.Load(File);
        _compressed = Snappier.Snappy.CompressToArray(_input);
        _compressBuffer = new byte[Math.Max(Snappy.GetMaxCompressedLength(_input.Length), Snappier.Snappy.GetMaxCompressedLength(_input.Length))];
        _decompressBuffer = new byte[_input.Length];
    }

    [BenchmarkCategory("Compress"), Benchmark(Baseline = true)]
    public int Compress_Snappier() => Snappier.Snappy.Compress(_input, _compressBuffer);

    [BenchmarkCategory("Compress"), Benchmark]
    public int Compress_SnappySimd() => Snappy.Compress(_input, _compressBuffer);

    [BenchmarkCategory("Decompress"), Benchmark(Baseline = true)]
    public int Decompress_Snappier() => Snappier.Snappy.Decompress(_compressed, _decompressBuffer);

    [BenchmarkCategory("Decompress"), Benchmark]
    public int Decompress_SnappySimd() => Snappy.Decompress(_compressed, _decompressBuffer);
}
