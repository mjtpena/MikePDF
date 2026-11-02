// TEST ONLY. An unsigned DLL for the sandbox probe. If its code runs, it says so through the probe's own output channel.
// It does nothing else: no files, no registry, no network.
#include <Windows.h>

namespace {
using EmitFn = void (*)(const char*);
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        if (const HMODULE host = GetModuleHandleW(nullptr)) {
            if (const auto emit = reinterpret_cast<EmitFn>(GetProcAddress(host, "quire_probe_emit"))) {
                emit("MARKER\tROGUE-CODE-RAN");
            }
        }
    }
    return TRUE;
}
