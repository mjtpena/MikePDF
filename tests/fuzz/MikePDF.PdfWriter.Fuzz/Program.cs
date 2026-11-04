using MikePDF.Fuzz.Shared;

namespace MikePDF.PdfWriter.Fuzz;

internal static class Program
{
    private delegate void Target(ReadOnlySpan<byte> input);

    // Targets: parse, append (the parser and the incremental writer), edit (stage 4 and the copies on arbitrary files), tail (NormalizeTail, stage 3), assemble (the page assembler on arbitrary files), journal (the edit journal's reader and the
    // recovery code behind it), editblob (the edit blob's reader and the appender on what it decodes). `<journal|editblob> --seeds-out <dir>` writes their seeds.
    // MikePDF.Fuzz.Shared and the targets below are also run by tests/fuzz/MikePDF.Fuzz.Tests on every build.
    // MikePDF.PdfWriter.Fuzz <parse|append> --libfuzzer                                               coverage-guided child mode for libfuzzer_dotnet.exe (ADR-0017)
    // MikePDF.PdfWriter.Fuzz <parse|append> --mutate <seconds> [--corpus d] [--seeds d]... [--dict f]   dependency-free mutational fuzzer, the fallback (no coverage feedback)
    // MikePDF.PdfWriter.Fuzz <parse|append> --minimize <file>                                         shrink a crashing input
    // MikePDF.PdfWriter.Fuzz <parse|append> --replay <file>                                           run the target once on a file (use this to reproduce a crash)
    private static int Main(string[] args)
    {
        string name = args.Length > 0 ? args[0] : "";
        Target? target = name switch
        {
            "parse" => FuzzTargets.ParseAndValidate,
            "append" => FuzzTargets.AppendRoundTrip,
            "edit" => SaveFuzzTargets.EditPdf,
            "tail" => SaveFuzzTargets.TailNormalize,
            "assemble" => AssembleFuzzTargets.Assemble,
            "journal" => SaveFuzzTargets.JournalParse,
            "editblob" => SaveFuzzTargets.EditBlobDecode,
            _ => null,
        };

        if (target is null)
        {
            Console.Error.WriteLine("usage: MikePDF.PdfWriter.Fuzz <parse|append|edit|tail|assemble|journal|editblob> --libfuzzer | <target> --replay <file> | <target> --mutate <seconds> ...");
            return 2;
        }

        if (args.Length == 3 && args[1] == "--seeds-out" && name is "journal" or "editblob" or "tail")
        {
            SaveFuzzTargets.WriteSeeds(name, args[2]);
            return 0;
        }

        var fuzz = new MutationalFuzzer.Target(
            name,
            name switch
            {
                "parse" => FuzzTargets.ParseOutcome,
                "append" => FuzzTargets.AppendOutcome,
                "edit" => SaveFuzzTargets.EditOutcome,
                "tail" => SaveFuzzTargets.TailOutcome,
                "assemble" => AssembleFuzzTargets.AssembleOutcome,
                "journal" => SaveFuzzTargets.JournalOutcome,
                _ => SaveFuzzTargets.EditBlobOutcome,
            },
            SeedExtensions: name switch { "journal" => [".jsonl"], "editblob" => [".bin"], _ => [".pdf"] },
            LeadingArgs: [name]);

        if (MutationalFuzzer.Handles(args)) return MutationalFuzzer.Run(fuzz, args[1..]);

        // `<target>` alone is what libfuzzer-dotnet's --target_arg=<target> starts (the driver's -jobs mode cannot pass more than one word).
        if (LibFuzzerEntry.Handles(args) || args.Length == 1) return LibFuzzerEntry.Run(fuzz, args);

        if (args.Length >= 3 && args[1] == "--replay")
        {
            target(File.ReadAllBytes(args[2]));
            Console.WriteLine("ok");
            return 0;
        }

        Console.Error.WriteLine("nothing to do: pass --libfuzzer, --mutate <seconds>, --minimize <file> or --replay <file>");
        return 2;
    }
}
