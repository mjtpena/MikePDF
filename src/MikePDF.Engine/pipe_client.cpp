#include "pipe_client.h"

#include <Windows.h>

#include <algorithm>
#include <string>

namespace mikepdf::engine {

namespace {
constexpr DWORD kWriteTimeoutMs = 10'000;
constexpr int kMaxBusyRetries = 20;

HANDLE as_handle(void* p) noexcept { return static_cast<HANDLE>(p); }
}  // namespace

bool is_valid_pipe_name(std::wstring_view name) noexcept {
    return name.size() == 32 && std::all_of(name.begin(), name.end(), [](wchar_t c) {
               return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f');
           });
}

PipeClient::~PipeClient() {
    if (pipe_ != nullptr) {
        CloseHandle(as_handle(pipe_));
    }
    if (read_event_ != nullptr) {
        CloseHandle(as_handle(read_event_));
    }
    if (write_event_ != nullptr) {
        CloseHandle(as_handle(write_event_));
    }
}

unsigned long PipeClient::connect(std::wstring_view name, std::uint32_t expected_server_pid) noexcept {
    if (!is_valid_pipe_name(name)) {
        return ERROR_INVALID_NAME;
    }
    read_event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    write_event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (read_event_ == nullptr || write_event_ == nullptr) {
        return GetLastError();
    }
    const std::wstring path = L"\\\\.\\pipe\\" + std::wstring(name);
    HANDLE pipe = INVALID_HANDLE_VALUE;
    for (int attempt = 0; attempt < kMaxBusyRetries; ++attempt) {
        // SECURITY_IDENTIFICATION: the server may learn who we are but cannot act as us.
        pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                           FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) {
            break;
        }
        const DWORD error = GetLastError();
        if (error != ERROR_PIPE_BUSY) {
            return error;
        }
        WaitNamedPipeW(path.c_str(), 500);
    }
    if (pipe == INVALID_HANDLE_VALUE) {
        return ERROR_PIPE_BUSY;
    }
    ULONG server_pid = 0;
    if (!GetNamedPipeServerProcessId(pipe, &server_pid) || server_pid != expected_server_pid) {
        CloseHandle(pipe);
        return ERROR_ACCESS_DENIED;
    }
    pipe_ = pipe;
    return 0;
}

bool PipeClient::read_exact(std::span<std::uint8_t> out) noexcept {
    HANDLE pipe = as_handle(pipe_);
    HANDLE event = as_handle(read_event_);
    std::size_t done = 0;
    while (done < out.size()) {
        OVERLAPPED overlapped{};
        overlapped.hEvent = event;
        ResetEvent(event);
        const DWORD want = static_cast<DWORD>(std::min<std::size_t>(out.size() - done, 1u << 20));
        DWORD got = 0;
        if (!ReadFile(pipe, out.data() + done, want, nullptr, &overlapped) && GetLastError() != ERROR_IO_PENDING) {
            return false;
        }
        if (!GetOverlappedResult(pipe, &overlapped, &got, TRUE) || got == 0) {
            return false;
        }
        done += got;
    }
    return true;
}

bool PipeClient::write_all(std::span<const std::uint8_t> data) noexcept {
    std::lock_guard lock(write_mutex_);
    HANDLE pipe = as_handle(pipe_);
    HANDLE event = as_handle(write_event_);
    std::size_t done = 0;
    while (done < data.size()) {
        OVERLAPPED overlapped{};
        overlapped.hEvent = event;
        ResetEvent(event);
        const DWORD want = static_cast<DWORD>(std::min<std::size_t>(data.size() - done, 1u << 20));
        DWORD put = 0;
        if (!WriteFile(pipe, data.data() + done, want, nullptr, &overlapped) && GetLastError() != ERROR_IO_PENDING) {
            return false;
        }
        if (WaitForSingleObject(event, kWriteTimeoutMs) != WAIT_OBJECT_0) {
            CancelIoEx(pipe, &overlapped);
            GetOverlappedResult(pipe, &overlapped, &put, TRUE);
            return false;
        }
        if (!GetOverlappedResult(pipe, &overlapped, &put, FALSE) || put == 0) {
            return false;
        }
        done += put;
    }
    return true;
}

}  // namespace mikepdf::engine
