using System.Text.RegularExpressions;

namespace MikePDF.Render.Corpus;

/// <summary>
/// The manifest check CI runs (ADR-0010 verification): structure, hashes, tiers, the licence rule (nothing UNVERIFIED in any tier), and for the smoke
/// tier the size and coverage the M1 gate asks for.
/// </summary>
internal static partial class ManifestValidator
{
    public static readonly string[] Tiers = ["smoke", "core", "full"];

    /// <summary>Licences ADR-0003 allows in files that are redistributed. Anything else needs an ADR first.</summary>
    public static readonly string[] AllowedLicences = ["BSD-3-Clause", "Apache-2.0", "MIT", "CC0-1.0", "LicenseRef-Public-Domain"];

    public const int SmokeMinimumFiles = 30;

    /// <summary>What the smoke tier must cover (§17 M1, brief W1-render): every one of these tags on at least one file.</summary>
    public static readonly string[] SmokeCoverage =
    [
        "text", "vector", "image", "jbig2", "ccitt", "form", "annotation", "encrypted", "broken-xref", "huge-page-count", "rotated", "cropbox-offset",
    ];

    public static List<string> Validate(CorpusManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var at = $"'{file.Id}'";
            if (string.IsNullOrWhiteSpace(file.Id) || file.Id.StartsWith('/') || file.Id.Contains('\\', StringComparison.Ordinal) || file.Id.Split('/').Any(part => part is "" or "." or ".." || part.Contains(':', StringComparison.Ordinal)))
            {
                problems.Add($"{at}: the id must be a relative path with forward slashes and no '..'.");
            }
            else if (!ids.Add(file.Id))
            {
                problems.Add($"{at}: the id appears twice.");
            }

            if (!Sha256Pattern().IsMatch(file.Sha256))
            {
                problems.Add($"{at}: sha256 must be 64 lower-case hex digits.");
            }

            if (file.Bytes < 0)
            {
                problems.Add($"{at}: bytes cannot be negative.");
            }

            if (string.IsNullOrWhiteSpace(file.Licence) || file.Licence.Contains("UNVERIFIED", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{at}: the licence is not verified (ADR-0010 allows no UNVERIFIED entry in any tier).");
            }
            else if (!AllowedLicences.Contains(file.Licence, StringComparer.Ordinal))
            {
                problems.Add($"{at}: licence '{file.Licence}' is not one ADR-0003 allows ({string.Join(", ", AllowedLicences)}).");
            }

            if (!file.Redistributable && file.Tiers.Count > 0)
            {
                problems.Add($"{at}: a file that may not be redistributed cannot be in a public tier.");
            }

            foreach (var tier in file.Tiers.Where(t => !Tiers.Contains(t, StringComparer.Ordinal)))
            {
                problems.Add($"{at}: unknown tier '{tier}'.");
            }

            switch (file.Origin)
            {
                case CorpusOrigin.Fetch:
                    if (!Uri.TryCreate(file.FetchUrl ?? file.Source, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                    {
                        problems.Add($"{at}: a fetched file needs an https source.");
                    }

                    break;
                case CorpusOrigin.Generated:
                    if (file.Generator is null || !CorpusGenerators.All.ContainsKey(file.Generator))
                    {
                        problems.Add($"{at}: unknown generator '{file.Generator}'.");
                    }

                    break;
                default:
                    problems.Add($"{at}: unknown origin '{file.Origin}'.");
                    break;
            }

            if (file.Expect.Opens != (file.Expect.Status == "Ok"))
            {
                problems.Add($"{at}: expect.opens must be true exactly when expect.status is Ok.");
            }

            if (file.Render?.Pages is { } spec)
            {
                try
                {
                    _ = Cli.PageSpec.Parse(spec);
                }
                catch (FormatException e)
                {
                    problems.Add($"{at}: render.pages: {e.Message}");
                }
            }
        }

        foreach (var excluded in manifest.Excluded.Where(e => string.IsNullOrWhiteSpace(e.Reason)))
        {
            problems.Add($"excluded '{excluded.Source}': say why it is left out.");
        }

        var smoke = manifest.Files.Where(f => f.Tiers.Contains("smoke", StringComparer.Ordinal)).ToList();
        if (smoke.Count > 0)
        {
            if (smoke.Count < SmokeMinimumFiles)
            {
                problems.Add($"The smoke tier has {smoke.Count} files; the M1 gate asks for at least {SmokeMinimumFiles}.");
            }

            foreach (var tag in SmokeCoverage.Where(tag => !smoke.Any(f => f.Tags.Contains(tag, StringComparer.Ordinal))))
            {
                problems.Add($"The smoke tier has no file tagged '{tag}'.");
            }
        }

        return problems;
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();
}
