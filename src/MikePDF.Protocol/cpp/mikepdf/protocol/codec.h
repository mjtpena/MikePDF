// Frames and envelopes (ADR-0005). The C++ twin of MikePDF.Protocol.ProtocolCodec; both are specified by
// src/MikePDF.Protocol/schema/mikepdf.protocol.json and must accept and reject exactly the same frames.
//
//   frame    = u32le bodyLength, body            (0 < bodyLength <= kMaxFrameBytes)
//   request  = [requestId u64, documentId u32, deadlineMs u32, messageType u16, payload]
//   response = [requestId u64, status u16, messageType u16, payload | nil]
//
// Every decoder treats its input as hostile and returns a Result; none throws or reads outside the span it is given.
#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <vector>

#include "mikepdf/protocol/enums.h"
#include "mikepdf/protocol/messages.h"
#include "mikepdf/protocol/msgpack.h"

namespace mikepdf::protocol {

inline constexpr std::size_t kFrameHeaderBytes = 4;

struct RequestEnvelope {
    std::uint64_t request_id{};
    /// 0 exactly when the message type takes no document.
    std::uint32_t document_id{};
    std::uint32_t deadline_ms{};
    MessageType message_type{};
    RequestPayload payload{};

    bool operator==(const RequestEnvelope&) const = default;
};

struct ResponseEnvelope {
    std::uint64_t request_id{};
    Status status{};
    /// The type of the request being answered. The caller must check it against the pending request.
    MessageType message_type{};
    /// Present exactly when carries_payload(status).
    std::optional<ReplyPayload> payload{};

    bool operator==(const ResponseEnvelope&) const = default;
};

[[nodiscard]] MessageType message_type_of(const RequestPayload& payload) noexcept;
[[nodiscard]] MessageType message_type_of(const ReplyPayload& payload) noexcept;

/// Reads the length prefix from the first four bytes of a stream, so an oversize length is refused before the body is read.
/// ProtocolError if there are fewer than four bytes, the length is 0 or exceeds kMaxFrameBytes.
[[nodiscard]] Status read_frame_length(std::span<const std::uint8_t> header, std::size_t& body_length) noexcept;

/// A frame is the 4-byte prefix plus the body; the buffer must hold exactly one.
[[nodiscard]] Result<RequestEnvelope> decode_request_frame(std::span<const std::uint8_t> frame);
[[nodiscard]] Result<ResponseEnvelope> decode_response_frame(std::span<const std::uint8_t> frame);
/// The body alone, once the prefix has been consumed.
[[nodiscard]] Result<RequestEnvelope> decode_request_body(std::span<const std::uint8_t> body);
[[nodiscard]] Result<ResponseEnvelope> decode_response_body(std::span<const std::uint8_t> body);

/// Validates, then returns a complete frame.
[[nodiscard]] Result<std::vector<std::uint8_t>> encode_request_frame(
    std::uint64_t request_id, std::uint32_t document_id, std::uint32_t deadline_ms, const RequestPayload& payload);

/// `payload` must be present exactly when carries_payload(status) and must belong to `type`.
[[nodiscard]] Result<std::vector<std::uint8_t>> encode_response_frame(
    std::uint64_t request_id, Status status, MessageType type, const ReplyPayload* payload);

}  // namespace mikepdf::protocol
