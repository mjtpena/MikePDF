using MikePDF.Render.Corpus;
using MikePDF.Render.Engine;

namespace MikePDF.Render.Runner;

/// <summary><c>mikepdf-render corpus (fetch | verify | check)</c>: the manifest tooling of ADR-0010.</summary>
internal static class CorpusCommand
{
    /// <summary>A client with a User-Agent that says who is asking (Wikimedia and arXiv ask for one) and time enough for the larger files of the core tier.</summary>
    internal static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MikePDF-corpus/1.0 (+https://github.com/mjtpena/MikePDF; test corpus fetch, hash-pinned)");
        return client;
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, HttpClient? http, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args[0] is not ("fetch" or "verify" or "check" or "generate"))
        {
            await error.WriteLineAsync("usage: mikepdf-render corpus (fetch | verify | check) [--tier smoke] [--manifest <file>] [--corpus-dir <dir>] [--filter <text>]").ConfigureAwait(false);
            await error.WriteLineAsync("       mikepdf-render corpus generate <directory>   (writes every generated file and prints its sha256 and size, for the manifest)").ConfigureAwait(false);
            return RenderCommand.CouldNotRun;
        }

        if (args[0] == "generate")
        {
            var target = args.Count > 1 ? Path.GetFullPath(args[1]) : throw new FormatException("corpus generate needs a directory.");
            Directory.CreateDirectory(target);
            foreach (var (name, generate) in CorpusGenerators.All.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var bytes = generate();
                await File.WriteAllBytesAsync(Path.Combine(target, name + ".pdf"), bytes, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"{name}\t{CorpusStore.Sha256Hex(bytes)}\t{bytes.Length}").ConfigureAwait(false);
            }

            return RenderCommand.Passed;
        }

        string tier = "smoke";
        string? manifestPath = null, corpusDirectory = null, filter = null;
        for (var i = 1; i < args.Count; i++)
        {
            string Next() => ++i < args.Count ? args[i] : throw new FormatException(args[i - 1] + " needs a value.");
            switch (args[i])
            {
                case "--tier": tier = Next(); break;
                case "--manifest": manifestPath = Next(); break;
                case "--corpus-dir": corpusDirectory = Next(); break;
                case "--filter": filter = Next(); break;
                default:
                    await error.WriteLineAsync($"Unknown option {args[i]}.").ConfigureAwait(false);
                    return RenderCommand.CouldNotRun;
            }
        }

        CorpusManifest manifest;
        try
        {
            manifest = CorpusManifest.Load(manifestPath ?? Locations.DefaultManifest());
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            await error.WriteLineAsync("Cannot read the manifest: " + e.Message).ConfigureAwait(false);
            return RenderCommand.CouldNotRun;
        }

        var problems = ManifestValidator.Validate(manifest);
        foreach (var problem in problems)
        {
            await error.WriteLineAsync("manifest: " + problem).ConfigureAwait(false);
        }

        if (args[0] == "check")
        {
            await output.WriteLineAsync(problems.Count == 0 ? $"The manifest is valid: {manifest.Files.Count} files, {manifest.Excluded.Count} sources left out." : $"{problems.Count} problem(s).").ConfigureAwait(false);
            return problems.Count == 0 ? RenderCommand.Passed : RenderCommand.GateFailed;
        }

        if (problems.Count > 0)
        {
            return RenderCommand.GateFailed;
        }

        var directory = Path.GetFullPath(corpusDirectory ?? Locations.DefaultCorpusDirectory());
        if (args[0] == "verify")
        {
            var mismatches = CorpusStore.Verify(manifest, tier, directory, filter);
            foreach (var mismatch in mismatches)
            {
                await error.WriteLineAsync(mismatch).ConfigureAwait(false);
            }

            await output.WriteLineAsync(mismatches.Count == 0 ? $"Every file of the {tier} tier is present and matches its hash." : $"{mismatches.Count} file(s) missing or different.").ConfigureAwait(false);
            return mismatches.Count == 0 ? RenderCommand.Passed : RenderCommand.GateFailed;
        }

        var client = http ?? CreateClient();
        try
        {
            var summary = await new CorpusFetcher(client).FetchAsync(manifest, tier, directory, filter, line => output.WriteLine(line), cancellationToken).ConfigureAwait(false);
            foreach (var failure in summary.Failures)
            {
                await error.WriteLineAsync("fetch: " + failure).ConfigureAwait(false);
            }

            await output.WriteLineAsync($"{tier} tier: {summary.Downloaded} downloaded, {summary.Generated} generated, {summary.AlreadyPresent} already present, {summary.Failures.Count} failed. Directory: {directory}").ConfigureAwait(false);
            return summary.Failures.Count == 0 ? RenderCommand.Passed : RenderCommand.GateFailed;
        }
        finally
        {
            if (http is null)
            {
                client.Dispose();
            }
        }
    }
}
