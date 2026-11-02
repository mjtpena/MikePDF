#include "validation.h"

#include <cmath>

namespace mikepdf::shellext {

CheckedRender check_render_reply(const protocol::ShellRenderPageReply& reply, std::uint32_t max_width, std::uint32_t max_height, std::uint64_t section_bytes) noexcept {
    if (reply.width < 1 || reply.height < 1) {
        return {false, 0, "empty bitmap"};
    }
    if (reply.width > max_width || reply.height > max_height) {
        return {false, 0, "bitmap larger than requested"};
    }
    // 64-bit arithmetic throughout: width and height are untrusted 32-bit values here, whatever the schema said.
    if (std::uint64_t{reply.stride} != std::uint64_t{reply.width} * 4) {
        return {false, 0, "stride is not 4 x width"};
    }
    const std::uint64_t bytes = std::uint64_t{reply.stride} * reply.height;
    if (bytes > section_bytes || bytes > kMaxPixelBytes) {
        return {false, 0, "pixels do not fit the section"};
    }
    return {true, bytes, ""};
}

bool check_open_reply(const protocol::ShellOpenReply& reply) noexcept {
    return reply.document_id >= 1 && reply.page_count >= 1 && std::isfinite(reply.first_width) && std::isfinite(reply.first_height) && reply.first_width > 0.0f &&
           reply.first_height > 0.0f;
}

}  // namespace mikepdf::shellext
