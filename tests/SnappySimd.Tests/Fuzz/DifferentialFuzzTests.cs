namespace SnappySimd.Tests.Fuzz;

/// <summary>
/// Layer 3: the same (often corrupt) bytes fed to SnappySimd, Snappier and a plain spec decoder
/// (<see cref="ReferenceDecoder"/>). SnappySimd must agree with the spec decoder exactly: accept the same blocks and
/// produce the same bytes. Snappier is compared too, but its known leniency is only reported: it accepts blocks whose
/// tags run past the declared length or past the end of the input, which google/snappy and SnappySimd reject.
/// </summary>
public class DifferentialFuzzTests
{
    [Test]
    public async Task Blocks()
    {
        var harness = new FuzzHarness(nameof(DifferentialFuzzTests) + "." + nameof(Blocks));
        int snappierLenient = 0, snappierMishandlesValid = 0;
        while (harness.Next() is { } random)
        {
            var (_, generate) = FuzzGenerators.All[random.Next(FuzzGenerators.All.Length)];
            byte[] source = generate(random, Math.Min(FuzzGenerators.Length(random), 100_000));
            byte[] candidate = random.Next(4) == 0
                ? FuzzGenerators.RandomBytes(random, random.Next(0, 200))
                : CorruptionFuzzTests.Mutate(random, Snappy.CompressToArray(source));

            // All three decoders allocate the declared length: keep random headers from asking for gigabytes
            int declared;
            try
            {
                declared = Snappy.GetUncompressedLength(candidate);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (declared > 1 << 22)
            {
                continue;
            }

            byte[]? reference = ReferenceDecoder.Decode(candidate, out string why);
            byte[]? ours = Decode(() => Snappy.DecompressToArray(candidate));
            byte[]? theirs = Decode(() => Snappier.Snappy.DecompressToArray(candidate));

            if (reference is null && ours is not null)
            {
                harness.Fail($"SnappySimd accepts a block the spec rejects ({why})", candidate);
            }
            else if (reference is not null && ours is null)
            {
                harness.Fail("SnappySimd rejects a valid block", candidate);
            }
            else if (reference is not null && !reference.AsSpan().SequenceEqual(ours))
            {
                harness.Fail("SnappySimd decodes a valid block to different bytes", candidate);
            }

            if (reference is null && theirs is not null)
            {
                snappierLenient++;
            }
            else if (reference is not null && (theirs is null || !reference.AsSpan().SequenceEqual(theirs)))
            {
                snappierMishandlesValid++;
            }
        }

        Console.WriteLine($"[fuzz] Snappier accepted {snappierLenient} invalid block(s) and mishandled {snappierMishandlesValid} valid block(s)");
        await Assert.That(harness.Failures).IsEmpty().Because(harness.Report());
    }

    private static byte[]? Decode(Func<byte[]> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
