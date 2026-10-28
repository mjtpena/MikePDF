#include "handle_guard.h"

namespace quire::engine {

namespace {
constexpr DWORD kStatusInvalidHandle = 0xC0000008;
constexpr DWORD kStatusHandleNotClosable = 0xC0000235;

int filter_invalid_handle(DWORD code) noexcept {
    return code == kStatusInvalidHandle || code == kStatusHandleNotClosable ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH;
}
}  // namespace

DWORD file_type_or_unknown(HANDLE handle) noexcept {
    __try {
        return GetFileType(handle);
    } __except (filter_invalid_handle(GetExceptionCode())) {
        return FILE_TYPE_UNKNOWN;
    }
}

void close_if_handle(HANDLE handle) noexcept {
    __try {
        CloseHandle(handle);
    } __except (filter_invalid_handle(GetExceptionCode())) {
    }
}

void* map_view_or_null(HANDLE section, DWORD access) noexcept {
    __try {
        return MapViewOfFile(section, access, 0, 0, 0);
    } __except (filter_invalid_handle(GetExceptionCode())) {
        return nullptr;
    }
}

}  // namespace quire::engine
