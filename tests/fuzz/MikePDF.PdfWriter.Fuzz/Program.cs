using MikePDF.Fuzz.Shared;
using SharpFuzz;

namespace MikePDF.PdfWriter.Fuzz;

internal static class Program
{
    private delegate void Target(ReadOnlySpan<byte> input);

    // MikePDF.PdfWriter.Fuzz <parse|append> --mutate <seconds> [--corpus d] [--seeds d]... [--dict f]   dependency-free mutational fuzzer (no coverage feedback)
    // MikePDF.PdfWriter.Fuzz <parse|append> --minimize <file>                                         shrink a crashing input
    // MikePDF.PdfWriter.Fuzz <parse|append>                  AFL-style: SharpFuzz.Fuzzer.OutOfProcess, input on stdin
    // MikePDF.PdfWriter.Fuzz <parse|append>-libfuzzer        in-process libFuzzer, for libfuzzer-dotnet (which passes one --target_arg)
    // MikePDF.PdfWriter.Fuzz <parse|append> --replay <file>  run the target once on a file (use this to reproduce a crash)
    private static int Main(string[] args)
    {
        string name = args.Length > 0 ? args[0] : "";
        bool libFuzzer = name.EndsWith("-libfuzzer", StringComparison.Ordinal);
        string bare = name.Replace("-libfuzzer", "", StringComparison.Ordinal);
        Target? target = bare switch
        {
            "parse" => FuzzTargets.ParseAndValidate,
            "append" => FuzzTargets.AppendRoundTrip,
            _ => null,
        };

        if (target is null)
        {
            Console.Error.WriteLine("usage: MikePDF.PdfWriter.Fuzz <parse|append>[-libfuzzer] | <parse|append> --replay <file> | <parse|append> --mutate <seconds> ...");
            return 2;
        }

        if (MutationalFuzzer.Handles(args))
        {
            var mutational = new MutationalFuzzer.Target(
                bare,
                bare == "parse" ? FuzzTargets.ParseOutcome : FuzzTargets.AppendOutcome,
                SeedExtensions: [".pdf"],
                LeadingArgs: [bare]);
            return MutationalFuzzer.Run(mutational, args[1..]);
        }

        if (args.Length >= 3 && args[1] == "--replay")
        {
            target(File.ReadAllBytes(args[2]));
            Console.WriteLine("ok");
            return 0;
        }

        if (libFuzzer)
        {
            Fuzzer.LibFuzzer.Run(span => target(span));
            return 0;
        }

        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            target(ms.ToArray());
        });
        return 0;
    }
}
