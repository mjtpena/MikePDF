// A read-only view of a file the broker opened and duplicated into this process (ADR-0005 rule 6). The engine never
// sees a path. The file handle and the mapping handle are closed as soon as the view exists; the view keeps the section
// (and with it the broker's deny-write share mode) alive until it is unmapped.
#pragma once

#include <cstdint>
#include <span>
#include <utility>

#include "mikepdf/protocol/msgpack.h"

namespace mikepdf::engine {

class MappedFile {
public:
    MappedFile() noexcept = default;
    ~MappedFile();
    MappedFile(MappedFile&& other) noexcept : view_(std::exchange(other.view_, nullptr)), size_(std::exchange(other.size_, 0)) {}
    MappedFile& operator=(MappedFile&& other) noexcept;
    MappedFile(const MappedFile&) = delete;
    MappedFile& operator=(const MappedFile&) = delete;

    /// Takes ownership of `handle_value` (the handle is closed on every path, success or not).
    /// InvalidArgument: not a disk file handle. PermissionDenied: the handle cannot be read. Damaged: empty file.
    /// Unsupported: the file cannot be mapped (address space).
    [[nodiscard]] static protocol::Result<MappedFile> from_handle(std::uint64_t handle_value) noexcept;

    /// Closes a handle value that arrived in a request that is being refused.
    static void discard_handle(std::uint64_t handle_value) noexcept;

    [[nodiscard]] std::span<const std::uint8_t> bytes() const noexcept {
        return {static_cast<const std::uint8_t*>(view_), size_};
    }

private:
    const void* view_{nullptr};
    std::size_t size_{0};
};

}  // namespace mikepdf::engine
