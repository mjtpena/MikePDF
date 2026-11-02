#include "bitmap.h"

#include <cstring>

#include "validation.h"

namespace mikepdf::shellext {

HRESULT make_bitmap(const std::uint8_t* pixels, std::uint32_t width, std::uint32_t height, HBITMAP& out) noexcept {
    out = nullptr;
    if (pixels == nullptr || width < 1 || height < 1 || width > kMaxEdge || height > kMaxEdge) {
        return E_INVALIDARG;
    }
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = static_cast<LONG>(width);
    info.bmiHeader.biHeight = -static_cast<LONG>(height);  // top-down
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP bitmap = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (bitmap == nullptr || bits == nullptr) {
        return E_OUTOFMEMORY;
    }
    const std::size_t count = static_cast<std::size_t>(width) * height;
    std::memcpy(bits, pixels, count * 4);
    auto* words = static_cast<std::uint32_t*>(bits);
    for (std::size_t i = 0; i < count; ++i) {
        words[i] |= 0xFF000000u;
    }
    out = bitmap;
    return S_OK;
}

}  // namespace mikepdf::shellext
