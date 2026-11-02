// The client end of the ShellHost pipe: overlapped I/O under a deadline, and under a cancel event the preview raises when it is unloaded. Frames are the
// engine's (4-byte length, MessagePack, ADR-0005); the length is checked against the protocol's limit before the body is read.
#pragma once

#include <Windows.h>

#include <chrono>
#include <cstdint>
#include <span>
#include <vector>

#include "section.h"

namespace mikepdf::shellext {

using Clock = std::chrono::steady_clock;
using Deadline = Clock::time_point;

[[nodiscard]] inline Deadline after(std::chrono::milliseconds span) noexcept { return Clock::now() + span; }

/// Milliseconds left before `deadline`, 0 when it has passed.
[[nodiscard]] std::uint32_t remaining_ms(Deadline deadline) noexcept;

class PipeChannel {
public:
    PipeChannel() noexcept = default;
    PipeChannel(const PipeChannel&) = delete;
    PipeChannel& operator=(const PipeChannel&) = delete;
    ~PipeChannel() { close(); }

    /// Opens the pipe once, without waiting for a server to appear: ERROR_FILE_NOT_FOUND when there is none, ERROR_PIPE_BUSY when every instance is taken
    /// (the caller waits and tries again), ERROR_ACCESS_DENIED when the DACL refuses us. The server is only given the right to identify us.
    [[nodiscard]] HRESULT open(const wchar_t* path) noexcept;

    [[nodiscard]] HRESULT write_all(std::span<const std::uint8_t> bytes, Deadline deadline, HANDLE cancel = nullptr) noexcept;
    [[nodiscard]] HRESULT read_exact(std::span<std::uint8_t> bytes, Deadline deadline, HANDLE cancel = nullptr) noexcept;
    /// Reads one frame and returns its body (the 4-byte prefix consumed). A length of 0 or above the protocol's limit is ERROR_INVALID_DATA.
    [[nodiscard]] HRESULT read_frame_body(std::vector<std::uint8_t>& body, Deadline deadline, HANDLE cancel = nullptr);

    [[nodiscard]] HANDLE native() const noexcept { return pipe_.get(); }
    [[nodiscard]] bool is_open() const noexcept { return static_cast<bool>(pipe_); }
    void close() noexcept { pipe_.reset(); }

private:
    [[nodiscard]] HRESULT transfer(bool write, std::uint8_t* data, std::size_t size, Deadline deadline, HANDLE cancel) noexcept;

    UniqueHandle pipe_;
};

}  // namespace mikepdf::shellext
