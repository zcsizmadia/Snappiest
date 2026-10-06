using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace SnappySimd.Benchmarks;

/// <summary>Framed stream compression and decompression, mirroring Snappier's CompressAll/DecompressAll.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class StreamBenchmarks
{
    private MemoryStream _source = new();
    private MemoryStream _destination = new();
    private MemoryStream _compressed = new();
    private byte[] _buffer = new byte[65536];

    public static IEnumerable<string> Files => Corpus.Files("alice29.txt", "fireworks.jpeg", "html_x_4", "urls.10K", "json_api.json", "events.ndjson");

    [ParamsSource(nameof(Files))]
    public string File { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _source = new MemoryStream(Corpus.Load(File));
        _destination = new MemoryStream(_source.Capacity * 2);

        _compressed = new MemoryStream();
        using (var compressor = new Snappier.SnappyStream(_compressed, CompressionMode.Compress, true))
        {
            _source.CopyTo(compressor);
        }
    }

    [BenchmarkCategory("Compress"), Benchmark(Baseline = true)]
    public void Compress_Snappier()
    {
        _source.Position = 0;
        _destination.Position = 0;
        using var stream = new Snappier.SnappyStream(_destination, CompressionMode.Compress, true);
        _source.CopyTo(stream, 65536);
        stream.Flush();
    }

    [BenchmarkCategory("Compress"), Benchmark]
    public void Compress_SnappySimd()
    {
        _source.Position = 0;
        _destination.Position = 0;
        using var stream = new SnappyStream(_destination, CompressionMode.Compress, true);
        _source.CopyTo(stream, 65536);
        stream.Flush();
    }

    [BenchmarkCategory("Decompress"), Benchmark(Baseline = true)]
    public void Decompress_Snappier()
    {
        _compressed.Position = 0;
        using var stream = new Snappier.SnappyStream(_compressed, CompressionMode.Decompress, true);
        while (stream.Read(_buffer, 0, _buffer.Length) > 0)
        {
        }
    }

    [BenchmarkCategory("Decompress"), Benchmark]
    public void Decompress_SnappySimd()
    {
        _compressed.Position = 0;
        using var stream = new SnappyStream(_compressed, CompressionMode.Decompress, true);
        while (stream.Read(_buffer, 0, _buffer.Length) > 0)
        {
        }
    }
}
