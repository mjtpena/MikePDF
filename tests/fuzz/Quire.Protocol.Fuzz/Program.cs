using SharpFuzz;

namespace Quire.Protocol.Fuzz;

/// <summary>
/// Feeds arbitrary bytes to every decoder in Quire.Protocol. A crash, an unexpected exception or a broken invariant is a
/// finding. With file arguments it replays those inputs instead of fuzzing, to reproduce a crash.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Fuzzer.LibFuzzer.Run(Target);
            return 0;
        }

        foreach (var path in args) Target(File.ReadAllBytes(path));
        return 0;
    }

    private static void Target(ReadOnlySpan<byte> input)
    {
        var bytes = input.ToArray();

        // As a whole frame, and as a body once the length prefix has been consumed.
        CheckRequest(ProtocolCodec.DecodeRequestFrame(bytes));
        CheckRequest(ProtocolCodec.DecodeRequestBody(bytes));
        CheckResponse(ProtocolCodec.DecodeResponseFrame(bytes));
        CheckResponse(ProtocolCodec.DecodeResponseBody(bytes));
        _ = ProtocolCodec.ReadFrameLength(bytes, out _);
    }

    // Anything the decoder accepts must be re-encodable, and encode to a fixed point.
    private static void CheckRequest(ProtocolResult<RequestEnvelope> decoded)
    {
        if (!decoded.IsOk)
        {
            Require(decoded.Status is Status.ProtocolError or Status.InvalidArgument, "decoder returned an unexpected status");
            return;
        }

        var e = decoded.Value;
        var first = ProtocolCodec.EncodeRequestFrame(e.RequestId, e.DocumentId, e.DeadlineMs, e.Payload);
        Require(first.IsOk, "an accepted request could not be re-encoded");
        var again = ProtocolCodec.DecodeRequestFrame(first.Value);
        Require(again.IsOk, "a re-encoded request was rejected");
        var second = ProtocolCodec.EncodeRequestFrame(again.Value.RequestId, again.Value.DocumentId, again.Value.DeadlineMs, again.Value.Payload);
        Require(second.IsOk && second.Value.AsSpan().SequenceEqual(first.Value), "request encoding is not a fixed point");
    }

    private static void CheckResponse(ProtocolResult<ResponseEnvelope> decoded)
    {
        if (!decoded.IsOk)
        {
            Require(decoded.Status is Status.ProtocolError or Status.InvalidArgument, "decoder returned an unexpected status");
            return;
        }

        var e = decoded.Value;
        var first = ProtocolCodec.EncodeResponseFrame(e.RequestId, e.Status, e.MessageType, e.Payload);
        Require(first.IsOk, "an accepted response could not be re-encoded");
        var again = ProtocolCodec.DecodeResponseFrame(first.Value);
        Require(again.IsOk, "a re-encoded response was rejected");
        var second = ProtocolCodec.EncodeResponseFrame(again.Value.RequestId, again.Value.Status, again.Value.MessageType, again.Value.Payload);
        Require(second.IsOk && second.Value.AsSpan().SequenceEqual(first.Value), "response encoding is not a fixed point");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }
}
