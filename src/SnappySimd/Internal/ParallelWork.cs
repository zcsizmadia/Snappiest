namespace SnappySimd.Internal;

/// <summary>
/// Runs a small number of equal-sized work items (64KB fragments or chunks) on several threads.
/// </summary>
internal static class ParallelWork
{
    /// <summary>
    /// Calls <paramref name="body"/> for every index in [0, <paramref name="count"/>) on up to
    /// <paramref name="options"/>.MaxDegreeOfParallelism threads, handing out one index at a time.
    /// </summary>
    /// <remarks>
    /// Parallel.For partitions indices into growing ranges, which balances long loops well, but with a batch of only
    /// a few items per thread one worker can take three or four while others get one, and the whole batch waits for
    /// it (measured: stream compression with 12 threads slower than with 8).
    /// </remarks>
    public static void For(int count, ParallelOptions options, Action<int> body)
    {
        int next = -1;
        int workers = Math.Min(count, options.MaxDegreeOfParallelism);
        Parallel.For(0, workers, options, _ =>
        {
            int index;
            while ((index = Interlocked.Increment(ref next)) < count)
            {
                body(index);
            }
        });
    }
}
