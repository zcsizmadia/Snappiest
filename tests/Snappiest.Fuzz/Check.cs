using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Snappiest.Fuzz;

/// <summary>A broken invariant. Not caught by the targets, so libFuzzer reports the input as a crash.</summary>
internal sealed class FuzzCheckException(string message) : Exception(message);

internal static class Check
{
    public static void That([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new FuzzCheckException(message);
        }
    }
}

internal static unsafe class Coverage
{
    // Same size as SharpFuzz's coverage map (edge ids are 16 bits)
    private static readonly byte* Discard = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(1 << 16);

    /// <summary>
    /// Runs <paramref name="action"/> without recording coverage. For the parallel paths: worker threads share the
    /// instrumentation's previous-location state, so their edges are random and would flood the corpus with inputs
    /// that only look new. Their results are still checked; the single-threaded runs of the same input give the
    /// coverage.
    /// </summary>
    public static T Untraced<T>(Func<T> action)
    {
        byte* traced = SharpFuzz.Common.Trace.SharedMem;
        SharpFuzz.Common.Trace.SharedMem = Discard;
        try
        {
            return action();
        }
        finally
        {
            SharpFuzz.Common.Trace.SharedMem = traced;
            SharpFuzz.Common.Trace.PrevLocation = 0;
        }
    }
}

internal static class Sequences
{
    /// <summary>A sequence of segments of <paramref name="segmentLength"/> bytes over <paramref name="data"/>.</summary>
    public static ReadOnlySequence<byte> Split(ReadOnlyMemory<byte> data, int segmentLength)
    {
        if (data.Length <= segmentLength)
        {
            return new ReadOnlySequence<byte>(data);
        }

        var first = new Segment(data.Slice(0, segmentLength), 0);
        Segment last = first;
        for (int start = segmentLength; start < data.Length; start += segmentLength)
        {
            var next = new Segment(data.Slice(start, Math.Min(segmentLength, data.Length - start)), start);
            last.SetNext(next);
            last = next;
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void SetNext(Segment next) => Next = next;
    }
}
