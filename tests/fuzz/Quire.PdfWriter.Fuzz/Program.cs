using SharpFuzz;

namespace Quire.PdfWriter.Fuzz;

internal static class Program
{
    private delegate void Target(ReadOnlySpan<byte> input);

    // Quire.PdfWriter.Fuzz <parse|append>                  AFL-style: SharpFuzz.Fuzzer.OutOfProcess, input on stdin
    // Quire.PdfWriter.Fuzz <parse|append>-libfuzzer        in-process libFuzzer, for libfuzzer-dotnet (which passes one --target_arg)
    // Quire.PdfWriter.Fuzz <parse|append> --replay <file>  run the target once on a file (use this to reproduce a crash)
    private static int Main(string[] args)
    {
        string name = args.Length > 0 ? args[0] : "";
        bool libFuzzer = name.EndsWith("-libfuzzer", StringComparison.Ordinal);
        Target? target = name.Replace("-libfuzzer", "", StringComparison.Ordinal) switch
        {
            "parse" => FuzzTargets.ParseAndValidate,
            "append" => FuzzTargets.AppendRoundTrip,
            _ => null,
        };

        if (target is null)
        {
            Console.Error.WriteLine("usage: Quire.PdfWriter.Fuzz <parse|append>[-libfuzzer] | <parse|append> --replay <file>");
            return 2;
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
