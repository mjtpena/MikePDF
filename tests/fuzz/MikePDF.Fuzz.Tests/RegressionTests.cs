using MikePDF.Fuzz.Shared;

namespace MikePDF.Fuzz.Tests;

/// <summary>
/// Replays the minimised reproducers of past findings (tests/fuzz/regressions) and the seed corpora against the real fuzz targets.
/// A target throws on any broken invariant, so "returns" is the assertion.
/// </summary>
public class RegressionTests
{
    [Fact]
    public void The_regression_folders_are_present_in_the_test_output()
    {
        // A missing folder would make the replay tests below pass vacuously.
        Assert.True(File.Exists(FuzzAssets.Directory("regressions", "README.md")), "regressions/README.md was not copied next to the tests");
    }

    [Fact]
    public void Every_protocol_regression_is_handled_by_both_decoder_directions()
    {
        var files = FuzzAssets.Files(FuzzAssets.Directory("regressions", "protocol"), "*.bin");
        foreach (var file in files)
        {
            var ex = Record.Exception(() => MikePDF.Protocol.Fuzz.Program.Outcome(File.ReadAllBytes(file)));
            Assert.True(ex is null, $"{Path.GetFileName(file)} still breaks the protocol decoders: {ex}");
        }
    }

    [Fact]
    public void Every_pdfwriter_regression_is_handled_by_the_parser_and_the_writer()
    {
        var files = FuzzAssets.Files(FuzzAssets.Directory("regressions", "pdfwriter"), "*.pdf");
        foreach (var file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            var parse = Record.Exception(() => MikePDF.PdfWriter.Fuzz.FuzzTargets.ParseOutcome(bytes));
            Assert.True(parse is null, $"{Path.GetFileName(file)} still breaks the parser or validator: {parse}");
            var append = Record.Exception(() => MikePDF.PdfWriter.Fuzz.FuzzTargets.AppendOutcome(bytes));
            Assert.True(append is null, $"{Path.GetFileName(file)} still breaks the incremental writer: {append}");
        }
    }

    [Fact]
    public void The_golden_frames_pass_the_protocol_target_whole_and_as_bodies()
    {
        Assert.NotEmpty(FuzzAssets.GoldenFrames);
        foreach (var file in FuzzAssets.GoldenFrames)
        {
            byte[] frame = File.ReadAllBytes(file);
            MikePDF.Protocol.Fuzz.Program.Outcome(frame);
            if (frame.Length > 4) MikePDF.Protocol.Fuzz.Program.Outcome(frame[4..]);
        }
    }

    [Fact]
    public void The_fixture_pdfs_pass_both_pdfwriter_targets()
    {
        Assert.NotEmpty(FuzzAssets.Pdfs);
        foreach (var file in FuzzAssets.Pdfs)
        {
            byte[] bytes = File.ReadAllBytes(file);
            MikePDF.PdfWriter.Fuzz.FuzzTargets.ParseOutcome(bytes);
            MikePDF.PdfWriter.Fuzz.FuzzTargets.AppendOutcome(bytes);
        }
    }

    [Fact]
    public void A_fixed_seed_mutation_pass_over_the_corpora_finds_nothing()
    {
        // The in-process counterpart of the nightly runs: the same mutator, a fixed seed, a few thousand inputs per target.
        var rng = new Random(20261102);
        var dictionary = MutationalFuzzer.LoadDictionary(FuzzAssets.Directory("dictionaries", "protocol.dict"));
        var frames = FuzzAssets.GoldenFrames.Select(File.ReadAllBytes).ToList();
        for (int i = 0; i < 4000; i++)
            MikePDF.Protocol.Fuzz.Program.Outcome(MutationalFuzzer.Mutate(frames[rng.Next(frames.Count)], frames, dictionary, rng, 65_540));

        var pdfDictionary = MutationalFuzzer.LoadDictionary(FuzzAssets.Directory("dictionaries", "pdf.dict"));
        var pdfs = FuzzAssets.Pdfs.Select(File.ReadAllBytes).Where(b => b.Length < 20_000).ToList();
        for (int i = 0; i < 400; i++)
        {
            byte[] input = MutationalFuzzer.Mutate(pdfs[rng.Next(pdfs.Count)], pdfs, pdfDictionary, rng, 65_536);
            MikePDF.PdfWriter.Fuzz.FuzzTargets.ParseOutcome(input);
            MikePDF.PdfWriter.Fuzz.FuzzTargets.AppendOutcome(input);
        }
    }
}
