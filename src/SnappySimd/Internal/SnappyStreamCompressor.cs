using System.Buffers;
using System.Buffers.Binary;

namespace SnappySimd.Internal;

/// <summary>
/// Emits the Snappy framing format. Input is gathered into 64KB chunks; each chunk is compressed straight into an
/// output buffer that is written to the stream once per <see cref="Write"/> call.
/// </summary>
internal sealed class SnappyStreamCompressor : IDisposable
{
    private const int ChunkOverhead = StreamFormat.ChunkHeaderLength + StreamFormat.ChecksumLength;

    // Room for the stream identifier and two maximum size chunks
    private static readonly int OutputBufferSize =
        StreamFormat.StreamHeader.Length + (2 * (ChunkOverhead + VarInt.MaxLength + BlockCompressor.MaxFragmentLength(StreamFormat.MaxChunkDataLength)));

    private static readonly int MaxChunkOutput =
        ChunkOverhead + VarInt.MaxLength + BlockCompressor.MaxFragmentLength(StreamFormat.MaxChunkDataLength);

    private byte[]? _input;
    private int _inputLength;

    private byte[]? _output;
    private int _outputLength;

    private bool _headerWritten;
    private bool _disposed;

    public void Write(ReadOnlySpan<byte> input, Stream stream)
    {
        CompressInput(input, stream);
        WriteOutput(stream);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, Stream stream, CancellationToken cancellationToken)
    {
        while (!input.IsEmpty)
        {
            // Bound the buffered output so it can be written between chunks
            int consumed = CompressInputOnce(input.Span);
            input = input.Slice(consumed);

            if (_outputLength > OutputBufferSize - MaxChunkOutput)
            {
                await WriteOutputAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }

        EnsureBuffers();
        EnsureHeader();
        await WriteOutputAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public void Flush(Stream stream)
    {
        CompressPending();
        WriteOutput(stream);
    }

    public ValueTask FlushAsync(Stream stream, CancellationToken cancellationToken)
    {
        CompressPending();
        return WriteOutputAsync(stream, cancellationToken);
    }

    private void CompressInput(ReadOnlySpan<byte> input, Stream stream)
    {
        EnsureBuffers();
        EnsureHeader();

        while (!input.IsEmpty)
        {
            int consumed = CompressInputOnce(input);
            input = input.Slice(consumed);

            if (_outputLength > OutputBufferSize - MaxChunkOutput)
            {
                WriteOutput(stream);
            }
        }
    }

    /// <summary>Consumes input up to the next chunk boundary, compressing a chunk if one completes.</summary>
    private int CompressInputOnce(ReadOnlySpan<byte> input)
    {
        EnsureBuffers();
        EnsureHeader();

        if (_inputLength == 0 && input.Length >= StreamFormat.MaxChunkDataLength)
        {
            // Compress directly from the caller's buffer
            CompressChunk(input.Slice(0, StreamFormat.MaxChunkDataLength));
            return StreamFormat.MaxChunkDataLength;
        }

        int append = Math.Min(input.Length, StreamFormat.MaxChunkDataLength - _inputLength);
        input.Slice(0, append).CopyTo(_input.AsSpan(_inputLength));
        _inputLength += append;

        if (_inputLength == StreamFormat.MaxChunkDataLength)
        {
            CompressChunk(_input.AsSpan(0, _inputLength));
            _inputLength = 0;
        }

        return append;
    }

    private void CompressPending()
    {
        EnsureBuffers();
        EnsureHeader();

        if (_inputLength > 0)
        {
            CompressChunk(_input.AsSpan(0, _inputLength));
            _inputLength = 0;
        }
    }

    private void CompressChunk(ReadOnlySpan<byte> input)
    {
        Span<byte> chunk = _output.AsSpan(_outputLength);
        Span<byte> body = chunk.Slice(ChunkOverhead);

        uint crc = Crc32C.ComputeMasked(input);

        // Cannot fail: the output buffer always has room for a maximum size chunk
        BlockCompressor.TryCompress(input, body, out int compressedLength);

        byte chunkType;
        int bodyLength;
        if (compressedLength < input.Length)
        {
            chunkType = StreamFormat.CompressedData;
            bodyLength = compressedLength;
        }
        else
        {
            // Compression did not help: store the data, overwriting the compressed attempt
            chunkType = StreamFormat.UncompressedData;
            bodyLength = input.Length;
            input.CopyTo(body);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(chunk, (uint)((bodyLength + StreamFormat.ChecksumLength) << 8) | chunkType);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.Slice(StreamFormat.ChunkHeaderLength), crc);

        _outputLength += ChunkOverhead + bodyLength;
    }

    private void WriteOutput(Stream stream)
    {
        if (_outputLength > 0)
        {
            stream.Write(_output!, 0, _outputLength);
            _outputLength = 0;
        }
    }

    private async ValueTask WriteOutputAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (_outputLength > 0)
        {
            int length = _outputLength;
            _outputLength = 0;
            await stream.WriteAsync(_output.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureHeader()
    {
        if (!_headerWritten)
        {
            StreamFormat.StreamHeader.CopyTo(_output.AsSpan(_outputLength));
            _outputLength += StreamFormat.StreamHeader.Length;
            _headerWritten = true;
        }
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_input), nameof(_output))]
    private void EnsureBuffers()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _input ??= ArrayPool<byte>.Shared.Rent(StreamFormat.MaxChunkDataLength);
        _output ??= ArrayPool<byte>.Shared.Rent(OutputBufferSize);
    }

    public void Dispose()
    {
        _disposed = true;

        if (_input is not null)
        {
            ArrayPool<byte>.Shared.Return(_input);
            _input = null;
        }

        if (_output is not null)
        {
            ArrayPool<byte>.Shared.Return(_output);
            _output = null;
        }
    }
}
