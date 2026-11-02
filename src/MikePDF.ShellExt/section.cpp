#include "section.h"

#include <algorithm>

namespace mikepdf::shellext {

HRESULT create_section(std::uint64_t bytes, Section& out) noexcept {
    if (bytes == 0 || bytes > 0xFFFFFFFFull) {
        return E_INVALIDARG;
    }
    HANDLE handle = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, static_cast<DWORD>(bytes >> 32), static_cast<DWORD>(bytes & 0xFFFFFFFFull), nullptr);
    if (handle == nullptr) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    out.handle.reset(handle);
    out.bytes = bytes;
    return S_OK;
}

HRESULT copy_stream_to_section(IStream& stream, std::uint64_t max_bytes, Section& out) noexcept {
    STATSTG stat{};
    HRESULT hr = stream.Stat(&stat, STATFLAG_NONAME);
    if (FAILED(hr)) {
        return hr;
    }
    const std::uint64_t size = stat.cbSize.QuadPart;
    if (size == 0) {
        return HRESULT_FROM_WIN32(ERROR_FILE_INVALID);
    }
    if (size > max_bytes) {
        return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
    }
    const LARGE_INTEGER start{};
    hr = stream.Seek(start, STREAM_SEEK_SET, nullptr);
    if (FAILED(hr)) {
        return hr;
    }

    Section section;
    hr = create_section(size, section);
    if (FAILED(hr)) {
        return hr;
    }
    void* view = MapViewOfFile(section.handle.get(), FILE_MAP_WRITE, 0, 0, static_cast<SIZE_T>(size));
    if (view == nullptr) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    // 1 MiB reads: smaller ones cost more (S6b measured 58.6 ms with 4 KiB against 25.8 ms with 64 KiB for 30 MB).
    constexpr ULONG kChunk = 1u << 20;
    auto* destination = static_cast<std::uint8_t*>(view);
    std::uint64_t done = 0;
    hr = S_OK;
    while (done < size) {
        ULONG got = 0;
        const ULONG want = static_cast<ULONG>(std::min<std::uint64_t>(kChunk, size - done));
        hr = stream.Read(destination + done, want, &got);
        if (FAILED(hr) || got == 0) {
            break;
        }
        done += got;
    }
    UnmapViewOfFile(view);
    if (done != size) {
        return FAILED(hr) ? hr : HRESULT_FROM_WIN32(ERROR_HANDLE_EOF);
    }
    out = std::move(section);
    return S_OK;
}

HRESULT MappedView::map(HANDLE section, std::uint64_t bytes) noexcept {
    unmap();
    if (bytes == 0 || bytes > 0xFFFFFFFFull) {
        return E_INVALIDARG;
    }
    view_ = MapViewOfFile(section, FILE_MAP_READ, 0, 0, static_cast<SIZE_T>(bytes));
    return view_ != nullptr ? S_OK : HRESULT_FROM_WIN32(GetLastError());
}

}  // namespace mikepdf::shellext
