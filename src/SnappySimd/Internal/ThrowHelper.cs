using System.Diagnostics.CodeAnalysis;

namespace SnappySimd.Internal;

internal static class ThrowHelper
{
    [DoesNotReturn]
    public static void ThrowArgumentException(string? message, string? paramName) =>
        throw new ArgumentException(message, paramName);

    [DoesNotReturn]
    public static void ThrowArgumentExceptionInsufficientOutputBuffer(string? paramName) =>
        throw new ArgumentException("Output buffer is too small.", paramName);

    [DoesNotReturn]
    public static void ThrowArgumentOutOfRangeException(string? paramName, string? message) =>
        throw new ArgumentOutOfRangeException(paramName, message);

    [DoesNotReturn]
    public static void ThrowInvalidDataException(string? message) =>
        throw new InvalidDataException(message);

    [DoesNotReturn]
    public static void ThrowInvalidDataExceptionInvalidLength() =>
        throw new InvalidDataException("Invalid stream length");

    [DoesNotReturn]
    public static void ThrowInvalidDataExceptionCorruptBlock() =>
        throw new InvalidDataException("Invalid Snappy block.");

    [DoesNotReturn]
    public static void ThrowInvalidOperationException(string? message = null) =>
        throw new InvalidOperationException(message);

    [DoesNotReturn]
    public static void ThrowNotSupportedException(string? message = null) =>
        throw new NotSupportedException(message);
}
