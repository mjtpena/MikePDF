// Everything the handler believes about ShellHost's replies is checked here, before a bitmap is built (spec section 7 "Hostile input", SHELL-11).
// Pure functions on plain numbers: no handle, no window, no allocation.
#pragma once

#include <cstdint>

#include "mikepdf/protocol/messages.h"

namespace mikepdf::shellext {

inline constexpr std::uint32_t kMinEdge = 16;
inline constexpr std::uint32_t kMaxEdge = 1024;
/// The largest stream the handler copies (spec 3.3); anything bigger fails and Explorer shows the generic icon.
inline constexpr std::uint64_t kMaxStreamBytes = 32ull * 1024 * 1024;
/// The schema's limit for the pixel section (4 MiB: 1024 x 1024 x 4).
inline constexpr std::uint64_t kMaxPixelBytes = 4ull * 1024 * 1024;

/// A requested thumbnail edge, clamped to 16..1024 (spec 3.3 step 1).
[[nodiscard]] constexpr std::uint32_t clamp_edge(std::uint64_t edge) noexcept {
    return edge < kMinEdge ? kMinEdge : (edge > kMaxEdge ? kMaxEdge : static_cast<std::uint32_t>(edge));
}

/// The bytes of a pixel section for a request of `max_width` x `max_height` BGRA pixels, as a 64-bit product (never overflows for the clamped edges).
[[nodiscard]] constexpr std::uint64_t pixel_section_bytes(std::uint32_t max_width, std::uint32_t max_height) noexcept {
    return std::uint64_t{max_width} * max_height * 4;
}

struct CheckedRender {
    bool ok{false};
    /// stride x height, valid when ok.
    std::uint64_t bytes{0};
    /// A string literal naming the first rule that failed, for the debug log. Never shown to the user.
    const char* why{""};
};

/// Checks a ShellRenderPage reply against the request that was sent and the section the pixels are in: width and height at least 1 and at most the request's
/// maxima, stride exactly 4 x width, and stride x height (64-bit) within `section_bytes`. Nothing is allocated and nothing is read from the section.
[[nodiscard]] CheckedRender check_render_reply(const protocol::ShellRenderPageReply& reply, std::uint32_t max_width, std::uint32_t max_height,
                                               std::uint64_t section_bytes) noexcept;

/// Checks a ShellOpen reply: a session id, at least one page, a first page that is a real size. (The schema's own limits were applied by the decoder.)
[[nodiscard]] bool check_open_reply(const protocol::ShellOpenReply& reply) noexcept;

}  // namespace mikepdf::shellext
