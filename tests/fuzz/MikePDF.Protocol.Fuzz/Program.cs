using MikePDF.Fuzz.Shared;

namespace MikePDF.Protocol.Fuzz;

/// <summary>
/// Feeds arbitrary bytes to every decoder in MikePDF.Protocol. A crash, an unexpected exception or a broken invariant is a
/// finding. Modes:
/// <list type="bullet">
/// <item><c>--mutate N ...</c>: the dependency-free mutational fuzzer, the fallback (see MutationalFuzzer; no coverage feedback).</item>
/// <item>file arguments: replay those inputs, to reproduce a finding.</item>
/// <item><c>--libfuzzer</c> (or no arguments): the coverage-guided child mode for libfuzzer_dotnet.exe (see LibFuzzerEntry and README.md).</item>
/// </list>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var target = new MutationalFuzzer.Target(
            "protocol",
            Outcome,
            SeedExtensions: [".bin"],
            // A golden file is a frame; the decoders also take a bare body, so seed those too.
            DerivedSeeds: seed => seed.Length > ProtocolCodec.FrameHeaderBytes ? [seed[ProtocolCodec.FrameHeaderBytes..]] : []);

        if (MutationalFuzzer.Handles(args)) return MutationalFuzzer.Run(target, args);

        // No arguments is what libfuzzer-dotnet's --target_arg-less launch runs; --libfuzzer is the explicit spelling.
        if (args.Length == 0 || LibFuzzerEntry.Handles(args)) return LibFuzzerEntry.Run(target, args);

        foreach (var path in args) _ = Outcome(File.ReadAllBytes(path));
        return 0;
    }

    /// <summary>Runs every decoder and the invariant checks; the result summarises the verdicts, for the mutational fuzzer's novelty test.</summary>
    internal static long Outcome(byte[] bytes)
    {
        long signature = 17;

        // As a whole frame, and as a body once the length prefix has been consumed.
        signature = MutationalFuzzer.Mix(signature, CheckRequest(ProtocolCodec.DecodeRequestFrame(bytes)));
        signature = MutationalFuzzer.Mix(signature, CheckRequest(ProtocolCodec.DecodeRequestBody(bytes)));
        signature = MutationalFuzzer.Mix(signature, CheckResponse(ProtocolCodec.DecodeResponseFrame(bytes)));
        signature = MutationalFuzzer.Mix(signature, CheckResponse(ProtocolCodec.DecodeResponseBody(bytes)));
        var failure = ProtocolCodec.ReadFrameLength(bytes, out _);
        return MutationalFuzzer.Mix(signature, failure is null ? 1 : MutationalFuzzer.TextSignature(failure.Value.Detail));
    }

    // Anything the decoder accepts must be re-encodable, and encode to a fixed point.
    private static long CheckRequest(ProtocolResult<RequestEnvelope> decoded)
    {
        if (!decoded.IsOk)
        {
            Require(decoded.Status is Status.ProtocolError or Status.InvalidArgument, "decoder returned an unexpected status");
            return MutationalFuzzer.TextSignature(decoded.Failure.Detail) ^ ((long)decoded.Status << 40);
        }

        var e = decoded.Value;
        var first = ProtocolCodec.EncodeRequestFrame(e.RequestId, e.DocumentId, e.DeadlineMs, e.Payload);
        Require(first.IsOk, "an accepted request could not be re-encoded");
        var again = ProtocolCodec.DecodeRequestFrame(first.Value);
        Require(again.IsOk, "a re-encoded request was rejected");
        var second = ProtocolCodec.EncodeRequestFrame(again.Value.RequestId, again.Value.DocumentId, again.Value.DeadlineMs, again.Value.Payload);
        Require(second.IsOk && second.Value.AsSpan().SequenceEqual(first.Value), "request encoding is not a fixed point");
        return 0x1000 + (long)e.MessageType;
    }

    private static long CheckResponse(ProtocolResult<ResponseEnvelope> decoded)
    {
        if (!decoded.IsOk)
        {
            Require(decoded.Status is Status.ProtocolError or Status.InvalidArgument, "decoder returned an unexpected status");
            return MutationalFuzzer.TextSignature(decoded.Failure.Detail) ^ ((long)decoded.Status << 40);
        }

        var e = decoded.Value;
        var first = ProtocolCodec.EncodeResponseFrame(e.RequestId, e.Status, e.MessageType, e.Payload);
        Require(first.IsOk, "an accepted response could not be re-encoded");
        var again = ProtocolCodec.DecodeResponseFrame(first.Value);
        Require(again.IsOk, "a re-encoded response was rejected");
        var second = ProtocolCodec.EncodeResponseFrame(again.Value.RequestId, again.Value.Status, again.Value.MessageType, again.Value.Payload);
        Require(second.IsOk && second.Value.AsSpan().SequenceEqual(first.Value), "response encoding is not a fixed point");
        return 0x2000 + ((long)e.MessageType << 8) + (long)e.Status;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }
}
