using MessagePack;

namespace MikePDF.Protocol;

/// <summary>
/// Why a frame or message was rejected. Status is ProtocolError for a malformed frame (wrong shape, wrong type, truncated,
/// trailing bytes, a value that does not fit its wire type, an undefined message type or status) and InvalidArgument for a
/// well-formed frame that breaks a schema limit (range, length, count, undefined enum value, non-finite float).
/// </summary>
public readonly record struct ProtocolFailure(Status Status, string Detail)
{
    public static ProtocolFailure Malformed(string detail) => new(Status.ProtocolError, detail);

    public static ProtocolFailure Invalid(string detail) => new(Status.InvalidArgument, detail);
}

/// <summary>A decoded or encoded value, or the reason there is none.</summary>
public readonly struct ProtocolResult<T> where T : class
{
    private readonly T? _value;

    internal ProtocolResult(T? value, ProtocolFailure failure)
    {
        _value = value;
        Failure = failure;
    }

    public bool IsOk => _value is not null;

    /// <summary>The Status a peer would report: Ok, or the failure's status.</summary>
    public Status Status => IsOk ? Status.Ok : Failure.Status;

    public ProtocolFailure Failure { get; }

    public T Value => _value ?? throw new InvalidOperationException("No value: " + Failure.Detail);
}

public static class ProtocolResult
{
    public static ProtocolResult<T> Ok<T>(T value) where T : class => new(value, default);

    public static ProtocolResult<T> Fail<T>(ProtocolFailure failure) where T : class => new(null, failure);
}

public interface IProtocolMessage
{
    /// <summary>The message this payload belongs to. A request and its reply share it.</summary>
    MessageType MessageType { get; }

    /// <summary>Checks every schema limit. Returns null when the message is valid.</summary>
    ProtocolFailure? Validate();

    /// <summary>Writes the payload array without validating it. The codec validates first; tests use this to build malformed input.</summary>
    void Write(ref MessagePackWriter writer);
}

public interface IRequestMessage : IProtocolMessage;

public interface IReplyMessage : IProtocolMessage;

/// <summary>A validated request. DocumentId is 0 exactly when the message type takes no document.</summary>
public sealed record RequestEnvelope(ulong RequestId, uint DocumentId, uint DeadlineMs, MessageType MessageType, IRequestMessage Payload);

/// <summary>
/// A validated response. MessageType echoes the request being answered; the caller must check it against the pending
/// request. Payload is non-null exactly when Status carries a payload.
/// </summary>
public sealed record ResponseEnvelope(ulong RequestId, Status Status, MessageType MessageType, IReplyMessage? Payload);
