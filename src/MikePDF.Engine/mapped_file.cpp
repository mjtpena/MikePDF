#include "mapped_file.h"

#include <Windows.h>

#include "handle_guard.h"

namespace mikepdf::engine {

using protocol::Result;
using protocol::Status;

namespace {

struct HandleCloser {
    HANDLE handle;
    explicit HandleCloser(HANDLE h) noexcept : handle(h) {}
    ~HandleCloser() {
        if (handle != nullptr) {
            close_if_handle(handle);
        }
    }
    HandleCloser(const HandleCloser&) = delete;
    HandleCloser& operator=(const HandleCloser&) = delete;
};

HANDLE to_handle(std::uint64_t value) noexcept { return reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(value)); }

Result<MappedFile> fail(Status status, const char* detail) { return {status, MappedFile{}, detail}; }
}  // namespace

MappedFile::~MappedFile() {
    if (view_ != nullptr) {
        UnmapViewOfFile(view_);
    }
}

MappedFile& MappedFile::operator=(MappedFile&& other) noexcept {
    if (this != &other) {
        if (view_ != nullptr) {
            UnmapViewOfFile(view_);
        }
        view_ = std::exchange(other.view_, nullptr);
        size_ = std::exchange(other.size_, 0);
    }
    return *this;
}

void MappedFile::discard_handle(std::uint64_t handle_value) noexcept {
    const HANDLE handle = to_handle(handle_value);
    if (handle != nullptr && handle != INVALID_HANDLE_VALUE) {
        close_if_handle(handle);
    }
}

Result<MappedFile> MappedFile::from_handle(std::uint64_t handle_value) noexcept {
    const HANDLE file = to_handle(handle_value);
    if (file == nullptr || file == INVALID_HANDLE_VALUE) {
        return fail(Status::InvalidArgument, "no handle");
    }
    // Whatever it is, it was given to us to consume (closing a value that is not a handle is harmless here, see above).
    HandleCloser file_guard(file);
    if (file_type_or_unknown(file) != FILE_TYPE_DISK) {
        return fail(Status::InvalidArgument, "not a disk file");
    }
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size)) {
        return fail(Status::PermissionDenied, "cannot query the file size");
    }
    if (size.QuadPart <= 0) {
        return fail(Status::Damaged, "empty file");
    }
    if (static_cast<unsigned long long>(size.QuadPart) > static_cast<unsigned long long>(SIZE_MAX)) {
        return fail(Status::Unsupported, "file larger than the address space");
    }
    HANDLE mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
    if (mapping == nullptr) {
        return fail(Status::PermissionDenied, "cannot create the mapping");
    }
    HandleCloser mapping_guard(mapping);
    const void* view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0);
    if (view == nullptr) {
        return fail(Status::Unsupported, "cannot map the file");
    }
    MappedFile mapped;
    mapped.view_ = view;
    mapped.size_ = static_cast<std::size_t>(size.QuadPart);
    return {Status::Ok, std::move(mapped), ""};
}

}  // namespace mikepdf::engine
