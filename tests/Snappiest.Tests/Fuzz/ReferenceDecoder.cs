namespace Snappiest.Tests.Fuzz;

/// <summary>
/// A deliberately plain decoder written straight from google/snappy's format_description.txt: no fast paths, every
/// rule checked. It is the referee for the differential tests.
/// </summary>
internal static class ReferenceDecoder
{
    /// <returns>The decoded bytes, or null if the block is invalid; <paramref name="error"/> says why.</returns>
    public static byte[]? Decode(ReadOnlySpan<byte> data, out string error)
    {
        int ip = 0;
        ulong length = 0;
        for (int shift = 0; ; shift += 7)
        {
            if (ip >= data.Length || shift > 28)
            {
                error = "bad length header";
                return null;
            }

            byte b = data[ip++];
            length |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                break;
            }
        }

        if (length > int.MaxValue)
        {
            error = "length too large";
            return null;
        }

        byte[] output = new byte[length];
        int op = 0;
        while (ip < data.Length)
        {
            int at = ip;
            byte tag = data[ip++];
            long count;
            long offset;
            switch (tag & 3)
            {
                case 0:
                    count = (tag >> 2) + 1;
                    if (count > 60)
                    {
                        int bytes = (int)count - 60;
                        if (ip + bytes > data.Length)
                        {
                            error = $"literal length bytes missing at {at}";
                            return null;
                        }

                        count = 0;
                        for (int i = 0; i < bytes; i++)
                        {
                            count |= (long)data[ip + i] << (8 * i);
                        }

                        count++;
                        ip += bytes;
                    }

                    if (ip + count > data.Length)
                    {
                        error = $"literal data missing at {at}";
                        return null;
                    }

                    if (op + count > output.Length)
                    {
                        error = $"literal overruns the declared length at {at}";
                        return null;
                    }

                    data.Slice(ip, (int)count).CopyTo(output.AsSpan(op));
                    ip += (int)count;
                    op += (int)count;
                    continue;

                case 1:
                    if (ip + 1 > data.Length)
                    {
                        error = "copy-1 truncated";
                        return null;
                    }

                    count = ((tag >> 2) & 7) + 4;
                    offset = ((tag & 0xE0) << 3) | data[ip];
                    ip += 1;
                    break;

                case 2:
                    if (ip + 2 > data.Length)
                    {
                        error = "copy-2 truncated";
                        return null;
                    }

                    count = (tag >> 2) + 1;
                    offset = data[ip] | (data[ip + 1] << 8);
                    ip += 2;
                    break;

                default:
                    if (ip + 4 > data.Length)
                    {
                        error = "copy-4 truncated";
                        return null;
                    }

                    count = (tag >> 2) + 1;
                    offset = BitConverter.ToUInt32(data.Slice(ip, 4));
                    ip += 4;
                    break;
            }

            if (offset == 0 || offset > op)
            {
                error = $"copy offset {offset} invalid at {at}";
                return null;
            }

            if (op + count > output.Length)
            {
                error = $"copy overruns the declared length at {at}";
                return null;
            }

            for (int i = 0; i < count; i++, op++)
            {
                output[op] = output[op - offset];
            }
        }

        if (op != output.Length)
        {
            error = $"output short: {op} of {output.Length}";
            return null;
        }

        error = "ok";
        return output;
    }
}
