using BenchmarkDotNet.Attributes;

namespace Snappiest.Benchmarks;

/// <summary>
/// Decompression speed after the process has mostly seen large or mostly tiny messages. The JIT's tiering and
/// dynamic PGO profile depend on that history, so each warmup runs in its own process: BENCH_WARMUP=large|small|none
/// (the remote runner starts one process per value). Snappier gets the same warmup.
/// </summary>
public class TieringBenchmarks
{
    private byte[] _compressed = [];
    private byte[] _output = [];

    [Params(64, 256, 4096, 65536)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Warmup(Environment.GetEnvironmentVariable("BENCH_WARMUP") ?? "none");

        byte[] input = Corpus.Load("html").AsSpan(0, Size).ToArray();
        _compressed = Snappier.Snappy.CompressToArray(input);
        _output = new byte[Size];
    }

    private static bool s_warmedUp;

    // Enough calls for both libraries' decode methods to reach Tier 1 with a profile from this input size
    private static void Warmup(string kind)
    {
        if (s_warmedUp || kind == "none")
        {
            return;
        }

        s_warmedUp = true;
        int size = kind == "large" ? 65536 : 64;
        int iterations = kind == "large" ? 20_000 : 2_000_000;
        byte[] input = Corpus.Load("html").AsSpan(0, size).ToArray();
        byte[] compressed = Snappier.Snappy.CompressToArray(input);
        byte[] output = new byte[size];
        for (int i = 0; i < iterations; i++)
        {
            Snappy.Decompress(compressed, output);
            Snappier.Snappy.Decompress(compressed, output);
        }

        // Let background tier-1 compilation finish before measuring
        Thread.Sleep(2000);
    }

    [Benchmark(Baseline = true)]
    public int Decompress_Snappier() => Snappier.Snappy.Decompress(_compressed, _output);

    [Benchmark]
    public int Decompress_Snappiest() => Snappy.Decompress(_compressed, _output);
}
