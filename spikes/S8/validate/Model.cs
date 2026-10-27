using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace S8.Validate;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SigStatus { Valid, ValidWithWarnings, Invalid, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Outcome { Pass, Fail, Warn, Info, NotRun }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeClass { Allowed, AllowedWithWarning, Disallowed }

public sealed record Check(string Id, Outcome Outcome, string Detail);

public sealed record Change(ChangeClass Class, string Description);

public sealed class SignatureReport
{
    public int Index { get; set; }
    public string? FieldName { get; set; }
    public int ObjectNumber { get; set; }
    public bool ReachableFromAcroForm { get; set; }
    public bool IsDocTimeStamp { get; set; }
    public string? SubFilter { get; set; }
    public int[]? ByteRange { get; set; }
    public int SignedRevisionEnd { get; set; }
    public int FileLength { get; set; }
    public int? DocMdpPermission { get; set; }
    public string? SignerSubject { get; set; }
    public string? IssuerSubject { get; set; }
    public string? SignerThumbprint { get; set; }
    public string? DigestAlgorithm { get; set; }
    public string? SignatureAlgorithm { get; set; }
    public string? ClaimedSigningTime { get; set; }
    public string? Reason { get; set; }
    public int ContentsCapacityBytes { get; set; }
    public int CmsLengthBytes { get; set; }
    public string Integrity { get; set; } = "NotEvaluated";   // Intact | Broken | NotEvaluated
    public string Identity { get; set; } = "NotEvaluated";    // Trusted | Untrusted | Expired | Revoked | NotEvaluated
    public string Modifications { get; set; } = "NotEvaluated"; // None | AllowedOnly | Disallowed | Unanalysable | NotEvaluated
    public SigStatus Status { get; set; } = SigStatus.Unknown;
    public string StatusReason { get; set; } = "";
    public List<Check> Checks { get; } = new();
    public List<Change> Changes { get; } = new();
    public double ValidateMs { get; set; }
}

public sealed class FileReport
{
    public string File { get; set; } = "";
    public long Size { get; set; }
    public string? StructureError { get; set; }
    public int Revisions { get; set; }
    public List<string> Notes { get; } = new();
    public List<SignatureReport> Signatures { get; } = new();
}

public sealed class ValidatorOptions
{
    /// <summary>Roots (or intermediates) trusted for this run. Never touches the Windows certificate stores.</summary>
    public X509Certificate2Collection TrustAnchors { get; } = new();
    /// <summary>If true, build against the Windows machine/user trust (read-only) instead of the custom store.</summary>
    public bool UseSystemTrust { get; set; }
    /// <summary>Verification time for chain building (default: now).</summary>
    public DateTime? VerificationTime { get; set; }
    /// <summary>Revocation checking. Default NoCheck: no network access (spike rule).</summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.NoCheck;
    public bool DisableCertificateDownloads { get; set; } = true;
}
