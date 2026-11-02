// S6b CI driver (THROWAWAY). The package's main executable. Runs on a disposable GitHub-hosted runner with the test package installed, and exercises the REAL shell paths:
//  - AssocQueryString: which handler CLSIDs does the shell resolve for .s6bpdf / .s6bexe / .pdf?
//  - IShellItemImageFactory::GetImage on unique files (the shell activates the packaged thumbnail handler in its COM surrogate), cold and warm, with timings;
//  - the same for a file carrying a Zone.Identifier stream (Mark of the Web);
//  - IPreviewHandler activated through COM as Explorer does (CLSCTX_LOCAL_SERVER, PreviewHost surrogate);
//  - activation latency of a packaged com:ExeServer (cold and warm) and the cost of reading a marshaled IStream in it.
// The handler DLL, ShellHost and the ExeServer log to C:\s6b-log, which the workflow uploads.
#define _CRT_SECURE_NO_WARNINGS
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <appmodel.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shobjidl.h>
#include <thumbcache.h>
#include <tlhelp32.h>
#include <propsys.h>

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <string>
#include <vector>

static const CLSID CLSID_S6bPreview = {0x6B7C2D10, 0x5A1E, 0x4F3B, {0x9C, 0x11, 0x0A, 0x5B, 0x6D, 0x6B, 0x00, 0x02}};
static const CLSID CLSID_S6bExe = {0x6B7C2D10, 0x5A1E, 0x4F3B, {0x9C, 0x11, 0x0A, 0x5B, 0x6D, 0x6B, 0x00, 0x03}};

static FILE* g_log;
static double NowMs() {
    static LARGE_INTEGER f = [] { LARGE_INTEGER x; QueryPerformanceFrequency(&x); return x; }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return double(c.QuadPart) * 1000.0 / double(f.QuadPart);
}
static void P(const char* fmt, ...) {
    char line[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(line, _TRUNCATE, fmt, ap);
    va_end(ap);
    fputs(line, stdout);
    fputs("\n", stdout);
    fflush(stdout);
    if (g_log) { fputs(line, g_log); fputs("\n", g_log); fflush(g_log); }
}

static std::string ReadAll(const char* path) {
    HANDLE h = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return {};
    std::string s;
    char buf[65536];
    DWORD n;
    while (ReadFile(h, buf, sizeof(buf), &n, nullptr) && n) s.append(buf, n);
    CloseHandle(h);
    return s;
}
static int CountIn(const char* path, const char* needle) {
    std::string s = ReadAll(path);
    int c = 0;
    for (size_t p = s.find(needle); p != std::string::npos; p = s.find(needle, p + 1)) ++c;
    return c;
}
static std::string LastLineWith(const char* path, const char* needle) {
    std::string s = ReadAll(path), last;
    size_t pos = 0;
    while (pos < s.size()) {
        size_t e = s.find('\n', pos);
        if (e == std::string::npos) e = s.size();
        std::string line = s.substr(pos, e - pos);
        if (line.find(needle) != std::string::npos) last = line;
        pos = e + 1;
    }
    return last;
}

static int KillByName(const wchar_t* name) {
    int n = 0;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W e{sizeof(e)};
    for (BOOL ok = Process32FirstW(snap, &e); ok; ok = Process32NextW(snap, &e)) {
        if (_wcsicmp(e.szExeFile, name) == 0) {
            HANDLE p = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, e.th32ProcessID);
            if (p) { TerminateProcess(p, 1); WaitForSingleObject(p, 5000); CloseHandle(p); ++n; }
        }
    }
    CloseHandle(snap);
    return n;
}
static bool IsRunning(const wchar_t* name) {
    bool r = false;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W e{sizeof(e)};
    for (BOOL ok = Process32FirstW(snap, &e); ok; ok = Process32NextW(snap, &e)) if (_wcsicmp(e.szExeFile, name) == 0) r = true;
    CloseHandle(snap);
    return r;
}

static void Environment() {
    HANDLE token = nullptr;
    OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token);
    DWORD len = 0, elevated = 0;
    GetTokenInformation(token, TokenElevation, &elevated, sizeof(elevated), &len);
    GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &len);
    auto* b = (TOKEN_MANDATORY_LABEL*)LocalAlloc(LPTR, len);
    DWORD rid = 0;
    if (b && GetTokenInformation(token, TokenIntegrityLevel, b, len, &len)) rid = *GetSidSubAuthority(b->Label.Sid, *GetSidSubAuthorityCount(b->Label.Sid) - 1);
    wchar_t pkg[PACKAGE_FULL_NAME_MAX_LENGTH + 1] = L"(none)", fam[PACKAGE_FAMILY_NAME_MAX_LENGTH + 1] = L"(none)";
    UINT32 pl = PACKAGE_FULL_NAME_MAX_LENGTH + 1, fl = PACKAGE_FAMILY_NAME_MAX_LENGTH + 1;
    GetCurrentPackageFullName(&pl, pkg);
    GetCurrentPackageFamilyName(&fl, fam);
    SYSTEM_INFO si;
    GetNativeSystemInfo(&si);
    P("driver pid %lu, integrity RID 0x%lx (%s), elevated token=%lu, package %ls, family %ls, native arch %u", GetCurrentProcessId(), rid, rid >= 0x3000 ? "high or above" : rid >= 0x2000 ? "medium" : "low", (unsigned long)elevated, pkg, fam, si.wProcessorArchitecture);
    LocalFree(b);
    CloseHandle(token);
}

static void Association(const wchar_t* ext) {
    struct { const wchar_t* what; const wchar_t* iid; } kinds[] = {{L"thumbnail handler {E357FCCD...}", L"{E357FCCD-A995-4576-B01F-234630154E96}"}, {L"preview handler {8895B1C6...}", L"{8895b1c6-b41f-4c1c-a562-0d564250836f}"}};
    for (auto& k : kinds) {
        wchar_t buf[256] = L"";
        DWORD n = 256;
        HRESULT hr = AssocQueryStringW(ASSOCF_NONE, ASSOCSTR_SHELLEXTENSION, ext, k.iid, buf, &n);
        P("  AssocQueryString(SHELLEXTENSION, %ls, %ls) -> hr 0x%08lx '%ls'", ext, k.what, (unsigned long)hr, buf);
    }
    wchar_t buf[512] = L"";
    DWORD n = 512;
    HRESULT hr = AssocQueryStringW(ASSOCF_NONE, ASSOCSTR_EXECUTABLE, ext, nullptr, buf, &n);
    P("  AssocQueryString(EXECUTABLE, %ls) -> hr 0x%08lx '%ls'", ext, (unsigned long)hr, buf);
    wchar_t prog[256] = L"(none)";
    DWORD sz = sizeof(prog);
    wchar_t key[256];
    swprintf_s(key, L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\%ls\\UserChoice", ext);
    if (RegGetValueW(HKEY_CURRENT_USER, key, L"ProgId", RRF_RT_REG_SZ, nullptr, prog, &sz) != ERROR_SUCCESS) wcscpy_s(prog, L"(no UserChoice)");
    P("  HKCU UserChoice ProgId for %ls: %ls", ext, prog);
}

static bool CopyFixture(const std::wstring& from, const std::wstring& to) { return CopyFileW(from.c_str(), to.c_str(), FALSE) != 0; }

// GetImage through the shell. Returns elapsed ms, or -1 on failure (hr in *hrOut).
static double GetThumb(const std::wstring& file, UINT size, HRESULT* hrOut, int* w = nullptr, int* h = nullptr, int* nonWhitePct = nullptr) {
    IShellItem* item = nullptr;
    HRESULT hr = SHCreateItemFromParsingName(file.c_str(), nullptr, IID_PPV_ARGS(&item));
    double ms = -1;
    if (SUCCEEDED(hr)) {
        IShellItemImageFactory* f = nullptr;
        hr = item->QueryInterface(IID_PPV_ARGS(&f));
        if (SUCCEEDED(hr)) {
            HBITMAP bmp = nullptr;
            const double t0 = NowMs();
            hr = f->GetImage(SIZE{(LONG)size, (LONG)size}, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, &bmp);
            ms = NowMs() - t0;
            if (SUCCEEDED(hr) && bmp) {
                BITMAP bm{};
                GetObjectW(bmp, sizeof(bm), &bm);
                if (w) *w = bm.bmWidth;
                if (h) *h = bm.bmHeight;
                if (nonWhitePct && bm.bmBitsPixel == 32 && bm.bmWidth > 0) {
                    std::vector<uint32_t> px((size_t)bm.bmWidth * bm.bmHeight);
                    GetBitmapBits(bmp, (LONG)(px.size() * 4), px.data());
                    size_t dark = 0;
                    for (uint32_t v : px) if ((v & 0xFFFFFF) != 0xFFFFFF && (v >> 24) != 0) ++dark;
                    *nonWhitePct = (int)(dark * 100 / px.size());
                }
                DeleteObject(bmp);
            }
            f->Release();
        }
        item->Release();
    }
    *hrOut = hr;
    return SUCCEEDED(hr) ? ms : -1;
}

static double Pct(std::vector<double> v, double p) {
    if (v.empty()) return -1;
    std::sort(v.begin(), v.end());
    double k = (v.size() - 1) * p / 100.0;
    size_t lo = (size_t)k, hi = std::min(lo + 1, v.size() - 1);
    return v[lo] + (v[hi] - v[lo]) * (k - lo);
}

static void ThumbSeries(const char* label, const std::wstring& dir, const std::wstring& pdf, const wchar_t* ext, const wchar_t* killTargets, int warmN, const char* handlerLog, const char* marker) {
    P("== thumbnails through the shell: %s (extension %ls)", label, ext);
    std::vector<double> warm;
    int ok = 0, failed = 0, invoked0 = CountIn(handlerLog, marker);
    for (int cold = 0; cold < 3; ++cold) {
        // "Cold": ShellHost, the engine and the COM surrogate are stopped first, so this call pays for starting everything on demand.
        if (killTargets) { KillByName(L"S6bShellHost.exe"); KillByName(L"S6bComServer.exe"); }
        wchar_t name[MAX_PATH];
        swprintf_s(name, L"%ls\\cold-%hs-%d%ls", dir.c_str(), label, cold, ext);
        CopyFixture(pdf, name);
        HRESULT hr;
        int w = 0, h = 0, nw = 0;
        double ms = GetThumb(name, 256, &hr, &w, &h, &nw);
        P("  cold call %d: %s hr=0x%08lx %.1f ms, %dx%d, non-white %d%%", cold, ms >= 0 ? "ok" : "FAILED", (unsigned long)hr, ms, w, h, nw);
        ms >= 0 ? ++ok : ++failed;
    }
    for (int i = 0; i < warmN; ++i) {
        wchar_t name[MAX_PATH];
        swprintf_s(name, L"%ls\\warm-%hs-%03d%ls", dir.c_str(), label, i, ext);
        CopyFixture(pdf, name);
        HRESULT hr;
        double ms = GetThumb(name, 256, &hr);
        if (ms >= 0) { warm.push_back(ms); ++ok; } else { ++failed; P("  warm call %d FAILED hr=0x%08lx", i, (unsigned long)hr); }
    }
    P("  warm (n=%zu): p50 %.1f ms, p95 %.1f ms, max %.1f ms", warm.size(), Pct(warm, 50), Pct(warm, 95), warm.empty() ? -1 : *std::max_element(warm.begin(), warm.end()));
    P("  succeeded %d, failed %d; handler invoked %d times (marker '%s' in %s)", ok, failed, CountIn(handlerLog, marker) - invoked0, marker, handlerLog);
}

static void Motw(const std::wstring& dir, const std::wstring& pdf) {
    P("== Mark of the Web: the same file with and without a Zone.Identifier stream, through the shell");
    wchar_t plain[MAX_PATH], marked[MAX_PATH];
    swprintf_s(plain, L"%ls\\motw-plain.s6bpdf", dir.c_str());
    swprintf_s(marked, L"%ls\\motw-marked.s6bpdf", dir.c_str());
    CopyFixture(pdf, plain);
    CopyFixture(pdf, marked);
    std::wstring ads = std::wstring(marked) + L":Zone.Identifier";
    HANDLE h = CreateFileW(ads.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    const char z[] = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.invalid/\r\nHostUrl=https://example.invalid/file.pdf\r\n";
    DWORD n;
    bool wrote = h != INVALID_HANDLE_VALUE && WriteFile(h, z, sizeof(z) - 1, &n, nullptr);
    if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    P("  wrote Zone.Identifier (ZoneId=3, Internet) on motw-marked.s6bpdf: %s", wrote ? "yes" : "NO");
    for (auto* f : {plain, marked}) {
        HRESULT hr;
        int w = 0, ht = 0, nw = 0;
        const int before = CountIn("C:\\s6b-log\\handler.log", "GetThumbnail cx=");
        double ms = GetThumb(f, 256, &hr, &w, &ht, &nw);
        P("  %ls: %s hr=0x%08lx %.1f ms %dx%d non-white %d%%; handler GetThumbnail calls: +%d", PathFindFileNameW(f), ms >= 0 ? "ok" : "FAILED", (unsigned long)hr, ms, w, ht, nw, CountIn("C:\\s6b-log\\handler.log", "GetThumbnail cx=") - before);
    }
    P("  handler-side stream size lines (a Zone.Identifier stream would not be part of the main data stream):");
    P("    %s", LastLineWith("C:\\s6b-log\\handler.log", "stream size").c_str());
}

static void Preview(const std::wstring& dir, const std::wstring& pdf) {
    P("== preview handler through COM as Explorer activates it (CLSCTX_LOCAL_SERVER, PreviewHost surrogate)");
    wchar_t file[MAX_PATH];
    swprintf_s(file, L"%ls\\preview.s6bpdf", dir.c_str());
    CopyFixture(pdf, file);
    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"S6bDriverPane";
    RegisterClassW(&wc);
    HWND pane = CreateWindowExW(0, L"S6bDriverPane", L"pane", WS_OVERLAPPEDWINDOW, 0, 0, 700, 900, nullptr, nullptr, wc.hInstance, nullptr);
    for (int run = 0; run < 4; ++run) {
        if (run == 0) KillByName(L"S6bShellHost.exe");
        IInitializeWithStream* init = nullptr;
        const double t0 = NowMs();
        HRESULT hr = CoCreateInstance(CLSID_S6bPreview, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&init));
        const double tCreate = NowMs() - t0;
        P("  run %d: CoCreateInstance(local server) hr=0x%08lx in %.1f ms", run, (unsigned long)hr, tCreate);
        if (FAILED(hr)) continue;
        IStream* stream = nullptr;
        hr = SHCreateStreamOnFileEx(file, STGM_READ | STGM_SHARE_DENY_NONE, 0, FALSE, nullptr, &stream);
        if (SUCCEEDED(hr)) hr = init->Initialize(stream, STGM_READ);
        IPreviewHandler* ph = nullptr;
        if (SUCCEEDED(hr)) hr = init->QueryInterface(IID_PPV_ARGS(&ph));
        RECT rc{0, 0, 600, 800};
        const int before = CountIn("C:\\s6b-log\\handler.log", "preview: first pixel");
        double t1 = NowMs();
        if (SUCCEEDED(hr)) hr = ph->SetWindow(pane, &rc);
        if (SUCCEEDED(hr)) hr = ph->DoPreview();
        P("    Initialize/QI/SetWindow/DoPreview hr=0x%08lx in %.1f ms", (unsigned long)hr, NowMs() - t1);
        double waited = 0;
        while (SUCCEEDED(hr) && waited < 8000 && CountIn("C:\\s6b-log\\handler.log", "preview: first pixel") == before) {
            MSG m;
            while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
            Sleep(10);
            waited = NowMs() - t1;
        }
        P("    first pixel reported by the handler after %.0f ms of waiting: %s", waited, LastLineWith("C:\\s6b-log\\handler.log", "preview: first pixel").c_str());
        if (ph) { ph->Unload(); ph->Release(); }
        if (init) init->Release();
        if (stream) stream->Release();
    }
    DestroyWindow(pane);
    P("  preview host environment as the handler saw it:");
    P("    %s", LastLineWith("C:\\s6b-log\\handler.log", "host exe=").c_str());
}

static void ExeServerActivation() {
    P("== packaged com:ExeServer activation (option b): CoCreateInstance(CLSCTX_LOCAL_SERVER) + one IPersist call");
    std::vector<double> coldAct, coldCall, warmAct, warmCall;
    for (int i = 0; i < 8; ++i) {
        KillByName(L"S6bComServer.exe");
        Sleep(200);
        IPersist* p = nullptr;
        double t0 = NowMs();
        HRESULT hr = CoCreateInstance(CLSID_S6bExe, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&p));
        double act = NowMs() - t0;
        if (FAILED(hr)) { P("  cold %d: CoCreateInstance hr=0x%08lx (%.1f ms)", i, (unsigned long)hr, act); continue; }
        CLSID c;
        t0 = NowMs();
        p->GetClassID(&c);
        double call = NowMs() - t0;
        coldAct.push_back(act);
        coldCall.push_back(call);
        // warm: the server is running now
        for (int j = 0; j < 20; ++j) {
            IPersist* q = nullptr;
            t0 = NowMs();
            hr = CoCreateInstance(CLSID_S6bExe, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&q));
            double a2 = NowMs() - t0;
            if (SUCCEEDED(hr)) {
                t0 = NowMs();
                q->GetClassID(&c);
                warmCall.push_back(NowMs() - t0);
                warmAct.push_back(a2);
                q->Release();
            }
        }
        p->Release();
    }
    P("  cold activation (server process not running; n=%zu): p50 %.1f ms, p95 %.1f ms, max %.1f ms", coldAct.size(), Pct(coldAct, 50), Pct(coldAct, 95), coldAct.empty() ? -1 : *std::max_element(coldAct.begin(), coldAct.end()));
    P("  first call after cold activation (n=%zu): p50 %.3f ms, p95 %.3f ms", coldCall.size(), Pct(coldCall, 50), Pct(coldCall, 95));
    P("  warm activation (server running; n=%zu): p50 %.3f ms, p95 %.3f ms", warmAct.size(), Pct(warmAct, 50), Pct(warmAct, 95));
    P("  warm IPersist::GetClassID round trip (n=%zu): p50 %.3f ms, p95 %.3f ms", warmCall.size(), Pct(warmCall, 50), Pct(warmCall, 95));
    P("  server environment: %s", LastLineWith("C:\\s6b-log\\comserver.log", "env:").c_str());
}

// What it costs to hand a file to an out-of-process server through COM: the server reads the marshaled IStream with 1 MiB IStream::Read calls (option b's data path).
static void ExeServerStream(const std::wstring& dir) {
    P("== bytes through a marshaled IStream into the packaged ExeServer (IInitializeWithStream::Initialize; the server reads everything)");
    for (size_t mb : {0, 5, 30}) {
        wchar_t file[MAX_PATH];
        swprintf_s(file, L"%ls\\blob-%zuMB.bin", dir.c_str(), mb);
        size_t bytes = mb ? mb << 20 : 4096;
        HANDLE h = CreateFileW(file, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        std::vector<char> chunk(1 << 20, 'x');
        for (size_t done = 0; done < bytes; done += chunk.size()) { DWORD n; WriteFile(h, chunk.data(), (DWORD)std::min(chunk.size(), bytes - done), &n, nullptr); }
        CloseHandle(h);
        std::vector<double> v;
        for (int i = 0; i < 6; ++i) {
            IInitializeWithStream* init = nullptr;
            HRESULT hr = CoCreateInstance(CLSID_S6bExe, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&init));
            IStream* s = nullptr;
            if (SUCCEEDED(hr)) hr = SHCreateStreamOnFileEx(file, STGM_READ | STGM_SHARE_DENY_NONE, 0, FALSE, nullptr, &s);
            const double t0 = NowMs();
            if (SUCCEEDED(hr)) hr = init->Initialize(s, STGM_READ);
            const double ms = NowMs() - t0;
            if (SUCCEEDED(hr)) v.push_back(ms); else P("  %zu bytes: hr=0x%08lx", bytes, (unsigned long)hr);
            if (s) s->Release();
            if (init) init->Release();
        }
        P("  %zu bytes: Initialize round trip p50 %.2f ms, p95 %.2f ms (n=%zu)", bytes, Pct(v, 50), Pct(v, 95), v.size());
        P("    server says: %s", LastLineWith("C:\\s6b-log\\comserver.log", "Initialize: stream size").c_str());
    }
}

int wmain(int argc, wchar_t** argv) {
    // usage: S6bDriver --selftest <outdir> <fixtures dir>
    if (argc < 4) { printf("usage: S6bDriver --selftest <outdir> <fixturesdir>\n"); return 2; }
    std::wstring outdir = argv[2], fx = argv[3];
    CreateDirectoryW(outdir.c_str(), nullptr);
    g_log = _wfopen((outdir + L"\\driver.log").c_str(), L"wb");
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    Environment();
    P("== association: what does the shell resolve for each extension?");
    for (auto* e : {L".s6bpdf", L".pdf"}) Association(e);

    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    std::wstring dir = std::wstring(tmp) + L"s6b-thumbs";
    CreateDirectoryW(dir.c_str(), nullptr);
    const std::wstring pdf = fx + L"\\text1.pdf";
    const char* hlog = "C:\\s6b-log\\handler.log";
    ThumbSeries("surrogate-dll", dir, pdf, L".s6bpdf", L"kill", 30, hlog, "GetThumbnail cx=");
    // The real .pdf: is our packaged handler used while it is not the default app?
    P("== a real .pdf through the shell (the package also registers handlers for .pdf; MikePDF is NOT the default app here)");
    {
        wchar_t name[MAX_PATH];
        swprintf_s(name, L"%ls\\real.pdf", dir.c_str());
        CopyFixture(pdf, name);
        const int before = CountIn(hlog, "GetThumbnail cx=");
        HRESULT hr;
        int w = 0, h = 0, nw = 0;
        double ms = GetThumb(name, 256, &hr, &w, &h, &nw);
        P("  GetImage(real.pdf, THUMBNAILONLY): %s hr=0x%08lx %.1f ms %dx%d non-white %d%%; our handler calls: +%d", ms >= 0 ? "ok" : "FAILED", (unsigned long)hr, ms, w, h, nw, CountIn(hlog, "GetThumbnail cx=") - before);
    }
    Motw(dir, pdf);
    Preview(dir, pdf);
    ExeServerActivation();
    ExeServerStream(dir);
    P("processes still running: S6bShellHost=%d S6bComServer=%d", IsRunning(L"S6bShellHost.exe"), IsRunning(L"S6bComServer.exe"));
    P("driver done");
    if (g_log) fclose(g_log);
    return 0;
}
