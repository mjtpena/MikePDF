namespace Quire.PdfWriter;

/// <summary>Which <see cref="ParserLimits"/> entry was exceeded.</summary>
public enum PdfLimit
{
    FileSize,
    ObjectCount,
    ObjectNumber,
    NestingDepth,
    StringLength,
    NameLength,
    ArrayLength,
    DictionaryEntries,
    DecompressedStreamBytes,
    DecompressedTotalBytes,
    XrefSections,
    FilterChainLength,
    ObjectStreamObjects,
    TokenLength,
    OutputSize,
}

/// <summary>Machine-readable reason a parse, validation or write operation failed (or, in lenient mode, was recovered from).</summary>
public enum PdfErrorCode
{
    LimitExceeded,
    UnsupportedEncryption,
    UnsupportedFilter,
    UnsupportedFeature,
    InvalidHeader,
    InvalidSyntax,
    InvalidNumber,
    InvalidName,
    InvalidString,
    InvalidReference,
    DuplicateKey,
    UnexpectedEndOfData,
    MissingStartXref,
    InvalidStartXref,
    MissingEof,
    TrailingData,
    InvalidXref,
    InvalidXrefEntry,
    DuplicateXrefEntry,
    XrefCycle,
    XrefOffsetOutOfRange,
    HybridConflict,
    InvalidTrailer,
    ObjectHeaderMismatch,
    MissingEndObj,
    StreamLengthMismatch,
    InvalidObjectStream,
    InvalidStream,
    InvalidUpdate,
}

/// <summary>A typed failure. <see cref="Offset"/> is a file (or decoded-stream) offset, or -1 when not applicable.</summary>
public sealed record PdfError(PdfErrorCode Code, string Message, long Offset = -1, PdfLimit? Limit = null)
{
    public override string ToString() =>
        Offset >= 0 ? $"{Code} at {Offset}: {Message}" : $"{Code}: {Message}";
}

/// <summary>
/// Outcome of an operation that can fail on untrusted input. Public parsing APIs never throw for bad input;
/// they return one of these.
/// </summary>
public readonly struct PdfResult<T> : IEquatable<PdfResult<T>>
{
    private readonly T? _value;
    private readonly PdfError? _error;

    internal PdfResult(T value)
    {
        _value = value;
        _error = null;
    }

    internal PdfResult(PdfError error)
    {
        _value = default;
        _error = error;
    }

    public bool IsSuccess => _error is null;

    /// <summary>The value; throws <see cref="InvalidOperationException"/> when the result is a failure.</summary>
    public T Value => _error is null
        ? _value!
        : throw new InvalidOperationException("Result is a failure: " + _error);

    /// <summary>The error; throws <see cref="InvalidOperationException"/> when the result is a success.</summary>
    public PdfError Error => _error ?? throw new InvalidOperationException("Result is a success.");

    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value)
    {
        value = _value;
        return _error is null;
    }

    public bool TryGetError([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PdfError? error)
    {
        error = _error;
        return _error is not null;
    }

    public bool Equals(PdfResult<T> other) =>
        EqualityComparer<T>.Default.Equals(_value, other._value) && Equals(_error, other._error);

    public override bool Equals(object? obj) => obj is PdfResult<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_value, _error);

    public static bool operator ==(PdfResult<T> left, PdfResult<T> right) => left.Equals(right);

    public static bool operator !=(PdfResult<T> left, PdfResult<T> right) => !left.Equals(right);
}

/// <summary>Factory helpers for <see cref="PdfResult{T}"/>.</summary>
public static class PdfResult
{
    public static PdfResult<T> Ok<T>(T value) => new(value);

    public static PdfResult<T> Fail<T>(PdfError error) => new(error);
}

/// <summary>Internal control-flow carrier. Always converted to a <see cref="PdfResult{T}"/> at the public API boundary.</summary>
internal sealed class PdfParseException(PdfError error) : Exception(error.ToString())
{
    public PdfError Error { get; } = error;
}
