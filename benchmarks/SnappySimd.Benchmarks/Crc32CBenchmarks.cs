using BenchmarkDotNet.Attributes;
using SnappySimd.Internal;

namespace SnappySimd.Benchmarks;

/// <summary>CRC-32C as used per framing chunk: the share of stream time spent on checksums.</summary>
public class Crc32CBenchmarks
{
    private byte[] _data = [];

    [Params(4096, 65536)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size];
        new Random(1).NextBytes(_data);
    }

    [Benchmark]
    public uint Crc32C_SnappySimd() => Crc32C.Compute(_data);
}
