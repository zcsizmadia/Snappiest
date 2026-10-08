namespace Snappiest;

/// <summary>
/// Opt-in multi-threading for large inputs. Snappy compresses 64KB fragments (blocks) and chunks (streams)
/// independently, so they can be processed on several threads. The output is byte-for-byte identical to the
/// single-threaded output and is readable by any Snappy implementation.
/// </summary>
/// <remarks>
/// The members that match Snappier's API never use more than the calling thread; only the overloads that take
/// these options do.
/// </remarks>
public sealed class SnappyParallelOptions
{
    private readonly int _maxDegreeOfParallelism = Environment.ProcessorCount;
    private readonly int _minimumParallelLength = 256 * 1024;

    /// <summary>Options using all processors, for inputs of at least 256KB.</summary>
    public static SnappyParallelOptions Default { get; } = new();

    /// <summary>
    /// The maximum number of threads to use, including the calling thread. Defaults to
    /// <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxDegreeOfParallelism
    {
        get => _maxDegreeOfParallelism;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxDegreeOfParallelism = value;
        }
    }

    /// <summary>
    /// Inputs shorter than this are processed on the calling thread only, where scheduling would cost more than
    /// it saves. Defaults to 256KB.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MinimumParallelLength
    {
        get => _minimumParallelLength;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _minimumParallelLength = value;
        }
    }
}
