using SharpFuzz;

namespace SnappySimd.Fuzz;

/// <summary>
/// Entry point for coverage-guided fuzzing (see scripts/fuzz.sh).
/// </summary>
/// <remarks>
/// <code>
/// SnappySimd.Fuzz &lt;target&gt;                    run under libfuzzer-dotnet (--target_arg=&lt;target&gt;)
/// SnappySimd.Fuzz &lt;target&gt; &lt;file|dir&gt;...       replay inputs (crash reproducers, a corpus) without libFuzzer
/// SnappySimd.Fuzz seeds &lt;testdata&gt; &lt;corpus&gt;   write the seed corpus of every target to &lt;corpus&gt;/&lt;target&gt;
/// </code>
/// </remarks>
internal static class Program
{
    public static readonly Dictionary<string, ReadOnlySpanAction> Targets = new(StringComparer.Ordinal)
    {
        ["block"] = BlockTarget.Run,
        ["stream"] = StreamTarget.Run,
        ["compress"] = CompressTarget.Run,
    };

    public static int Main(string[] args)
    {
        if (args is ["seeds", string testData, string corpus])
        {
            EnableStandaloneTrace();
            SeedCorpus.Write(testData, corpus);
            return 0;
        }

        if (args.Length == 0 || !Targets.TryGetValue(args[0], out ReadOnlySpanAction? target))
        {
            Console.Error.WriteLine($"Usage: SnappySimd.Fuzz <{string.Join('|', Targets.Keys)}> [input files or directories]");
            Console.Error.WriteLine("       SnappySimd.Fuzz seeds <testdata dir> <corpus dir>");
            return 2;
        }

        if (args.Length == 1)
        {
            Fuzzer.LibFuzzer.Run(target);
            return 0;
        }

        EnableStandaloneTrace();
        return Replay(target, args.AsSpan(1));
    }

    /// <summary>
    /// Gives the instrumentation in an instrumented SnappySimd.dll somewhere to write when there is no libFuzzer
    /// (replaying inputs, writing seeds); harmless for an uninstrumented build.
    /// </summary>
    private static unsafe void EnableStandaloneTrace() =>
        SharpFuzz.Common.Trace.SharedMem = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(1 << 16);

    private static int Replay(ReadOnlySpanAction target, ReadOnlySpan<string> paths)
    {
        int count = 0, failures = 0;
        foreach (string path in paths)
        {
            IEnumerable<string> files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path).Order(StringComparer.Ordinal)
                : [path];
            foreach (string file in files)
            {
                count++;
                try
                {
                    target(File.ReadAllBytes(file));
                }
                catch (Exception exception)
                {
                    failures++;
                    Console.WriteLine($"FAIL {file}: {exception}");
                }
            }
        }

        Console.WriteLine($"{count} input(s), {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }
}
