// Engine command line (ADR-0004/0005). Everything is a value the broker chose; there are no paths and no handles: nothing is inherited.
//
//   MikePDF.Engine.exe --pipe <32 hex> --parent <pid> --kind document|pool
// The slot ring is not here: its handle is duplicated into the process and announced over the pipe (AttachSlotRing).
#pragma once

#include <cstdint>
#include <span>
#include <string>
#include <string_view>

#include "engine_core.h"

namespace mikepdf::engine {

struct EngineArguments {
    std::wstring pipe_name;
    std::uint32_t parent_pid{0};
    EngineKind kind{EngineKind::Document};
};

/// `args` excludes the program name. Returns an empty string on success, else a short reason. Every option is required,
/// exactly once; unknown options and malformed values are refused.
[[nodiscard]] std::string parse_command_line(std::span<const std::wstring_view> args, EngineArguments& out);

}  // namespace mikepdf::engine
