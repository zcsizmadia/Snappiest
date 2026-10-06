namespace SnappySimd.Benchmarks;

internal static class Corpus
{
    private static readonly string[] AllFiles =
    [
        "alice29.txt", "asyoulik.txt", "fireworks.jpeg", "geo.protodata", "html", "html_x_4",
        "kppkn.gtb", "lcet10.txt", "paper-100k.pdf", "plrabn12.txt", "urls.10K",
        "json_api.json", "json_indented.json", "events.ndjson",
    ];

    public static IReadOnlyList<string> All => AllFiles;

    /// <summary>
    /// Corpus files to benchmark. BENCH_FILES (comma separated) narrows the set, so a run can be split across
    /// processes pinned to different cores.
    /// </summary>
    public static IEnumerable<string> Files(params string[] defaults)
    {
        string? filter = Environment.GetEnvironmentVariable("BENCH_FILES");
        if (!string.IsNullOrEmpty(filter))
        {
            return filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return defaults.Length > 0 ? defaults : AllFiles;
    }

    public static byte[] Load(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", name));
}
