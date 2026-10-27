namespace Quire.Protocol.Tests;

/// <summary>
/// A fixed-seed in-process fuzz run: random and mutated frames must never throw, and anything the decoder lets through
/// must re-encode to a frame that decodes to the same thing.
/// </summary>
public sealed class RandomizedTests
{
    private const int Iterations = 100_000;

    private static readonly byte[][] Seeds =
    [
        .. Samples.Requests.Select(r => ProtocolCodec.EncodeRequestFrame(r.RequestId, r.DocumentId, r.DeadlineMs, r.Payload).Value),
        .. Samples.Responses.Select(r => ProtocolCodec.EncodeResponseFrame(r.RequestId, r.Status, r.Type, r.Payload).Value),
        .. GoldenVectors.InvalidFrames.Select(v => v.Frame),
    ];

    // Bytes that change how MessagePack is read: type markers, length markers, and the edges of the integer ranges.
    private static readonly byte[] Interesting =
        [0x00, 0x01, 0x7F, 0x80, 0x90, 0x91, 0x9F, 0xA0, 0xBF, 0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC7, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF, 0xD0, 0xD3, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF, 0xE0, 0xFF];

    private static byte[] Mutate(Random rng, byte[] seed)
    {
        var data = seed.ToList();
        var edits = rng.Next(1, 5);
        for (var e = 0; e < edits && data.Count > 0; e++)
        {
            var at = rng.Next(data.Count);
            switch (rng.Next(7))
            {
                case 0: data[at] = (byte)rng.Next(256); break;
                case 1: data[at] = Interesting[rng.Next(Interesting.Length)]; break;
                case 2: data[at] ^= (byte)(1 << rng.Next(8)); break;
                case 3: data.RemoveAt(at); break;
                case 4: data.Insert(at, Interesting[rng.Next(Interesting.Length)]); break;
                case 5: data.RemoveRange(at, Math.Min(data.Count - at, rng.Next(1, 16))); break;
                default: data.InsertRange(at, data.Skip(rng.Next(data.Count)).Take(rng.Next(1, 12)).ToArray()); break;
            }
        }

        // Half the time keep the length prefix honest, so the mutation reaches the body decoder.
        var bytes = data.ToArray();
        if (bytes.Length >= 4 && rng.Next(2) == 0) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(bytes.Length - 4));
        return bytes;
    }

    private static byte[] RandomBytes(Random rng)
    {
        var bytes = new byte[rng.Next(0, 300)];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = rng.Next(3) == 0 ? Interesting[rng.Next(Interesting.Length)] : (byte)rng.Next(256);
        if (bytes.Length >= 4 && rng.Next(2) == 0) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(bytes.Length - 4));
        return bytes;
    }

    [Fact]
    public void Random_and_mutated_frames_never_throw_and_survivors_are_stable()
    {
        var rng = new Random(0x51_0E);
        int requestsAccepted = 0, responsesAccepted = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var frame = i % 4 == 0 ? RandomBytes(rng) : Mutate(rng, Seeds[rng.Next(Seeds.Length)]);

            var request = ProtocolCodec.DecodeRequestFrame(frame);
            if (request.IsOk)
            {
                requestsAccepted++;
                var e = request.Value;
                var again = ProtocolCodec.EncodeRequestFrame(e.RequestId, e.DocumentId, e.DeadlineMs, e.Payload);
                Assert.True(again.IsOk, "an accepted request must be re-encodable: " + again.Failure.Detail);
                var e2 = ProtocolCodec.DecodeRequestFrame(again.Value).Value;
                Assert.Equal(again.Value, ProtocolCodec.EncodeRequestFrame(e2.RequestId, e2.DocumentId, e2.DeadlineMs, e2.Payload).Value);
            }
            else
            {
                Assert.True(request.Status is Status.ProtocolError or Status.InvalidArgument);
            }

            var response = ProtocolCodec.DecodeResponseFrame(frame);
            if (response.IsOk)
            {
                responsesAccepted++;
                var e = response.Value;
                var again = ProtocolCodec.EncodeResponseFrame(e.RequestId, e.Status, e.MessageType, e.Payload);
                Assert.True(again.IsOk, "an accepted response must be re-encodable: " + again.Failure.Detail);
                var e2 = ProtocolCodec.DecodeResponseFrame(again.Value).Value;
                Assert.Equal(again.Value, ProtocolCodec.EncodeResponseFrame(e2.RequestId, e2.Status, e2.MessageType, e2.Payload).Value);
            }
            else
            {
                Assert.True(response.Status is Status.ProtocolError or Status.InvalidArgument);
            }
        }

        // The run is only meaningful if it reaches deep into the decoders as well as bouncing off the framing checks.
        Assert.True(requestsAccepted > 500, "accepted requests: " + requestsAccepted);
        Assert.True(responsesAccepted > 500, "accepted responses: " + responsesAccepted);
    }
}
