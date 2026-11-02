// S6b CI probe: a packaged COM ExeServer (com:ExeServer) used two ways (THROWAWAY).
//  1. directly as the thumbnail handler (desktop2:ThumbnailHandler -> an ExeServer class): can the shell activate a local server, marshal the file's IStream into it and get an
//     HBITMAP back? It also reads the whole stream across the process boundary and logs how long that took (the cost of copying through COM IStream::Read).
//  2. as an activation target for option (b): CoCreateInstance(CLSCTX_LOCAL_SERVER) latency, cold and warm, and one IPersist call.
// It renders nothing real: the bitmap is a test pattern. Logs to C:\s6b-log\comserver.log.
#define _CRT_SECURE_NO_WARNINGS
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <appmodel.h>
#include <objbase.h>
#include <propsys.h>
#include <shlwapi.h>
#include <thumbcache.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <vector>

// {6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0003}
static const CLSID CLSID_S6bExe = {0x6B7C2D10, 0x5A1E, 0x4F3B, {0x9C, 0x11, 0x0A, 0x5B, 0x6D, 0x6B, 0x00, 0x03}};

static HANDLE g_stop;
static std::atomic<long> g_lastActivityTick{0};
static std::atomic<long> g_live{0};

static double NowMs() {
    static LARGE_INTEGER f = [] { LARGE_INTEGER x; QueryPerformanceFrequency(&x); return x; }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return double(c.QuadPart) * 1000.0 / double(f.QuadPart);
}

static void Log(const char* fmt, ...) {
    char line[1024];
    SYSTEMTIME t;
    GetLocalTime(&t);
    int k = _snprintf_s(line, _TRUNCATE, "%02d:%02d:%02d.%03d [comserver %lu] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentProcessId());
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(line + k, sizeof(line) - k - 2, _TRUNCATE, fmt, ap);
    va_end(ap);
    strcat_s(line, "\r\n");
    HANDLE h = CreateFileA("C:\\s6b-log\\comserver.log", FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD w;
    WriteFile(h, line, (DWORD)strlen(line), &w, nullptr);
    CloseHandle(h);
}

static void LogEnvironment() {
    HANDLE token = nullptr;
    OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token);
    DWORD len = 0, isAc = 0;
    const char* il = "?";
    if (token) {
        GetTokenInformation(token, TokenIsAppContainer, &isAc, sizeof(isAc), &len);
        GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &len);
        auto* b = (TOKEN_MANDATORY_LABEL*)LocalAlloc(LPTR, len);
        if (b && GetTokenInformation(token, TokenIntegrityLevel, b, len, &len)) {
            DWORD rid = *GetSidSubAuthority(b->Label.Sid, *GetSidSubAuthorityCount(b->Label.Sid) - 1);
            il = rid < SECURITY_MANDATORY_MEDIUM_RID ? "low" : rid < SECURITY_MANDATORY_HIGH_RID ? "medium" : rid < SECURITY_MANDATORY_SYSTEM_RID ? "high" : "system";
        }
        LocalFree(b);
        CloseHandle(token);
    }
    wchar_t pkg[PACKAGE_FULL_NAME_MAX_LENGTH + 1] = L"(none)";
    UINT32 pl = PACKAGE_FULL_NAME_MAX_LENGTH + 1;
    if (GetCurrentPackageFullName(&pl, pkg) != ERROR_SUCCESS) wcscpy_s(pkg, L"(none)");
    Log("env: cmdline=%ls integrity=%s appcontainer=%lu package=%ls", GetCommandLineW(), il, isAc, pkg);
}

static void Touch() { g_lastActivityTick = (long)GetTickCount64(); }

class Obj final : public IPersist, public IInitializeWithStream, public IThumbnailProvider {
    std::atomic<long> ref{1};
    size_t bytesRead = 0;

public:
    Obj() { ++g_live; Touch(); }
    ~Obj() { --g_live; Touch(); }
    STDMETHODIMP QueryInterface(REFIID r, void** p) override {
        if (!p) return E_POINTER;
        *p = nullptr;
        if (IsEqualIID(r, IID_IUnknown) || IsEqualIID(r, IID_IPersist)) *p = (IPersist*)this;
        else if (IsEqualIID(r, IID_IInitializeWithStream)) *p = (IInitializeWithStream*)this;
        else if (IsEqualIID(r, IID_IThumbnailProvider)) *p = (IThumbnailProvider*)this;
        if (!*p) return E_NOINTERFACE;
        ((IUnknown*)*p)->AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++ref; }
    STDMETHODIMP_(ULONG) Release() override { long r = --ref; if (!r) delete this; return r; }
    STDMETHODIMP GetClassID(CLSID* c) override { Touch(); *c = CLSID_S6bExe; return S_OK; }
    STDMETHODIMP Initialize(IStream* s, DWORD) override {
        Touch();
        // Read the whole stream across the process boundary: what an out-of-process handler has to do to get at the bytes.
        STATSTG st{};
        s->Stat(&st, STATFLAG_NONAME);
        const double t0 = NowMs();
        std::vector<BYTE> buf(1 << 20);
        size_t total = 0;
        for (;;) {
            ULONG got = 0;
            HRESULT hr = s->Read(buf.data(), (ULONG)buf.size(), &got);
            if (FAILED(hr) || got == 0) break;
            total += got;
        }
        bytesRead = total;
        Log("Initialize: stream size %llu, read %zu bytes through the marshaled IStream in %.2f ms (1 MiB reads)", (unsigned long long)st.cbSize.QuadPart, total, NowMs() - t0);
        return S_OK;
    }
    STDMETHODIMP GetThumbnail(UINT cx, HBITMAP* ph, WTS_ALPHATYPE* pa) override {
        Touch();
        Log("GetThumbnail cx=%u (exe server)", cx);
        const int n = (int)std::max(16u, std::min(cx, 1024u));
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = n;
        bi.bmiHeader.biHeight = -n;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        void* bits = nullptr;
        HBITMAP bmp = CreateDIBSection(nullptr, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!bmp) return E_OUTOFMEMORY;
        auto* px = (uint32_t*)bits;
        for (int y = 0; y < n; ++y) for (int x = 0; x < n; ++x) px[y * n + x] = 0xFF000000u | (((x / 32 + y / 32) & 1) ? 0x2060C0u : 0xE0E0E0u);
        *ph = bmp;
        *pa = WTSAT_ARGB;
        return S_OK;
    }
};

class Factory final : public IClassFactory {
    std::atomic<long> ref{1};

public:
    STDMETHODIMP QueryInterface(REFIID r, void** p) override {
        if (IsEqualIID(r, IID_IUnknown) || IsEqualIID(r, IID_IClassFactory)) { *p = (IClassFactory*)this; AddRef(); return S_OK; }
        *p = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++ref; }
    STDMETHODIMP_(ULONG) Release() override { long r = --ref; if (!r) delete this; return r; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (outer) return CLASS_E_NOAGGREGATION;
        Touch();
        auto* o = new (std::nothrow) Obj();
        if (!o) return E_OUTOFMEMORY;
        HRESULT hr = o->QueryInterface(riid, ppv);
        o->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL) override { return S_OK; }
};

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    const double t0 = NowMs();
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    LogEnvironment();
    g_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    auto* f = new Factory();
    DWORD cookie = 0;
    HRESULT hr = CoRegisterClassObject(CLSID_S6bExe, f, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, &cookie);
    Log("CoRegisterClassObject hr=0x%08lx after %.2f ms of process start", (unsigned long)hr, NowMs() - t0);
    Touch();
    // Exit 6 s after the last activity and when no object is alive: a server that goes away by itself (the on-demand model).
    while (WaitForSingleObject(g_stop, 500) == WAIT_TIMEOUT) {
        if (g_live == 0 && (long)GetTickCount64() - g_lastActivityTick > 6000) break;
    }
    CoRevokeClassObject(cookie);
    f->Release();
    Log("exiting");
    CoUninitialize();
    return 0;
}
