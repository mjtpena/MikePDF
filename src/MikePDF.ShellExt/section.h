// Handles and sections. The handler never parses the bytes it is given: it copies the IStream into an unnamed, pagefile-backed section, and ShellHost
// duplicates the handle from this process (ADR-0018 decision 3).
#pragma once

#include <Windows.h>
#include <objidl.h>

#include <cstdint>
#include <utility>

namespace mikepdf::shellext {

/// An owned kernel handle that is closed exactly once. Not for file handles that can be INVALID_HANDLE_VALUE: null is the empty value.
class UniqueHandle {
public:
    UniqueHandle() noexcept = default;
    explicit UniqueHandle(HANDLE handle) noexcept : handle_(handle) {}
    UniqueHandle(UniqueHandle&& other) noexcept : handle_(std::exchange(other.handle_, nullptr)) {}
    UniqueHandle& operator=(UniqueHandle&& other) noexcept {
        if (this != &other) {
            reset(std::exchange(other.handle_, nullptr));
        }
        return *this;
    }
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    ~UniqueHandle() { reset(); }

    [[nodiscard]] HANDLE get() const noexcept { return handle_; }
    [[nodiscard]] explicit operator bool() const noexcept { return handle_ != nullptr; }
    void reset(HANDLE handle = nullptr) noexcept {
        if (handle_ != nullptr) {
            CloseHandle(handle_);
        }
        handle_ = handle;
    }
    /// The value ShellHost is told in a message: valid in this process only, so ShellHost duplicates it from here.
    [[nodiscard]] std::uint64_t value() const noexcept { return static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(handle_)); }

private:
    HANDLE handle_{nullptr};
};

/// An unnamed section and its size in bytes.
struct Section {
    UniqueHandle handle;
    std::uint64_t bytes{0};
};

/// Creates an unnamed read-write section of `bytes` (at least 1, at most 4 GiB - 1). The default DACL applies.
[[nodiscard]] HRESULT create_section(std::uint64_t bytes, Section& out) noexcept;

/// Copies the whole stream into a new section. Fails, before reading a byte, for an empty stream (ERROR_FILE_INVALID) or one over `max_bytes`
/// (ERROR_FILE_TOO_LARGE); fails if the stream ends early. `out.bytes` is the stream's size.
[[nodiscard]] HRESULT copy_stream_to_section(IStream& stream, std::uint64_t max_bytes, Section& out) noexcept;

/// A read-only mapping of the first `bytes` bytes of a section, unmapped on destruction.
class MappedView {
public:
    MappedView() noexcept = default;
    MappedView(const MappedView&) = delete;
    MappedView& operator=(const MappedView&) = delete;
    ~MappedView() { unmap(); }

    [[nodiscard]] HRESULT map(HANDLE section, std::uint64_t bytes) noexcept;
    [[nodiscard]] const std::uint8_t* data() const noexcept { return static_cast<const std::uint8_t*>(view_); }
    void unmap() noexcept {
        if (view_ != nullptr) {
            UnmapViewOfFile(view_);
            view_ = nullptr;
        }
    }

private:
    void* view_{nullptr};
};

}  // namespace mikepdf::shellext
