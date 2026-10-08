using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Snappiest.Benchmarks;

/// <summary>
/// Inputs from 64 bytes to 64KB, where per-call overhead (setup, hash table clearing, pooling) matters. Compressible
/// (html) and incompressible (jpeg; Google's jpg_200 case is the 200 byte size) sources.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SmallBlockBenchmarks
{
    private byte[] _input = [];
    private byte[] _compressed = [];
    private byte[] _compressBuffer = [];
    private byte[] _decompressBuffer = [];

    public static IEnumerable<string> Sources => Corpus.Files("html", "fireworks.jpeg");

    [ParamsSource(nameof(Sources))]
    public string File { get; set; } = "";

    [Params(64, 200, 256, 1024, 4096, 16384, 65536)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _input = Corpus.Load(File).AsSpan(0, Size).ToArray();
        _compressed = Snappier.Snappy.CompressToArray(_input);
        _compressBuffer = new byte[Snappy.GetMaxCompressedLength(Size) + 64];
        _decompressBuffer = new byte[Size];
    }

    [BenchmarkCategory("Compress"), Benchmark(Baseline = true)]
    public int Compress_Snappier() => Snappier.Snappy.Compress(_input, _compressBuffer);

    [BenchmarkCategory("Compress"), Benchmark]
    public int Compress_Snappiest() => Snappy.Compress(_input, _compressBuffer);

    [BenchmarkCategory("Decompress"), Benchmark(Baseline = true)]
    public int Decompress_Snappier() => Snappier.Snappy.Decompress(_compressed, _decompressBuffer);

    [BenchmarkCategory("Decompress"), Benchmark]
    public int Decompress_Snappiest() => Snappy.Decompress(_compressed, _decompressBuffer);

    [BenchmarkCategory("ToArray"), Benchmark(Baseline = true)]
    public byte[] RoundTripArray_Snappier() => Snappier.Snappy.DecompressToArray(Snappier.Snappy.CompressToArray(_input));

    [BenchmarkCategory("ToArray"), Benchmark]
    public byte[] RoundTripArray_Snappiest() => Snappy.DecompressToArray(Snappy.CompressToArray(_input));
}
