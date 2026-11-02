#include "mikepdf/protocol/codec.h"

#include <limits>
#include <utility>

namespace mikepdf::protocol {

namespace {

template <class T>
Result<T> failure(Status status, const char* detail) {
    return {status, T{}, detail};
}

// Header rules that depend on the message type. Shared by decoding and encoding.
Status check_request_header(std::uint32_t document_id, std::uint32_t deadline_ms, MessageType type, const char*& why) noexcept {
    if (deadline_ms > kMaxDeadlineMs) {
        why = "deadline is out of range";
        return Status::InvalidArgument;
    }
    if (document_rule(type) == DocumentIdRule::None && document_id != 0) {
        why = "this message takes no document";
        return Status::InvalidArgument;
    }
    if (document_rule(type) == DocumentIdRule::Required && document_id == 0) {
        why = "this message needs a document";
        return Status::InvalidArgument;
    }
    return Status::Ok;
}

// The prefix is a u32 little-endian count of body bytes.
void put_prefix(std::vector<std::uint8_t>& out, std::size_t body_length) {
    for (int i = 0; i < 4; ++i) {
        out.push_back(static_cast<std::uint8_t>(body_length >> (8 * i)));
    }
}

Result<std::vector<std::uint8_t>> finish_frame(Writer& writer) {
    if (const Status s = writer.status(); s != Status::Ok) {
        return failure<std::vector<std::uint8_t>>(s, "the message does not fit in one frame");
    }
    const auto body = writer.bytes();
    std::vector<std::uint8_t> frame;
    frame.reserve(kFrameHeaderBytes + body.size());
    put_prefix(frame, body.size());
    frame.insert(frame.end(), body.begin(), body.end());
    return {Status::Ok, std::move(frame), ""};
}

}  // namespace

MessageType message_type_of(const RequestPayload& payload) noexcept {
    return std::visit([](const auto& m) { return std::remove_cvref_t<decltype(m)>::kMessageType; }, payload);
}

MessageType message_type_of(const ReplyPayload& payload) noexcept {
    return std::visit([](const auto& m) { return std::remove_cvref_t<decltype(m)>::kMessageType; }, payload);
}

Status read_frame_length(std::span<const std::uint8_t> header, std::size_t& body_length) noexcept {
    if (header.size() < kFrameHeaderBytes) {
        return Status::ProtocolError;
    }
    std::uint32_t length = 0;
    for (int i = 3; i >= 0; --i) {
        length = (length << 8) | header[static_cast<std::size_t>(i)];
    }
    if (length == 0 || length > kMaxFrameBytes) {
        return Status::ProtocolError;
    }
    body_length = length;
    return Status::Ok;
}

namespace {

// Splits one frame into its body. Anything other than exactly one complete frame is a ProtocolError.
Status split_frame(std::span<const std::uint8_t> frame, std::span<const std::uint8_t>& body, const char*& why) noexcept {
    std::size_t length = 0;
    if (read_frame_length(frame, length) != Status::Ok) {
        why = "bad frame header";
        return Status::ProtocolError;
    }
    const std::size_t available = frame.size() - kFrameHeaderBytes;
    if (available < length) {
        why = "truncated frame";
        return Status::ProtocolError;
    }
    if (available > length) {
        why = "bytes after the end of the frame";
        return Status::ProtocolError;
    }
    body = frame.subspan(kFrameHeaderBytes, length);
    return Status::Ok;
}

}  // namespace

Result<RequestEnvelope> decode_request_frame(std::span<const std::uint8_t> frame) {
    std::span<const std::uint8_t> body;
    const char* why = "";
    if (const Status s = split_frame(frame, body, why); s != Status::Ok) {
        return failure<RequestEnvelope>(s, why);
    }
    return decode_request_body(body);
}

Result<ResponseEnvelope> decode_response_frame(std::span<const std::uint8_t> frame) {
    std::span<const std::uint8_t> body;
    const char* why = "";
    if (const Status s = split_frame(frame, body, why); s != Status::Ok) {
        return failure<ResponseEnvelope>(s, why);
    }
    return decode_response_body(body);
}

Result<RequestEnvelope> decode_request_body(std::span<const std::uint8_t> body) {
    Reader reader(body);
    RequestEnvelope env;
    Status s = reader.expect_array(5);
    std::uint16_t raw_type = 0;
    constexpr auto kAllU32 = std::numeric_limits<std::uint32_t>::max();
    if (s == Status::Ok) {
        s = reader.read_u64(env.request_id, 0, std::numeric_limits<std::uint64_t>::max());
    }
    if (s == Status::Ok) {
        s = reader.read_u32(env.document_id, 0, kAllU32);
    }
    if (s == Status::Ok) {
        s = reader.read_u32(env.deadline_ms, 0, kAllU32);
    }
    if (s == Status::Ok) {
        s = reader.read_u16(raw_type, 0, std::numeric_limits<std::uint16_t>::max());
    }
    if (s == Status::Ok && !is_defined_message_type(raw_type)) {
        s = reader.fail(Status::ProtocolError, "unknown message type");
    }
    if (s == Status::Ok) {
        env.message_type = static_cast<MessageType>(raw_type);
        const char* why = "";
        s = check_request_header(env.document_id, env.deadline_ms, env.message_type, why);
        if (s != Status::Ok) {
            reader.fail(s, why);
        }
    }
    if (s != Status::Ok) {
        return failure<RequestEnvelope>(s, reader.detail());
    }

    auto payload = decode_request_payload(env.message_type, reader);
    if (!payload) {
        return failure<RequestEnvelope>(payload.status, payload.detail);
    }
    if (!reader.at_end()) {
        return failure<RequestEnvelope>(Status::ProtocolError, "bytes after the end of the message");
    }
    env.payload = std::move(payload.value);
    return {Status::Ok, std::move(env), ""};
}

Result<ResponseEnvelope> decode_response_body(std::span<const std::uint8_t> body) {
    Reader reader(body);
    ResponseEnvelope env;
    Status s = reader.expect_array(4);
    std::uint16_t raw_status = 0;
    std::uint16_t raw_type = 0;
    if (s == Status::Ok) {
        s = reader.read_u64(env.request_id, 0, std::numeric_limits<std::uint64_t>::max());
    }
    if (s == Status::Ok) {
        s = reader.read_u16(raw_status, 0, std::numeric_limits<std::uint16_t>::max());
    }
    if (s == Status::Ok) {
        s = reader.read_u16(raw_type, 0, std::numeric_limits<std::uint16_t>::max());
    }
    if (s == Status::Ok && !is_defined_status(raw_status)) {
        s = reader.fail(Status::ProtocolError, "unknown status");
    }
    if (s == Status::Ok && !is_defined_message_type(raw_type)) {
        s = reader.fail(Status::ProtocolError, "unknown message type");
    }
    if (s != Status::Ok) {
        return failure<ResponseEnvelope>(s, reader.detail());
    }
    env.status = static_cast<Status>(raw_status);
    env.message_type = static_cast<MessageType>(raw_type);

    if (carries_payload(env.status)) {
        auto payload = decode_reply_payload(env.message_type, reader);
        if (!payload) {
            return failure<ResponseEnvelope>(payload.status, payload.detail);
        }
        env.payload = std::move(payload.value);
    } else if (reader.read_nil() != Status::Ok) {
        return failure<ResponseEnvelope>(Status::ProtocolError, "an error response carries no payload");
    }
    if (!reader.at_end()) {
        return failure<ResponseEnvelope>(Status::ProtocolError, "bytes after the end of the message");
    }
    return {Status::Ok, std::move(env), ""};
}

Result<std::vector<std::uint8_t>> encode_request_frame(
    std::uint64_t request_id, std::uint32_t document_id, std::uint32_t deadline_ms, const RequestPayload& payload) {
    const MessageType type = message_type_of(payload);
    const char* why = "";
    if (const Status s = check_request_header(document_id, deadline_ms, type, why); s != Status::Ok) {
        return failure<std::vector<std::uint8_t>>(s, why);
    }
    Writer writer;
    writer.write_array_header(5);
    writer.write_uint(request_id);
    writer.write_uint(document_id);
    writer.write_uint(deadline_ms);
    writer.write_uint(static_cast<std::uint16_t>(type));
    const Status s = std::visit([&writer](const auto& m) { return m.encode(writer); }, payload);
    if (s != Status::Ok) {
        return failure<std::vector<std::uint8_t>>(s, "payload failed validation or does not fit");
    }
    return finish_frame(writer);
}

Result<std::vector<std::uint8_t>> encode_response_frame(
    std::uint64_t request_id, Status status, MessageType type, const ReplyPayload* payload) {
    using Bytes = std::vector<std::uint8_t>;
    if (!is_defined_status(static_cast<std::uint16_t>(status)) || !is_defined_message_type(static_cast<std::uint16_t>(type))) {
        return failure<Bytes>(Status::InvalidArgument, "unknown status or message type");
    }
    if (carries_payload(status) != (payload != nullptr)) {
        return failure<Bytes>(Status::InvalidArgument, "the payload does not match the status");
    }
    if (payload != nullptr && message_type_of(*payload) != type) {
        return failure<Bytes>(Status::InvalidArgument, "the payload belongs to another message type");
    }
    Writer writer;
    writer.write_array_header(4);
    writer.write_uint(request_id);
    writer.write_uint(static_cast<std::uint16_t>(status));
    writer.write_uint(static_cast<std::uint16_t>(type));
    if (payload == nullptr) {
        writer.write_nil();
    } else if (const Status s = std::visit([&writer](const auto& m) { return m.encode(writer); }, *payload); s != Status::Ok) {
        return failure<Bytes>(s, "payload failed validation or does not fit");
    }
    return finish_frame(writer);
}

}  // namespace mikepdf::protocol
