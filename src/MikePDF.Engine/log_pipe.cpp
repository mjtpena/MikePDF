#include "log_pipe.h"

#include <Windows.h>

#include <algorithm>
#include <string>

namespace mikepdf::engine {

namespace {
HANDLE g_pipe = INVALID_HANDLE_VALUE;
SRWLOCK g_lock = SRWLOCK_INIT;
}  // namespace

bool connect_log_pipe() noexcept {
    wchar_t name[64]{};
    const DWORD length = GetEnvironmentVariableW(L"MIKEPDF_LOG_PIPE", name, static_cast<DWORD>(std::size(name)));
    if (length != 32 || !std::all_of(name, name + length, [](wchar_t c) { return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'); })) {
        return false;
    }
    const std::wstring path = L"\\\\.\\pipe\\" + std::wstring(name, length);
    const HANDLE pipe = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) {
        return false;
    }
    AcquireSRWLockExclusive(&g_lock);
    g_pipe = pipe;
    ReleaseSRWLockExclusive(&g_lock);
    return true;
}

void write_log_line(std::string_view text) noexcept {
    AcquireSRWLockExclusive(&g_lock);
    if (g_pipe != INVALID_HANDLE_VALUE) {
        std::string line(text);
        line += '\n';
        DWORD written = 0;
        WriteFile(g_pipe, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    }
    ReleaseSRWLockExclusive(&g_lock);
}

}  // namespace mikepdf::engine
