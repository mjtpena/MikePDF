using System.Collections.Concurrent;

namespace MikePDF.Render.Corpus;

/// <summary>What a fetch did.</summary>
internal sealed record FetchSummary(int Downloaded, int Generated, int AlreadyPresent, IReadOnlyList<string> Failures);

/// <summary>
/// Brings the files of a tier onto disk, exactly as the manifest records them: generated files are made, the others downloaded. Every file is
/// checked against its SHA-256 and size before it is kept, and a mismatch fails the fetch (ADR-0010 decision 3). A file already on disk with
/// the right hash is left alone.
/// </summary>
/// <param name="http">The client downloads go through.</param>
/// <param name="retryDelay">The wait before a retry; null for the default (2 s, 4 s, ... or 6 s, 12 s, ... when the server says it is being asked too often).</param>
internal sealed class CorpusFetcher(HttpClient http, TimeSpan? retryDelay = null)
{
    private const int Attempts = 6;
    private const int Parallelism = 6;

    public async Task<FetchSummary> FetchAsync(CorpusManifest manifest, string tier, string corpusDirectory, string? filter, Action<string>? log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var downloaded = 0;
        var generated = 0;
        var present = 0;
        var failures = new ConcurrentQueue<string>();
        var files = CorpusStore.InTier(manifest, tier, filter).ToList();
        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                var path = CorpusStore.PathFor(corpusDirectory, file);
                if (File.Exists(path) && Matches(file, await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)))
                {
                    Interlocked.Increment(ref present);
                    return;
                }

                try
                {
                    var bytes = file.Origin == CorpusOrigin.Generated
                        ? CorpusGenerators.Generate(file.Generator ?? "")
                        : await DownloadAsync(file, token).ConfigureAwait(false);
                    if (!Matches(file, bytes))
                    {
                        failures.Enqueue($"{file.Id}: expected sha256 {file.Sha256} ({file.Bytes} bytes), got {CorpusStore.Sha256Hex(bytes)} ({bytes.Length} bytes).{(file.Origin == CorpusOrigin.Generated ? " The generator changed; update the manifest on purpose." : "")}");
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var partial = path + "." + Guid.NewGuid().ToString("N")[..6] + ".part";
                    await File.WriteAllBytesAsync(partial, bytes, token).ConfigureAwait(false);
                    File.Move(partial, path, overwrite: true);
                    if (file.Origin == CorpusOrigin.Generated)
                    {
                        Interlocked.Increment(ref generated);
                    }
                    else
                    {
                        Interlocked.Increment(ref downloaded);
                    }

                    log?.Invoke($"{(file.Origin == CorpusOrigin.Generated ? "generated" : "fetched  ")} {file.Id}");
                }
                catch (Exception e) when ((e is HttpRequestException or IOException or FormatException or KeyNotFoundException or TaskCanceledException) && !token.IsCancellationRequested)
                {
                    failures.Enqueue($"{file.Id}: {e.Message}");
                }
            }).ConfigureAwait(false);

        return new FetchSummary(downloaded, generated, present, [.. failures.Order(StringComparer.Ordinal)]);
    }

    /// <summary>A network error, a timeout, a server error or throttling may pass; a 404 will not.</summary>
    private static bool IsWorthRetrying(Exception e) => e switch
    {
        HttpRequestException http => http.StatusCode is null or >= System.Net.HttpStatusCode.InternalServerError or System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.RequestTimeout,
        TaskCanceledException => true,
        _ => false,
    };

    private static bool Matches(CorpusFile file, byte[] bytes) => bytes.LongLength == file.Bytes && CorpusStore.Sha256Hex(bytes) == file.Sha256;

    /// <summary>
    /// Hosts that limit how fast a client may ask. Requests to them are made one at a time, with at least this long between one starting and the next:
    /// arXiv wants 3 seconds (https://info.arxiv.org/help/api/tou.html); Gitiles answered 429 to a burst (six parallel requests: 171 of 306 refused); Wikimedia
    /// answered 429 "your bot is making too many requests" to six parallel downloads of 1 to 6 MB files.
    /// </summary>
    private static readonly Dictionary<string, TimeSpan> SlowHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arxiv.org"] = TimeSpan.FromSeconds(3),
        ["export.arxiv.org"] = TimeSpan.FromSeconds(3),
        ["pdfium.googlesource.com"] = TimeSpan.FromMilliseconds(300),
        ["upload.wikimedia.org"] = TimeSpan.FromMilliseconds(600),
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostTurns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastRequest = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Waits for this host's turn when it is one of <see cref="SlowHosts"/>; returns the gate to release when the request is over, or null for any other host.</summary>
    private async Task<SemaphoreSlim?> EnterHostAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !SlowHosts.TryGetValue(uri.Host, out var interval))
        {
            return null;
        }

        var turn = _hostTurns.GetOrAdd(uri.Host, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequest.TryGetValue(uri.Host, out var last) && DateTime.UtcNow - last is var since && since < interval)
            {
                await Task.Delay(interval - since, cancellationToken).ConfigureAwait(false);
            }

            // A host that answered 429 asked everyone to stop for a while: wait out what it asked for before the next request, not only before the retry of the one that was refused.
            if (_cooldownUntil.TryGetValue(uri.Host, out var until) && until > DateTime.UtcNow)
            {
                await Task.Delay(until - DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            }

            _lastRequest[uri.Host] = DateTime.UtcNow;
            return turn;
        }
        catch
        {
            turn.Release();
            throw;
        }
    }

    private readonly ConcurrentDictionary<string, DateTime> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records that a slow host refused for being asked too often: it is left alone for what its Retry-After says, or 30 s, and at most 3 minutes.</summary>
    private void NoteThrottled(string url, HttpResponseMessage response)
    {
        if (response.StatusCode is not (System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !SlowHosts.ContainsKey(uri.Host))
        {
            return;
        }

        var asked = response.Headers.RetryAfter is { } retry ? retry.Delta ?? (retry.Date - DateTimeOffset.UtcNow) : null;
        var wait = TimeSpan.FromSeconds(Math.Clamp((asked ?? TimeSpan.FromSeconds(30)).TotalSeconds, 5, 180));
        _cooldownUntil[uri.Host] = DateTime.UtcNow + wait;
    }

    private async Task<byte[]> DownloadAsync(CorpusFile file, CancellationToken cancellationToken)
    {
        var url = file.FetchUrl ?? file.Source;
        for (var attempt = 1; ; attempt++)
        {
            SemaphoreSlim? gate = null;
            try
            {
                gate = await EnterHostAsync(url, cancellationToken).ConfigureAwait(false);
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    NoteThrottled(url, response);
                    response.EnsureSuccessStatusCode();
                }

                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return file.FetchEncoding switch
                {
                    null => body,
                    "base64" => Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(body).Trim()),
                    var other => throw new FormatException($"unknown fetch encoding '{other}'"),
                };
            }
            catch (Exception e) when (attempt < Attempts && IsWorthRetrying(e) && !cancellationToken.IsCancellationRequested)
            {
                gate?.Release();
                gate = null;
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } ? attempt * 6 : attempt * 2), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate?.Release();
            }
        }
    }
}
