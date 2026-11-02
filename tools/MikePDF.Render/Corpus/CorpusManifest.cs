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

    public string? Note { get; init; }
}

internal sealed record CorpusRender
{
    /// <summary>Which pages a run renders (a page spec). Used for files with thousands of pages, where all of them would take too long for a smoke run.</summary>
    public string? Pages { get; init; }
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
