using System.Buffers;

namespace Snappiest.Internal;

/// <summary>
/// An <see cref="IMemoryOwner{T}"/> over an array rented from <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
internal sealed class PooledMemoryOwner(byte[] buffer, int length) : IMemoryOwner<byte>
{
    private byte[]? _buffer = buffer;

    public Memory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            return _buffer.AsMemory(0, length);
        }
    }

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
