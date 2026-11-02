using MikePDF.Fuzz.Shared;
using MikePDF.Protocol;

namespace MikePDF.ShellHost.Fuzz;

/// <summary>
/// Feeds arbitrary bytes to ShellHost's request decoder (<see cref="ProtocolCodec"/>) and to the rules it applies to what the decoder accepts (<see cref="ShellRequestRules"/>), as
/// a whole frame and as a body (SHELL-18). A handler is untrusted: this is the first code that reads its bytes. A crash, an unexpected exception or a broken invariant is a finding.
/// Modes are those of the other harnesses: <c>--mutate N ...</c> (the dependency-free fallback), file arguments (replay a finding), <c>--libfuzzer</c> or no arguments (coverage-guided,
/// as the child of libfuzzer_dotnet.exe).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var target = new MutationalFuzzer.Target(
            "shell-request",
            Outcome,
            SeedExtensions: [".bin"],
            // A seed is a frame; the decoder also takes a bare body, so seed those too.
            DerivedSeeds: seed => seed.Length > ProtocolCodec.FrameHeaderBytes ? [seed[ProtocolCodec.FrameHeaderBytes..]] : []);

        if (MutationalFuzzer.Handles(args)) return MutationalFuzzer.Run(target, args);
        if (args.Length == 0 || LibFuzzerEntry.Handles(args)) return LibFuzzerEntry.Run(target, args);

        foreach (var path in args) _ = Outcome(File.ReadAllBytes(path));
        return 0;
    }

    /// <summary>Runs the decoder and the rules and checks the invariants; the result summarises the verdicts, for the mutational fuzzer's novelty test.</summary>
    internal static long Outcome(byte[] bytes)
    {
        long signature = 29;
        signature = MutationalFuzzer.Mix(signature, Check(ProtocolCodec.DecodeRequestFrame(bytes)));
        signature = MutationalFuzzer.Mix(signature, Check(ProtocolCodec.DecodeRequestBody(bytes)));
        var failure = ProtocolCodec.ReadFrameLength(bytes, out _);
        return MutationalFuzzer.Mix(signature, failure is null ? 1 : MutationalFuzzer.TextSignature(failure.Value.Detail));
    }

    private static long Check(ProtocolResult<RequestEnvelope> decoded)
    {
        if (!decoded.IsOk)
        {
            Require(decoded.Status is Status.ProtocolError or Status.InvalidArgument, "decoder returned an unexpected status");
            return MutationalFuzzer.TextSignature(decoded.Failure.Detail) ^ ((long)decoded.Status << 40);
        }

        var e = decoded.Value;
        // The decoder's own guarantee: what it accepts re-encodes to a fixed point.
        var first = ProtocolCodec.EncodeRequestFrame(e.RequestId, e.DocumentId, e.DeadlineMs, e.Payload);
        Require(first.IsOk, "an accepted request could not be re-encoded");
        var again = ProtocolCodec.DecodeRequestFrame(first.Value);
        Require(again.IsOk, "a re-encoded request was rejected");
        var second = ProtocolCodec.EncodeRequestFrame(again.Value.RequestId, again.Value.DocumentId, again.Value.DeadlineMs, again.Value.Payload);
        Require(second.IsOk && second.Value.AsSpan().SequenceEqual(first.Value), "request encoding is not a fixed point");

        // ShellHost's rules.
        var served = ShellRequestRules.IsServedAfterHello(e.Payload);
        Require(served == e.MessageType is MessageType.Ping or MessageType.ShellOpen or MessageType.ShellRenderPage or MessageType.ShellClose, "the allow-list of a handler's messages changed");
        var budget = ShellRequestRules.Budget(e.DeadlineMs);
        Require(budget >= TimeSpan.FromMilliseconds(1) && budget <= TimeSpan.FromMilliseconds(ShellRequestRules.MaxDeadlineMs), "a budget outside 1 ms to 5 s");
        Require(ShellRequestRules.IsHandlerHello(e, out _) == (e.Payload is Hello { ProcessKind: ProcessKind.ShellHandler }), "the Hello check disagrees with the Hello");

        switch (e.Payload)
        {
            case ShellOpen open:
                // What the schema promised the rules: a real handle value and 1 byte to 32 MiB.
                Require(open.Section >= 1 && open.Bytes is >= 1 and <= 33_554_432, "a ShellOpen outside the schema got through the decoder");
                break;
            case ShellRenderPage render:
                Require(render.MaxWidth is >= 16 and <= 1024 && render.MaxHeight is >= 16 and <= 1024 && render.Flags <= 1 && render.Pixels >= 1 && render.PixelBytes is >= 4 and <= 4_194_304,
                    "a ShellRenderPage outside the schema got through the decoder");
                // The rule against an independent computation (128-bit, so nothing can wrap).
                var needed = (UInt128)render.MaxWidth * render.MaxHeight * 4;
                Require(ShellRequestRules.PixelsCoverTheBox(render) == (needed <= render.PixelBytes), "the pixel-section rule disagrees with the arithmetic");
                break;
        }

        return 0x3000 + (long)e.MessageType;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }
}
