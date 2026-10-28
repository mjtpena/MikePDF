// Diagnostics channel to the broker. No handle is inherited into a sandboxed process (ADR-0004), so there is no inherited stderr either:
// the broker creates a log pipe and names it in QUIRE_LOG_PIPE (32 lower-case hex digits). The process opens it once, early.
// Never log document content, passwords or paths.
#pragma once

#include <string_view>

namespace quire::engine {

/// Opens the pipe named by QUIRE_LOG_PIPE for writing. Returns false (and logging stays a no-op) if there is none.
bool connect_log_pipe() noexcept;

/// One line. Safe from any thread; silent if the pipe is not connected.
void write_log_line(std::string_view text) noexcept;

}  // namespace quire::engine
