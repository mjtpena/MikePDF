namespace MikePDF.Fuzz.Tests;

/// <summary>
/// Replays the seeds, and a fuzz corpus too when MIKEPDF_FUZZ_CORPUS_ROOT names the folder that tests/fuzz/run-fuzz.ps1 wrote (with
/// protocol-cs, pdfwriter-parse and pdfwriter-append subfolders), through the .NET targets. Its purpose is measuring coverage: run under the
/// code-coverage extension, the report says which lines of MikePDF.Protocol and MikePDF.PdfWriter the inputs reached (docs/fuzzing.md,
/// "How coverage is measured"). Run it without the variable for the seeds alone, and with it for seeds plus corpus. It also re-checks that every
/// input a past run kept still passes the current code.
/// </summary>
public class CorpusReplayTests
{
    private static string? Root => Environment.GetEnvironmentVariable("MIKEPDF_FUZZ_CORPUS_ROOT");

    private static IEnumerable<string> Corpus(string target) => Root is null ? [] : FuzzAssets.Files(Path.Combine(Root, target));

    [Fact]
    public void The_protocol_seeds_and_corpus_pass_the_current_decoders()
    {
        foreach (var file in FuzzAssets.GoldenFrames)
        {
            byte[] frame = File.ReadAllBytes(file);
            MikePDF.Protocol.Fuzz.Program.Outcome(frame);
            if (frame.Length > 4) MikePDF.Protocol.Fuzz.Program.Outcome(frame[4..]);
        }

        foreach (var file in Corpus("protocol-cs"))
            MikePDF.Protocol.Fuzz.Program.Outcome(File.ReadAllBytes(file));
    }

    [Fact]
    public void The_shell_request_seeds_and_corpus_pass_ShellHosts_decoder_and_rules()
    {
        var seeds = FuzzAssets.Files(FuzzAssets.Directory("seeds", "shell-request"), "*.bin");
        Assert.True(seeds.Count >= 25, "the shell request seeds are missing");
        foreach (var file in seeds)
        {
            byte[] frame = File.ReadAllBytes(file);
            MikePDF.ShellHost.Fuzz.Program.Outcome(frame);
            if (frame.Length > 4) MikePDF.ShellHost.Fuzz.Program.Outcome(frame[4..]);
            // Every truncation and a byte flip at every position: nothing may throw, whatever the bytes.
            for (int cut = 0; cut < frame.Length; cut++) MikePDF.ShellHost.Fuzz.Program.Outcome(frame[..cut]);
            for (int at = 0; at < frame.Length; at++)
            {
                byte[] mutated = (byte[])frame.Clone();
                mutated[at] ^= 0xFF;
                MikePDF.ShellHost.Fuzz.Program.Outcome(mutated);
            }
        }

        foreach (var file in Corpus("shell-request-cs"))
            MikePDF.ShellHost.Fuzz.Program.Outcome(File.ReadAllBytes(file));
    }

    [Fact]
    public void The_journal_seeds_and_corpus_pass_the_reader_and_the_recovery_code()
    {
        var seeds = FuzzAssets.Files(FuzzAssets.Directory("seeds", "journal"), "*.jsonl");
        Assert.True(seeds.Count >= 6, "the journal seeds are missing");

        // The seeds must still be journals: a line ending converted on checkout breaks every checksum and leaves the fuzzer nothing valid to start from.
        var expected = new (MikePDF.Core.Editing.Journal.JournalReadStatus Status, int Records)[]
        {
            (MikePDF.Core.Editing.Journal.JournalReadStatus.Ok, 0),
            (MikePDF.Core.Editing.Journal.JournalReadStatus.Ok, 1),
            (MikePDF.Core.Editing.Journal.JournalReadStatus.Ok, 4),
            (MikePDF.Core.Editing.Journal.JournalReadStatus.Ok, 8),
            (MikePDF.Core.Editing.Journal.JournalReadStatus.TornTail, 7),
        };
        for (var i = 0; i < expected.Length; i++)
        {
            var read = MikePDF.Core.Editing.Journal.JournalCodec.Parse(File.ReadAllBytes(seeds[i]));
            Assert.Equal(expected[i], (read.Status, read.ValidRecords));
        }

        foreach (var file in seeds)
            ReplayWithEveryTruncationAndFlip(File.ReadAllBytes(file), MikePDF.PdfWriter.Fuzz.SaveFuzzTargets.JournalOutcome);

        foreach (var file in Corpus("journal-cs"))
            MikePDF.PdfWriter.Fuzz.SaveFuzzTargets.JournalOutcome(File.ReadAllBytes(file));
    }

    [Fact]
    public void The_edit_blob_seeds_and_corpus_pass_the_reader_and_stage_four()
    {
        var seeds = FuzzAssets.Files(FuzzAssets.Directory("seeds", "editblob"), "*.bin");
        Assert.True(seeds.Count >= 3, "the edit blob seeds are missing");
        foreach (var file in seeds)
            Assert.True(MikePDF.Protocol.Edits.EditBlobCodec.TryDecode(File.ReadAllBytes(file), out _, out var why), file + ": " + why);

        foreach (var file in seeds)
            ReplayWithEveryTruncationAndFlip(File.ReadAllBytes(file), MikePDF.PdfWriter.Fuzz.SaveFuzzTargets.EditBlobOutcome);

        foreach (var file in Corpus("editblob-cs"))
            MikePDF.PdfWriter.Fuzz.SaveFuzzTargets.EditBlobOutcome(File.ReadAllBytes(file));
    }

    [Fact]
    public void The_pdf_seeds_and_corpus_pass_stage_four_and_the_copies()
    {
        foreach (var file in FuzzAssets.Pdfs)
            MikePDF.PdfWriter.Fuzz.SaveFuzzTargets.EditOutcome(File.ReadAllBytes(file));

        foreach (var file in Corpus("pdfwriter-edit"))
            MikePDF.PdfWriter.Fuzz.SaveFuzzTargets.EditOutcome(File.ReadAllBytes(file));
    }

    // Every truncation, and a byte flip at every position: nothing may throw, whatever the bytes.
    private static void ReplayWithEveryTruncationAndFlip(byte[] seed, Func<byte[], long> outcome)
    {
        outcome(seed);
        for (int cut = 0; cut < seed.Length; cut++) outcome(seed[..cut]);
        for (int at = 0; at < seed.Length; at++)
        {
            byte[] mutated = (byte[])seed.Clone();
            mutated[at] ^= 0xFF;
            outcome(mutated);
        }
    }

    [Fact]
    public void The_pdf_seeds_and_corpora_pass_the_current_parser_and_writer()
    {
        foreach (var file in FuzzAssets.Pdfs)
        {
            byte[] bytes = File.ReadAllBytes(file);
            MikePDF.PdfWriter.Fuzz.FuzzTargets.ParseOutcome(bytes);
            MikePDF.PdfWriter.Fuzz.FuzzTargets.AppendOutcome(bytes);
        }

        foreach (var file in Corpus("pdfwriter-parse"))
            MikePDF.PdfWriter.Fuzz.FuzzTargets.ParseOutcome(File.ReadAllBytes(file));
        foreach (var file in Corpus("pdfwriter-append"))
            MikePDF.PdfWriter.Fuzz.FuzzTargets.AppendOutcome(File.ReadAllBytes(file));
    }
}
