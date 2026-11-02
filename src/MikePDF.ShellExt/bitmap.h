#pragma once

#include <Windows.h>

#include <cstdint>

namespace mikepdf::shellext {

/// Builds a 32-bit top-down DIB section from `width` x `height` BGRA pixels (stride exactly 4 x width, already validated by check_render_reply; re-checked here
/// cheaply). The engine draws an opaque white page, so premultiplied and straight alpha are the same; the alpha byte is forced to 255 rather than trusted.
[[nodiscard]] HRESULT make_bitmap(const std::uint8_t* pixels, std::uint32_t width, std::uint32_t height, HBITMAP& out) noexcept;

}  // namespace mikepdf::shellext
