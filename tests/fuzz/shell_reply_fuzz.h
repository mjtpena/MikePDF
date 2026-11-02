// The body of the handler-reply fuzz target (SHELL-18), shared by the libFuzzer entry point (shell_reply_fuzz.cpp) and by the unit test that replays the seed corpus on every
// build (tests/MikePDF.ShellExt.Tests/fuzz_seed_tests.cpp), so the checked-in seeds can never silently stop passing.
//
// Input layout: 8 bytes of context, then the response body.
//   [0..2) the request's maxWidth, [2..4) maxHeight (little endian u16, as the handler clamps them to 16..1024)
//   [4..8) the size in bytes of the pixel section the handler made
//   the rest: a response body (the frame's length prefix is not part of it; the whole input is also tried as a frame, which exercises the length checks)
//
// What is checked: no crash or sanitizer report in the decoder or in check_render_reply / check_open_reply, and the invariant that matters for memory safety: whenever
// check_render_reply accepts a reply, width and height are at least 1 and at most the request, the stride is exactly four times the width, and stride x height (64-bit) is
// within the section, so a bitmap built from it never reads outside it.
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <span>
#include <variant>

#include "mikepdf/protocol/codec.h"
#include "validation.h"

namespace mikepdf::fuzz {

[[noreturn]] inline void shell_reply_broken_invariant() { std::abort(); }

inline void shell_reply_check_payload(const protocol::ReplyPayload& payload, std::uint32_t max_width, std::uint32_t max_height, std::uint64_t section_bytes) {
    if (const auto* render = std::get_if<protocol::ShellRenderPageReply>(&payload)) {
        const auto checked = shellext::check_render_reply(*render, max_width, max_height, section_bytes);
        if (checked.ok) {
            const std::uint64_t width = render->width;
            const std::uint64_t height = render->height;
            const std::uint64_t stride = render->stride;
            if (width < 1 || height < 1 || width > max_width || height > max_height || stride != width * 4 || checked.bytes != stride * height || checked.bytes > section_bytes ||
                checked.bytes > shellext::kMaxPixelBytes) {
                shell_reply_broken_invariant();
            }
        }
    } else if (const auto* open = std::get_if<protocol::ShellOpenReply>(&payload)) {
        const bool ok = shellext::check_open_reply(*open);
        if (ok && (open->document_id < 1 || open->page_count < 1)) {
            shell_reply_broken_invariant();
        }
    }
}

inline void shell_reply_one_input(const std::uint8_t* data, std::size_t size) {
    // The whole input as a frame first: the length prefix and the body limits are the first line of defence.
    (void)protocol::decode_response_frame(std::span<const std::uint8_t>(data, size));
    std::size_t length = 0;
    (void)protocol::read_frame_length(std::span<const std::uint8_t>(data, size), length);
    if (size < 8) {
        return;
    }
    const std::uint32_t max_width = shellext::clamp_edge(static_cast<std::uint32_t>(data[0]) | (static_cast<std::uint32_t>(data[1]) << 8));
    const std::uint32_t max_height = shellext::clamp_edge(static_cast<std::uint32_t>(data[2]) | (static_cast<std::uint32_t>(data[3]) << 8));
    const std::uint64_t section = static_cast<std::uint64_t>(data[4]) | (static_cast<std::uint64_t>(data[5]) << 8) | (static_cast<std::uint64_t>(data[6]) << 16) |
                                  (static_cast<std::uint64_t>(data[7]) << 24);
    const auto decoded = protocol::decode_response_body(std::span<const std::uint8_t>(data + 8, size - 8));
    if (decoded.ok() && decoded.value.payload) {
        shell_reply_check_payload(*decoded.value.payload, max_width, max_height, section);
    }
}

}  // namespace mikepdf::fuzz
