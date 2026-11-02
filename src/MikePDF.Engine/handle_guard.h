// Handle values arrive from the broker as numbers. The engine runs with strict handle checks (ADR-0004), under which touching a
// value that is not a handle raises STATUS_INVALID_HANDLE instead of failing quietly. The broker is trusted to send real ones, but a
// wrong one must cost one request, not the process. These helpers contain that exception; they have no C++ objects so that __try is allowed.
#pragma once

#include <Windows.h>

namespace mikepdf::engine {

/// FILE_TYPE_UNKNOWN for anything that is not a live handle.
[[nodiscard]] DWORD file_type_or_unknown(HANDLE handle) noexcept;

/// Closes `handle`; a value that is not a handle is ignored.
void close_if_handle(HANDLE handle) noexcept;

/// MapViewOfFile for a section handle value; nullptr if the value is not a section handle that grants `access`.
[[nodiscard]] void* map_view_or_null(HANDLE section, DWORD access) noexcept;

}  // namespace mikepdf::engine
