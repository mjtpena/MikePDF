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
