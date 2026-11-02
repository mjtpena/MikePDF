// MikePDF.Engine entry point (ADR-0004, ADR-0005). Started by the broker inside an AppContainer. Nothing is inherited: no handle, no
// file path. The slot ring arrives later, as a duplicated handle announced by AttachSlotRing.
#include <Windows.h>

#include <string_view>
#include <vector>

#include "command_line.h"
#include "engine_host.h"
#include "log_pipe.h"

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    using namespace mikepdf::engine;

    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    HeapSetInformation(nullptr, HeapEnableTerminationOnCorruption, nullptr, 0);
    // Any LoadLibrary from here on looks only in System32 and the application directory.
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR);
    connect_log_pipe();

    std::vector<std::wstring_view> args;
    for (int i = 1; i < __argc; ++i) {
        args.emplace_back(__wargv[i]);
    }
    EngineArguments parsed;
    if (const std::string error = parse_command_line(args, parsed); !error.empty()) {
        die(kExitBadArguments, error.c_str());
    }

    EngineHost host(parsed.kind, SlotMemory{}, parsed.pipe_name, parsed.parent_pid);
    return host.run();
}
