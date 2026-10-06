using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace SnappySimd.Benchmarks;

/// <summary>
/// Every corpus file in one operation, like google/snappy's BM_ZFlatAll / BM_UFlatMedley: a single headline number
/// per library. Throughput = total corpus bytes / mean time.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CorpusBenchmarks
{
    private byte[][] _inputs = [];
    private byte[][] _compressed = [];
    private byte[][] _compressBuffers = [];
    private byte[][] _decompressBuffers = [];

    [GlobalSetup]
    public void Setup()
    {
        _inputs = [.. Corpus.All.Select(Corpus.Load)];
        _compressed = [.. _inputs.Select(i => Snappier.Snappy.CompressToArray(i))];
        _compressBuffers = [.. _inputs.Select(i => new byte[Snappy.GetMaxCompressedLength(i.Length)])];
        _decompressBuffers = [.. _inputs.Select(i => new byte[i.Length])];
        Console.WriteLine($"// Corpus: {_inputs.Length} files, {_inputs.Sum(i => (long)i.Length)} bytes");
    }

    [BenchmarkCategory("Compress"), Benchmark(Baseline = true)]
    public int Compress_Snappier()
    {
        int total = 0;
        for (int i = 0; i < _inputs.Length; i++)
        {
            total += Snappier.Snappy.Compress(_inputs[i], _compressBuffers[i]);
        }

        return total;
    }

    [BenchmarkCategory("Compress"), Benchmark]
    public int Compress_SnappySimd()
    {
        int total = 0;
        for (int i = 0; i < _inputs.Length; i++)
        {
            total += Snappy.Compress(_inputs[i], _compressBuffers[i]);
        }

        return total;
    }

    [BenchmarkCategory("Decompress"), Benchmark(Baseline = true)]
    public int Decompress_Snappier()
    {
        int total = 0;
        for (int i = 0; i < _compressed.Length; i++)
        {
            total += Snappier.Snappy.Decompress(_compressed[i], _decompressBuffers[i]);
        }

        return total;
    }

    [BenchmarkCategory("Decompress"), Benchmark]
    public int Decompress_SnappySimd()
    {
        int total = 0;
        for (int i = 0; i < _compressed.Length; i++)
        {
            total += Snappy.Decompress(_compressed[i], _decompressBuffers[i]);
        }

        return total;
    }
}
