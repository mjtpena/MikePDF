using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>
/// Randomised robustness pass (fixed seed): mutated files must never raise an unhandled exception, never exceed a limit,
/// and must keep the strict/lenient/writer invariants. Failing inputs are written to the temp directory for replay.
/// </summary>
public sealed class MutationTests
{
    // CI runs the fixed defaults. Set MIKEPDF_MUTATION_ITERATIONS / MIKEPDF_MUTATION_SEED for a longer local soak.
    private static readonly int Iterations = int.TryParse(Environment.GetEnvironmentVariable("MIKEPDF_MUTATION_ITERATIONS"), out int n) ? n : 20_000;
    private static readonly int Seed = int.TryParse(Environment.GetEnvironmentVariable("MIKEPDF_MUTATION_SEED"), out int s) ? s : 20261028;

    // Tight enough that the limit paths are exercised constantly, loose enough that every unmutated seed passes.
    private static readonly ParserLimits Limits = new()
    {
        MaxFileSize = 4 << 20,
        MaxObjectCount = 20_000,
        MaxObjectNumber = 50_000,
        MaxNestingDepth = 32,
        MaxStringLength = 1 << 16,
        MaxNameLength = 256,
        MaxArrayLength = 10_000,
        MaxDictionaryEntries = 1_000,
        MaxDecompressedStreamBytes = 1 << 20,
        MaxDecompressedTotalBytes = 4 << 20,
        MaxXrefSections = 16,
        MaxObjectStreamObjects = 5_000,
    };

    private static readonly PdfErrorCode[] AllowedWriterFailures = [PdfErrorCode.InvalidTrailer, PdfErrorCode.LimitExceeded];

    [Fact]
    public void SeedCorpusIsHealthy()
    {
        var seeds = Seeds.Build();
        Assert.True(seeds.Count >= 20);
        foreach (var (name, bytes) in seeds)
        {
            var strict = StrictDocument.Open(bytes, Limits);
            if (name.StartsWith("bug_", StringComparison.Ordinal) || name.StartsWith("enc", StringComparison.Ordinal) || name.StartsWith("hello_world.pdf", StringComparison.Ordinal))
                continue;
            Assert.True(strict.IsSuccess, name + ": " + (strict.IsSuccess ? "" : strict.Error));
        }
    }

    [Fact]
    public void RandomMutationsNeverEscapeTheResultTypeOrTheLimits()
    {
        var seeds = Seeds.Build();
        var rng = new Random(Seed);
        int strictOk = 0, strictFail = 0, updated = 0, limitErrors = 0;

        for (int i = 0; i < Iterations; i++)
        {
            var (name, seed) = seeds[rng.Next(seeds.Count)];
            byte[] mutant = Mutator.Mutate(rng, seed);
            try
            {
                switch (Exercise(mutant, rng))
                {
                    case Outcome.StrictRejected: strictFail++; break;
                    case Outcome.StrictOpened: strictOk++; break;
                    case Outcome.Updated: strictOk++; updated++; break;
                    case Outcome.LimitHit: strictFail++; limitErrors++; break;
                }
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                string path = Path.Combine(Path.GetTempPath(), $"mikepdf-mutant-{Seed}-{i}.pdf");
                File.WriteAllBytes(path, mutant);
                throw new InvalidOperationException($"iteration {i} (seed {name}) raised {ex.GetType().Name}: {ex.Message}. Input saved to {path}", ex);
            }
            catch (Xunit.Sdk.XunitException)
            {
                string path = Path.Combine(Path.GetTempPath(), $"mikepdf-mutant-{Seed}-{i}.pdf");
                File.WriteAllBytes(path, mutant);
                throw;
            }
        }

        TestContext.Current.SendDiagnosticMessage($"mutation pass: {Iterations} iterations, seed {Seed}: {strictOk} strict-open, {strictFail} rejected ({limitErrors} by a limit), {updated} extended");

        // The corpus must actually reach all the interesting paths, or the test proves little.
        Assert.True(strictOk > 2000, $"only {strictOk} mutants opened strictly");
        Assert.True(strictFail > 2000, $"only {strictFail} mutants were rejected");
        Assert.True(updated > 1000, $"only {updated} mutants were successfully extended");
        Assert.True(limitErrors > 0, "no mutant ever hit a limit");
    }

    private enum Outcome
    {
        StrictRejected,
        StrictOpened,
        Updated,
        LimitHit,
    }

    private static Outcome Exercise(byte[] mutant, Random rng)
    {
        var strict = StrictDocument.Open(mutant, Limits);
        var lenient = LenientDocument.Open(mutant, Limits);
        var report = StructureValidator.Validate(mutant, Limits);

        int issuesAtOpen = 0;
        if (lenient.TryGetValue(out var lenientDoc))
        {
            issuesAtOpen = lenientDoc.Issues.Count;
            AssertWithinLimits(lenientDoc);
            Assert.True(lenientDoc.Issues.Count <= 1000);
            foreach (var kv in lenientDoc.Latest.EnumerateEffectiveEntries().Take(40))
                _ = lenientDoc.GetObject(kv.Key); // lazily parse some objects: must return, never throw
        }
        else
        {
            AssertTyped(lenient.Error);
        }

        if (!strict.TryGetValue(out var doc))
        {
            AssertTyped(strict.Error);
            Assert.False(report.IsValid); // a refused file can never validate
            return strict.Error.Code == PdfErrorCode.LimitExceeded ? Outcome.LimitHit : Outcome.StrictRejected;
        }

        // Anything strict accepts, lenient accepts too, and without having to recover from anything.
        Assert.True(lenient.IsSuccess, "lenient refused what strict opened");
        Assert.Equal(0, issuesAtOpen);
        AssertWithinLimits(doc);

        // Walk the structure and decode whatever decodes; every failure must be a typed result.
        foreach (var kv in doc.Latest.EnumerateEffectiveEntries().Take(200))
        {
            var obj = doc.GetObject(kv.Key);
            if (obj.TryGetValue(out var o) && o is PdfStream s)
                _ = doc.DecodeStream(s);
            else if (obj.TryGetError(out var oe))
                AssertTyped(oe);
        }

        _ = DocInfo.Pages(doc);
        AssertWithinLimits(doc);

        var validated = StructureValidator.Validate(doc);
        Assert.Equal(report.IsValid, validated.IsValid); // the bytes entry point and the document entry point agree
        if (validated.IsValid)
        {
            foreach (var kv in doc.Latest.EnumerateEffectiveEntries().Where(kv => kv.Value.Type != XrefEntryType.Free))
                Assert.True(doc.GetObject(kv.Key).IsSuccess, "a valid file has an unreadable live object");
        }

        return TryUpdate(doc, mutant, rng) ? Outcome.Updated : Outcome.StrictOpened;
    }

    private static bool TryUpdate(StrictDocument doc, byte[] mutant, Random rng)
    {
        var update = new IncrementalUpdate(doc);
        if (update.Add(new PdfDictionary([new(new PdfName("Marker"), PdfString.FromAscii("mutation"))])).TryGetError(out var addError))
        {
            AssertTyped(addError);
            return false;
        }

        // Re-serialise a few parsed objects unchanged: parse -> write -> parse must be the identity.
        var live = doc.Latest.EnumerateEffectiveEntries().Where(kv => kv.Value.Type != XrefEntryType.Free && kv.Key > 0).Select(kv => kv.Key).ToList();
        for (int k = 0; k < 3 && live.Count > 0; k++)
        {
            int number = live[rng.Next(live.Count)];
            if (!doc.GetObject(number).TryGetValue(out var obj) || obj is PdfNull || !ReferencesResolve(doc, obj))
                continue;
            _ = update.Replace(number, obj); // refused (a structural stream) or accepted; both fine
        }

        var built = update.Build();
        if (built.TryGetError(out var error))
        {
            AssertTyped(error);
            Assert.True(Array.IndexOf(AllowedWriterFailures, error.Code) >= 0, $"the writer's self-check failed on a strictly valid base: {error}");
            return false;
        }

        byte[] output = built.Value;
        Assert.True(output.AsSpan(0, mutant.Length).SequenceEqual(mutant), "prefix property broken");
        var reopened = StrictDocument.Open(output, Limits);
        Assert.True(reopened.IsSuccess, "output does not reopen: " + (reopened.IsSuccess ? "" : reopened.Error));
        Assert.Equal(doc.Revisions.Count + 1, reopened.Value.Revisions.Count);
        return true;
    }

    private static bool ReferencesResolve(StrictDocument doc, PdfObject obj) =>
        PdfObjects.EnumerateReferences(obj).All(r => doc.Latest.TryGetEntry(r.Number, out var e) && e.Type != XrefEntryType.Free && e.Generation == r.Generation);

    private static void AssertTyped(PdfError error)
    {
        Assert.False(string.IsNullOrEmpty(error.Message));
        if (error.Code == PdfErrorCode.LimitExceeded)
            Assert.NotNull(error.Limit);
        else
            Assert.Null(error.Limit);
    }

    private static void AssertWithinLimits(PdfDocument doc)
    {
        Assert.True(doc.DecompressedBytes <= Limits.MaxDecompressedTotalBytes);
        Assert.True(doc.XrefEntryCount <= Limits.MaxObjectCount);
        Assert.True(doc.Revisions.Count <= Limits.MaxXrefSections);
        Assert.True(doc.Data.Length <= Limits.MaxFileSize);
        Assert.True(doc.Size <= Limits.MaxObjectNumber + 1);
        Assert.All(doc.Latest.Entries.Keys, n => Assert.InRange(n, 0, Limits.MaxObjectNumber));
    }
}
