#include "policy_gate.h"

namespace mikepdf::shellext {

namespace {
constexpr wchar_t kPolicyKey[] = L"Software\\Policies\\MikePDF";

/// Whether one hive's answer switches the feature off. Absent and a valid 1 do not; a valid 0, an invalid value and a failed read do (fail closed).
bool turns_off(const DwordRead& read) noexcept {
    switch (read.kind) {
        case DwordRead::Kind::Absent:
            return false;
        case DwordRead::Kind::Valid:
            return read.value != 1;  // 0 is off; any other DWORD is out of range, and out of range fails closed
        case DwordRead::Kind::Invalid:
        case DwordRead::Kind::Failed:
            return true;
    }
    return true;
}
}  // namespace

DwordRead read_policy_from_registry(HKEY hive, const wchar_t* value_name) noexcept {
    HKEY key = nullptr;
    const LONG opened = RegOpenKeyExW(hive, kPolicyKey, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key);
    if (opened == ERROR_FILE_NOT_FOUND || opened == ERROR_PATH_NOT_FOUND) {
        return {DwordRead::Kind::Absent, 0};
    }
    if (opened != ERROR_SUCCESS) {
        return {DwordRead::Kind::Failed, 0};
    }
    DWORD type = 0;
    DWORD data = 0;
    DWORD size = sizeof(data);
    const LONG queried = RegQueryValueExW(key, value_name, nullptr, &type, reinterpret_cast<LPBYTE>(&data), &size);
    RegCloseKey(key);
    if (queried == ERROR_FILE_NOT_FOUND) {
        return {DwordRead::Kind::Absent, 0};
    }
    if (queried == ERROR_MORE_DATA || (queried == ERROR_SUCCESS && (type != REG_DWORD || size != sizeof(DWORD)))) {
        return {DwordRead::Kind::Invalid, 0};
    }
    if (queried != ERROR_SUCCESS) {
        return {DwordRead::Kind::Failed, 0};
    }
    return {DwordRead::Kind::Valid, data};
}

const wchar_t* policy_value_name(ShellFeature feature) noexcept {
    return feature == ShellFeature::Thumbnails ? L"ShellThumbnailsEnabled" : L"ShellPreviewEnabled";
}

PolicyState evaluate_policy(ShellFeature feature, const PolicyReader& reader) {
    const wchar_t* name = policy_value_name(feature);
    // Machine first, then the user hive. The user hive can only add restriction, so "off in either" is the whole rule.
    if (turns_off(reader(HKEY_LOCAL_MACHINE, name)) || turns_off(reader(HKEY_CURRENT_USER, name))) {
        return PolicyState::OffByPolicy;
    }
    return PolicyState::Allowed;
}

}  // namespace mikepdf::shellext
