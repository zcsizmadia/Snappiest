using System.Buffers;

namespace Snappiest.Tests.Infrastructure;

internal static class SequenceHelpers
{
    public static ReadOnlySequence<byte> CreateSequence(ReadOnlyMemory<byte> source, int maxSegmentSize)
    {
        Segment? first = null;
        Segment? last = null;
        long runningIndex = 0;

        while (source.Length > 0)
        {
            int length = Math.Min(maxSegmentSize, source.Length);
            var segment = new Segment(source.Slice(0, length), runningIndex);
            if (last is null)
            {
                first = segment;
            }
            else
            {
                last.SetNext(segment);
            }

            last = segment;
            runningIndex += length;
            source = source.Slice(length);
        }

        return first is null ? default : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    /// <summary>A sequence of <paramref name="count"/> segments that all point at the same memory.</summary>
    public static ReadOnlySequence<byte> CreateRepeatedSequence(ReadOnlyMemory<byte> chunk, int count)
    {
        var first = new Segment(chunk, 0);
        Segment last = first;
        for (int i = 1; i < count; i++)
        {
            var next = new Segment(chunk, (long)i * chunk.Length);
            last.SetNext(next);
            last = next;
        }

        return new ReadOnlySequence<byte>(first, 0, last, chunk.Length);
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
