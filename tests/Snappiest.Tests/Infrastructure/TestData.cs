namespace Snappiest.Tests.Infrastructure;

internal static class TestData
{
    /// <summary>The standard Snappy benchmark corpus (from google/snappy testdata).</summary>
    public static IEnumerable<string> CorpusFiles() =>
    [
        "alice29.txt", "asyoulik.txt", "fireworks.jpeg", "geo.protodata", "html", "html_x_4",
        "kppkn.gtb", "lcet10.txt", "paper-100k.pdf", "plrabn12.txt", "urls.10K",
    ];

    public static IEnumerable<string> BadDataFiles() => ["baddata1.snappy", "baddata2.snappy", "baddata3.snappy"];

    public static byte[] Load(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    public static bool Same(byte[] expected, byte[] actual) => expected.AsSpan().SequenceEqual(actual);

    public static bool Same(ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> actual) => expected.Span.SequenceEqual(actual.Span);

    /// <summary>Data with runs and repeats of varying lengths, similar to the generator in Snappier's tests.</summary>
    public static byte[] Generate(Random random, int length, int alphabetBits = 8)
    {
        byte[] buffer = new byte[length];
        int size = 0;
        while (size < length)
        {
            int runLength = 1;
            if (random.Next(0, 9) == 0)
            {
                runLength = random.Next(0, (1 << random.Next(0, 8)) - 1);
            }

            byte value = (byte)random.Next(0, 1 << alphabetBits);
            int count = Math.Min(runLength, length - size);
            buffer.AsSpan(size, count).Fill(value);
            size += Math.Max(runLength, 1);
        }

        return buffer;
    }

    /// <summary>Text-like data built from a small vocabulary, so it has matches at many offsets.</summary>
    public static byte[] GenerateText(Random random, int length)
    {
        string[] words = ["snappy", "simd", "vector", "the", "a", "compression", "fast", "lorem", "ipsum", "\n", " ", ", "];
        var builder = new System.Text.StringBuilder(length + 16);
        while (builder.Length < length)
        {
            builder.Append(words[random.Next(words.Length)]);
        }

        return System.Text.Encoding.ASCII.GetBytes(builder.ToString(0, length));
    }
}
