using MikePDF.Fuzz.Shared;

namespace MikePDF.PdfWriter.Fuzz;

internal static class Program
{
    private delegate void Target(ReadOnlySpan<byte> input);

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
            _ => null,
        };

        if (target is null)
        {
            Console.Error.WriteLine("usage: MikePDF.PdfWriter.Fuzz <parse|append> --libfuzzer | <parse|append> --replay <file> | <parse|append> --mutate <seconds> ...");
            return 2;
        }

        var fuzz = new MutationalFuzzer.Target(
            name,
            name == "parse" ? FuzzTargets.ParseOutcome : FuzzTargets.AppendOutcome,
            SeedExtensions: [".pdf"],
            LeadingArgs: [name]);

        if (MutationalFuzzer.Handles(args)) return MutationalFuzzer.Run(fuzz, args[1..]);
        if (LibFuzzerEntry.Handles(args)) return LibFuzzerEntry.Run(fuzz, args);

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
