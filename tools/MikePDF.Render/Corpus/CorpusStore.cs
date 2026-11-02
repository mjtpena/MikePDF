using System.Security.Cryptography;

namespace MikePDF.Render.Corpus;

/// <summary>A corpus file that is on disk and has been checked against the manifest.</summary>
internal sealed record CorpusItem(CorpusFile Entry, string Path);

/// <summary>Where fetched and generated corpus files live and how they are checked.</summary>
internal static class CorpusStore
{
    public static string PathFor(string corpusDirectory, CorpusFile file) =>
        System.IO.Path.Combine(corpusDirectory, "files", file.Id.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public static IEnumerable<CorpusFile> InTier(CorpusManifest manifest, string tier, string? filter = null) =>
        manifest.Files.Where(f => f.Tiers.Contains(tier, StringComparer.Ordinal) && (filter is null || f.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)));

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The problems with the files of a tier on disk: missing, or not the bytes the manifest names. Empty means every file is exactly as recorded.</summary>
    public static List<string> Verify(CorpusManifest manifest, string tier, string corpusDirectory, string? filter = null)
    {
        var problems = new List<string>();
        foreach (var file in InTier(manifest, tier, filter))
        {
            var path = PathFor(corpusDirectory, file);
            if (!File.Exists(path))
            {
                problems.Add($"{file.Id}: missing (run: mikepdf-render corpus fetch --tier {tier}).");
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            var hash = Sha256Hex(bytes);
            if (hash != file.Sha256 || bytes.LongLength != file.Bytes)
            {
                problems.Add($"{file.Id}: expected sha256 {file.Sha256} ({file.Bytes} bytes), found {hash} ({bytes.Length} bytes).");
            }
        }

        return problems;
    }

    /// <summary>The tier's files, verified. Throws <see cref="InvalidDataException"/> listing everything wrong: a corpus that does not match its manifest must not be rendered.</summary>
    public static List<CorpusItem> Resolve(CorpusManifest manifest, string tier, string corpusDirectory, string? filter = null)
    {
        var problems = Verify(manifest, tier, corpusDirectory, filter);
        if (problems.Count > 0)
        {
            throw new InvalidDataException("The corpus on disk does not match the manifest:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        return [.. InTier(manifest, tier, filter).Select(f => new CorpusItem(f, PathFor(corpusDirectory, f)))];
    }
}
