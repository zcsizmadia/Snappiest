using BenchmarkDotNet.Attributes;
using Snappiest.Internal;

namespace Snappiest.Benchmarks;

/// <summary>CRC-32C as used per framing chunk: three-way CRC32 instruction vs carry-less multiply folding.</summary>
public class Crc32CBenchmarks
{
    private byte[] _data = [];

    [Params(256, 4096, 65536)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size];
        new Random(1).NextBytes(_data);
    }

    [Benchmark(Baseline = true)]
    public uint Interleaved() => Crc32C.UpdateHardware(~0u, _data);

    [Benchmark]
    public uint Folding() => Crc32C.UpdateFolding(~0u, _data);

#if NET10_0_OR_GREATER
    [Benchmark]
    public uint Folding256() => System.Runtime.Intrinsics.X86.Pclmulqdq.V256.IsSupported ? Crc32C.UpdateFolding256(~0u, _data) : 0;
#endif
}
