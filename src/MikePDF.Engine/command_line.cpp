#include "command_line.h"

#include <optional>

#include "pipe_client.h"

namespace mikepdf::engine {

namespace {

std::optional<std::uint64_t> parse_decimal(std::wstring_view text) {
    if (text.empty() || text.size() > 20) {
        return std::nullopt;
    }
    std::uint64_t value = 0;
    for (const wchar_t c : text) {
        if (c < L'0' || c > L'9') {
            return std::nullopt;
        }
        const std::uint64_t digit = static_cast<std::uint64_t>(c - L'0');
        if (value > (UINT64_MAX - digit) / 10) {
            return std::nullopt;
        }
        value = value * 10 + digit;
    }
    return value;
}

}  // namespace

std::string parse_command_line(std::span<const std::wstring_view> args, EngineArguments& out) {
    bool have_pipe = false, have_parent = false, have_kind = false;
    if (args.size() % 2 != 0) {
        return "option without a value";
    }
    for (std::size_t i = 0; i < args.size(); i += 2) {
        const std::wstring_view name = args[i];
        const std::wstring_view value = args[i + 1];
        if (name == L"--pipe") {
            if (have_pipe || !is_valid_pipe_name(value)) {
                return "bad --pipe";
            }
            out.pipe_name = std::wstring(value);
            have_pipe = true;
        } else if (name == L"--parent") {
            const auto pid = parse_decimal(value);
            if (have_parent || !pid || *pid == 0 || *pid > UINT32_MAX) {
                return "bad --parent";
            }
            out.parent_pid = static_cast<std::uint32_t>(*pid);
            have_parent = true;
        } else if (name == L"--kind") {
            if (have_kind || (value != L"document" && value != L"pool")) {
                return "bad --kind";
            }
            out.kind = value == L"pool" ? EngineKind::Pool : EngineKind::Document;
            have_kind = true;
        } else {
            return "unknown option";
        }
    }
    if (!(have_pipe && have_parent && have_kind)) {
        return "missing option";
    }
    return {};
}

}  // namespace mikepdf::engine
