// The shell integration probe. CI ONLY: it runs on a disposable GitHub-hosted runner (the shell integration workflow) with the real MikePDF package installed, from outside the
// package, and exercises the real shell: thumbnails through IShellItemImageFactory, the preview handler as Explorer activates it, ShellHost's pipe, the switches, the policy
// and, for the open question OQ-SHELL-14, what the shell shows when MikePDF's handler declines. It prints one line per check ("CHECK SHELL-xx PASS|FAIL|INFO ...") and
// exits non-zero if a gating check failed.
//
// It writes the registry in exactly one place (HKLM Software\Policies\MikePDF and a competitor handler under HKLM Software\Classes), and only after OnDisposableRunner() has
// confirmed GITHUB_ACTIONS=true and RUNNER_ENVIRONMENT=github-hosted. On any other machine those phases are skipped, and nothing else here touches the registry.
#define _CRT_SECURE_NO_WARNINGS
#include <Windows.h>
#include <ole2.h>
#include <UIAutomation.h>
#include <aclapi.h>
#include <psapi.h>
#include <sddl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shobjidl.h>
#include <thumbcache.h>
#include <tlhelp32.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "mikepdf/protocol/codec.h"
#include "shellext_ids.h"

namespace {

using Clock = std::chrono::steady_clock;
using mikepdf::shellext::kClsidPreviewHandler;
using mikepdf::shellext::kClsidThumbnailProvider;

FILE* g_log = nullptr;
int g_failed = 0;
std::wstring g_work;
std::wstring g_fixtures;
std::wstring g_settings_dir;
std::atomic<int> g_unique{0};

constexpr wchar_t kShellHost[] = L"MikePDF.ShellHost.exe";
constexpr wchar_t kEngine[] = L"MikePDF.Engine.exe";

void P(const char* format, ...) {
    char line[2048];
    va_list args;
    va_start(args, format);
    _vsnprintf_s(line, _TRUNCATE, format, args);
    va_end(args);
    std::fputs(line, stdout);
    std::fputs("\n", stdout);
    std::fflush(stdout);
    if (g_log != nullptr) {
        std::fputs(line, g_log);
        std::fputs("\n", g_log);
        std::fflush(g_log);
    }
}

void Check(const char* id, bool ok, const char* format, ...) {
    char text[1536];
    va_list args;
    va_start(args, format);
    _vsnprintf_s(text, _TRUNCATE, format, args);
    va_end(args);
    P("CHECK %s %s %s", id, ok ? "PASS" : "FAIL", text);
    if (!ok) {
        ++g_failed;
    }
}

void Info(const char* id, const char* format, ...) {
    char text[1536];
    va_list args;
    va_start(args, format);
    _vsnprintf_s(text, _TRUNCATE, format, args);
    va_end(args);
    P("CHECK %s INFO %s", id, text);
}

double NowMs() { return std::chrono::duration<double, std::milli>(Clock::now().time_since_epoch()).count(); }

bool OnDisposableRunner() {
    wchar_t actions[16]{};
    wchar_t environment[32]{};
    GetEnvironmentVariableW(L"GITHUB_ACTIONS", actions, 16);
    GetEnvironmentVariableW(L"RUNNER_ENVIRONMENT", environment, 32);
    return wcscmp(actions, L"true") == 0 && wcscmp(environment, L"github-hosted") == 0;
}

// ---- processes ------------------------------------------------------------------------------------------------------------------------------------------

int CountRunning(const wchar_t* name) {
    int count = 0;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W entry{sizeof(entry)};
    for (BOOL ok = Process32FirstW(snapshot, &entry); ok; ok = Process32NextW(snapshot, &entry)) {
        count += _wcsicmp(entry.szExeFile, name) == 0 ? 1 : 0;
    }
    CloseHandle(snapshot);
    return count;
}

DWORD FirstPid(const wchar_t* name) {
    DWORD pid = 0;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W entry{sizeof(entry)};
    for (BOOL ok = Process32FirstW(snapshot, &entry); ok && pid == 0; ok = Process32NextW(snapshot, &entry)) {
        pid = _wcsicmp(entry.szExeFile, name) == 0 ? entry.th32ProcessID : 0;
    }
    CloseHandle(snapshot);
    return pid;
}

void KillAll(const wchar_t* name) {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W entry{sizeof(entry)};
    for (BOOL ok = Process32FirstW(snapshot, &entry); ok; ok = Process32NextW(snapshot, &entry)) {
        if (_wcsicmp(entry.szExeFile, name) == 0) {
            HANDLE process = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, entry.th32ProcessID);
            if (process != nullptr) {
                TerminateProcess(process, 1);
                WaitForSingleObject(process, 5000);
                CloseHandle(process);
            }
        }
    }
    CloseHandle(snapshot);
}

std::wstring UserSid() {
    HANDLE token = nullptr;
    OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token);
    DWORD size = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    std::vector<BYTE> buffer(size);
    GetTokenInformation(token, TokenUser, buffer.data(), size, &size);
    LPWSTR text = nullptr;
    ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &text);
    std::wstring sid = text != nullptr ? text : L"";
    LocalFree(text);
    CloseHandle(token);
    return sid;
}

// ---- files and thumbnails ---------------------------------------------------------------------------------------------------------------------

std::wstring UniqueCopy(const wchar_t* fixture, const wchar_t* tag) {
    wchar_t name[MAX_PATH];
    swprintf_s(name, L"%ls\\%ls-%d-%lu.pdf", g_work.c_str(), tag, ++g_unique, GetCurrentProcessId());
    CopyFileW((g_fixtures + L"\\" + fixture).c_str(), name, FALSE);
    return name;
}

struct Thumb {
    HRESULT hr{E_FAIL};
    int width{0};
    int height{0};
    double ms{0};
    std::vector<std::uint32_t> pixels;  // top-down BGRA
};

Thumb GetThumb(const std::wstring& file, int size) {
    Thumb thumb;
    IShellItem* item = nullptr;
    HRESULT hr = SHCreateItemFromParsingName(file.c_str(), nullptr, IID_PPV_ARGS(&item));
    if (SUCCEEDED(hr)) {
        IShellItemImageFactory* factory = nullptr;
        hr = item->QueryInterface(IID_PPV_ARGS(&factory));
        if (SUCCEEDED(hr)) {
            HBITMAP bitmap = nullptr;
            const double started = NowMs();
            hr = factory->GetImage(SIZE{size, size}, SIIGBF_THUMBNAILONLY, &bitmap);
            thumb.ms = NowMs() - started;
            if (SUCCEEDED(hr) && bitmap != nullptr) {
                BITMAP info{};
                GetObjectW(bitmap, sizeof(info), &info);
                thumb.width = info.bmWidth;
                thumb.height = info.bmHeight;
                BITMAPINFO bi{};
                bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                bi.bmiHeader.biWidth = info.bmWidth;
                bi.bmiHeader.biHeight = -info.bmHeight;
                bi.bmiHeader.biPlanes = 1;
                bi.bmiHeader.biBitCount = 32;
                bi.bmiHeader.biCompression = BI_RGB;
                thumb.pixels.resize(static_cast<std::size_t>(info.bmWidth) * info.bmHeight);
                HDC dc = GetDC(nullptr);
                GetDIBits(dc, bitmap, 0, info.bmHeight, thumb.pixels.data(), &bi, DIB_RGB_COLORS);
                ReleaseDC(nullptr, dc);
                DeleteObject(bitmap);
            }
            factory->Release();
        }
        item->Release();
    }
    thumb.hr = hr;
    return thumb;
}

std::uint32_t Px(const Thumb& t, int x, int y) { return t.pixels[static_cast<std::size_t>(y) * t.width + x] & 0xFFFFFF; }
int Blue(std::uint32_t p) { return p & 0xFF; }
int Green(std::uint32_t p) { return (p >> 8) & 0xFF; }
int Red(std::uint32_t p) { return (p >> 16) & 0xFF; }

/// rectangles.pdf: black top left, blue top right, green bottom right, red bottom left.
bool FourCorners(const Thumb& t) {
    if (t.width < 16 || t.height < 16) {
        return false;
    }
    const int inset = std::max(2, t.height / 80);
    const auto tl = Px(t, inset, inset), tr = Px(t, t.width - 1 - inset, inset), br = Px(t, t.width - 1 - inset, t.height - 1 - inset), bl = Px(t, inset, t.height - 1 - inset);
    return Red(tl) < 90 && Green(tl) < 90 && Blue(tl) < 90 && Blue(tr) > 180 && Red(tr) < 90 && Green(tr) < 90 && Green(br) > 180 && Red(br) < 90 && Blue(br) < 90 && Red(bl) > 180 &&
           Green(bl) < 90 && Blue(bl) < 90;
}

/// The competitor's checkerboard (0x2060C0 and 0xE0E0E0 in 8-pixel squares).
bool IsCompetitor(const Thumb& t) {
    if (t.width < 16 || t.height < 16) {
        return false;
    }
    const auto a = Px(t, 2, 2), b = Px(t, 10, 2);
    return (a == 0x2060C0 && b == 0xE0E0E0) || (a == 0xE0E0E0 && b == 0x2060C0);
}

double Percentile(std::vector<double> values, double p) {
    if (values.empty()) {
        return -1;
    }
    std::sort(values.begin(), values.end());
    const double k = (values.size() - 1) * p / 100.0;
    const std::size_t low = static_cast<std::size_t>(k);
    const std::size_t high = std::min(low + 1, values.size() - 1);
    return values[low] + (values[high] - values[low]) * (k - low);
}

// ---- the preview, as Explorer's host activates it ---------------------------------------------------------------------------------------

struct PreviewRun {
    HRESULT activation{E_FAIL};
    std::wstring name;
    std::wstring value;
    double ms_to_value{-1};
};

bool UiaRead(HWND window, std::wstring& name, std::wstring& value) {
    IUIAutomation* automation = nullptr;
    if (FAILED(CoCreateInstance(__uuidof(CUIAutomation), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&automation)))) {
        return false;
    }
    bool ok = false;
    IUIAutomationElement* element = nullptr;
    if (SUCCEEDED(automation->ElementFromHandle(window, &element)) && element != nullptr) {
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
        ok = true;
    }
    automation->Release();
    return ok;
}

/// Activates the preview class through COM (CLSCTX_LOCAL_SERVER: the PreviewHost surrogate), previews `file` in a pane window of ours, and waits up to `wait_ms` for the
/// window's UI Automation value to be `expected` (empty: any value that is not empty). Returns what it saw.
PreviewRun RunPreview(const std::wstring& file, const std::wstring& expected, int wait_ms, bool show_and_capture, bool* ink_found) {
    PreviewRun run;
    static const wchar_t kClass[] = L"MikePDF.ProbePane";
    WNDCLASSW cls{};
    cls.lpfnWndProc = DefWindowProcW;
    cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpszClassName = kClass;
    RegisterClassW(&cls);
    HWND pane = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST, kClass, L"pane", WS_POPUP, 0, 0, 640, 820, nullptr, nullptr, cls.hInstance, nullptr);
    if (show_and_capture) {
        ShowWindow(pane, SW_SHOWNOACTIVATE);
    }
    IInitializeWithStream* init = nullptr;
    run.activation = CoCreateInstance(kClsidPreviewHandler, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&init));
    IStream* stream = nullptr;
    IPreviewHandler* handler = nullptr;
    HWND child = nullptr;
    if (SUCCEEDED(run.activation)) {
        HRESULT hr = SHCreateStreamOnFileEx(file.c_str(), STGM_READ | STGM_SHARE_DENY_NONE, 0, FALSE, nullptr, &stream);
        if (SUCCEEDED(hr)) hr = init->Initialize(stream, STGM_READ);
        if (SUCCEEDED(hr)) hr = init->QueryInterface(IID_PPV_ARGS(&handler));
        const RECT rect{0, 0, 600, 800};
        const double started = NowMs();
        if (SUCCEEDED(hr)) hr = handler->SetWindow(pane, &rect);
        if (SUCCEEDED(hr)) hr = handler->DoPreview();
        IOleWindow* ole = nullptr;
        if (SUCCEEDED(hr) && SUCCEEDED(handler->QueryInterface(IID_PPV_ARGS(&ole)))) {
            ole->GetWindow(&child);
            ole->Release();
        }
        while (SUCCEEDED(hr) && child != nullptr && NowMs() - started < wait_ms) {
            MSG message;
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            if (UiaRead(child, run.name, run.value) && (expected.empty() ? !run.value.empty() : run.value == expected)) {
                run.ms_to_value = NowMs() - started;
                break;
            }
            Sleep(50);
        }
        if (ink_found != nullptr && show_and_capture && run.ms_to_value >= 0) {
            // Best effort: whatever is on the screen in the pane's rectangle after the first page had time to be drawn. A runner without a desktop shows nothing.
            Sleep(1500);
            HDC screen = GetDC(nullptr);
            int dark = 0, total = 0;
            for (int y = 40; y < 780; y += 4) {
                for (int x = 40; x < 560; x += 4) {
                    const COLORREF c = GetPixel(screen, x, y);
                    dark += (GetRValue(c) < 80 && GetGValue(c) < 80 && GetBValue(c) < 80) ? 1 : 0;
                    ++total;
                }
            }
            ReleaseDC(nullptr, screen);
            *ink_found = dark > 20;
            P("  screen capture of the pane: %d of %d sampled pixels are dark", dark, total);
        }
        if (handler != nullptr) {
            handler->Unload();
            handler->Release();
        }
    }
    if (init != nullptr) init->Release();
    if (stream != nullptr) stream->Release();
    DestroyWindow(pane);
    return run;
}

// ---- registry (only on a disposable runner) ---------------------------------------------------------------------------------------------------

void AssocChanged() {
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, nullptr, nullptr);
    Sleep(1500);
}

bool SetString(HKEY root, const std::wstring& key, const wchar_t* name, const std::wstring& value) {
    HKEY k = nullptr;
    if (RegCreateKeyExW(root, key.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &k, nullptr) != ERROR_SUCCESS) {
        return false;
    }
    const LONG r = RegSetValueExW(k, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()), static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(k);
    return r == ERROR_SUCCESS;
}

bool SetDword(const wchar_t* name, DWORD value) {
    HKEY k = nullptr;
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, L"Software\\Policies\\MikePDF", 0, nullptr, 0, KEY_SET_VALUE, nullptr, &k, nullptr) != ERROR_SUCCESS) {
        return false;
    }
    const LONG r = RegSetValueExW(k, name, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&value), sizeof(value));
    RegCloseKey(k);
    return r == ERROR_SUCCESS;
}

void ClearPolicies() { RegDeleteTreeW(HKEY_LOCAL_MACHINE, L"Software\\Policies\\MikePDF"); }

// ---- the phases ------------------------------------------------------------------------------------------------------------------------------------

void Environment() {
    HANDLE token = nullptr;
    OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token);
    DWORD size = 0, elevated = 0;
    GetTokenInformation(token, TokenElevation, &elevated, sizeof(elevated), &size);
    GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &size);
    std::vector<BYTE> buffer(size);
    DWORD rid = 0;
    if (GetTokenInformation(token, TokenIntegrityLevel, buffer.data(), size, &size)) {
        auto* label = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(buffer.data());
        rid = *GetSidSubAuthority(label->Label.Sid, *GetSidSubAuthorityCount(label->Label.Sid) - 1);
    }
    CloseHandle(token);
    SYSTEM_INFO system{};
    GetNativeSystemInfo(&system);
    P("probe pid %lu, integrity RID 0x%lx, elevated=%lu, native architecture %u, disposable runner=%d", GetCurrentProcessId(), rid, elevated, system.wProcessorArchitecture, OnDisposableRunner() ? 1 : 0);
}

void PhaseActivation() {
    // SHELL-02: the shell can create both classes from the installed package, as it does for Explorer.
    for (const auto& [name, clsid] : {std::pair{"thumbnail provider", kClsidThumbnailProvider}, std::pair{"preview handler", kClsidPreviewHandler}}) {
        IInitializeWithStream* init = nullptr;
        const HRESULT hr = CoCreateInstance(clsid, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&init));
        Check("SHELL-02", SUCCEEDED(hr) && init != nullptr, "%s: CoCreateInstance hr=0x%08lx", name, static_cast<unsigned long>(hr));
        if (init != nullptr) {
            init->Release();
        }
    }
}

void PhaseThumbnails() {
    KillAll(kShellHost);
    Sleep(300);
    // Cold: ShellHost and the engine are started by this call.
    const Thumb cold = GetThumb(UniqueCopy(L"rectangles.pdf", L"cold"), 256);
    Check("SHELL-05", SUCCEEDED(cold.hr) && cold.ms < 2000, "cold thumbnail through the shell: hr=0x%08lx %.0f ms (limit 2000)", static_cast<unsigned long>(cold.hr), cold.ms);
    Check("SHELL-03", SUCCEEDED(cold.hr) && FourCorners(cold), "the four coloured corners of rectangles.pdf are in the right places (%dx%d)", cold.width, cold.height);

    for (const int cx : {32, 96, 256, 1024}) {
        const Thumb t = GetThumb(UniqueCopy(L"rectangles.pdf", L"size"), cx);
        const int longer = std::max(t.width, t.height);
        const bool sized = SUCCEEDED(t.hr) && std::abs(longer - cx) <= 1 && std::abs(static_cast<double>(t.width) / std::max(t.height, 1) - 200.0 / 300.0) <= 1.0 / std::max(t.height, 1) + 0.01;
        Check("SHELL-03", sized, "cx=%d: %dx%d hr=0x%08lx", cx, t.width, t.height, static_cast<unsigned long>(t.hr));
        if (cx >= 96) {
            Check("SHELL-04", FourCorners(t), "cx=%d: the page content is right (corner colours)", cx);
        }
    }

    for (const wchar_t* fixture : {L"hello_world.pdf", L"hello_world_2_pages.pdf"}) {
        const Thumb t = GetThumb(UniqueCopy(fixture, L"fix"), 256);
        Check("SHELL-03", SUCCEEDED(t.hr) && t.width > 0, "%ls: hr=0x%08lx %dx%d", fixture, static_cast<unsigned long>(t.hr), t.width, t.height);
    }

    std::vector<double> warm;
    int failed = 0;
    for (int i = 0; i < 40; ++i) {
        const Thumb t = GetThumb(UniqueCopy(L"hello_world.pdf", L"warm"), 256);
        if (SUCCEEDED(t.hr)) {
            warm.push_back(t.ms);
        } else {
            ++failed;
        }
    }
    const double p50 = Percentile(warm, 50), p95 = Percentile(warm, 95);
    Check("SHELL-05", failed == 0 && p95 <= 2000, "warm thumbnails through the shell: n=%zu failed=%d p50 %.1f ms p95 %.1f ms (S6b baseline: p95 <= 56.5 ms)", warm.size(), failed, p50, p95);

    // Files that must fail cleanly (SHELL-13): the shell reports failure, ShellHost keeps serving.
    const std::wstring junk = g_work + L"\\junk.pdf";
    HANDLE f = CreateFileW(junk.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    const char text[] = "this is not a pdf";
    DWORD written = 0;
    WriteFile(f, text, sizeof(text) - 1, &written, nullptr);
    CloseHandle(f);
    const Thumb bad = GetThumb(junk, 256);
    Check("SHELL-13", FAILED(bad.hr), "a file that is not a PDF fails (hr=0x%08lx)", static_cast<unsigned long>(bad.hr));
    Check("SHELL-13", SUCCEEDED(GetThumb(UniqueCopy(L"rectangles.pdf", L"after"), 64).hr), "and the next good file works");
    const Thumb encrypted = GetThumb(UniqueCopy(L"encrypted.pdf", L"enc"), 256);
    Check("SHELL-13", FAILED(encrypted.hr), "a password-protected file fails (hr=0x%08lx)", static_cast<unsigned long>(encrypted.hr));

    // Mark of the Web: a thumbnail is produced for an Internet-zone file (S6b measured it); recorded, not gated.
    const std::wstring marked = UniqueCopy(L"rectangles.pdf", L"motw");
    const std::wstring stream = marked + L":Zone.Identifier";
    HANDLE ads = CreateFileW(stream.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (ads != INVALID_HANDLE_VALUE) {
        const char zone[] = "[ZoneTransfer]\r\nZoneId=3\r\n";
        WriteFile(ads, zone, sizeof(zone) - 1, &written, nullptr);
        CloseHandle(ads);
    }
    const Thumb motw = GetThumb(marked, 256);
    Info("OQ-SHELL-8", "thumbnail of an Internet-zone file: hr=0x%08lx %dx%d", static_cast<unsigned long>(motw.hr), motw.width, motw.height);
}

void PhasePreview() {
    bool ink = false;
    const PreviewRun run = RunPreview(UniqueCopy(L"hello_world_2_pages.pdf", L"preview"), L"Page 1 of 2", 10000, true, &ink);
    Check("SHELL-02", SUCCEEDED(run.activation), "the preview class activates in the shell's preview host (hr=0x%08lx)", static_cast<unsigned long>(run.activation));
    Check("SHELL-22", run.name == L"PDF preview" && run.value == L"Page 1 of 2", "UI Automation: name '%ls', value '%ls' after %.0f ms", run.name.c_str(), run.value.c_str(), run.ms_to_value);
    Check("SHELL-06", run.ms_to_value >= 0 && run.ms_to_value < 5000, "the preview reached its first page (document opened through ShellHost and the engine) in %.0f ms (target 1000 ms, limit 5000 ms)", run.ms_to_value);
    Info("SHELL-06", "pixels of the first page %s", ink ? "were seen on screen" : "were not seen on screen (UNVERIFIED: a runner without a visible desktop shows nothing; the UI Automation value is the gate)");
}

void PhaseKillAndRestart() {
    Check("SHELL-09", FirstPid(kShellHost) != 0 || SUCCEEDED(GetThumb(UniqueCopy(L"rectangles.pdf", L"up"), 64).hr), "ShellHost is running before it is killed");
    KillAll(kShellHost);
    const Thumb after = GetThumb(UniqueCopy(L"rectangles.pdf", L"restart"), 256);
    Check("SHELL-09", SUCCEEDED(after.hr) && FirstPid(kShellHost) != 0, "after ShellHost was killed the next request started a new one and succeeded (hr=0x%08lx, %.0f ms)", static_cast<unsigned long>(after.hr), after.ms);
}

void PhaseFlood() {
    PROCESS_MEMORY_COUNTERS_EX before{}, after{};
    HANDLE host = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, FALSE, FirstPid(kShellHost));
    if (host != nullptr) {
        GetProcessMemoryInfo(host, reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&before), sizeof(before));
    }
    std::atomic<bool> stop{false};
    std::atomic<int> peak{0};
    std::thread sampler([&] {
        while (!stop) {
            const int n = CountRunning(kEngine);
            int seen = peak.load();
            while (n > seen && !peak.compare_exchange_weak(seen, n)) {
            }
            Sleep(5);
        }
    });
    constexpr int kThreads = 12, kPerThread = 42;  // 504 requests
    std::atomic<int> ok{0}, failed{0}, slow{0};
    std::vector<std::thread> threads;
    const double started = NowMs();
    for (int t = 0; t < kThreads; ++t) {
        threads.emplace_back([&] {
            CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            for (int i = 0; i < kPerThread; ++i) {
                const Thumb thumb = GetThumb(UniqueCopy(L"rectangles.pdf", L"flood"), 96);
                (SUCCEEDED(thumb.hr) ? ok : failed)++;
                slow += thumb.ms > 2500 ? 1 : 0;
            }
            CoUninitialize();
        });
    }
    for (auto& t : threads) {
        t.join();
    }
    const double elapsed = NowMs() - started;
    stop = true;
    sampler.join();
    if (host != nullptr) {
        GetProcessMemoryInfo(host, reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&after), sizeof(after));
        CloseHandle(host);
    }
    Check("SHELL-12", peak.load() <= 3, "%d requests (%d threads): at most %d engine processes alive at once (limit 3, the spare included)", kThreads * kPerThread, kThreads, peak.load());
    Check("SHELL-12", failed.load() == 0 && slow.load() == 0, "%d succeeded, %d failed, %d took longer than 2.5 s; %.1f s in all", ok.load(), failed.load(), slow.load(), elapsed / 1000);
    const double growth = (static_cast<double>(after.PrivateUsage) - static_cast<double>(before.PrivateUsage)) / (1024 * 1024);
    Check("SHELL-12", growth < 128, "ShellHost private bytes: %.1f MB before, %.1f MB after (growth %.1f MB; bound 128 MB until a number is set from R1 and R2 runs)", before.PrivateUsage / 1048576.0,
          after.PrivateUsage / 1048576.0, growth);
}

void PhasePipeSecurity() {
    const std::wstring sid = UserSid();
    const std::wstring pipe_path = L"\\\\.\\pipe\\MikePDF.Shell." + sid;
    // A running ShellHost first.
    (void)GetThumb(UniqueCopy(L"rectangles.pdf", L"pipe"), 64);

    // SHELL-17 (a): this probe has no package identity, so ShellHost must close the connection without a word, even for a perfectly formed Hello.
    HANDLE client = CreateFileW(pipe_path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
    Check("SHELL-17", client != INVALID_HANDLE_VALUE, "the user can open the pipe (error %lu)", client == INVALID_HANDLE_VALUE ? GetLastError() : 0ul);
    if (client != INVALID_HANDLE_VALUE) {
        // The pipe's security descriptor: a protected DACL naming the user and SYSTEM, and the Low label.
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (GetSecurityInfo(client, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION, nullptr, nullptr, nullptr, nullptr, &descriptor) == ERROR_SUCCESS) {
            LPWSTR text = nullptr;
            if (ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, SDDL_REVISION_1, DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION, &text, nullptr)) {
                const std::wstring sddl = text;
                LocalFree(text);
                Check("SHELL-17", sddl.rfind(L"D:P", 0) == 0 && sddl.find(L";;;" + sid + L")") != std::wstring::npos && sddl.find(L";;;SY)") != std::wstring::npos && sddl.find(L"(ML;;NW;;;LW)") != std::wstring::npos &&
                                      sddl.find(L";;;WD)") == std::wstring::npos,
                      "the pipe's security descriptor is %ls", sddl.c_str());
            }
            LocalFree(descriptor);
        }
        mikepdf::protocol::Hello hello;
        hello.schema_version = mikepdf::protocol::kSchemaVersion;
        hello.process_kind = mikepdf::protocol::ProcessKind::ShellHandler;
        hello.pid = GetCurrentProcessId();
        auto frame = mikepdf::protocol::encode_request_frame(1, 0, 1000, hello);
        DWORD written = 0;
        OVERLAPPED write{};
        write.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!WriteFile(client, frame.value.data(), static_cast<DWORD>(frame.value.size()), nullptr, &write) && GetLastError() == ERROR_IO_PENDING) {
            GetOverlappedResult(client, &write, &written, TRUE);
        }
        BYTE reply[16];
        OVERLAPPED read{};
        read.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        DWORD got = 0;
        bool answered = false, closed = false;
        if (!ReadFile(client, reply, sizeof(reply), nullptr, &read)) {
            if (GetLastError() == ERROR_IO_PENDING) {
                if (WaitForSingleObject(read.hEvent, 3000) == WAIT_OBJECT_0) {
                    answered = GetOverlappedResult(client, &read, &got, FALSE) && got > 0;
                    closed = !answered;
                } else {
                    CancelIoEx(client, &read);
                }
            } else {
                closed = true;
            }
        } else {
            answered = true;
        }
        Check("SHELL-17", closed && !answered, "a client outside the package family gets no answer and is closed (answered=%d closed=%d)", answered ? 1 : 0, closed ? 1 : 0);
        CloseHandle(write.hEvent);
        CloseHandle(read.hEvent);
        CloseHandle(client);
    }

    // SHELL-17 (b): a process of the same user that owns the pipe name before ShellHost does. The handler must refuse it and send it nothing.
    KillAll(kShellHost);
    for (int i = 0; i < 50 && WaitNamedPipeW(pipe_path.c_str(), 1); ++i) {
        Sleep(100);
    }
    HANDLE squatter = CreateNamedPipeW(pipe_path.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 4, 65536, 65536, 0, nullptr);
    Check("SHELL-17", squatter != INVALID_HANDLE_VALUE, "the squatter pipe was created (error %lu)", squatter == INVALID_HANDLE_VALUE ? GetLastError() : 0ul);
    if (squatter != INVALID_HANDLE_VALUE) {
        OVERLAPPED connect{};
        connect.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        ConnectNamedPipe(squatter, &connect);
        const Thumb t = GetThumb(UniqueCopy(L"rectangles.pdf", L"squat"), 64);
        Check("SHELL-17", FAILED(t.hr), "a server that is not this package's is refused by the handler: the thumbnail fails (hr=0x%08lx)", static_cast<unsigned long>(t.hr));
        BYTE byte;
        OVERLAPPED read{};
        read.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        DWORD got = 0;
        bool received = false;
        if (WaitForSingleObject(connect.hEvent, 0) == WAIT_OBJECT_0 || GetLastError() == ERROR_PIPE_CONNECTED) {
            if (!ReadFile(squatter, &byte, 1, nullptr, &read) && GetLastError() == ERROR_IO_PENDING) {
                if (WaitForSingleObject(read.hEvent, 500) == WAIT_OBJECT_0) {
                    received = GetOverlappedResult(squatter, &read, &got, FALSE) && got > 0;
                } else {
                    CancelIoEx(squatter, &read);
                }
            }
        }
        Check("SHELL-17", !received, "and the squatter received no byte of the document or of any request");
        Check("SHELL-17", FirstPid(kShellHost) == 0, "the handler did not start ShellHost while the pipe name was taken");
        CloseHandle(read.hEvent);
        CloseHandle(connect.hEvent);
        CloseHandle(squatter);
    }
    Sleep(300);
}

void WriteSettingsFile(const char* json) {
    if (g_settings_dir.empty()) {
        return;
    }
    const std::wstring path = g_settings_dir + L"\\settings.json";
    HANDLE f = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(f, json, static_cast<DWORD>(std::strlen(json)), &written, nullptr);
        CloseHandle(f);
    }
}

void PhaseUserSwitch() {
    if (g_settings_dir.empty()) {
        Info("SHELL-16", "skipped: no settings folder was given");
        return;
    }
    (void)GetThumb(UniqueCopy(L"rectangles.pdf", L"switch-up"), 64);  // ShellHost is up
    WriteSettingsFile("{ \"schemaVersion\": 1, \"values\": { \"shell.thumbnails\": false, \"shell.preview\": false } }");
    const Thumb off = GetThumb(UniqueCopy(L"rectangles.pdf", L"switch-off"), 256);
    Check("SHELL-16", FAILED(off.hr) && off.ms < 300, "with the thumbnail switch off the request fails fast: hr=0x%08lx in %.0f ms (ShellHost answers within 100 ms; the shell adds its own 15-20 ms)", static_cast<unsigned long>(off.hr), off.ms);
    const PreviewRun preview = RunPreview(UniqueCopy(L"hello_world.pdf", L"switch-preview"), L"Preview is turned off", 8000, false, nullptr);
    Check("SHELL-16", preview.value == L"Preview is turned off", "with the preview switch off the pane says '%ls'", preview.value.c_str());
    WriteSettingsFile("{ \"schemaVersion\": 1, \"values\": { \"shell.thumbnails\": true, \"shell.preview\": true } }");
    Check("SHELL-16", SUCCEEDED(GetThumb(UniqueCopy(L"rectangles.pdf", L"switch-on"), 64).hr), "and turning it back on applies without restarting ShellHost");
}

void PhasePolicy() {
    if (!OnDisposableRunner()) {
        Info("SHELL-15", "skipped: this is not a GitHub-hosted runner, and the policy phases write HKLM");
        return;
    }
    wchar_t competitor_dll[MAX_PATH];
    GetModuleFileNameW(nullptr, competitor_dll, MAX_PATH);
    PathRemoveFileSpecW(competitor_dll);
    PathAppendW(competitor_dll, L"mikepdf_shell_competitor.dll");
    const std::wstring clsid = L"{C0FFEE00-5A1E-4F3B-9C11-0A5B6D6B0004}";
    const std::wstring thumb_iid = L"{E357FCCD-A995-4576-B01F-234630154E96}";
    const std::wstring ext_key = L"Software\\Classes\\.pdf\\ShellEx\\" + thumb_iid;
    ClearPolicies();
    KillAll(kShellHost);
    Sleep(300);

    // The competitor: another app's classic handler for .pdf, registered the way an installer does. With nothing switched off, who serves .pdf?
    SetString(HKEY_LOCAL_MACHINE, L"Software\\Classes\\CLSID\\" + clsid + L"\\InprocServer32", nullptr, competitor_dll);
    SetString(HKEY_LOCAL_MACHINE, L"Software\\Classes\\CLSID\\" + clsid + L"\\InprocServer32", L"ThreadingModel", L"Both");
    SetString(HKEY_LOCAL_MACHINE, ext_key, nullptr, clsid);
    AssocChanged();
    const Thumb baseline = GetThumb(UniqueCopy(L"rectangles.pdf", L"policy-base"), 256);
    Info("OQ-SHELL-14", "with a classic handler registered for .pdf and nothing switched off, the shell shows: %s (hr=0x%08lx)", IsCompetitor(baseline) ? "the other app's thumbnail" : (SUCCEEDED(baseline.hr) && FourCorners(baseline) ? "MikePDF's thumbnail" : "something else"),
         static_cast<unsigned long>(baseline.hr));

    // SHELL-15: the policy off. ShellHost must not be started, and the call must fail or fall through; which one is the OQ-SHELL-14 measurement.
    KillAll(kShellHost);
    Sleep(300);
    SetDword(L"ShellThumbnailsEnabled", 0);
    const Thumb declined = GetThumb(UniqueCopy(L"rectangles.pdf", L"policy-off"), 256);
    Sleep(300);
    const bool theirs = SUCCEEDED(declined.hr) && IsCompetitor(declined);
    const bool ours = SUCCEEDED(declined.hr) && FourCorners(declined);
    Check("SHELL-15", !ours, "with ShellThumbnailsEnabled=0 the shell does not show MikePDF's thumbnail (hr=0x%08lx)", static_cast<unsigned long>(declined.hr));
    Check("SHELL-15", FirstPid(kShellHost) == 0, "and ShellHost was never started for it");
    P("RESULT OQ-SHELL-14 handler-declines: %s", theirs ? "FALLS THROUGH to the next registered handler (the other app's thumbnail was shown)" : (FAILED(declined.hr) ? "NO FALL-THROUGH: the request failed, so Explorer shows the generic icon" : "INCONCLUSIVE"));
    P("RESULT OQ-SHELL-14 fallthrough=%s", theirs ? "yes" : (FAILED(declined.hr) ? "no" : "inconclusive"));

    // The preview policy.
    SetDword(L"ShellPreviewEnabled", 0);
    const PreviewRun preview = RunPreview(UniqueCopy(L"hello_world.pdf", L"policy-preview"), L"Preview is turned off by your organisation", 8000, false, nullptr);
    Check("SHELL-15", preview.value == L"Preview is turned off by your organisation", "ShellPreviewEnabled=0: the pane says '%ls'", preview.value.c_str());
    Check("SHELL-15", FirstPid(kShellHost) == 0, "and ShellHost was still not started");

    // An invalid value fails closed, a 1 changes nothing, and removing the policy brings the handler back.
    SetDword(L"ShellThumbnailsEnabled", 7);
    Check("SHELL-15", !FourCorners(GetThumb(UniqueCopy(L"rectangles.pdf", L"policy-bad"), 256)), "an invalid policy value (7) fails closed");
    ClearPolicies();
    Sleep(500);
    const Thumb back = GetThumb(UniqueCopy(L"rectangles.pdf", L"policy-gone"), 256);
    Check("SHELL-15", SUCCEEDED(back.hr) && FourCorners(back), "with the policy removed MikePDF's thumbnails are back (hr=0x%08lx)", static_cast<unsigned long>(back.hr));

    // The user switch, the same question: what does the shell show when ShellHost declines?
    if (!g_settings_dir.empty()) {
        WriteSettingsFile("{ \"schemaVersion\": 1, \"values\": { \"shell.thumbnails\": false } }");
        const Thumb setting = GetThumb(UniqueCopy(L"rectangles.pdf", L"setting-off"), 256);
        P("RESULT OQ-SHELL-14 setting-off: %s (hr=0x%08lx)", IsCompetitor(setting) ? "falls through to the other app's thumbnail" : (FAILED(setting.hr) ? "request fails, generic icon" : "inconclusive"), static_cast<unsigned long>(setting.hr));
        WriteSettingsFile("{ \"schemaVersion\": 1, \"values\": { \"shell.thumbnails\": true } }");
    }

    // Clean up everything this phase wrote.
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, ext_key.c_str());
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, (L"Software\\Classes\\CLSID\\" + clsid).c_str());
    ClearPolicies();
    AssocChanged();
}

void PhaseIdle(bool skip) {
    if (skip) {
        Info("SHELL-10", "skipped by request");
        return;
    }
    (void)GetThumb(UniqueCopy(L"rectangles.pdf", L"idle"), 64);
    Check("SHELL-10", FirstPid(kShellHost) != 0, "ShellHost is running after a request");
    Sleep(75000);
    Check("SHELL-10", FirstPid(kShellHost) == 0, "75 s after the last request ShellHost has exited");
    Check("SHELL-10", CountRunning(kEngine) == 0, "and no engine process of the shell requests remains");
}

int AfterUninstall() {
    // SHELL-26: with the package removed, the classes are gone.
    for (const auto& [name, clsid] : {std::pair{"thumbnail provider", kClsidThumbnailProvider}, std::pair{"preview handler", kClsidPreviewHandler}}) {
        IUnknown* unknown = nullptr;
        const HRESULT hr = CoCreateInstance(clsid, nullptr, CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&unknown));
        Check("SHELL-26", hr == REGDB_E_CLASSNOTREG, "%s: CoCreateInstance after uninstall hr=0x%08lx (expected REGDB_E_CLASSNOTREG)", name, static_cast<unsigned long>(hr));
        if (unknown != nullptr) {
            unknown->Release();
        }
        LPOLESTR text = nullptr;
        StringFromCLSID(clsid, &text);
        HKEY key = nullptr;
        const std::wstring path = std::wstring(L"CLSID\\") + text;
        const bool present = RegOpenKeyExW(HKEY_CLASSES_ROOT, path.c_str(), 0, KEY_READ, &key) == ERROR_SUCCESS;
        if (present) RegCloseKey(key);
        Check("SHELL-26", !present, "%s: no HKCR\\%ls key remains", name, path.c_str());
        CoTaskMemFree(text);
    }
    Check("SHELL-26", FirstPid(kShellHost) == 0, "no ShellHost process remains");
    return g_failed == 0 ? 0 : 1;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    // mikepdf_shell_probe --run <outdir> <fixturesdir> [<settingsdir>] [--skip-idle]
    // mikepdf_shell_probe --after-uninstall <outdir>
    if (argc < 3) {
        std::printf("usage: mikepdf_shell_probe --run <outdir> <fixturesdir> [settingsdir] [--skip-idle] | --after-uninstall <outdir>\n");
        return 2;
    }
    const std::wstring mode = argv[1];
    const std::wstring outdir = argv[2];
    CreateDirectoryW(outdir.c_str(), nullptr);
    g_log = _wfopen((outdir + L"\\probe-" + (mode == L"--run" ? L"run" : L"uninstall") + L".log").c_str(), L"wb");
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    Environment();
    if (mode == L"--after-uninstall") {
        const int rc = AfterUninstall();
        std::fclose(g_log);
        return rc;
    }
    if (argc < 4) {
        return 2;
    }
    g_fixtures = argv[3];
    bool skip_idle = false;
    for (int i = 4; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--skip-idle") {
            skip_idle = true;
        } else {
            g_settings_dir = argv[i];
        }
    }
    wchar_t temp[MAX_PATH];
    GetTempPathW(MAX_PATH, temp);
    g_work = std::wstring(temp) + L"mikepdf-shell-probe";
    CreateDirectoryW(g_work.c_str(), nullptr);

    PhaseActivation();
    PhaseThumbnails();
    PhasePreview();
    PhaseKillAndRestart();
    PhaseFlood();
    PhasePipeSecurity();
    PhaseUserSwitch();
    PhasePolicy();
    PhaseIdle(skip_idle);
    P("probe done: %d gating check(s) failed", g_failed);
    std::fclose(g_log);
    return g_failed == 0 ? 0 : 1;
}
