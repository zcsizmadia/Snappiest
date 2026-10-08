namespace Snappiest.Tests.Fuzz;

/// <summary>
/// Time budget, seed and failure capture shared by the fuzz tests.
/// </summary>
/// <remarks>
/// Normal test runs give each fuzz test about a second (SNAPPY_FUZZ_SECONDS unset). The nightly workflow sets
/// SNAPPY_FUZZ_SECONDS to a large budget. SNAPPY_FUZZ_SEED replays a reported seed; otherwise a fixed seed is used
/// in normal runs (reproducible CI) and a fresh one in budgeted runs.
/// </remarks>
internal sealed class FuzzHarness
{
    private readonly DateTime _deadline;
    private readonly string _name;
    private int _iteration;

    public FuzzHarness(string name)
    {
        _name = name;
        string? seconds = Environment.GetEnvironmentVariable("SNAPPY_FUZZ_SECONDS");
        bool budgeted = double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out double budget);
        _deadline = DateTime.UtcNow + TimeSpan.FromSeconds(budgeted ? budget : 1);

        Seed = int.TryParse(Environment.GetEnvironmentVariable("SNAPPY_FUZZ_SEED"), out int seed)
            ? seed
            : budgeted ? Random.Shared.Next() : name.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF;
        Console.WriteLine($"[fuzz] {name}: seed {Seed}, budget {(budgeted ? budget : 1)} s");
    }

    public int Seed { get; }

    public int Iterations => _iteration;

    public List<string> Failures { get; } = [];

    /// <summary>Next iteration's random source, or null when the time budget is used up.</summary>
    /// <remarks>Each iteration gets its own seed (Seed + iteration), so a single failure replays on its own.</remarks>
    public Random? Next()
    {
        if (DateTime.UtcNow >= _deadline)
        {
            return null;
        }

        return new Random(unchecked(Seed + (_iteration++ * 7919)));
    }

    /// <summary>Records a failure and saves the input that caused it.</summary>
    public void Fail(string description, byte[] input)
    {
        if (Failures.Count >= 20)
        {
            return;
        }

        string directory = Path.Combine(AppContext.BaseDirectory, "fuzz-failures");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, $"{_name}-seed{Seed}-iteration{_iteration - 1}.bin");
        File.WriteAllBytes(file, input);
        Failures.Add($"iteration {_iteration - 1} (SNAPPY_FUZZ_SEED={Seed}): {description} -> {file}");
    }

    public string Report() =>
        $"{_name}: {Failures.Count} failure(s) in {Iterations} iterations, seed {Seed}\n" + string.Join("\n", Failures);
}
