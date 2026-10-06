using System.IO.Compression;
using SnappySimd.Internal;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Tests.Fuzz;

/// <summary>
/// Layer 2: corrupted blocks and streams must either decode or throw <see cref="InvalidDataException"/>; never
/// another exception, a hang, or an out-of-bounds access. Validate must agree with Decompress.
/// </summary>
public class CorruptionFuzzTests
{
    /// <summary>Bit flips, byte overwrites, truncation, splicing and length tampering.</summary>
    internal static byte[] Mutate(Random random, byte[] valid)
    {
        byte[] data = (byte[])valid.Clone();
        int mutations = random.Next(1, 6);
        for (int m = 0; m < mutations && data.Length > 0; m++)
        {
            switch (random.Next(6))
            {
                case 0:
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                    break;
                case 2:
                    data = data.AsSpan(0, random.Next(data.Length)).ToArray();
                    break;
                case 3:
                {
                    // Duplicate a slice somewhere else
                    int start = random.Next(data.Length);
                    int count = random.Next(Math.Min(64, data.Length - start) + 1);
                    int at = random.Next(data.Length);
                    data = [.. data.AsSpan(0, at), .. data.AsSpan(start, count), .. data.AsSpan(at)];
                    break;
                }
                case 4:
                    // Tamper with the first bytes: block length header or stream chunk header
                    data[random.Next(Math.Min(8, data.Length))] = (byte)random.Next(256);
                    break;
                default:
                {
                    int start = random.Next(data.Length);
                    data = [.. data.AsSpan(0, start), .. data.AsSpan(Math.Min(data.Length, start + random.Next(1, 16)))];
                    break;
                }
            }
        }

        return data;
    }

    [Test]
    public async Task Blocks()
    {
        var harness = new FuzzHarness(nameof(CorruptionFuzzTests) + "." + nameof(Blocks));
        while (harness.Next() is { } random)
        {
            var (_, generate) = FuzzGenerators.All[random.Next(FuzzGenerators.All.Length)];
            byte[] source = generate(random, Math.Min(FuzzGenerators.Length(random), 100_000));
            byte[] corrupt = Mutate(random, Snappy.CompressToArray(source));

            bool decoded;
            try
            {
                Snappy.DecompressToArray(corrupt);
                decoded = true;
            }
            catch (InvalidDataException)
            {
                decoded = false;
            }
            catch (Exception exception)
            {
                harness.Fail($"block: {exception.GetType().Name}: {exception.Message}", corrupt);
                continue;
            }

            // Validate (no output) must reach the same verdict as decoding
            try
            {
                int length = BlockDecompressor.ReadUncompressedLength(corrupt, out int header);
                if (BlockDecompressor.Validate(corrupt, header, length) != decoded)
                {
                    harness.Fail($"block: Validate says {!decoded}, Decompress says {decoded}", corrupt);
                }
            }
            catch (InvalidDataException)
            {
                if (decoded)
                {
                    harness.Fail("block: header rejected but Decompress succeeded", corrupt);
                }
            }

            // A share against guard pages, to catch reads past the end of corrupt input
            if (random.Next(8) == 0)
            {
                using GuardedBuffer guarded = GuardedBuffer.From(corrupt);
                try
                {
                    int length = Snappy.GetUncompressedLength(guarded.Span);
                    if (length <= 1 << 20)
                    {
                        using var output = new GuardedBuffer(length);
                        Snappy.Decompress(guarded.Span, output.Span);
                    }
                }
                catch (InvalidDataException)
                {
                }
                catch (ArgumentException)
                {
                    // Output too small for a tampered length header
                }
            }
        }

        await Assert.That(harness.Failures).IsEmpty().Because(harness.Report());
    }

    [Test]
    public async Task Streams()
    {
        var harness = new FuzzHarness(nameof(CorruptionFuzzTests) + "." + nameof(Streams));
        while (harness.Next() is { } random)
        {
            var (_, generate) = FuzzGenerators.All[random.Next(FuzzGenerators.All.Length)];
            byte[] source = generate(random, Math.Min(FuzzGenerators.Length(random), 300_000));
            byte[] corrupt = Mutate(random, RoundTripFuzzTests.WriteStream(source, random, null));
            SnappyParallelOptions? options = random.Next(2) == 0 ? null : new SnappyParallelOptions { MaxDegreeOfParallelism = 3, MinimumParallelLength = 0 };

            try
            {
                RoundTripFuzzTests.ReadStream(corrupt, options, random.Next(1, 70_000));
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception exception)
            {
                harness.Fail($"stream{(options is null ? string.Empty : " (parallel)")}: {exception.GetType().Name}: {exception.Message}", corrupt);
            }
        }

        await Assert.That(harness.Failures).IsEmpty().Because(harness.Report());
    }
}
