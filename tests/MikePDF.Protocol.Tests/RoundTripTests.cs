namespace MikePDF.Protocol.Tests;

public sealed class RoundTripTests
{
    public static TheoryData<string> RequestNames => [.. Samples.Requests.Select(r => r.Name)];

    public static TheoryData<string> ResponseNames => [.. Samples.Responses.Select(r => r.Name)];

    [Theory]
    [MemberData(nameof(RequestNames))]
    public void Request_round_trips(string name)
    {
        var s = Samples.Requests.Single(r => r.Name == name);
        var frame = ProtocolCodec.EncodeRequestFrame(s.RequestId, s.DocumentId, s.DeadlineMs, s.Payload);
        Assert.True(frame.IsOk, frame.Failure.Detail);

        var decoded = ProtocolCodec.DecodeRequestFrame(frame.Value);
        Assert.True(decoded.IsOk, decoded.Failure.Detail);
        var e = decoded.Value;
        Assert.Equal(s.RequestId, e.RequestId);
        Assert.Equal(s.DocumentId, e.DocumentId);
        Assert.Equal(s.DeadlineMs, e.DeadlineMs);
        Assert.Equal(s.Payload.MessageType, e.MessageType);
        Assert.Equal(s.Payload.GetType(), e.Payload.GetType());
        Assert.Equivalent(s.Payload, e.Payload, strict: true);

        // Canonical: encoding the decoded message gives the same bytes.
        Assert.Equal(frame.Value, ProtocolCodec.EncodeRequestFrame(e.RequestId, e.DocumentId, e.DeadlineMs, e.Payload).Value);
    }

    [Theory]
    [MemberData(nameof(ResponseNames))]
    public void Response_round_trips(string name)
    {
        var s = Samples.Responses.Single(r => r.Name == name);
        var frame = ProtocolCodec.EncodeResponseFrame(s.RequestId, s.Status, s.Type, s.Payload);
        Assert.True(frame.IsOk, frame.Failure.Detail);

        var decoded = ProtocolCodec.DecodeResponseFrame(frame.Value);
        Assert.True(decoded.IsOk, decoded.Failure.Detail);
        var e = decoded.Value;
        Assert.Equal(s.RequestId, e.RequestId);
        Assert.Equal(s.Status, e.Status);
        Assert.Equal(s.Type, e.MessageType);
        if (s.Payload is null)
        {
            Assert.Null(e.Payload);
        }
        else
        {
            Assert.Equal(s.Payload.GetType(), e.Payload!.GetType());
            Assert.Equivalent(s.Payload, e.Payload, strict: true);
        }

        Assert.Equal(frame.Value, ProtocolCodec.EncodeResponseFrame(e.RequestId, e.Status, e.MessageType, e.Payload).Value);
    }

    [Fact]
    public void Every_message_type_has_a_request_and_a_reply_sample()
    {
        foreach (var type in Enum.GetValues<MessageType>())
        {
            Assert.Contains(Samples.Requests, r => r.Payload.MessageType == type);
            Assert.Contains(Samples.Responses, r => r.Type == type && r.Status == Status.Ok);
        }
    }

    [Fact]
    public void Every_status_round_trips_as_an_error_response()
    {
        foreach (var status in Enum.GetValues<Status>())
        {
            var carries = StatusInfo.CarriesPayload(status);
            IReplyMessage? payload = carries ? new Pong { Nonce = 1 } : null;
            var frame = ProtocolCodec.EncodeResponseFrame(5, status, MessageType.Ping, payload);
            var decoded = ProtocolCodec.DecodeResponseFrame(frame.Value);
            Assert.Equal(status, decoded.Value.Status);
            Assert.Equal(carries, decoded.Value.Payload is not null);
        }
    }

    [Fact]
    public void Status_covers_the_master_prompt_error_codes()
    {
        string[] required =
        [
            "Ok", "Cancelled", "InvalidArgument", "ProtocolError", "Unsupported", "PasswordRequired", "PasswordIncorrect",
            "UnsupportedEncryption", "Damaged", "PartiallyRendered", "Timeout", "EngineCrashed", "PermissionDenied",
        ];
        Assert.Equal(required.Order(), Enum.GetNames<Status>().Order());
    }

    [Fact]
    public void Encoding_refuses_an_invalid_message()
    {
        var bad = new RenderTile { Scale = float.NaN, TileWidth = 1, TileHeight = 1 };
        var result = ProtocolCodec.EncodeRequestFrame(1, 1, 0, bad);
        Assert.False(result.IsOk);
        Assert.Equal(Status.InvalidArgument, result.Status);
    }

    [Fact]
    public void Encoding_refuses_a_header_that_breaks_the_document_rule()
    {
        Assert.Equal(Status.InvalidArgument, ProtocolCodec.EncodeRequestFrame(1, 0, 0, new CloseDocument()).Status);
        Assert.Equal(Status.InvalidArgument, ProtocolCodec.EncodeRequestFrame(1, 9, 0, new Ping()).Status);
        Assert.Equal(Status.InvalidArgument, ProtocolCodec.EncodeRequestFrame(1, 0, ProtocolInfo.MaxDeadlineMs + 1, new Ping()).Status);
    }

    [Fact]
    public void Encoding_refuses_a_response_that_does_not_fit_its_status()
    {
        Assert.Equal(Status.InvalidArgument, ProtocolCodec.EncodeResponseFrame(1, Status.Ok, MessageType.Ping, null).Status);
        Assert.Equal(Status.InvalidArgument, ProtocolCodec.EncodeResponseFrame(1, Status.Timeout, MessageType.Ping, new Pong()).Status);
        Assert.Equal(Status.InvalidArgument, ProtocolCodec.EncodeResponseFrame(1, Status.Ok, MessageType.Ping, new ShutdownReply()).Status);
        Assert.Equal(Status.InvalidArgument, ProtocolCodec.EncodeResponseFrame(1, (Status)99, MessageType.Ping, null).Status);
        Assert.Equal(Status.InvalidArgument, ProtocolCodec.EncodeResponseFrame(1, Status.Ok, (MessageType)99, new Pong()).Status);
    }

    [Fact]
    public void The_worst_case_replies_fit_in_one_frame()
    {
        var sizes = new GetPageSizesReply
        {
            Widths = [.. Enumerable.Repeat(14400f, 5000)],
            Heights = [.. Enumerable.Repeat(14400f, 5000)],
            Rotations = [.. Enumerable.Repeat((ushort)3, 5000)],
        };
        var big = ProtocolCodec.EncodeResponseFrame(ulong.MaxValue, Status.Ok, MessageType.GetPageSizes, sizes);
        Assert.True(big.IsOk, big.Failure.Detail);
        Assert.InRange(big.Value.Length, 1, ProtocolInfo.MaxFrameBytes + 4);
        Assert.True(ProtocolCodec.DecodeResponseFrame(big.Value).IsOk);

        var issues = new ValidateStructureReply
        {
            Ok = false,
            Truncated = true,
            IssueCodes = [.. Enumerable.Repeat(65535u, 256)],
            IssueMessages = [.. Enumerable.Repeat(new string('é', 96), 256)], // 192 bytes each
        };
        var full = ProtocolCodec.EncodeResponseFrame(ulong.MaxValue, Status.Ok, MessageType.ValidateStructure, issues);
        Assert.True(full.IsOk, full.Failure.Detail);
        Assert.True(ProtocolCodec.DecodeResponseFrame(full.Value).IsOk);

        var info = new GetDocumentInfoReply
        {
            Title = new string('t', 1024), Author = new string('a', 1024), Subject = new string('s', 1024),
            Keywords = new string('k', 1024), Creator = new string('c', 1024), Producer = new string('p', 1024),
        };
        Assert.True(ProtocolCodec.EncodeResponseFrame(1, Status.Ok, MessageType.GetDocumentInfo, info).IsOk);
    }
}
