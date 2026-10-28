// The engine's end of the broker's named pipe (ADR-0005 rule 1): overlapped I/O, one reader (the I/O lane) and any
// number of writers serialised by a lock. A synchronous handle shared by a reader and a writer thread would deadlock.
#pragma once

#include <cstdint>
#include <mutex>
#include <span>
#include <string_view>

namespace quire::engine {

/// Pipe names are 32 lower-case hex digits (128 random bits). Anything else is refused before it is used in a path.
[[nodiscard]] bool is_valid_pipe_name(std::wstring_view name) noexcept;

class PipeClient {
public:
    PipeClient() = default;
    ~PipeClient();
    PipeClient(const PipeClient&) = delete;
    PipeClient& operator=(const PipeClient&) = delete;

    /// Opens `\\.\pipe\<name>` overlapped, with impersonation disabled, and checks that the server is `expected_server_pid`.
    /// Returns 0, or the Win32 error (ERROR_ACCESS_DENIED for a wrong server).
    [[nodiscard]] unsigned long connect(std::wstring_view name, std::uint32_t expected_server_pid) noexcept;

    /// I/O lane only. False if the pipe closed or failed.
    [[nodiscard]] bool read_exact(std::span<std::uint8_t> out) noexcept;

    /// Any thread. False if the pipe closed, failed or the broker stopped reading for 10 s.
    [[nodiscard]] bool write_all(std::span<const std::uint8_t> data) noexcept;

private:
    void* pipe_{nullptr};
    void* read_event_{nullptr};
    void* write_event_{nullptr};
    std::mutex write_mutex_;
};

}  // namespace quire::engine
