#include "runtime_policy.h"

#include <Windows.h>

namespace mikepdf::engine {

unsigned long apply_runtime_signature_policy() noexcept {
    PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY policy{};
    policy.MicrosoftSignedOnly = 1;
    if (!SetProcessMitigationPolicy(ProcessSignaturePolicy, &policy, sizeof(policy))) {
        return GetLastError();
    }
    return 0;
}

}  // namespace mikepdf::engine
