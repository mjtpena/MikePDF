// S6b test host (THROWAWAY). Loads the handler DLL by path (no registration anywhere) and calls it the way Explorer's thumbnail path and prevhost do:
// DllGetClassObject -> IClassFactory::CreateInstance -> IInitializeWithStream::Initialize(file stream) -> IThumbnailProvider::GetThumbnail, or
// IPreviewHandler::SetWindow/DoPreview on a hidden parent window. Never shows a window.
#define _CRT_SECURE_NO_WARNINGS
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <psapi.h>
#include <sddl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <thumbcache.h>
#include <propsys.h>
#include <shobjidl.h>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "s6b_proto.h"

static const CLSID CLSID_S6bThumb = {0x6B7C2D10, 0x5A1E, 0x4F3B, {0x9C, 0x11, 0x0A, 0x5B, 0x6D, 0x6B, 0x00, 0x01}};
static const CLSID CLSID_S6bPreview = {0x6B7C2D10, 0x5A1E, 0x4F3B, {0x9C, 0x11, 0x0A, 0x5B, 0x6D, 0x6B, 0x00, 0x02}};

static double NowMs() {
    static LARGE_INTEGER f = [] { LARGE_INTEGER x; QueryPerformanceFrequency(&x); return x; }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return double(c.QuadPart) * 1000.0 / double(f.QuadPart);
}

static FILE* g_out = stdout;
#define POUT(...) do { fprintf(g_out, __VA_ARGS__); fflush(g_out); } while (0)

struct Opts {
    std::wstring dll, pdf, out, save;
    UINT cx = 256;
    int runs = 20;
    bool cold = false, preview = false, lowIl = false, killAfter = false, memStats = false;
    std::wstring label = L"run";
};

static std::wstring PipePath() {
    wchar_t name[128];
    DWORD n = GetEnvironmentVariableW(L"S6B_PIPE", name, 128);
    return std::wstring(L"\\\\.\\pipe\\") + (n && n < 128 ? name : L"mikepdf-s6b-shell");
}

// Gracefully stops ShellHost (op 9) and waits for the process to be gone. Returns false when none was running.
static bool ShutdownShellHost() {
    HANDLE p = CreateFileW(PipePath().c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (p == INVALID_HANDLE_VALUE) return false;
    ULONG pid = 0;
    GetNamedPipeServerProcessId(p, &pid);
    HANDLE proc = OpenProcess(SYNCHRONIZE, FALSE, pid);
    S6bReq q{};
    q.magic = kS6bMagic;
    q.op = kOpShutdown;
    q.clientPid = GetCurrentProcessId();
    S6bRep r{};
    DWORD n;
    WriteFile(p, &q, sizeof(q), &n, nullptr);
    ReadFile(p, &r, sizeof(r), &n, nullptr);
    CloseHandle(p);
    if (proc) { WaitForSingleObject(proc, 10000); CloseHandle(proc); }
    return true;
}

static ULONG ShellHostPid() {
    HANDLE p = CreateFileW(PipePath().c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (p == INVALID_HANDLE_VALUE) return 0;
    ULONG pid = 0;
    GetNamedPipeServerProcessId(p, &pid);
    CloseHandle(p);
    return pid;
}

static SIZE_T WorkingSetOf(ULONG pid, SIZE_T* priv) {
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!h) return 0;
    PROCESS_MEMORY_COUNTERS_EX m{};
    GetProcessMemoryInfo(h, (PROCESS_MEMORY_COUNTERS*)&m, sizeof(m));
    CloseHandle(h);
    if (priv) *priv = m.PrivateUsage;
    return m.WorkingSetSize;
}

using GetClassObjectFn = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
using TimingsFn = void(*)(S6bTimings*);

static const char* SelfIntegrity() {
    HANDLE t = nullptr;
    OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &t);
    DWORD len = 0;
    GetTokenInformation(t, TokenIntegrityLevel, nullptr, 0, &len);
    auto* b = (TOKEN_MANDATORY_LABEL*)LocalAlloc(LPTR, len);
    const char* r = "?";
    if (b && GetTokenInformation(t, TokenIntegrityLevel, b, len, &len)) {
        DWORD rid = *GetSidSubAuthority(b->Label.Sid, *GetSidSubAuthorityCount(b->Label.Sid) - 1);
        r = rid < SECURITY_MANDATORY_MEDIUM_RID ? "low" : rid < SECURITY_MANDATORY_HIGH_RID ? "medium" : "high or above";
    }
    CloseHandle(t);
    return r;
}

static int RunThumbnail(const Opts& o, HMODULE dll) {
    auto getClass = (GetClassObjectFn)GetProcAddress(dll, "DllGetClassObject");
    auto timings = (TimingsFn)GetProcAddress(dll, "S6bLastTimings");
    if (!getClass || !timings) { POUT("missing exports\n"); return 2; }
    POUT("# self integrity level: %s\n", SelfIntegrity());
    POUT("# label=%ls mode=%s cx=%u runs=%d pdf=%ls\n", o.label.c_str(), o.cold ? "cold" : "warm", o.cx, o.runs, o.pdf.c_str());
    POUT("run,ok,hr,total_ms,copy_ms,connect_ms,launched,open_rt_ms,render_rt_ms,close_ms,convert_ms,srv_stage_us,srv_spawn_us,srv_open_us,srv_render_us,srv_out_us,srv_total_open_us,srv_total_render_us,engine_peak_kb,w,h,pages\n");
    for (int i = 0; i < o.runs; ++i) {
        if (o.cold) ShutdownShellHost();
        // What Explorer's path does: create the handler, give it the file's stream, ask for the bitmap.
        const double t0 = NowMs();
        IClassFactory* f = nullptr;
        HRESULT hr = getClass(CLSID_S6bThumb, IID_PPV_ARGS(&f));
        IInitializeWithStream* init = nullptr;
        IThumbnailProvider* tp = nullptr;
        IStream* stream = nullptr;
        HBITMAP bmp = nullptr;
        WTS_ALPHATYPE alpha{};
        if (SUCCEEDED(hr)) hr = f->CreateInstance(nullptr, IID_PPV_ARGS(&init));
        if (SUCCEEDED(hr)) hr = SHCreateStreamOnFileEx(o.pdf.c_str(), STGM_READ | STGM_SHARE_DENY_NONE, 0, FALSE, nullptr, &stream);
        if (SUCCEEDED(hr)) hr = init->Initialize(stream, STGM_READ);
        if (SUCCEEDED(hr)) hr = init->QueryInterface(IID_PPV_ARGS(&tp));
        if (SUCCEEDED(hr)) hr = tp->GetThumbnail(o.cx, &bmp, &alpha);
        const double total = NowMs() - t0;
        S6bTimings t{};
        timings(&t);
        if (!SUCCEEDED(hr)) POUT("# call failed: hr=0x%08lx connectError=%lu launchError=%lu\n", (unsigned long)hr, (unsigned long)t.connectError, (unsigned long)t.launchError);
        BITMAP bm{};
        if (bmp) GetObjectW(bmp, sizeof(bm), &bm);
        POUT("%d,%d,0x%08lx,%.2f,%.2f,%.2f,%.0f,%.2f,%.2f,%.2f,%.2f,%u,%u,%u,%u,%u,%u,%d,%d,%u\n", i, SUCCEEDED(hr), (unsigned long)hr, total, t.copyMs, t.connectMs, t.launchedShellHost, t.openRtMs,
            t.renderRtMs, t.closeRtMs, t.convertMs, t.open.tStageUs, t.open.tSpawnUs, t.open.tOpenUs, t.render.tRenderUs, t.render.tOutUs, t.open.tTotalUs, t.render.tTotalUs, t.render.enginePeakKb, bm.bmWidth, bm.bmHeight, t.open.pageCount);
        if (bmp && i == 0 && !o.save.empty()) {
            // raw BGRA + a tiny header line, converted to PNG by the driver script
            FILE* fp = _wfopen(o.save.c_str(), L"wb");
            if (fp) {
                std::vector<BYTE> px((size_t)bm.bmWidth * bm.bmHeight * 4);
                GetBitmapBits(bmp, (LONG)px.size(), px.data());
                fprintf(fp, "BGRA %d %d %d\n", bm.bmWidth, bm.bmHeight, (int)alpha);
                fwrite(px.data(), 1, px.size(), fp);
                fclose(fp);
            }
        }
        if (bmp) DeleteObject(bmp);
        if (tp) tp->Release();
        if (init) init->Release();
        if (stream) stream->Release();
        if (f) f->Release();
    }
    if (o.memStats) {
        SIZE_T priv = 0, ws = WorkingSetOf(GetCurrentProcessId(), &priv);
        POUT("# handler-host process after %d calls: working set %zu KB, private %zu KB\n", o.runs, ws / 1024, priv / 1024);
        ULONG sh = ShellHostPid();
        if (sh) { SIZE_T sp = 0, sw = WorkingSetOf(sh, &sp); POUT("# ShellHost pid %lu: working set %zu KB, private %zu KB\n", sh, sw / 1024, sp / 1024); }
    }
    if (o.killAfter) ShutdownShellHost();
    return 0;
}

static int RunPreview(const Opts& o, HMODULE dll) {
    auto getClass = (GetClassObjectFn)GetProcAddress(dll, "DllGetClassObject");
    // A hidden top-level window as the "preview pane": created, never shown.
    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"S6bHiddenPane";
    RegisterClassW(&wc);
    HWND pane = CreateWindowExW(0, L"S6bHiddenPane", L"hidden", WS_OVERLAPPEDWINDOW, 0, 0, 700, 900, nullptr, nullptr, wc.hInstance, nullptr);
    POUT("# label=%ls preview mode=%s runs=%d pdf=%ls\n", o.label.c_str(), o.cold ? "cold" : "warm", o.runs, o.pdf.c_str());
    POUT("run,ok,first_pixel_ms,do_preview_call_ms,painted_ok\n");
    for (int i = 0; i < o.runs; ++i) {
        if (o.cold) ShutdownShellHost();
        IClassFactory* f = nullptr;
        HRESULT hr = getClass(CLSID_S6bPreview, IID_PPV_ARGS(&f));
        IInitializeWithStream* init = nullptr;
        IPreviewHandler* ph = nullptr;
        IOleWindow* ow = nullptr;
        IStream* stream = nullptr;
        if (SUCCEEDED(hr)) hr = f->CreateInstance(nullptr, IID_PPV_ARGS(&init));
        if (SUCCEEDED(hr)) hr = SHCreateStreamOnFileEx(o.pdf.c_str(), STGM_READ | STGM_SHARE_DENY_NONE, 0, FALSE, nullptr, &stream);
        if (SUCCEEDED(hr)) hr = init->Initialize(stream, STGM_READ);
        if (SUCCEEDED(hr)) hr = init->QueryInterface(IID_PPV_ARGS(&ph));
        RECT rc{0, 0, 600, 800};
        double t0 = NowMs(), callMs = 0;
        HWND child = nullptr;
        if (SUCCEEDED(hr)) hr = ph->SetWindow(pane, &rc);
        if (SUCCEEDED(hr)) hr = ph->DoPreview();
        callMs = NowMs() - t0;
        if (SUCCEEDED(hr) && SUCCEEDED(ph->QueryInterface(IID_PPV_ARGS(&ow)))) ow->GetWindow(&child);
        double firstPixel = -1;
        bool painted = false;
        // Pump this thread's messages like a preview host would, until the handler's window has its bitmap.
        while (SUCCEEDED(hr) && NowMs() - t0 < 5000) {
            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
            if (child && GetPropW(child, L"S6B_FIRST_PIXEL_MS")) { firstPixel = NowMs() - t0; break; }
            Sleep(1);
        }
        if (child && firstPixel >= 0) {
            // Ask the window to draw into a memory DC (WM_PRINTCLIENT) and check that it is not blank white.
            HDC dc = GetDC(nullptr);
            HDC mem = CreateCompatibleDC(dc);
            HBITMAP bmp = CreateCompatibleBitmap(dc, 600, 800);
            HGDIOBJ old = SelectObject(mem, bmp);
            SendMessageW(child, WM_PRINTCLIENT, (WPARAM)mem, PRF_CLIENT);
            int dark = 0;
            for (int y = 0; y < 800; y += 4) for (int x = 0; x < 600; x += 4) if ((GetPixel(mem, x, y) & 0xFFFFFF) != 0xFFFFFF) ++dark;
            painted = dark > 50;
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(nullptr, dc);
        }
        POUT("%d,%d,%.2f,%.2f,%d\n", i, firstPixel >= 0, firstPixel, callMs, painted);
        if (ph) ph->Unload();
        if (ow) ow->Release();
        if (ph) ph->Release();
        if (init) init->Release();
        if (stream) stream->Release();
        if (f) f->Release();
    }
    if (o.killAfter) ShutdownShellHost();
    DestroyWindow(pane);
    return 0;
}

// Re-runs this executable with a LOW integrity token (what prevhost uses by default for stream-initialised preview handlers), output to a Low-writable file.
static int RunAsLowIntegrity(int argc, wchar_t** argv, const Opts& o) {
    HANDLE tok = nullptr, dup = nullptr;
    OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT, &tok);
    DuplicateTokenEx(tok, MAXIMUM_ALLOWED, nullptr, SecurityImpersonation, TokenPrimary, &dup);
    PSID low = nullptr;
    ConvertStringSidToSidW(L"S-1-16-4096", &low);
    TOKEN_MANDATORY_LABEL tml{};
    tml.Label.Attributes = SE_GROUP_INTEGRITY;
    tml.Label.Sid = low;
    if (!SetTokenInformation(dup, TokenIntegrityLevel, &tml, sizeof(tml) + GetLengthSid(low))) { POUT("SetTokenInformation failed %lu\n", GetLastError()); return 3; }
    PWSTR lowRoot = nullptr; // %USERPROFILE%\AppData\LocalLow: the folder Windows labels Low for exactly this purpose
    SHGetKnownFolderPath(FOLDERID_LocalAppDataLow, 0, nullptr, &lowRoot);
    std::wstring lowTemp = std::wstring(lowRoot) + L"\\s6b";
    CoTaskMemFree(lowRoot);
    CreateDirectoryW(lowTemp.c_str(), nullptr);
    std::wstring outFile = std::wstring(lowTemp) + L"\\s6b-low-out.txt";
    DeleteFileW(outFile.c_str());
    std::wstring cmd = L"\"" + std::wstring(argv[0]) + L"\"";
    for (int i = 1; i < argc; ++i) {
        if (wcscmp(argv[i], L"--low-il") == 0) continue;
        cmd += L" \"" + std::wstring(argv[i]) + L"\"";
    }
    cmd += L" --out \"" + outFile + L"\"";
    STARTUPINFOW si{sizeof(si)};
    PROCESS_INFORMATION pi{};
    if (!CreateProcessAsUserW(dup, nullptr, cmd.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) { POUT("CreateProcessAsUser failed %lu\n", GetLastError()); return 3; }
    WaitForSingleObject(pi.hProcess, 120000);
    DWORD code = 0;
    GetExitCodeProcess(pi.hProcess, &code);
    POUT("# low-integrity child exit code %lu\n", code);
    FILE* fp = _wfopen(outFile.c_str(), L"rb");
    if (fp) { char buf[4096]; size_t n; while ((n = fread(buf, 1, sizeof(buf), fp)) > 0) fwrite(buf, 1, n, g_out); fclose(fp); } else POUT("# low-integrity child wrote no output\n");
    (void)o;
    return (int)code;
}

int wmain(int argc, wchar_t** argv) {
    Opts o;
    for (int i = 1; i < argc; ++i) {
        std::wstring a = argv[i];
        auto next = [&]() -> std::wstring { return i + 1 < argc ? argv[++i] : L""; };
        if (a == L"--dll") o.dll = next();
        else if (a == L"--pdf") o.pdf = next();
        else if (a == L"--cx") o.cx = (UINT)_wtoi(next().c_str());
        else if (a == L"--runs") o.runs = _wtoi(next().c_str());
        else if (a == L"--cold") o.cold = true;
        else if (a == L"--preview") o.preview = true;
        else if (a == L"--low-il") o.lowIl = true;
        else if (a == L"--out") o.out = next();
        else if (a == L"--save") o.save = next();
        else if (a == L"--label") o.label = next();
        else if (a == L"--mem") o.memStats = true;
        else if (a == L"--kill-after") o.killAfter = true;
        else if (a == L"--shutdown") { return ShutdownShellHost() ? 0 : 1; }
    }
    if (!o.out.empty()) { g_out = _wfopen(o.out.c_str(), L"wb"); if (!g_out) return 4; }
    if (o.lowIl) return RunAsLowIntegrity(argc, argv, o);
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED); // the shell calls handlers on STAs
    HMODULE dll = LoadLibraryW(o.dll.c_str());
    if (!dll) { POUT("LoadLibrary(%ls) failed %lu\n", o.dll.c_str(), GetLastError()); return 2; }
    int rc = o.preview ? RunPreview(o, dll) : RunThumbnail(o, dll);
    return rc;
}
