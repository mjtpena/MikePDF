using SharpFuzz;

namespace MikePDF.Fuzz.Shared;

/// <summary>
/// The coverage-guided mode of the C# harnesses (ADR-0017). The harness runs as the child process of <c>libfuzzer_dotnet.exe</c> (the
/// vendored Metalnem/libfuzzer-dotnet Windows driver, built with MSVC's libFuzzer): SharpFuzz reads each input from shared memory, runs the
/// target, and the instrumented assemblies (MikePDF.Protocol.dll, MikePDF.PdfWriter.dll, rewritten by <c>dotnet sharpfuzz</c> into a separate
/// folder) record the edges they hit in the same shared memory. The driver copies that map into libFuzzer's counters and libFuzzer picks
/// the next input by it.
///
/// An exception that escapes the target ends the child process; the driver sees the broken pipe, aborts, and libFuzzer writes the input as
/// <c>crash-*</c>. A hang is caught by libFuzzer's <c>-timeout</c>. Both are therefore findings with a reproducer, as in the native targets.
/// A reproducer is always replayed against the plain build (<c>--replay</c> or a file argument), never against the instrumented one.
/// </summary>
internal static class LibFuzzerEntry
{
    /// <summary>True when `args` ask for the libFuzzer child mode: <c>--libfuzzer</c>, optionally with <c>--selftest throw|hang|overflow</c>.</summary>
    public static bool Handles(string[] args) => args.Contains("--libfuzzer", StringComparer.Ordinal);

    /// <summary>Runs the target for as long as the driver keeps sending inputs. Never returns normally while the driver is alive.</summary>
    public static int Run(MutationalFuzzer.Target target, string[] args)
    {
        MutationalFuzzer.DisableErrorDialogs();
        int at = Array.IndexOf(args, "--selftest");
        if (at >= 0 && at + 1 < args.Length) target = MutationalFuzzer.SelfTestTarget(args[at + 1]);
        Fuzzer.LibFuzzer.Run(span => _ = target.Run(span.ToArray()));
        return 0;
    }
}
