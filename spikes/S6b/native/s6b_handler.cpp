// S6b prototype shell handler DLL (THROWAWAY). A thumbnail provider and a preview handler that never parse PDF bytes: they copy the IStream into
// an unnamed section, ask ShellHost (over a per-user pipe, started on demand) to render through the sandboxed engine, validate the reply and
// build a bitmap. There is no DllRegisterServer on purpose: nothing registers this on a developer machine.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <appmodel.h>
#include <shlwapi.h>
#include <shlobj.h>
#include <thumbcache.h>
#include <propsys.h>
#include <shobjidl.h>
#include <sddl.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <initializer_list>
#include <memory>
#include <new>
#include <utility>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>

#include "s6b_proto.h"

// {6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0001} thumbnail, ...0002 preview
static const CLSID CLSID_S6bThumb = {0x6B7C2D10, 0x5A1E, 0x4F3B, {0x9C, 0x11, 0x0A, 0x5B, 0x6D, 0x6B, 0x00, 0x01}};
static const CLSID CLSID_S6bPreview = {0x6B7C2D10, 0x5A1E, 0x4F3B, {0x9C, 0x11, 0x0A, 0x5B, 0x6D, 0x6B, 0x00, 0x02}};

static HINSTANCE g_module;
static std::atomic<long> g_objects{0};
static S6bTimings g_last{};

static double NowMs() {
    static LARGE_INTEGER f = [] { LARGE_INTEGER x; QueryPerformanceFrequency(&x); return x; }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return double(c.QuadPart) * 1000.0 / double(f.QuadPart);
}

// ---------------------------------------------------------------------------------------------------------------- logging
static void Log(const char* fmt, ...) {
    char path[MAX_PATH];
    DWORD n = GetEnvironmentVariableA("S6B_LOG", path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) {
        if (GetFileAttributesA("C:\\s6b-log") == INVALID_FILE_ATTRIBUTES) return;
        strcpy_s(path, "C:\\s6b-log\\handler.log");
    }
    char line[1024];
    SYSTEMTIME t;
    GetLocalTime(&t);
    int k = _snprintf_s(line, _TRUNCATE, "%02d:%02d:%02d.%03d [handler %lu] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentProcessId());
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(line + k, sizeof(line) - k - 2, _TRUNCATE, fmt, ap);
    va_end(ap);
    strcat_s(line, "\r\n");
    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD w;
    WriteFile(h, line, (DWORD)strlen(line), &w, nullptr);
    CloseHandle(h);
}

static const char* IntegrityName(HANDLE token) {
    DWORD len = 0;
    GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &len);
    if (len == 0) return "?";
    auto* buf = (TOKEN_MANDATORY_LABEL*)LocalAlloc(LPTR, len);
    const char* r = "?";
    if (buf && GetTokenInformation(token, TokenIntegrityLevel, buf, len, &len)) {
        DWORD rid = *GetSidSubAuthority(buf->Label.Sid, *GetSidSubAuthorityCount(buf->Label.Sid) - 1);
        r = rid < SECURITY_MANDATORY_MEDIUM_RID ? "low" : rid < SECURITY_MANDATORY_HIGH_RID ? "medium" : rid < SECURITY_MANDATORY_SYSTEM_RID ? "high" : "system";
    }
    LocalFree(buf);
    return r;
}

static void LogEnvironmentOnce() {
    static std::atomic<bool> done{false};
    if (done.exchange(true)) return;
    wchar_t exe[MAX_PATH] = L"?";
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    HANDLE token = nullptr;
    OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token);
    DWORD isAc = 0, len = 0;
    if (token) GetTokenInformation(token, TokenIsAppContainer, &isAc, sizeof(isAc), &len);
    wchar_t pkg[PACKAGE_FULL_NAME_MAX_LENGTH + 1] = L"(none)";
    UINT32 pl = PACKAGE_FULL_NAME_MAX_LENGTH + 1;
    if (GetCurrentPackageFullName(&pl, pkg) != ERROR_SUCCESS) wcscpy_s(pkg, L"(none)");
    Log("host exe=%ls integrity=%s appcontainer=%lu package=%ls", exe, token ? IntegrityName(token) : "?", isAc, pkg);
    if (token) CloseHandle(token);
}

// ---------------------------------------------------------------------------------------------------------------- client
static std::wstring PipePath() {
    wchar_t name[128];
    DWORD n = GetEnvironmentVariableW(L"S6B_PIPE", name, 128);
    return std::wstring(L"\\\\.\\pipe\\") + (n && n < 128 ? name : L"mikepdf-s6b-shell");
}

static std::wstring ShellHostPath() {
    wchar_t p[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"S6B_SHELLHOST", p, MAX_PATH);
    if (n && n < MAX_PATH) return p;
    wchar_t dll[MAX_PATH];
    GetModuleFileNameW(g_module, dll, MAX_PATH);
    PathRemoveFileSpecW(dll);
    std::wstring dir = dll;
    return dir + L"\\ShellHost\\S6bShellHost.exe";
}

static DWORD Budget(DWORD def) {
    char b[16];
    return GetEnvironmentVariableA("S6B_BUDGET_MS", b, sizeof(b)) ? (DWORD)atoi(b) : def;
}

static bool Launch(const std::wstring& exe) {
    wchar_t extra[1024];
    DWORD n = GetEnvironmentVariableW(L"S6B_SHELLHOST_ARGS", extra, 1024);
    std::wstring args = n && n < 1024 ? std::wstring(L" ") + extra : L"";
    if (!(n && n < 1024) && GetFileAttributesA("C:\\s6b-log") != INVALID_FILE_ATTRIBUTES) args = L" --log C:\\s6b-log\\shellhost.log"; // CI packages: no environment, a shared log folder
    std::wstring cmd = L"\"" + exe + L"\"" + args;
    STARTUPINFOW si{sizeof(si)};
    PROCESS_INFORMATION pi{};
    // CREATE_BREAKAWAY_FROM_JOB first: the host that loads us (dllhost, prevhost) may be in a job that would end ShellHost with it.
    DWORD flags = CREATE_NO_WINDOW | DETACHED_PROCESS | CREATE_UNICODE_ENVIRONMENT;
    std::wstring buf = cmd;
    BOOL ok = CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE, flags | CREATE_BREAKAWAY_FROM_JOB, nullptr, nullptr, &si, &pi);
    DWORD err = ok ? 0 : GetLastError();
    g_last.launchError = err;
    if (!ok) {
        buf = cmd;
        ok = CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE, flags, nullptr, nullptr, &si, &pi);
        g_last.launchError = ok ? 0 : GetLastError();
        Log("launch: breakaway failed (%lu); without breakaway: %s (%lu)", err, ok ? "ok" : "FAILED", ok ? 0 : GetLastError());
    } else {
        Log("launch: ok pid=%lu", pi.dwProcessId);
    }
    if (ok) {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
    return ok != FALSE;
}

static bool SameFile(const std::wstring& a, const std::wstring& b) {
    wchar_t fa[MAX_PATH], fb[MAX_PATH];
    if (!GetFullPathNameW(a.c_str(), MAX_PATH, fa, nullptr) || !GetFullPathNameW(b.c_str(), MAX_PATH, fb, nullptr)) return false;
    return _wcsicmp(fa, fb) == 0;
}

// Is the process at the other end of the pipe the ShellHost we expect? Packaged: same package family as this DLL's host. Unpackaged: same image path.
static bool VerifyServer(HANDLE pipe, const std::wstring& expectedExe) {
    ULONG pid = 0;
    if (!GetNamedPipeServerProcessId(pipe, &pid)) return false;
    HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!p) {
        Log("verify: OpenProcess(%lu) failed %lu", pid, GetLastError());
        return false;
    }
    wchar_t image[MAX_PATH];
    DWORD size = MAX_PATH;
    bool imageOk = QueryFullProcessImageNameW(p, 0, image, &size) && SameFile(image, expectedExe);
    wchar_t mine[PACKAGE_FAMILY_NAME_MAX_LENGTH + 1], theirs[PACKAGE_FAMILY_NAME_MAX_LENGTH + 1];
    UINT32 ml = PACKAGE_FAMILY_NAME_MAX_LENGTH + 1, tl = PACKAGE_FAMILY_NAME_MAX_LENGTH + 1;
    bool haveMine = GetCurrentPackageFamilyName(&ml, mine) == ERROR_SUCCESS;
    bool haveTheirs = GetPackageFamilyName(p, &tl, theirs) == ERROR_SUCCESS;
    bool familyOk = haveMine && haveTheirs && wcscmp(mine, theirs) == 0;
    CloseHandle(p);
    Log("verify: server pid=%lu image=%ls imageMatches=%d family(mine)=%ls family(server)=%ls familyMatches=%d", pid, image, imageOk, haveMine ? mine : L"-", haveTheirs ? theirs : L"-", familyOk);
    return haveMine ? familyOk : imageOk;
}

static bool PipeIo(HANDLE h, void* buf, DWORD len, bool write, DWORD timeoutMs) {
    char* p = (char*)buf;
    DWORD done = 0;
    HANDLE ev = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    bool ok = true;
    while (done < len && ok) {
        OVERLAPPED ov{};
        ov.hEvent = ev;
        ResetEvent(ev);
        DWORD n = 0;
        BOOL r = write ? WriteFile(h, p + done, len - done, nullptr, &ov) : ReadFile(h, p + done, len - done, nullptr, &ov);
        if (!r && GetLastError() != ERROR_IO_PENDING) { ok = false; break; }
        if (WaitForSingleObject(ev, timeoutMs) != WAIT_OBJECT_0) {
            CancelIoEx(h, &ov);
            GetOverlappedResult(h, &ov, &n, TRUE);
            ok = false;
            break;
        }
        if (!GetOverlappedResult(h, &ov, &n, FALSE) || n == 0) { ok = false; break; }
        done += n;
    }
    CloseHandle(ev);
    return ok;
}

struct Client {
    HANDLE pipe = INVALID_HANDLE_VALUE;
    ~Client() { if (pipe != INVALID_HANDLE_VALUE) CloseHandle(pipe); }

    // Connects, starting ShellHost once if the pipe does not exist. Returns false on timeout or when the server is not the expected one.
    bool Connect(DWORD budgetMs, bool& launched) {
        const std::wstring path = PipePath();
        const std::wstring exe = ShellHostPath();
        const double t0 = NowMs();
        launched = false;
        for (;;) {
            pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
            if (pipe != INVALID_HANDLE_VALUE) break;
            DWORD e = GetLastError();
            g_last.connectError = e;
            if (NowMs() - t0 > budgetMs) { Log("connect: timeout (last error %lu)", e); return false; }
            if (e == ERROR_FILE_NOT_FOUND) {
                if (!launched) {
                    launched = true;
                    if (!Launch(exe)) return false;
                }
                Sleep(5);
            } else if (e == ERROR_PIPE_BUSY) {
                WaitNamedPipeW(path.c_str(), 20);
            } else {
                g_last.connectError = e;
                Log("connect: error %lu", e);
                return false;
            }
        }
        if (!VerifyServer(pipe, exe)) { g_last.connectError = 0xFFFF0001; CloseHandle(pipe); pipe = INVALID_HANDLE_VALUE; return false; }
        return true;
    }

    bool Call(S6bReq& req, S6bRep& rep, void* inlineBuf, size_t inlineCap, DWORD timeoutMs) {
        req.magic = kS6bMagic;
        req.clientPid = GetCurrentProcessId();
        if (!PipeIo(pipe, &req, sizeof(req), true, timeoutMs)) return false;
        if (!PipeIo(pipe, &rep, sizeof(rep), false, timeoutMs)) return false;
        if (rep.magic != kS6bMagic) return false;
        if (inlineBuf && rep.status == 0 && rep.stride && rep.height) {
            size_t need = (size_t)rep.stride * rep.height;
            if (need > inlineCap) return false;
            return PipeIo(pipe, inlineBuf, (DWORD)need, false, timeoutMs);
        }
        return true;
    }
};

struct Section {
    HANDLE h = nullptr;
    uint64_t bytes = 0;
    ~Section() { if (h) CloseHandle(h); }
};

// IStream -> unnamed pagefile-backed section. The copy the handler cannot avoid: a stream has no handle to duplicate.
static HRESULT StreamToSection(IStream* s, Section& out, double& ms) {
    const double t0 = NowMs();
    STATSTG st{};
    HRESULT hr = s->Stat(&st, STATFLAG_NONAME);
    if (FAILED(hr)) return hr;
    const ULONGLONG size = st.cbSize.QuadPart;
    if (size == 0 || size > (32ULL << 20)) return size == 0 ? HRESULT_FROM_WIN32(ERROR_FILE_INVALID) : HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
    LARGE_INTEGER zero{};
    s->Seek(zero, STREAM_SEEK_SET, nullptr);
    out.h = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, (DWORD)(size >> 32), (DWORD)size, nullptr);
    if (!out.h) return HRESULT_FROM_WIN32(GetLastError());
    auto* view = (BYTE*)MapViewOfFile(out.h, FILE_MAP_WRITE, 0, 0, (SIZE_T)size);
    if (!view) return HRESULT_FROM_WIN32(GetLastError());
    ULONGLONG off = 0;
    DWORD chunk = 1u << 20;
    char kb[16];
    if (GetEnvironmentVariableA("S6B_CHUNK_KB", kb, sizeof(kb))) chunk = (DWORD)atoi(kb) << 10;
    while (off < size) {
        ULONG got = 0;
        ULONG want = (ULONG)std::min<ULONGLONG>(chunk, size - off);
        hr = s->Read(view + off, want, &got);
        if (FAILED(hr) || got == 0) break;
        off += got;
    }
    UnmapViewOfFile(view);
    if (off != size) return FAILED(hr) ? hr : HRESULT_FROM_WIN32(ERROR_HANDLE_EOF);
    out.bytes = size;
    ms = NowMs() - t0;
    return S_OK;
}

struct Rendered {
    HBITMAP bitmap = nullptr;
    int width = 0, height = 0;
    uint32_t pageCount = 0;
};

// The whole handler-side flow. `inlineMode` (env S6B_INLINE=1) returns pixels over the pipe instead of through a section, for the comparison.
static HRESULT RenderSection(Section& src, uint32_t cx, uint32_t cy, uint32_t page, Rendered& out) {
    S6bTimings& T = g_last;
    const double tStart = NowMs() - T.copyMs;
    cx = std::max(16u, std::min(cx, 1024u));
    cy = std::max(16u, std::min(cy, 1024u));

    Client c;
    bool launched = false;
    double t = NowMs();
    if (!c.Connect(Budget(2000), launched)) return HRESULT_FROM_WIN32(ERROR_CONNECTION_REFUSED);
    T.connectMs = NowMs() - t;
    T.launchedShellHost = launched ? 1 : 0;

    char inl[8];
    const bool inlineMode = GetEnvironmentVariableA("S6B_INLINE", inl, sizeof(inl)) > 0;
    Section dst;
    const size_t dstBytes = (size_t)cx * cy * 4;
    std::string inlineBuf;
    if (inlineMode) {
        inlineBuf.resize(dstBytes);
    } else {
        dst.h = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, (DWORD)dstBytes, nullptr);
        if (!dst.h) return HRESULT_FROM_WIN32(GetLastError());
        dst.bytes = dstBytes;
    }

    S6bReq req{};
    S6bRep rep{};
    req.op = kOpOpen;
    req.srcSection = (uint64_t)(uintptr_t)src.h;
    req.srcBytes = src.bytes;
    t = NowMs();
    if (!c.Call(req, rep, nullptr, 0, Budget(1700))) { Log("open: pipe failure"); return HRESULT_FROM_WIN32(ERROR_TIMEOUT); }
    T.openRtMs = NowMs() - t;
    T.open = rep;
    if (rep.status != 0) { Log("open: status %u", rep.status); return HRESULT_FROM_WIN32(ERROR_BAD_FORMAT); }

    S6bReq r2{};
    S6bRep rp2{};
    r2.op = kOpRender;
    r2.cx = cx;
    r2.cy = cy;
    r2.page = page;
    r2.flags = inlineMode ? 1 : 0;
    r2.dstSection = (uint64_t)(uintptr_t)dst.h;
    r2.dstBytes = dst.bytes;
    t = NowMs();
    if (!c.Call(r2, rp2, inlineMode ? inlineBuf.data() : nullptr, dstBytes, Budget(1700))) { Log("render: pipe failure"); return HRESULT_FROM_WIN32(ERROR_TIMEOUT); }
    T.renderRtMs = NowMs() - t;
    T.render = rp2;
    if (rp2.status != 0) { Log("render: status %u", rp2.status); return HRESULT_FROM_WIN32(ERROR_BAD_FORMAT); }

    // Validate as untrusted (spec 7 "Hostile input"): 1..4096, <= request, stride == 4 * width, 64-bit multiplication, section big enough.
    if (rp2.width < 1 || rp2.width > cx || rp2.height < 1 || rp2.height > cy || rp2.stride != 4ull * rp2.width) return E_FAIL;
    const uint64_t need = (uint64_t)rp2.stride * rp2.height;
    if (need > dstBytes) return E_FAIL;

    t = NowMs();
    const BYTE* pixels = nullptr;
    BYTE* view = nullptr;
    if (inlineMode) {
        pixels = (const BYTE*)inlineBuf.data();
    } else {
        view = (BYTE*)MapViewOfFile(dst.h, FILE_MAP_READ, 0, 0, (SIZE_T)need);
        if (!view) return HRESULT_FROM_WIN32(GetLastError());
        pixels = view;
    }
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = (LONG)rp2.width;
    bi.bmiHeader.biHeight = -(LONG)rp2.height; // top-down
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP bmp = CreateDIBSection(nullptr, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!bmp) { if (view) UnmapViewOfFile(view); return E_OUTOFMEMORY; }
    memcpy(bits, pixels, (size_t)need);
    // The engine draws an opaque white page, so straight and premultiplied alpha are the same; force alpha 255 rather than trust the engine's byte.
    auto* px = (uint32_t*)bits;
    for (uint64_t i = 0; i < need / 4; ++i) px[i] |= 0xFF000000u;
    if (view) UnmapViewOfFile(view);
    T.convertMs = NowMs() - t;

    // The caller has its bitmap now: that is the latency that matters. Closing the session (which kills the one-shot engine) is measured after it; the
    // product handler just closes the pipe and ShellHost cleans up by itself.
    out.bitmap = bmp;
    out.width = (int)rp2.width;
    out.height = (int)rp2.height;
    out.pageCount = rep.pageCount;
    T.totalMs = NowMs() - tStart;
    S6bReq cl{};
    S6bRep cr{};
    cl.op = kOpClose;
    t = NowMs();
    c.Call(cl, cr, nullptr, 0, 500);
    T.closeRtMs = NowMs() - t;
    return S_OK;
}

static HRESULT RenderViaShellHost(IStream* stream, uint32_t cx, uint32_t cy, uint32_t page, Rendered& out) {
    memset(&g_last, 0, sizeof(g_last));
    LogEnvironmentOnce();
    Section src;
    HRESULT hr = StreamToSection(stream, src, g_last.copyMs);
    if (FAILED(hr)) { Log("stream copy failed 0x%08lx", hr); return hr; }
    return RenderSection(src, cx, cy, page, out);
}

// ---------------------------------------------------------------------------------------------------------------- COM boilerplate
template <class T>
static HRESULT QI(T* self, REFIID riid, void** ppv, std::initializer_list<std::pair<const IID*, void*>> table) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (IsEqualIID(riid, IID_IUnknown)) { *ppv = table.begin()->second; }
    for (auto& e : table) if (IsEqualIID(riid, *e.first)) { *ppv = e.second; break; }
    if (!*ppv) return E_NOINTERFACE;
    ((IUnknown*)*ppv)->AddRef();
    (void)self;
    return S_OK;
}

class Thumb final : public IInitializeWithStream, public IThumbnailProvider {
    std::atomic<long> ref{1};
    IStream* stream = nullptr;

public:
    Thumb() { ++g_objects; }
    ~Thumb() { if (stream) stream->Release(); --g_objects; }
    STDMETHODIMP QueryInterface(REFIID r, void** p) override {
        return QI(this, r, p, {{&IID_IInitializeWithStream, (IInitializeWithStream*)this}, {&IID_IThumbnailProvider, (IThumbnailProvider*)this}});
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++ref; }
    STDMETHODIMP_(ULONG) Release() override { long r = --ref; if (!r) delete this; return r; }
    STDMETHODIMP Initialize(IStream* s, DWORD) override {
        if (stream) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        stream = s;
        s->AddRef();
        return S_OK; // no work here: the shell may initialise handlers it never asks anything of
    }
    STDMETHODIMP GetThumbnail(UINT cx, HBITMAP* ph, WTS_ALPHATYPE* pa) override {
        if (!ph || !pa) return E_POINTER;
        *ph = nullptr;
        if (!stream) return E_UNEXPECTED;
        Rendered r;
        STATSTG st{};
        stream->Stat(&st, STATFLAG_NONAME);
        Log("GetThumbnail cx=%u stream size=%llu", cx, (unsigned long long)st.cbSize.QuadPart);
        HRESULT hr = RenderViaShellHost(stream, cx, cx, 0, r);
        Log("GetThumbnail done hr=0x%08lx total %.2f ms (copy %.2f, connect %.2f, launched %.0f, open %.2f, render %.2f)", (unsigned long)hr, g_last.totalMs, g_last.copyMs, g_last.connectMs, g_last.launchedShellHost, g_last.openRtMs, g_last.renderRtMs);
        g_last.hr = (uint32_t)hr;
        if (SUCCEEDED(hr)) { *ph = r.bitmap; *pa = WTSAT_ARGB; }
        return hr;
    }
};

static const wchar_t kPreviewClass[] = L"S6bPreviewWindow";
constexpr UINT WM_S6B_DONE = WM_APP + 1;

class Preview final : public IInitializeWithStream, public IPreviewHandler, public IOleWindow, public IObjectWithSite, public IPreviewHandlerVisuals {
    std::atomic<long> ref{1};
    IStream* stream = nullptr;
    IUnknown* site = nullptr;
    HWND parent = nullptr, wnd = nullptr;
    RECT rect{};
    COLORREF bg = RGB(255, 255, 255);
    HBITMAP bitmap = nullptr;
    int bw = 0, bh = 0;
    std::thread worker;
    double doPreviewMs = 0;

    static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
        auto* self = (Preview*)GetWindowLongPtrW(h, GWLP_USERDATA);
        switch (m) {
        case WM_NCCREATE: SetWindowLongPtrW(h, GWLP_USERDATA, (LONG_PTR)((CREATESTRUCTW*)l)->lpCreateParams); break;
        case WM_S6B_DONE:
            if (self) {
                self->bitmap = (HBITMAP)l;
                self->bw = LOWORD(w);
                self->bh = HIWORD(w);
                char b[64];
                _snprintf_s(b, _TRUNCATE, "%.3f", NowMs() - self->doPreviewMs);
                SetPropW(h, L"S6B_FIRST_PIXEL_MS", (HANDLE)(INT_PTR)(int)(atof(b) * 1000)); // microseconds since DoPreview
                Log("preview: first pixel %s ms after DoPreview", b);
                InvalidateRect(h, nullptr, TRUE);
            }
            return 0;
        case WM_PAINT:
        case WM_PRINTCLIENT: {
            PAINTSTRUCT ps;
            HDC dc = m == WM_PAINT ? BeginPaint(h, &ps) : (HDC)w;
            RECT rc;
            GetClientRect(h, &rc);
            HBRUSH br = CreateSolidBrush(self ? self->bg : RGB(255, 255, 255));
            FillRect(dc, &rc, br);
            DeleteObject(br);
            if (self && self->bitmap) {
                HDC mem = CreateCompatibleDC(dc);
                HGDIOBJ old = SelectObject(mem, self->bitmap);
                BitBlt(dc, 0, 0, self->bw, self->bh, mem, 0, 0, SRCCOPY);
                SelectObject(mem, old);
                DeleteDC(mem);
            } else {
                DrawTextW(dc, L"Loading...", -1, &rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
            }
            if (m == WM_PAINT) EndPaint(h, &ps);
            return 0;
        }
        }
        return DefWindowProcW(h, m, w, l);
    }

public:
    Preview() { ++g_objects; }
    ~Preview() { Unload(); if (site) site->Release(); --g_objects; }
    STDMETHODIMP QueryInterface(REFIID r, void** p) override {
        return QI(this, r, p, {{&IID_IInitializeWithStream, (IInitializeWithStream*)this}, {&IID_IPreviewHandler, (IPreviewHandler*)this}, {&IID_IOleWindow, (IOleWindow*)this},
                               {&IID_IObjectWithSite, (IObjectWithSite*)this}, {&IID_IPreviewHandlerVisuals, (IPreviewHandlerVisuals*)this}});
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++ref; }
    STDMETHODIMP_(ULONG) Release() override { long r = --ref; if (!r) delete this; return r; }
    STDMETHODIMP Initialize(IStream* s, DWORD) override {
        if (stream) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        stream = s;
        s->AddRef();
        return S_OK;
    }
    STDMETHODIMP SetWindow(HWND h, const RECT* prc) override { parent = h; if (prc) rect = *prc; if (wnd) SetParent(wnd, h); return S_OK; }
    STDMETHODIMP SetRect(const RECT* prc) override {
        if (!prc) return E_POINTER;
        rect = *prc;
        if (wnd) MoveWindow(wnd, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top, TRUE);
        return S_OK;
    }
    STDMETHODIMP DoPreview() override {
        if (!stream || !parent) return E_UNEXPECTED;
        if (wnd) return S_OK;
        LogEnvironmentOnce();
        doPreviewMs = NowMs();
        WNDCLASSW wc{};
        wc.lpfnWndProc = WndProc;
        wc.hInstance = g_module;
        wc.lpszClassName = kPreviewClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        RegisterClassW(&wc); // already-registered is fine
        wnd = CreateWindowExW(0, kPreviewClass, L"PDF preview", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top, parent, nullptr, g_module, this);
        if (!wnd) return HRESULT_FROM_WIN32(GetLastError());
        // The stream belongs to the shell's apartment: copy it here, on the calling thread. Only the pipe round trip runs on the worker.
        auto sec = std::make_shared<Section>();
        memset(&g_last, 0, sizeof(g_last));
        HRESULT hr = StreamToSection(stream, *sec, g_last.copyMs);
        if (FAILED(hr)) return hr;
        const uint32_t cx = std::max<LONG>(16, std::min<LONG>(rect.right - rect.left, 1024)), cy = 1024;
        HWND target = wnd;
        worker = std::thread([sec, cx, cy, target, this]() {
            Rendered r;
            HRESULT hr = RenderSection(*sec, cx, cy, 0, r);
            if (SUCCEEDED(hr)) PostMessageW(target, WM_S6B_DONE, MAKEWPARAM(r.width, r.height), (LPARAM)r.bitmap);
        });
        return S_OK;
    }
    STDMETHODIMP Unload() override {
        if (worker.joinable()) worker.join();
        if (wnd) { DestroyWindow(wnd); wnd = nullptr; }
        if (bitmap) { DeleteObject(bitmap); bitmap = nullptr; }
        if (stream) { stream->Release(); stream = nullptr; }
        return S_OK;
    }
    STDMETHODIMP SetFocus() override { if (wnd) ::SetFocus(wnd); return S_OK; }
    STDMETHODIMP QueryFocus(HWND* ph) override { if (!ph) return E_POINTER; *ph = GetFocus(); return *ph ? S_OK : HRESULT_FROM_WIN32(GetLastError()); }
    STDMETHODIMP TranslateAccelerator(MSG*) override { return S_FALSE; }
    STDMETHODIMP GetWindow(HWND* ph) override { if (!ph) return E_POINTER; *ph = wnd; return wnd ? S_OK : E_FAIL; }
    STDMETHODIMP ContextSensitiveHelp(BOOL) override { return E_NOTIMPL; }
    STDMETHODIMP SetSite(IUnknown* s) override { if (site) site->Release(); site = s; if (site) site->AddRef(); return S_OK; }
    STDMETHODIMP GetSite(REFIID r, void** p) override { return site ? site->QueryInterface(r, p) : E_FAIL; }
    STDMETHODIMP SetBackgroundColor(COLORREF c) override { bg = c; return S_OK; }
    STDMETHODIMP SetFont(const LOGFONTW*) override { return S_OK; }
    STDMETHODIMP SetTextColor(COLORREF) override { return S_OK; }
};

class Factory final : public IClassFactory {
    std::atomic<long> ref{1};
    bool preview;

public:
    explicit Factory(bool p) : preview(p) {}
    STDMETHODIMP QueryInterface(REFIID r, void** p) override { return QI(this, r, p, {{&IID_IClassFactory, (IClassFactory*)this}}); }
    STDMETHODIMP_(ULONG) AddRef() override { return ++ref; }
    STDMETHODIMP_(ULONG) Release() override { long r = --ref; if (!r) delete this; return r; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (outer) return CLASS_E_NOAGGREGATION;
        IUnknown* obj = preview ? (IUnknown*)(IPreviewHandler*)new (std::nothrow) Preview() : (IUnknown*)(IThumbnailProvider*)new (std::nothrow) Thumb();
        if (!obj) return E_OUTOFMEMORY;
        HRESULT hr = obj->QueryInterface(riid, ppv);
        obj->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL) override { return S_OK; }
};

extern "C" {
BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) { g_module = h; DisableThreadLibraryCalls(h); }
    return TRUE;
}
STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv) {
    if (!IsEqualCLSID(clsid, CLSID_S6bThumb) && !IsEqualCLSID(clsid, CLSID_S6bPreview)) return CLASS_E_CLASSNOTAVAILABLE;
    auto* f = new (std::nothrow) Factory(IsEqualCLSID(clsid, CLSID_S6bPreview));
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}
STDAPI DllCanUnloadNow() { return g_objects.load() == 0 ? S_OK : S_FALSE; }
__declspec(dllexport) void S6bLastTimings(S6bTimings* out) { *out = g_last; }
}
