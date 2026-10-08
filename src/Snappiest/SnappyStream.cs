using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Snappiest.Internal;

namespace Snappiest;

/// <summary>
/// Stream which supports compressing or decompressing data using the Snappy compression algorithm.
/// To decompress data, supply a stream to be read. To compress data, provide a stream to be written to.
/// </summary>
/// <remarks>
/// <para>
/// This class uses the Snappy framing format, which differs from the raw blocks handled by <see cref="Snappy"/>.
/// See https://github.com/google/snappy/blob/main/framing_format.txt.
/// </para>
/// <para>
/// The constructors and members match <c>Snappier.SnappyStream</c>.
/// </para>
/// </remarks>
public sealed class SnappyStream : Stream
{
    private Stream? _stream;
    private readonly CompressionMode _mode;
    private readonly bool _leaveOpen;

    private SnappyStreamDecompressor? _decompressor;
    private SnappyStreamCompressor? _compressor;

    private bool _wroteBytes;
    private int _activeAsyncOperation;

    /// <summary>
    /// Create a stream which supports compressing or decompressing data using the Snappy compression algorithm.
    /// To decompress data, supply a stream to be read. To compress data, provide a stream to be written to.
    /// </summary>
    /// <param name="stream">Source or destination stream.</param>
    /// <param name="mode">Compression or decompression mode.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException">Stream read/write capability doesn't match with <paramref name="mode"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Invalid <paramref name="mode"/>.</exception>
    /// <remarks>
    /// The stream will be closed when this stream is disposed.
    /// </remarks>
    public SnappyStream(Stream stream, CompressionMode mode)
        : this(stream, mode, false)
    {
    }

    /// <summary>
    /// Create a stream which supports compressing or decompressing data using the Snappy compression algorithm.
    /// To decompress data, supply a stream to be read. To compress data, provide a stream to be written to.
    /// </summary>
    /// <param name="stream">Source or destination stream.</param>
    /// <param name="mode">Compression or decompression mode.</param>
    /// <param name="leaveOpen">If true, close the stream when this stream is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException">Stream read/write capability doesn't match with <paramref name="mode"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Invalid <paramref name="mode"/>.</exception>
    public SnappyStream(Stream stream, CompressionMode mode, bool leaveOpen)
        : this(parallel: null, stream, mode, leaveOpen)
    {
    }

    /// <summary>
    /// Create a stream which compresses or decompresses using several threads. Batches of 64KB chunks are processed
    /// concurrently; the compressed stream is identical to the single-threaded one.
    /// </summary>
    /// <param name="stream">Source or destination stream.</param>
    /// <param name="mode">Compression or decompression mode.</param>
    /// <param name="leaveOpen">If true, leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <param name="options">Threading options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Stream read/write capability doesn't match with <paramref name="mode"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Invalid <paramref name="mode"/>.</exception>
    /// <remarks>
    /// Buffers grow with the number of threads: about 900KB per thread when compressing (a batch of input and two
    /// batches of output, so one is written while the next is compressed) and 600KB per thread when decompressing
    /// (two batches of input and two sets of decoded chunks, one served while the next is decoded).
    /// </remarks>
    public SnappyStream(Stream stream, CompressionMode mode, bool leaveOpen, SnappyParallelOptions options)
        : this(options ?? throw new ArgumentNullException(nameof(options)), stream, mode, leaveOpen)
    {
    }

    private SnappyStream(SnappyParallelOptions? parallel, Stream stream, CompressionMode mode, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _mode = mode;
        _leaveOpen = leaveOpen;

        switch (mode)
        {
            case CompressionMode.Decompress:
                if (!stream.CanRead)
                {
                    ThrowHelper.ThrowArgumentException("Unreadable stream", nameof(stream));
                }

                _decompressor = new SnappyStreamDecompressor(parallel);
                break;

            case CompressionMode.Compress:
                if (!stream.CanWrite)
                {
                    ThrowHelper.ThrowArgumentException("Unwritable stream", nameof(stream));
                }

                _compressor = new SnappyStreamCompressor(parallel);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), "Invalid mode");
        }
    }

    /// <summary>
    /// The base stream being read from or written to.
    /// </summary>
    public Stream BaseStream
    {
        get
        {
            EnsureNotDisposed();
            return _stream;
        }
    }

    /// <inheritdoc />
    public override bool CanRead => _mode == CompressionMode.Decompress && (_stream?.CanRead ?? false);

    /// <inheritdoc />
    public override bool CanWrite => _mode == CompressionMode.Compress && (_stream?.CanWrite ?? false);

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Writes any buffered data as a compressed chunk, then flushes the base stream.
    /// </summary>
    public override void Flush()
    {
        EnsureNotDisposed();

        if (_mode == CompressionMode.Compress && _wroteBytes)
        {
            _compressor!.Flush(_stream);
            _stream.Flush();
        }
    }

    /// <summary>
    /// Writes any buffered data as a compressed chunk, then flushes the base stream.
    /// </summary>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        // Claim the stream even when there is nothing to flush, so a flush during a pending write is rejected
        AsyncOperationStarting();
        if (_mode == CompressionMode.Compress && _wroteBytes)
        {
            return FlushAsyncCore(cancellationToken);
        }

        AsyncOperationCompleting();
        return Task.CompletedTask;
    }

    // Called after AsyncOperationStarting
    private async Task FlushAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            await _compressor!.FlushAsync(_stream!, cancellationToken).ConfigureAwait(false);
            await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AsyncOperationCompleting();
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadCore(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => ReadCore(buffer);

    /// <inheritdoc />
    public override int ReadByte()
    {
        byte b = 0;
        int read = ReadCore(MemoryMarshal.CreateSpan(ref b, 1));
        return read == 0 ? -1 : b;
    }

    private int ReadCore(Span<byte> buffer)
    {
        EnsureDecompressionMode();
        EnsureNotDisposed();

        if (buffer.IsEmpty)
        {
            return 0;
        }

        SnappyStreamDecompressor decompressor = _decompressor!;
        while (true)
        {
            int read = decompressor.Read(buffer);
            if (read > 0)
            {
                return read;
            }

            int bytes = _stream.Read(decompressor.GetInputBuffer().Span);
            if (bytes <= 0)
            {
                decompressor.Complete();
                return 0;
            }

            decompressor.CommitInput(bytes);
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureDecompressionMode();
        EnsureNotDisposed();

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        if (buffer.IsEmpty)
        {
            return ValueTask.FromResult(0);
        }

        AsyncOperationStarting();

        // Complete synchronously when buffered input already holds data
        int read;
        try
        {
            read = _decompressor!.Read(buffer.Span);
        }
        catch
        {
            AsyncOperationCompleting();
            throw;
        }

        if (read > 0)
        {
            AsyncOperationCompleting();
            return ValueTask.FromResult(read);
        }

        return ReadAsyncCore(buffer, cancellationToken);
    }

    // Called after AsyncOperationStarting
    private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            SnappyStreamDecompressor decompressor = _decompressor!;
            while (true)
            {
                int bytes = await _stream!.ReadAsync(decompressor.GetInputBuffer(), cancellationToken).ConfigureAwait(false);
                EnsureNotDisposed();

                if (bytes <= 0)
                {
                    decompressor.Complete();
                    return 0;
                }

                decompressor.CommitInput(bytes);

                int read = decompressor.Read(buffer.Span);
                if (read > 0)
                {
                    return read;
                }
            }
        }
        finally
        {
            AsyncOperationCompleting();
        }
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        WriteCore(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => WriteCore(buffer);

    /// <inheritdoc />
    public override void WriteByte(byte value) => WriteCore(MemoryMarshal.CreateReadOnlySpan(ref value, 1));

    private void WriteCore(ReadOnlySpan<byte> buffer)
    {
        EnsureCompressionMode();
        EnsureNotDisposed();

        _compressor!.Write(buffer, _stream);
        _wroteBytes = true;
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureCompressionMode();
        EnsureNotDisposed();

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        AsyncOperationStarting();
        return WriteAsyncCore(buffer, cancellationToken);
    }

    // Called after AsyncOperationStarting
    private async ValueTask WriteAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await _compressor!.WriteAsync(buffer, _stream!, cancellationToken).ConfigureAwait(false);
            _wroteBytes = true;
        }
        finally
        {
            AsyncOperationCompleting();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && _stream is not null && _mode == CompressionMode.Compress && _wroteBytes)
            {
                Flush();
            }
        }
        finally
        {
            try
            {
                if (disposing && !_leaveOpen)
                {
                    _stream?.Dispose();
                }
            }
            finally
            {
                _stream = null;
                ReleaseCodec();
                base.Dispose(disposing);
            }
        }
    }

    /// <inheritdoc />
#pragma warning disable CA2215 // base.DisposeAsync would only call Dispose() again; the cleanup is done here
    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (_stream is not null && _mode == CompressionMode.Compress && _wroteBytes)
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Stream? stream = _stream;
            _stream = null;
            try
            {
                if (!_leaveOpen && stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                ReleaseCodec();
                GC.SuppressFinalize(this);
            }
        }
    }
#pragma warning restore CA2215

    private void ReleaseCodec()
    {
        if (_activeAsyncOperation == 0)
        {
            _decompressor?.Dispose();
            _compressor?.Dispose();
        }

        _decompressor = null;
        _compressor = null;
    }

    [MemberNotNull(nameof(_stream))]
    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_stream is null, this);

    private void EnsureDecompressionMode()
    {
        if (_mode != CompressionMode.Decompress)
        {
            ThrowHelper.ThrowNotSupportedException();
        }
    }

    private void EnsureCompressionMode()
    {
        if (_mode != CompressionMode.Compress)
        {
            ThrowHelper.ThrowNotSupportedException();
        }
    }

    private void AsyncOperationStarting()
    {
        if (Interlocked.CompareExchange(ref _activeAsyncOperation, 1, 0) != 0)
        {
            ThrowHelper.ThrowInvalidOperationException("Invalid begin call");
        }
    }

    private void AsyncOperationCompleting() => Interlocked.Exchange(ref _activeAsyncOperation, 0);
}
