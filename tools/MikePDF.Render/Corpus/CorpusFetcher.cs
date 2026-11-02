namespace MikePDF.Render.Corpus;

/// <summary>What a fetch did.</summary>
internal sealed record FetchSummary(int Downloaded, int Generated, int AlreadyPresent, IReadOnlyList<string> Failures);

/// <summary>
/// Brings the files of a tier onto disk, exactly as the manifest records them: generated files are made, the others downloaded. Every file is
/// checked against its SHA-256 and size before it is kept, and a mismatch fails the fetch (ADR-0010 decision 3). A file already on disk with
/// the right hash is left alone.
/// </summary>
internal sealed class CorpusFetcher(HttpClient http)
{
    private const int Attempts = 3;

    public async Task<FetchSummary> FetchAsync(CorpusManifest manifest, string tier, string corpusDirectory, string? filter, Action<string>? log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var downloaded = 0;
        var generated = 0;
        var present = 0;
        var failures = new List<string>();
        foreach (var file in CorpusStore.InTier(manifest, tier, filter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = CorpusStore.PathFor(corpusDirectory, file);
            if (File.Exists(path) && Matches(file, File.ReadAllBytes(path)))
            {
                present++;
                continue;
            }

            try
            {
                var bytes = file.Origin == CorpusOrigin.Generated
                    ? CorpusGenerators.Generate(file.Generator ?? "")
                    : await DownloadAsync(file, cancellationToken).ConfigureAwait(false);
                if (!Matches(file, bytes))
                {
                    failures.Add($"{file.Id}: expected sha256 {file.Sha256} ({file.Bytes} bytes), got {CorpusStore.Sha256Hex(bytes)} ({bytes.Length} bytes).{(file.Origin == CorpusOrigin.Generated ? " The generator changed; update the manifest on purpose." : "")}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var partial = path + ".part";
                await File.WriteAllBytesAsync(partial, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(partial, path, overwrite: true);
                if (file.Origin == CorpusOrigin.Generated)
                {
                    generated++;
                }
                else
                {
                    downloaded++;
                }

                log?.Invoke($"{(file.Origin == CorpusOrigin.Generated ? "generated" : "fetched  ")} {file.Id}");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or FormatException or KeyNotFoundException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{file.Id}: {e.Message}");
            }
        }

        return new FetchSummary(downloaded, generated, present, failures);
    }

    private static bool Matches(CorpusFile file, byte[] bytes) => bytes.LongLength == file.Bytes && CorpusStore.Sha256Hex(bytes) == file.Sha256;

    private async Task<byte[]> DownloadAsync(CorpusFile file, CancellationToken cancellationToken)
    {
        var url = file.FetchUrl ?? file.Source;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var body = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                return file.FetchEncoding switch
                {
                    null => body,
                    "base64" => Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(body).Trim()),
                    var other => throw new FormatException($"unknown fetch encoding '{other}'"),
                };
            }
            catch (Exception e) when (attempt < Attempts && e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
