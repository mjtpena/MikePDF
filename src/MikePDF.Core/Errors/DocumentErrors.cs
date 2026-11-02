using MikePDF.Protocol;

namespace MikePDF.Core.Errors;

/// <summary>What the person sees when something goes wrong (master prompt 6.4). The text lives in the .resw files; this is the key and the actions.</summary>
public enum DocumentErrorKind
{
    None,
    PasswordRequired,
    PasswordIncorrect,
    UnsupportedEncryption,
    Damaged,
    Timeout,
    EngineCrashed,
    PermissionDenied,
    FileNotFound,
    FileInUse,
    Unsupported,
    SandboxUnavailable,
    Unknown,
}

/// <summary>How the UI handles an error: which message, and what it can offer.</summary>
public sealed record DocumentErrorInfo(DocumentErrorKind Kind, string ResourceKey, bool CanRetry, bool BlocksDocument)
{
    public static DocumentErrorInfo For(DocumentErrorKind kind) => kind switch
    {
        DocumentErrorKind.PasswordRequired => new(kind, "Error_PasswordRequired", CanRetry: true, BlocksDocument: true),
        DocumentErrorKind.PasswordIncorrect => new(kind, "Error_PasswordIncorrect", CanRetry: true, BlocksDocument: true),
        DocumentErrorKind.UnsupportedEncryption => new(kind, "Error_UnsupportedEncryption", CanRetry: false, BlocksDocument: true),
        DocumentErrorKind.Damaged => new(kind, "Error_Damaged", CanRetry: false, BlocksDocument: true),
        DocumentErrorKind.Timeout => new(kind, "Error_Timeout", CanRetry: true, BlocksDocument: false),
        DocumentErrorKind.EngineCrashed => new(kind, "Error_EngineCrashed", CanRetry: true, BlocksDocument: false),
        DocumentErrorKind.PermissionDenied => new(kind, "Error_PermissionDenied", CanRetry: false, BlocksDocument: false),
        DocumentErrorKind.FileNotFound => new(kind, "Error_FileNotFound", CanRetry: false, BlocksDocument: true),
        DocumentErrorKind.FileInUse => new(kind, "Error_FileInUse", CanRetry: true, BlocksDocument: true),
        DocumentErrorKind.Unsupported => new(kind, "Error_Unsupported", CanRetry: false, BlocksDocument: true),
        DocumentErrorKind.SandboxUnavailable => new(kind, "Error_SandboxUnavailable", CanRetry: true, BlocksDocument: true),
        _ => new(DocumentErrorKind.Unknown, "Error_Unknown", CanRetry: true, BlocksDocument: true),
    };

    /// <summary>Maps an engine status to the error shown. <c>Ok</c> and <c>PartiallyRendered</c> are not errors (the latter is a small warning chip).</summary>
    public static DocumentErrorKind FromStatus(Status status) => status switch
    {
        Status.Ok or Status.PartiallyRendered or Status.Cancelled => DocumentErrorKind.None,
        Status.PasswordRequired => DocumentErrorKind.PasswordRequired,
        Status.PasswordIncorrect => DocumentErrorKind.PasswordIncorrect,
        Status.UnsupportedEncryption => DocumentErrorKind.UnsupportedEncryption,
        Status.Damaged => DocumentErrorKind.Damaged,
        Status.Timeout => DocumentErrorKind.Timeout,
        Status.EngineCrashed => DocumentErrorKind.EngineCrashed,
        Status.PermissionDenied => DocumentErrorKind.PermissionDenied,
        Status.Unsupported => DocumentErrorKind.Unsupported,
        Status.InvalidArgument or Status.ProtocolError => DocumentErrorKind.Unknown,
        _ => DocumentErrorKind.Unknown,
    };
}

/// <summary>The retry limit of the password dialog (master prompt 6.4: "retry, with limit").</summary>
public sealed class PasswordAttempts(int limit = 5)
{
    public int Limit { get; } = limit;

    public int Used { get; private set; }

    public bool Exhausted => Used >= Limit;

    public int Remaining => Math.Max(0, Limit - Used);

    public void Record() => Used++;

    public void Reset() => Used = 0;
}
