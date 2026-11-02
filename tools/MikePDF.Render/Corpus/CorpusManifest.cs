using System.Text.Json;
using System.Text.Json.Serialization;

namespace MikePDF.Render.Corpus;

/// <summary>
/// <c>tests/corpus/manifest.json</c> (ADR-0010): one record per corpus file with where it comes from, its licence, its SHA-256 and what a correct run
/// does with it. The manifest is the source of truth; the files themselves are fetched or generated and verified against it, never committed.
/// </summary>
internal sealed record CorpusManifest
{
    public const string SchemaId = "mikepdf-corpus-manifest/1";

    public string Schema { get; init; } = SchemaId;

    public string? Description { get; init; }

    public List<CorpusFile> Files { get; init; } = [];

    /// <summary>Sources considered and left out, with the reason (ADR-0010: no source enters a tier until its licence is verified).</summary>
    public List<ExcludedSource> Excluded { get; init; } = [];

    public static CorpusManifest Load(string path)
    {
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize(stream, CorpusJson.Default.CorpusManifest) ?? throw new InvalidDataException("The manifest is empty.");
        return manifest.Schema == SchemaId ? manifest : throw new InvalidDataException($"The manifest has schema '{manifest.Schema}', expected '{SchemaId}'.");
    }

    public string ToJson() => JsonSerializer.Serialize(this, CorpusJson.Default.CorpusManifest);
}

internal static class CorpusOrigin
{
    /// <summary>Downloaded from <see cref="CorpusFile.Source"/> (through <see cref="CorpusFile.FetchUrl"/> when that differs) and checked against the hash.</summary>
    public const string Fetch = "fetch";

    /// <summary>Made by <see cref="CorpusGenerators"/>.</summary>
    public const string Generated = "generated";
}

internal sealed record CorpusFile
{
    /// <summary>Unique, a relative path with forward slashes. The fetched file lives at <c>files/&lt;id&gt;</c> in the corpus directory.</summary>
    public required string Id { get; init; }

    public required string Origin { get; init; }

    /// <summary>For a fetched file, the page it comes from. For a generated one, <c>generated:&lt;generator&gt;</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Where the bytes are actually fetched from when that is not <see cref="Source"/> (a raw-file endpoint).</summary>
    public string? FetchUrl { get; init; }

    /// <summary><c>base64</c> when the endpoint returns the file base64-encoded (Gitiles <c>?format=TEXT</c>); absent when it returns the bytes.</summary>
    public string? FetchEncoding { get; init; }

    public string? Generator { get; init; }

    /// <summary>An SPDX identifier. <c>UNVERIFIED</c> is refused by the manifest check (ADR-0010 verification).</summary>
    public required string Licence { get; init; }

    public bool Redistributable { get; init; }

    /// <summary>Where the licence of this file (not just of its source's repository) was read: a URL or a short statement. Required for files outside the smoke tier.</summary>
    public string? LicenceEvidence { get; init; }

    /// <summary>The result of the per-file licence audit: what was checked, by whom and when, and anything noted. Absent means the file was not audited individually.</summary>
    public string? Audit { get; init; }

    public required string Sha256 { get; init; }

    public long Bytes { get; init; }

    public List<string> Tags { get; init; } = [];

    public List<string> Tiers { get; init; } = [];

    public CorpusExpect Expect { get; init; } = new();

    public CorpusRender? Render { get; init; }
}

internal sealed record CorpusExpect
{
    /// <summary>False for a file that is meant not to open (broken on purpose).</summary>
    public bool Opens { get; init; } = true;

    /// <summary>The status opening ends with: <c>Ok</c>, or <c>Damaged</c>, <c>PasswordRequired</c> ... for a file that is meant to fail.</summary>
    public string Status { get; init; } = "Ok";

    public string? Password { get; init; }

    public uint? Pages { get; init; }

    public bool AllowsEngineCrash { get; init; }

    public bool AllowsPartialRender { get; init; }

    /// <summary>
    /// <c>crash</c> or <c>hang</c>: the file is made to take the engine down (it runs it out of memory, or PDFium spins inside one drawing operation), and a run
    /// must see exactly that, and see the engine replaced. A run that does not is a failure: the file would no longer exercise recovery. Implies <see cref="AllowsEngineCrash"/>.
    /// </summary>
    public string? EngineEvent { get; init; }

    /// <summary>
    /// Set on a real-world file that should open but, when it was added, did not (a PDFium limitation, not a file broken on purpose). It still counts as a file that
    /// should open, so it lowers the open-success rate (§0.3) instead of hiding among the expected failures. The text says what is known.
    /// </summary>
    public string? KnownIssue { get; init; }

    public string? Note { get; init; }
}

internal sealed record CorpusRender
{
    /// <summary>Which pages a run renders (a page spec). Used for files with thousands of pages, where all of them would take too long for a smoke run.</summary>
    public string? Pages { get; init; }

    /// <summary>A tile deadline for this file, in milliseconds, instead of the run's. A file made to hang the engine uses a short one so the gate stays quick.</summary>
    public int? TileDeadlineMs { get; init; }

    /// <summary>False for a file the reference renderer would also hang or crash on: it is rendered through the engine only and has no SSIM.</summary>
    public bool? Reference { get; init; }
}

internal sealed record ExcludedSource
{
    public required string Source { get; init; }

    public string? Licence { get; init; }

    public required string Reason { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(CorpusManifest))]
internal sealed partial class CorpusJson : JsonSerializerContext;
