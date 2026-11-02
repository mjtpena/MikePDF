// A host for MikePDF.ShellExt.dll that behaves like Explorer's hosts do, and is driven by tests/MikePDF.Integration.Tests/ShellHandlerTests.cs. It loads the DLL with
// LoadLibrary and DllGetClassObject (no COM registration of any kind: nothing is written to the registry, no class is registered, no package is installed), creates the
// objects, initialises them with a stream made from a file, and either asks for one thumbnail or runs a preview under scripted input.
//
//   testhost thumb <pdf> <cx> [<out.raw>]   one GetThumbnail; prints "OK w h nonwhitePercent ms alpha" or "FAIL 0xHRESULT ms"; out.raw gets the BGRA bytes
//   testhost preview                        reads commands from stdin, one per line, and answers each with one line (see run_command)
//   testhost pipename                       prints the pipe name the DLL would use here
#define _CRT_SECURE_NO_WARNINGS
#include <Windows.h>
#include <ole2.h>
#include <UIAutomation.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shobjidl.h>
#include <thumbcache.h>

#include <atomic>
#include <cstdarg>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "shell_client.h"
#include "shellext_ids.h"

namespace {

using namespace mikepdf::shellext;
using Clock = std::chrono::steady_clock;

typedef HRESULT(STDAPICALLTYPE* GetClassObjectFn)(REFCLSID, REFIID, void**);

HMODULE g_dll = nullptr;

bool load_dll() {
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    PathRemoveFileSpecW(path);
    PathAppendW(path, L"MikePDF.ShellExt.dll");
    g_dll = LoadLibraryW(path);
    return g_dll != nullptr;
}

HRESULT create(REFCLSID clsid, REFIID iid, void** out) {
    auto get = reinterpret_cast<GetClassObjectFn>(GetProcAddress(g_dll, "DllGetClassObject"));
    if (get == nullptr) {
        return E_FAIL;
    }
    IClassFactory* factory = nullptr;
    HRESULT hr = get(clsid, IID_PPV_ARGS(&factory));
    if (FAILED(hr)) {
        return hr;
    }
    hr = factory->CreateInstance(nullptr, iid, out);
    factory->Release();
    return hr;
}

std::wstring widen(const char* text) {
    const int n = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
    std::wstring out(n > 0 ? n - 1 : 0, L'\0');
    if (n > 1) {
        MultiByteToWideChar(CP_UTF8, 0, text, -1, out.data(), n);
    }
    return out;
}

double elapsed_ms(Clock::time_point since) { return std::chrono::duration<double, std::milli>(Clock::now() - since).count(); }

IStream* open_stream(const std::wstring& path) {
    IStream* stream = nullptr;
    return SUCCEEDED(SHCreateStreamOnFileEx(path.c_str(), STGM_READ | STGM_SHARE_DENY_NONE, 0, FALSE, nullptr, &stream)) ? stream : nullptr;
}

int nonwhite_percent(const std::uint32_t* pixels, std::size_t count) {
    std::size_t dark = 0;
    for (std::size_t i = 0; i < count; ++i) {
        dark += (pixels[i] & 0xFFFFFF) != 0xFFFFFF ? 1 : 0;
    }
    return count == 0 ? 0 : static_cast<int>(dark * 100 / count);
}

// ---- thumb --------------------------------------------------------------------------------------------------------------------------------------------

int run_thumb(const std::wstring& pdf, UINT cx, const std::wstring& raw_out) {
    IStream* stream = open_stream(pdf);
    if (stream == nullptr) {
        std::printf("FAIL 0x%08X 0\n", static_cast<unsigned>(HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)));
        return 0;
    }
    IInitializeWithStream* init = nullptr;
    HRESULT hr = create(kClsidThumbnailProvider, IID_PPV_ARGS(&init));
    IThumbnailProvider* thumbs = nullptr;
    if (SUCCEEDED(hr)) {
        hr = init->Initialize(stream, STGM_READ);
    }
    if (SUCCEEDED(hr)) {
        hr = init->QueryInterface(IID_PPV_ARGS(&thumbs));
    }
    HBITMAP bitmap = nullptr;
    WTS_ALPHATYPE alpha = WTSAT_UNKNOWN;
    const auto started = Clock::now();
    if (SUCCEEDED(hr)) {
        hr = thumbs->GetThumbnail(cx, &bitmap, &alpha);
    }
    const double ms = elapsed_ms(started);
    if (FAILED(hr) || bitmap == nullptr) {
        std::printf("FAIL 0x%08X %.1f\n", static_cast<unsigned>(hr), ms);
    } else {
        BITMAP info{};
        GetObjectW(bitmap, sizeof(info), &info);
        std::vector<std::uint32_t> pixels(static_cast<std::size_t>(info.bmWidth) * info.bmHeight);
        GetBitmapBits(bitmap, static_cast<LONG>(pixels.size() * 4), pixels.data());
        std::printf("OK %d %d %d %.1f %d\n", info.bmWidth, info.bmHeight, nonwhite_percent(pixels.data(), pixels.size()), ms, static_cast<int>(alpha));
        if (!raw_out.empty()) {
            HANDLE file = CreateFileW(raw_out.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
            if (file != INVALID_HANDLE_VALUE) {
                DWORD written = 0;
                WriteFile(file, pixels.data(), static_cast<DWORD>(pixels.size() * 4), &written, nullptr);
                CloseHandle(file);
            }
        }
        DeleteObject(bitmap);
    }
    if (thumbs != nullptr) {
        thumbs->Release();
    }
    if (init != nullptr) {
        init->Release();
    }
    stream->Release();
    return 0;
}

// ---- preview ------------------------------------------------------------------------------------------------------------------------------------------

// The shell's side of the preview handshake: a frame that records the keys the handler hands back to it.
class Frame final : public IPreviewHandlerFrame {
public:
    std::atomic<int> tabs{0};
    STDMETHODIMP QueryInterface(REFIID iid, void** out) override {
        if (iid == IID_IUnknown || iid == IID_IPreviewHandlerFrame) {
            *out = static_cast<IPreviewHandlerFrame*>(this);
            return S_OK;
        }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP GetWindowContext(PREVIEWHANDLERFRAMEINFO* info) override {
        *info = {};
        return S_OK;
    }
    STDMETHODIMP TranslateAccelerator(MSG* message) override {
        if (message != nullptr && message->message == WM_KEYDOWN && message->wParam == VK_TAB) {
            ++tabs;
            return S_OK;
        }
        return S_FALSE;
    }
};

struct PreviewState {
    HWND pane = nullptr;
    HWND child = nullptr;
    IStream* stream = nullptr;
    IInitializeWithStream* init = nullptr;
    IPreviewHandler* handler = nullptr;
    IUIAutomation* automation = nullptr;
    Frame frame;
    int width = 0, height = 0;
    COLORREF background = RGB(0x40, 0x40, 0x40);
} g_preview;

void pump(int milliseconds) {
    const auto until = Clock::now() + std::chrono::milliseconds(milliseconds);
    do {
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(5);
    } while (Clock::now() < until);
}

struct Capture {
    int width = 0, height = 0;
    std::vector<std::uint32_t> pixels;
    int content_percent = 0;  // pixels that are neither the background nor the page white: the ink of the page
    int page_percent = 0;     // pixels that are not the background: page area
};

Capture capture_child() {
    Capture capture;
    RECT rect{};
    GetClientRect(g_preview.child, &rect);
    capture.width = rect.right - rect.left;
    capture.height = rect.bottom - rect.top;
    if (capture.width <= 0 || capture.height <= 0) {
        return capture;
    }
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = capture.width;
    info.bmiHeader.biHeight = -capture.height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HDC screen = GetDC(nullptr);
    HDC dc = CreateCompatibleDC(screen);
    HBITMAP bitmap = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    HGDIOBJ previous = SelectObject(dc, bitmap);
    SendMessageW(g_preview.child, WM_PRINTCLIENT, reinterpret_cast<WPARAM>(dc), PRF_CLIENT);
    const std::size_t count = static_cast<std::size_t>(capture.width) * capture.height;
    capture.pixels.assign(static_cast<std::uint32_t*>(bits), static_cast<std::uint32_t*>(bits) + count);
    SelectObject(dc, previous);
    DeleteObject(bitmap);
    DeleteDC(dc);
    ReleaseDC(nullptr, screen);
    const std::uint32_t background = (GetRValue(g_preview.background) << 16) | (GetGValue(g_preview.background) << 8) | GetBValue(g_preview.background);
    std::size_t ink = 0, page = 0;
    for (const std::uint32_t pixel : capture.pixels) {
        const std::uint32_t rgb = pixel & 0xFFFFFF;
        if (rgb != background) {
            ++page;
            ink += rgb != 0xFFFFFF ? 1 : 0;
        }
    }
    capture.content_percent = static_cast<int>(ink * 10000 / count);  // hundredths of a percent
    capture.page_percent = static_cast<int>(page * 100 / count);
    return capture;
}

bool uia_read(std::wstring& name, std::wstring& value) {
    if (g_preview.automation == nullptr) {
        if (FAILED(CoCreateInstance(__uuidof(CUIAutomation), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&g_preview.automation)))) {
            return false;
        }
    }
    IUIAutomationElement* element = nullptr;
    if (FAILED(g_preview.automation->ElementFromHandle(g_preview.child, &element)) || element == nullptr) {
        return false;
    }
    BSTR n = nullptr;
    element->get_CurrentName(&n);
    name = n != nullptr ? n : L"";
    SysFreeString(n);
    VARIANT v;
    VariantInit(&v);
    value.clear();
    if (SUCCEEDED(element->GetCurrentPropertyValue(UIA_ValueValuePropertyId, &v)) && v.vt == VT_BSTR && v.bstrVal != nullptr) {
        value = v.bstrVal;
    }
    VariantClear(&v);
    element->Release();
    return true;
}

LRESULT CALLBACK pane_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) { return DefWindowProcW(window, message, wparam, lparam); }

std::string to_utf8(const std::wstring& text) {
    const int n = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string out(n > 0 ? n - 1 : 0, '\0');
    if (n > 1) {
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, out.data(), n, nullptr, nullptr);
    }
    return out;
}

void reply(const char* format, ...) {
    va_list args;
    va_start(args, format);
    std::vprintf(format, args);
    va_end(args);
    std::printf("\n");
    std::fflush(stdout);
}

// Commands: open <w> <h> <pdf> | state | key <vk> | wheel <delta> | tab | resize <w> <h> | capture [<out.raw>] | wait-ink <ms> | settle <ms> | gdi | unload | quit
bool run_command(const std::string& line) {
    char verb[32]{};
    char argument[1024]{};
    int a = 0, b = 0;
    std::sscanf(line.c_str(), "%31s", verb);
    const std::string v = verb;
    if (v == "open") {
        int consumed = 0;
        std::sscanf(line.c_str(), "open %d %d %n", &a, &b, &consumed);
        std::snprintf(argument, sizeof(argument), "%s", line.c_str() + consumed);  // the rest of the line is the path, spaces and all
        WNDCLASSW cls{};
        cls.lpfnWndProc = pane_proc;
        cls.hInstance = GetModuleHandleW(nullptr);
        cls.lpszClassName = L"MikePDF.TestPane";
        RegisterClassW(&cls);
        // Never shown: the pane is a hidden top-level window, as in S6b. Nothing appears on the desktop and nothing takes focus.
        g_preview.pane = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, L"MikePDF.TestPane", L"pane", WS_POPUP, 0, 0, a, b, nullptr, nullptr, cls.hInstance, nullptr);
        g_preview.width = a;
        g_preview.height = b;
        g_preview.stream = open_stream(widen(argument));
        HRESULT hr = g_preview.stream != nullptr ? create(kClsidPreviewHandler, IID_PPV_ARGS(&g_preview.init)) : HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        if (SUCCEEDED(hr)) hr = g_preview.init->Initialize(g_preview.stream, STGM_READ);
        if (SUCCEEDED(hr)) hr = g_preview.init->QueryInterface(IID_PPV_ARGS(&g_preview.handler));
        IObjectWithSite* site = nullptr;
        if (SUCCEEDED(hr) && SUCCEEDED(g_preview.handler->QueryInterface(IID_PPV_ARGS(&site)))) {
            site->SetSite(static_cast<IPreviewHandlerFrame*>(&g_preview.frame));
            site->Release();
        }
        IPreviewHandlerVisuals* visuals = nullptr;
        if (SUCCEEDED(hr) && SUCCEEDED(g_preview.handler->QueryInterface(IID_PPV_ARGS(&visuals)))) {
            visuals->SetBackgroundColor(g_preview.background);
            visuals->Release();
        }
        const RECT rect{0, 0, a, b};
        const auto started = Clock::now();
        if (SUCCEEDED(hr)) hr = g_preview.handler->SetWindow(g_preview.pane, &rect);
        if (SUCCEEDED(hr)) hr = g_preview.handler->DoPreview();
        IOleWindow* ole = nullptr;
        if (SUCCEEDED(hr) && SUCCEEDED(g_preview.handler->QueryInterface(IID_PPV_ARGS(&ole)))) {
            ole->GetWindow(&g_preview.child);
            ole->Release();
        }
        reply("OPENED 0x%08X %.1f", static_cast<unsigned>(hr), elapsed_ms(started));
    } else if (v == "state") {
        std::wstring name, value;
        const bool ok = uia_read(name, value);
        reply("STATE %d name=%s value=%s", ok ? 1 : 0, to_utf8(name).c_str(), to_utf8(value).c_str());
    } else if (v == "key") {
        std::sscanf(line.c_str(), "key %d", &a);
        SendMessageW(g_preview.child, WM_KEYDOWN, static_cast<WPARAM>(a), 0);
        reply("KEY");
    } else if (v == "wheel") {
        std::sscanf(line.c_str(), "wheel %d", &a);
        SendMessageW(g_preview.child, WM_MOUSEWHEEL, MAKEWPARAM(0, static_cast<WORD>(static_cast<short>(a))), 0);
        reply("WHEEL");
    } else if (v == "tab") {
        const int before = g_preview.frame.tabs;
        SendMessageW(g_preview.child, WM_KEYDOWN, VK_TAB, 0);
        MSG message{g_preview.child, WM_KEYDOWN, VK_TAB, 0, 0, {}};
        const HRESULT hr = g_preview.handler->TranslateAccelerator(&message);
        reply("TAB frame_calls=%d accelerator_hr=0x%08X", g_preview.frame.tabs - before, static_cast<unsigned>(hr));
    } else if (v == "resize") {
        std::sscanf(line.c_str(), "resize %d %d", &a, &b);
        SetWindowPos(g_preview.pane, nullptr, 0, 0, a, b, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        const RECT rect{0, 0, a, b};
        g_preview.handler->SetRect(&rect);
        reply("RESIZED");
    } else if (v == "capture") {
        argument[0] = '\0';
        std::sscanf(line.c_str(), "capture %1023s", argument);
        const Capture capture = capture_child();
        if (argument[0] != '\0') {
            HANDLE file = CreateFileW(widen(argument).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
            if (file != INVALID_HANDLE_VALUE) {
                DWORD written = 0;
                WriteFile(file, capture.pixels.data(), static_cast<DWORD>(capture.pixels.size() * 4), &written, nullptr);
                CloseHandle(file);
            }
        }
        reply("CAPTURED %d %d page=%d%% ink=%d", capture.width, capture.height, capture.page_percent, capture.content_percent);
    } else if (v == "wait-ink") {
        // Waits until the visible page has ink (black text or colour on white), up to <ms>.
        std::sscanf(line.c_str(), "wait-ink %d", &a);
        const auto started = Clock::now();
        Capture capture;
        do {
            pump(20);
            capture = capture_child();
        } while (capture.content_percent == 0 && elapsed_ms(started) < a);
        reply("INK %d after %.1f ms (page=%d%% ink=%d)", capture.content_percent > 0 ? 1 : 0, elapsed_ms(started), capture.page_percent, capture.content_percent);
    } else if (v == "settle") {
        std::sscanf(line.c_str(), "settle %d", &a);
        pump(a);
        reply("SETTLED");
    } else if (v == "gdi") {
        reply("GDI %lu", GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS));
    } else if (v == "unload") {
        const auto started = Clock::now();
        const HRESULT hr = g_preview.handler->Unload();
        reply("UNLOADED 0x%08X %.1f", static_cast<unsigned>(hr), elapsed_ms(started));
    } else if (v == "quit") {
        if (g_preview.handler != nullptr) { g_preview.handler->Release(); g_preview.handler = nullptr; }
        if (g_preview.init != nullptr) { g_preview.init->Release(); g_preview.init = nullptr; }
        if (g_preview.stream != nullptr) { g_preview.stream->Release(); }
        if (g_preview.pane != nullptr) { DestroyWindow(g_preview.pane); }
        reply("BYE");
        return false;
    } else {
        reply("ERROR unknown command");
    }
    return true;
}

int run_preview() {
    std::mutex mutex;
    std::deque<std::string> lines;
    const DWORD main_thread = GetCurrentThreadId();
    std::thread reader([&] {
        char buffer[2048];
        while (std::fgets(buffer, sizeof(buffer), stdin) != nullptr) {
            std::string line = buffer;
            while (!line.empty() && (line.back() == '\n' || line.back() == '\r')) {
                line.pop_back();
            }
            {
                std::lock_guard lock(mutex);
                lines.push_back(line);
            }
            PostThreadMessageW(main_thread, WM_APP, 0, 0);
        }
        {
            std::lock_guard lock(mutex);
            lines.push_back("quit");
        }
        PostThreadMessageW(main_thread, WM_APP, 0, 0);
    });
    reader.detach();
    reply("READY");
    MSG message;
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        if (message.hwnd == nullptr && message.message == WM_APP) {
            for (;;) {
                std::string line;
                {
                    std::lock_guard lock(mutex);
                    if (lines.empty()) {
                        break;
                    }
                    line = lines.front();
                    lines.pop_front();
                }
                if (!run_command(line)) {
                    return 0;
                }
            }
            continue;
        }
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return 0;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (argc >= 2 && std::wstring(argv[1]) == L"pipename") {
        std::wprintf(L"%ls\n", production_config(nullptr, ShellFeature::Thumbnails).pipe_path.c_str());
        return 0;
    }
    if (!load_dll()) {
        std::printf("FAIL cannot load MikePDF.ShellExt.dll (%lu)\n", GetLastError());
        return 3;
    }
    if (argc >= 4 && std::wstring(argv[1]) == L"thumb") {
        return run_thumb(argv[2], static_cast<UINT>(_wtoi(argv[3])), argc >= 5 ? argv[4] : L"");
    }
    if (argc >= 2 && std::wstring(argv[1]) == L"preview") {
        return run_preview();
    }
    std::printf("usage: testhost thumb <pdf> <cx> [out.raw] | preview | pipename\n");
    return 2;
}
