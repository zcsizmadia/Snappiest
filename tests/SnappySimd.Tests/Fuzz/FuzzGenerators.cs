using System.Text;
using SnappySimd.Tests.Infrastructure;

namespace SnappySimd.Tests.Fuzz;

/// <summary>
/// Random inputs shaped like real payloads. Every generator is deterministic for a given <see cref="Random"/>.
/// </summary>
internal static class FuzzGenerators
{
    public delegate byte[] Generator(Random random, int length);

    public static readonly (string Name, Generator Generate)[] All =
    [
        ("text", Text),
        ("html", Html),
        ("json", Json),
        ("random", RandomBytes),
        ("numeric", NumericColumns),
        ("runs", Runs),
        ("pattern", RepeatingPattern),
        ("mixed", Mixed),
    ];

    /// <summary>Sizes that matter to the codec: tiny, around the fast-loop margins, around 64KB fragments.</summary>
    public static int Length(Random random)
    {
        return random.Next(10) switch
        {
            0 => random.Next(0, 16),
            1 => random.Next(16, 300),
            2 => random.Next(300, 4096),
            3 => random.Next(4096, 65536),
            4 => (65536 * random.Next(1, 4)) + random.Next(-3, 4),
            5 => random.Next(100_000, 600_000),
            6 => 120 + random.Next(0, 20), // around the decoder's fast-loop entry margin
            _ => random.Next(0, 70_000),
        };
    }

    private static readonly Lazy<string[]> s_words = new(() =>
        Encoding.ASCII.GetString(TestData.Load("alice29.txt"))
            .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>English-like text: word bigrams sampled from alice29.txt, so phrases repeat realistically.</summary>
    public static byte[] Text(Random random, int length)
    {
        string[] words = s_words.Value;
        var builder = new StringBuilder(length + 32);
        int position = random.Next(words.Length);
        while (builder.Length < length)
        {
            // Usually continue the source text (realistic phrases), sometimes jump
            position = random.Next(5) == 0 ? random.Next(words.Length) : (position + 1) % words.Length;
            builder.Append(words[position]).Append(random.Next(12) == 0 ? '\n' : ' ');
        }

        return Encoding.ASCII.GetBytes(builder.ToString(0, length));
    }

    private static readonly string[] s_tags = ["div", "span", "p", "a", "li", "ul", "td", "tr", "table", "section", "img", "script", "style", "h1", "h2"];
    private static readonly string[] s_attributes = ["class", "id", "href", "src", "style", "data-id", "title", "alt"];

    /// <summary>Nested HTML with attributes, entities, inline script/style and text.</summary>
    public static byte[] Html(Random random, int length)
    {
        var builder = new StringBuilder(length + 64);
        var stack = new Stack<string>();
        builder.Append("<!DOCTYPE html><html><body>");
        while (builder.Length < length)
        {
            int action = random.Next(10);
            if (action < 4 || stack.Count == 0)
            {
                string tag = s_tags[random.Next(s_tags.Length)];
                builder.Append('<').Append(tag);
                for (int i = random.Next(3); i > 0; i--)
                {
                    builder.Append(' ').Append(s_attributes[random.Next(s_attributes.Length)])
                        .Append("=\"").Append(Word(random)).Append('-').Append(random.Next(1000)).Append('"');
                }

                builder.Append('>');
                stack.Push(tag);
            }
            else if (action < 7)
            {
                builder.Append(Word(random)).Append(' ').Append(Word(random)).Append(random.Next(8) == 0 ? " &amp; " : " ");
            }
            else if (action == 7)
            {
                builder.Append("<script>var x").Append(random.Next(100)).Append(" = document.getElementById('")
                    .Append(Word(random)).Append("');</script>");
            }
            else
            {
                builder.Append("</").Append(stack.Pop()).Append(">\n");
            }
        }

        return Encoding.UTF8.GetBytes(builder.ToString()).AsSpan(0, Math.Min(length, builder.Length)).ToArray();
    }

    /// <summary>JSON objects with random schemas, nesting, unicode, numbers; sometimes newline-delimited.</summary>
    public static byte[] Json(Random random, int length)
    {
        var builder = new StringBuilder(length + 64);
        bool ndjson = random.Next(2) == 0;
        string[] keys = Enumerable.Range(0, random.Next(3, 12)).Select(_ => Word(random)).ToArray();
        if (!ndjson)
        {
            builder.Append('[');
        }

        while (builder.Length < length)
        {
            AppendJsonValue(builder, random, keys, depth: 0);
            builder.Append(ndjson ? "\n" : ",");
        }

        return Encoding.UTF8.GetBytes(builder.ToString()).AsSpan(0, Math.Min(length, Encoding.UTF8.GetByteCount(builder.ToString()))).ToArray();
    }

    private static void AppendJsonValue(StringBuilder builder, Random random, string[] keys, int depth)
    {
        builder.Append('{');
        int fields = random.Next(1, keys.Length);
        for (int i = 0; i < fields; i++)
        {
            builder.Append('"').Append(keys[random.Next(keys.Length)]).Append("\":");
            switch (random.Next(depth < 2 ? 7 : 6))
            {
                case 0: builder.Append(random.Next(-100000, 100000)); break;
                case 1: builder.Append(Math.Round(random.NextDouble() * 1000, random.Next(0, 6))); break;
                case 2: builder.Append(random.Next(2) == 0 ? "true" : "null"); break;
                case 3: builder.Append('"').Append(Word(random)).Append(random.Next(6) == 0 ? " Zürich 東京 🚀" : string.Empty).Append('"'); break;
                case 4: builder.Append('"').Append(RandomGuid(random)).Append('"'); break; // GUIDs: incompressible spans
                case 5: builder.Append("[1,2,3,").Append(random.Next(100)).Append(']'); break;
                default: AppendJsonValue(builder, random, keys, depth + 1); break;
            }

            if (i < fields - 1)
            {
                builder.Append(',');
            }
        }

        builder.Append('}');
    }

    public static byte[] RandomBytes(Random random, int length)
    {
        byte[] data = new byte[length];
        random.NextBytes(data);
        return data;
    }

    /// <summary>Little-endian int32/float64 columns with slowly varying values (Parquet/protobuf-like).</summary>
    public static byte[] NumericColumns(Random random, int length)
    {
        byte[] data = new byte[length];
        int value = random.Next();
        double real = random.NextDouble();
        bool doubles = random.Next(2) == 0;
        for (int i = 0; i + 8 <= length; i += 8)
        {
            if (doubles)
            {
                real += (random.NextDouble() - 0.5) * 0.01;
                BitConverter.TryWriteBytes(data.AsSpan(i), real);
            }
            else
            {
                value += random.Next(-3, 4);
                BitConverter.TryWriteBytes(data.AsSpan(i), value);
                BitConverter.TryWriteBytes(data.AsSpan(i + 4), value / 7);
            }
        }

        return data;
    }

    /// <summary>Runs of repeated bytes of random lengths (the generator from Snappier's tests, wider).</summary>
    public static byte[] Runs(Random random, int length) => TestData.Generate(random, length, random.Next(1, 9));

    /// <summary>A short random pattern repeated, with occasional mutations: exercises overlapping copies.</summary>
    public static byte[] RepeatingPattern(Random random, int length)
    {
        byte[] pattern = new byte[random.Next(1, 70)];
        random.NextBytes(pattern);
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = random.Next(500) == 0 ? (byte)random.Next(256) : pattern[i % pattern.Length];
        }

        return data;
    }

    /// <summary>Concatenated segments from the other generators: text with binary blobs, etc.</summary>
    public static byte[] Mixed(Random random, int length)
    {
        var output = new List<byte>(length);
        while (output.Count < length)
        {
            var (_, generate) = All[random.Next(All.Length - 1)]; // anything but Mixed itself
            output.AddRange(generate(random, Math.Min(length - output.Count, random.Next(1, 20_000))));
        }

        return [.. output];
    }

    private static Guid RandomGuid(Random random)
    {
        Span<byte> bytes = stackalloc byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static string Word(Random random)
    {
        string[] words = s_words.Value;
        return words[random.Next(words.Length)].Trim('"', '\'', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '-');
    }
}
