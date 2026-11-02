// S6b spike wire format between the handler DLL and ShellHost (THROWAWAY). Fixed 72-byte request and reply, little endian.
// The product protocol is the engine framing (4-byte length + MessagePack, generated from the schema); see docs/spikes/S6b-shell-transport.md.
#pragma once
#include <cstdint>

#pragma pack(push, 1)
struct S6bReq {
    uint32_t magic;      // 'S6B1'
    uint32_t op;         // 1 hello, 2 open, 3 render, 4 close, 9 shutdown
    uint32_t deadlineMs;
    uint32_t flags;      // 1 = pixels inline after the reply
    uint32_t cx, cy;     // fit box for render
    uint32_t page;
    uint32_t sessionId;
    uint64_t clientPid;  // must equal GetNamedPipeClientProcessId on the server
    uint64_t srcSection; // handle value valid in the CLIENT; the server duplicates it (server pulls: works from low integrity)
    uint64_t srcBytes;
    uint64_t dstSection; // handle value valid in the client, FILE_MAP_WRITE
    uint64_t dstBytes;
};
struct S6bRep {
    uint32_t magic;
    uint32_t status;     // 0 ok, 101 bad request, 102 handle, 103 busy, 104 timeout, 105 internal, 200+N protocol Status N from the engine
    uint32_t width, height, stride;
    uint32_t pageCount;
    uint32_t sessionId;
    uint32_t flags;      // hello: 1 = the server could open the client process for DuplicateHandle
    float pageW, pageH;
    uint32_t serverPid;
    uint32_t tStageUs;   // open: section -> temp file (needed only because the unchanged engine wants a disk file)
    uint32_t tSpawnUs;   // open: engine acquire (spawn + Hello + ring)
    uint32_t tOpenUs;    // open: OpenDocument + page sizes
    uint32_t tRenderUs;  // render: engine RenderTile
    uint32_t tOutUs;     // render: copy of the pixels to the client's section
    uint32_t enginePeakKb;
    uint32_t tTotalUs;   // server time for this op
};
#pragma pack(pop)
static_assert(sizeof(S6bReq) == 72, "request size");
static_assert(sizeof(S6bRep) == 72, "reply size");

constexpr uint32_t kS6bMagic = 0x31423653;
enum : uint32_t { kOpHello = 1, kOpOpen = 2, kOpRender = 3, kOpClose = 4, kOpShutdown = 9 };

// Filled by the handler DLL after every call; read by the test host through the exported S6bLastTimings.
struct S6bTimings {
    double copyMs;       // IStream -> section
    double connectMs;    // connect, including launching ShellHost when it was not running
    double launchedShellHost; // 1 if this call started ShellHost
    double verifyMs;
    double openRtMs;     // round trip of Open
    double renderRtMs;
    double closeRtMs;
    double convertMs;    // section -> HBITMAP
    double totalMs;
    S6bRep open, render; // the server's own breakdown
    uint32_t hr;
    uint32_t connectError; // last Win32 error seen while connecting (0 = none)
    uint32_t launchError;  // CreateProcess error when this call tried to start ShellHost (0 = started)
};
