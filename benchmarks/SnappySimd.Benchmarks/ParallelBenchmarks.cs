using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace SnappySimd.Benchmarks;

/// <summary>
/// Large inputs (corpus files repeated to 16MB): Snappier vs single-threaded SnappySimd vs opt-in parallel
/// SnappySimd. Run on a process with several cores (the remote runner pins this class to a core range).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ParallelBenchmarks
{
    private byte[] _input = [];
    private byte[] _output = [];
    private SnappyParallelOptions _options = SnappyParallelOptions.Default;
    private MemoryStream _streamOutput = new();
    private MemoryStream _compressedStream = new();
    private byte[] _readBuffer = new byte[1 << 20];

    public static IEnumerable<string> Sources => Corpus.Files("json_api.json", "html_x_4", "urls.10K");

    [ParamsSource(nameof(Sources))]
    public string File { get; set; } = "";

    [Params(2, 4, 8, 16)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] data = Corpus.Load(File);
        _input = new byte[16 << 20];
        for (int i = 0; i < _input.Length; i += data.Length)
        {
            data.AsSpan(0, Math.Min(data.Length, _input.Length - i)).CopyTo(_input.AsSpan(i));
        }

        _output = new byte[Snappy.GetMaxCompressedLength(_input.Length)];
        _options = new SnappyParallelOptions { MaxDegreeOfParallelism = Threads };
        _streamOutput = new MemoryStream(_output.Length);

        _compressedStream = new MemoryStream();
        using (var compressor = new SnappyStream(_compressedStream, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            compressor.Write(_input);
        }
    }

    [BenchmarkCategory("Compress"), Benchmark(Baseline = true)]
    public int Compress_Snappier() => Snappier.Snappy.Compress(_input, _output);

    [BenchmarkCategory("Compress"), Benchmark]
    public int Compress_SnappySimd() => Snappy.Compress(_input, _output);

    [BenchmarkCategory("Compress"), Benchmark]
    public int Compress_SnappySimdParallel() => Snappy.Compress(_input, _output, _options);

    [BenchmarkCategory("StreamCompress"), Benchmark(Baseline = true)]
    public void StreamCompress_Snappier()
    {
        _streamOutput.Position = 0;
        using var stream = new Snappier.SnappyStream(_streamOutput, System.IO.Compression.CompressionMode.Compress, true);
        stream.Write(_input);
    }

    [BenchmarkCategory("StreamCompress"), Benchmark]
    public void StreamCompress_SnappySimd()
    {
        _streamOutput.Position = 0;
        using var stream = new SnappyStream(_streamOutput, System.IO.Compression.CompressionMode.Compress, true);
        stream.Write(_input);
    }

    [BenchmarkCategory("StreamCompress"), Benchmark]
    public void StreamCompress_SnappySimdParallel()
    {
        _streamOutput.Position = 0;
        using var stream = new SnappyStream(_streamOutput, System.IO.Compression.CompressionMode.Compress, true, _options);
        stream.Write(_input);
    }

    [BenchmarkCategory("StreamDecompress"), Benchmark(Baseline = true)]
    public void StreamDecompress_Snappier()
    {
        _compressedStream.Position = 0;
        using var stream = new Snappier.SnappyStream(_compressedStream, System.IO.Compression.CompressionMode.Decompress, true);
        while (stream.Read(_readBuffer) > 0)
        {
        }
    }

    [BenchmarkCategory("StreamDecompress"), Benchmark]
    public void StreamDecompress_SnappySimd()
    {
        _compressedStream.Position = 0;
        using var stream = new SnappyStream(_compressedStream, System.IO.Compression.CompressionMode.Decompress, true);
        while (stream.Read(_readBuffer) > 0)
        {
        }
    }

    [BenchmarkCategory("StreamDecompress"), Benchmark]
    public void StreamDecompress_SnappySimdParallel()
    {
        _compressedStream.Position = 0;
        using var stream = new SnappyStream(_compressedStream, System.IO.Compression.CompressionMode.Decompress, true, _options);
        while (stream.Read(_readBuffer) > 0)
        {
        }
    }
}
