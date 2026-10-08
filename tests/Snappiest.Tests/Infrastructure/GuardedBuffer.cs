using System.Runtime.InteropServices;

namespace Snappiest.Tests.Infrastructure;

/// <summary>
/// Native memory with inaccessible pages on both sides, placed so the buffer touches one of them. Any read or
/// write outside the buffer on that side crashes the test run, which catches over-reads that would otherwise
/// silently use neighbouring memory.
/// </summary>
internal sealed unsafe partial class GuardedBuffer : IDisposable
{
    private readonly byte* _base;
    private readonly nuint _totalSize;

    public GuardedBuffer(int length, bool alignToEnd = true)
    {
        nuint pageSize = (nuint)Environment.SystemPageSize;
        nuint dataPages = ((nuint)length + pageSize - 1) / pageSize;
        if (dataPages == 0)
        {
            dataPages = 1;
        }

        _totalSize = (dataPages + 2) * pageSize;
        _base = Allocate(_totalSize);
        Protect(_base, pageSize);
        Protect(_base + ((dataPages + 1) * pageSize), pageSize);

        byte* dataStart = _base + pageSize;
        Pointer = alignToEnd ? dataStart + (dataPages * pageSize) - (nuint)length : dataStart;
        Length = length;
    }

    public byte* Pointer { get; }

    public int Length { get; }

    public Span<byte> Span => new(Pointer, Length);

    public static GuardedBuffer From(ReadOnlySpan<byte> data, bool alignToEnd = true)
    {
        var buffer = new GuardedBuffer(data.Length, alignToEnd);
        data.CopyTo(buffer.Span);
        return buffer;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            VirtualFree(_base, 0, MemRelease);
        }
        else
        {
            munmap(_base, _totalSize);
        }
    }

    private static byte* Allocate(nuint size)
    {
        if (OperatingSystem.IsWindows())
        {
            byte* p = VirtualAlloc(null, size, MemCommit | MemReserve, PageReadWrite);
            return p != null ? p : throw new OutOfMemoryException();
        }

        int mapAnonymous = OperatingSystem.IsMacOS() ? 0x1000 : 0x20;
        byte* m = mmap(null, size, ProtRead | ProtWrite, MapPrivate | mapAnonymous, -1, 0);
        return m != (byte*)-1 ? m : throw new OutOfMemoryException();
    }

    private static void Protect(byte* address, nuint size)
    {
        bool ok = OperatingSystem.IsWindows()
            ? VirtualProtect(address, size, PageNoAccess, out _)
            : mprotect(address, size, ProtNone) == 0;
        if (!ok)
        {
            throw new InvalidOperationException("Failed to protect guard page.");
        }
    }

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageNoAccess = 0x01;
    private const uint PageReadWrite = 0x04;

    private const int ProtNone = 0;
    private const int ProtRead = 1;
    private const int ProtWrite = 2;
    private const int MapPrivate = 2;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial byte* VirtualAlloc(void* address, nuint size, uint allocationType, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(void* address, nuint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(void* address, nuint size, uint freeType);

    [LibraryImport("libc", SetLastError = true)]
    private static partial byte* mmap(void* address, nuint length, int prot, int flags, int fd, nint offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mprotect(void* address, nuint length, int prot);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(void* address, nuint length);
}
