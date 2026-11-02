// libFuzzer target for the handler's reply decoder (SHELL-18): everything the Explorer thumbnail provider and preview handler believe about a ShellHost reply, on arbitrary
// bytes. The DLL runs unsandboxed inside Explorer's hosts, so this is the code that has to survive a hostile or broken peer. The input layout and the invariants are in
// shell_reply_fuzz.h.
#include <cstddef>
#include <cstdint>

#include "shell_reply_fuzz.h"

extern "C" int LLVMFuzzerTestOneInput(const std::uint8_t* data, std::size_t size) {
    mikepdf::fuzz::shell_reply_one_input(data, size);
    return 0;
}
