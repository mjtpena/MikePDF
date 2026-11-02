namespace Quire.Core.Reading;

/// <summary>
/// The address behind an external link, sorted for the person who clicked it. Quire does not follow external links: it makes no network
/// connection and starts no other program (the privacy tests forbid the APIs). The window shows <see cref="Text"/>, the whole address as the
/// file has it, says what kind of address it is, and offers to copy it. <see cref="IsWebOrMail"/> picks the wording.
/// </summary>
public sealed record ExternalAddress(string Text, string Scheme, string Host, string? AsciiHost, bool IsWebOrMail)
{
    /// <summary>Addresses that are a web page or a new mail: the only kinds the window describes as such. Everything else (a file, a program, a script, a custom scheme) is described as "not a web address".</summary>
    public static IReadOnlyList<string> WebAndMailSchemes { get; } = ["http", "https", "mailto"];

    /// <summary>Longer than this, an address is shown but not described as a web address.</summary>
    public const int MaxDescribedLength = 2048;

    public static ExternalAddress Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Length > MaxDescribedLength || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return new ExternalAddress(text, string.Empty, string.Empty, null, false);
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var isWeb = scheme is "http" or "https";
        var describable = WebAndMailSchemes.Contains(scheme) && (!isWeb || uri.Host.Length > 0);
        // A host written in non-Latin letters is shown in its ASCII form as well: look-alike letters are how addresses deceive.
        var ascii = isWeb && !string.Equals(uri.Host, uri.IdnHost, StringComparison.OrdinalIgnoreCase) ? uri.IdnHost : null;
        return new ExternalAddress(text, scheme, isWeb ? uri.Host : string.Empty, ascii, describable);
    }
}
