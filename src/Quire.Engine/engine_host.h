// The engine process's two lanes (ADR-0005 rule 2):
//   - the I/O lane (the calling thread of run()) reads frames and answers Hello, Ping, CancelBefore and Shutdown itself, so
//     the broker's heartbeat keeps working during a long render;
//   - the engine thread owns PDFium and runs everything else from a work queue.
// Anything the broker sends that is malformed or out of sequence ends the process: there is no recovery from a peer that
// breaks the protocol, and the broker restarts us.
#pragma once

#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>

#include "engine_core.h"
#include "pipe_client.h"
#include "slot_memory.h"

namespace quire::engine {

enum ExitCode : int {
    kExitOk = 0,
    kExitBadArguments = 2,
    kExitSectionFailed = 3,
    kExitPipeFailed = 4,
    kExitProtocolViolation = 5,
    kExitPdfiumFailed = 6,
    kExitPolicyFailed = 7,
    kExitBrokerGone = 8,
    kExitInternalError = 9,
};

class EngineHost {
public:
    EngineHost(EngineKind kind, SlotMemory slots, std::wstring pipe_name, std::uint32_t parent_pid);

    /// Connects, serves until Shutdown or until the pipe breaks, and returns the process exit code.
    /// Protocol violations and internal errors do not return: they terminate the process.
    [[nodiscard]] int run();

private:
    struct Work {
        protocol::RequestEnvelope request;
        Clock::time_point received;
    };

    void engine_thread_main();
    void handle_frame(protocol::RequestEnvelope request, Clock::time_point received, bool& shutting_down);
    void send_reply(std::uint64_t request_id, protocol::MessageType type, const Reply& reply);
    bool wait_until_ready();

    EngineCore core_;
    std::wstring pipe_name_;
    std::uint32_t parent_pid_;
    PipeClient pipe_;

    std::mutex mutex_;
    std::condition_variable cv_;
    std::deque<Work> queue_;
    bool stopping_{false};
    enum class Readiness { Starting, Ready, Failed } readiness_{Readiness::Starting};
    bool engine_thread_done_{false};
    std::thread engine_thread_;
};

/// Writes one diagnostic line to the broker log pipe (log_pipe.h). Never put document content, passwords or paths here.
void log_line(const char* text) noexcept;

/// Logs and terminates the process immediately, without running destructors.
[[noreturn]] void die(int exit_code, const char* reason) noexcept;

}  // namespace quire::engine
