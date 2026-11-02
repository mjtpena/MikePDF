// The enterprise policy gate (spec 3.5, SHELL-15): a policy-off machine never starts ShellHost, because the handler reads the policy itself, from the registry,
// before it copies a byte or opens a pipe. The rules are the ones the app's PolicyProvider applies to the same two values (docs/admin/policies.md):
// the machine value wins, a user-level value can only make things stricter, and both policies are "fail closed" (a value that is not a DWORD 0 or 1, or a
// key that cannot be read, switches the feature off). The user's own on/off setting is ShellHost's to apply (SHELL-16): it lives in settings.json, which
// the handler does not read.
#pragma once

#include <Windows.h>

#include <cstdint>
#include <functional>

namespace mikepdf::shellext {

enum class ShellFeature { Thumbnails, Preview };

struct DwordRead {
    enum class Kind { Absent, Valid, Invalid, Failed };
    Kind kind{Kind::Absent};
    std::uint32_t value{0};
};

/// Reads one value from `hive` (HKEY_LOCAL_MACHINE or HKEY_CURRENT_USER) under Software\Policies\MikePDF.
using PolicyReader = std::function<DwordRead(HKEY hive, const wchar_t* value_name)>;

/// The real reader: the registry. Absent means the value (or the key) does not exist; Invalid means it exists with another type or size; Failed means the
/// registry could not be read (access denied and the like).
[[nodiscard]] DwordRead read_policy_from_registry(HKEY hive, const wchar_t* value_name) noexcept;

/// The value name of a feature's policy: ShellThumbnailsEnabled or ShellPreviewEnabled.
[[nodiscard]] const wchar_t* policy_value_name(ShellFeature feature) noexcept;

enum class PolicyState {
    /// Not configured, or configured on.
    Allowed,
    /// Switched off by an organisation (or by a value that cannot be trusted).
    OffByPolicy,
};

[[nodiscard]] PolicyState evaluate_policy(ShellFeature feature, const PolicyReader& reader);

}  // namespace mikepdf::shellext
