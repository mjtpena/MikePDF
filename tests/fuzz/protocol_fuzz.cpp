// libFuzzer target for the C++ protocol decoder (ADR-0005, master prompt 12.6).
// NOT BUILT by CMake or build.ps1: the DEV machine has no clang. See README.md in this folder for the command line.
//
// Every decoder is fed the input as a whole frame and as a bare body. A crash, a sanitizer report or a broken invariant
// (an accepted message that cannot be re-encoded, or whose encoding is not a fixed point) is a finding.
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <span>
#include <vector>

#include "quire/protocol/codec.h"

namespace {

using namespace quire::protocol;

[[noreturn]] void broken_invariant() {
    std::abort();
}

void check_request(const Result<RequestEnvelope>& decoded) {
    if (!decoded.ok()) {
        if (decoded.status != Status::ProtocolError && decoded.status != Status::InvalidArgument) {
            broken_invariant();
        }
        return;
    }
    const auto& e = decoded.value;
    const auto first = encode_request_frame(e.request_id, e.document_id, e.deadline_ms, e.payload);
    if (!first.ok()) {
        broken_invariant();
    }
    const auto again = decode_request_frame(first.value);
    if (!again.ok()) {
        broken_invariant();
    }
    const auto second = encode_request_frame(again.value.request_id, again.value.document_id, again.value.deadline_ms, again.value.payload);
    if (!second.ok() || second.value != first.value) {
        broken_invariant();
    }
}

void check_response(const Result<ResponseEnvelope>& decoded) {
    if (!decoded.ok()) {
        if (decoded.status != Status::ProtocolError && decoded.status != Status::InvalidArgument) {
            broken_invariant();
        }
        return;
    }
    const auto& e = decoded.value;
    const auto first = encode_response_frame(e.request_id, e.status, e.message_type, e.payload ? &*e.payload : nullptr);
    if (!first.ok()) {
        broken_invariant();
    }
    const auto again = decode_response_frame(first.value);
    if (!again.ok()) {
        broken_invariant();
    }
    const auto& a = again.value;
    const auto second = encode_response_frame(a.request_id, a.status, a.message_type, a.payload ? &*a.payload : nullptr);
    if (!second.ok() || second.value != first.value) {
        broken_invariant();
    }
}

}  // namespace

extern "C" int LLVMFuzzerTestOneInput(const std::uint8_t* data, std::size_t size) {
    const std::span<const std::uint8_t> input(data, size);
    check_request(decode_request_frame(input));
    check_request(decode_request_body(input));
    check_response(decode_response_frame(input));
    check_response(decode_response_body(input));
    std::size_t length = 0;
    (void)read_frame_length(input, length);
    return 0;
}
