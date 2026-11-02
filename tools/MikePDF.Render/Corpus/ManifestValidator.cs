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

    private static readonly string[] EngineEventKinds = ["crash", "hang"];

    /// <summary>§12.2 aims at about 5,000 core files. This is the floor below which the manifest check says the tier has not been built, not the target.</summary>
    public const int CoreMinimumFiles = 300;

    /// <summary>What the core tier must cover (§12.2, brief W2-corpus): scanned, tagged, forms, large page counts, CJK, RTL, transparency, annotations, encryption, broken files.</summary>
    public static readonly string[] CoreCoverage =
    [
        "scanned", "tagged", "form", "huge-page-count", "cjk", "rtl", "transparency", "annotation", "encrypted", "broken-xref", "broken-structure",
    ];

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

            if (file.Tiers.Count > 0 && !file.Tiers.Contains("smoke", StringComparer.Ordinal) && string.IsNullOrWhiteSpace(file.LicenceEvidence))
            {
                problems.Add($"{at}: a file outside the smoke tier needs licenceEvidence (where its licence was read, per file or per source).");
            }

            if (file.Expect.EngineEvent is { } engineEvent && engineEvent is not ("crash" or "hang"))
            {
                problems.Add($"{at}: expect.engineEvent must be 'crash' or 'hang'.");
            }

            if (file.Expect.EngineEvent is not null && !file.Expect.Opens)
            {
                problems.Add($"{at}: a file made to take the engine down must open (the crash or hang comes while rendering).");
            }

            if (file.Expect.KnownIssue is not null && file.Expect.Status == "Ok")
            {
                problems.Add($"{at}: expect.knownIssue is for a file that should open and does not; its status is not Ok.");
            }

            if (file.Render?.TileDeadlineMs is <= 0)
            {
                problems.Add($"{at}: render.tileDeadlineMs must be positive.");
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

            foreach (var kind in EngineEventKinds.Where(kind => !smoke.Any(f => f.Expect.EngineEvent == kind)))
            {
                problems.Add($"The smoke tier has no file that makes the engine {kind} (expect.engineEvent '{kind}'): the gate would not exercise that recovery.");
            }
        }

        var core = manifest.Files.Where(f => f.Tiers.Contains("core", StringComparer.Ordinal)).ToList();
        if (core.Count > 0)
        {
            var inCore = CorpusStore.InTier(manifest, "core").ToList();
            if (inCore.Count < CoreMinimumFiles)
            {
                problems.Add($"The core tier has {inCore.Count} files; at least {CoreMinimumFiles} are expected (ADR-0010, §12.2).");
            }

            foreach (var tag in CoreCoverage.Where(tag => !inCore.Any(f => f.Tags.Contains(tag, StringComparer.Ordinal))))
            {
                problems.Add($"The core tier has no file tagged '{tag}'.");
            }
        }

        return problems;
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();
}
