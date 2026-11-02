#include "pipe_channel.h"

#include <algorithm>

#include "mikepdf/protocol/codec.h"

namespace mikepdf::shellext {

std::uint32_t remaining_ms(Deadline deadline) noexcept {
    const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now()).count();
    return left <= 0 ? 0u : static_cast<std::uint32_t>(std::min<long long>(left, 0x7FFFFFFF));
}

HRESULT PipeChannel::open(const wchar_t* path) noexcept {
    close();
    // SECURITY_IDENTIFICATION: whatever listens on this pipe may learn who we are, and may never act as us.
    HANDLE handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    pipe_.reset(handle);
    return S_OK;
}

HRESULT PipeChannel::transfer(bool write, std::uint8_t* data, std::size_t size, Deadline deadline, HANDLE cancel) noexcept {
    if (!pipe_) {
        return HRESULT_FROM_WIN32(ERROR_INVALID_HANDLE);
    }
    UniqueHandle done(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!done) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    std::size_t transferred = 0;
    while (transferred < size) {
        OVERLAPPED overlapped{};
        overlapped.hEvent = done.get();
        ResetEvent(done.get());
        const DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(size - transferred, 1u << 20));
        const BOOL started = write ? WriteFile(pipe_.get(), data + transferred, chunk, nullptr, &overlapped) : ReadFile(pipe_.get(), data + transferred, chunk, nullptr, &overlapped);
        if (!started && GetLastError() != ERROR_IO_PENDING) {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        HANDLE waits[2] = {done.get(), cancel};
        const DWORD count = cancel != nullptr ? 2 : 1;
        const DWORD wait = WaitForMultipleObjects(count, waits, FALSE, remaining_ms(deadline));
        if (wait != WAIT_OBJECT_0) {
            // Timed out or cancelled: stop the I/O and wait for it to finish, because the OVERLAPPED and the buffer are ours and must not be touched afterwards.
            CancelIoEx(pipe_.get(), &overlapped);
            DWORD ignored = 0;
            GetOverlappedResult(pipe_.get(), &overlapped, &ignored, TRUE);
            return HRESULT_FROM_WIN32(wait == WAIT_OBJECT_0 + 1 ? ERROR_OPERATION_ABORTED : ERROR_TIMEOUT);
        }
        DWORD got = 0;
        if (!GetOverlappedResult(pipe_.get(), &overlapped, &got, FALSE)) {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        if (got == 0) {
            return HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE);
        }
        transferred += got;
    }
    return S_OK;
}

HRESULT PipeChannel::write_all(std::span<const std::uint8_t> bytes, Deadline deadline, HANDLE cancel) noexcept {
    return transfer(true, const_cast<std::uint8_t*>(bytes.data()), bytes.size(), deadline, cancel);
}

HRESULT PipeChannel::read_exact(std::span<std::uint8_t> bytes, Deadline deadline, HANDLE cancel) noexcept {
    return transfer(false, bytes.data(), bytes.size(), deadline, cancel);
}

HRESULT PipeChannel::read_frame_body(std::vector<std::uint8_t>& body, Deadline deadline, HANDLE cancel) {
    std::uint8_t header[protocol::kFrameHeaderBytes]{};
    HRESULT hr = read_exact(header, deadline, cancel);
    if (FAILED(hr)) {
        return hr;
    }
    std::size_t length = 0;
    if (protocol::read_frame_length(header, length) != protocol::Status::Ok) {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }
    body.resize(length);
    return read_exact(body, deadline, cancel);
}

}  // namespace mikepdf::shellext
