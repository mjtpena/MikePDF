// Spike S12 candidate (c): which printing calls survive inside the engine's sandbox?
// Same checks as Probe/Program.cs, but native C++ so the engine's FULL creation-time mitigation set (including prohibit-dynamic-code) can be applied.
// args: report allowed-pdf denied-pdf [signed-only]
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <cstdarg>
#include <winspool.h>
#include <cstdio>
#include <cwchar>

static FILE* g = nullptr;
static void R(const wchar_t* name, const wchar_t* fmt, ...) {
    fwprintf(g, L"%s | ", name);
    va_list a; va_start(a, fmt); vfwprintf(g, fmt, a); va_end(a);
    fwprintf(g, L"\n"); fflush(g);
}

static void Job(const wchar_t* label, const wchar_t* printer, const wchar_t* out) {
    HDC dc = CreateDCW(L"WINSPOOL", printer, nullptr, nullptr);
    if (!dc) { R(label, L"CreateDC DENIED win32=%lu", GetLastError()); return; }
    DOCINFOW di{}; di.cbSize = sizeof(di); di.lpszDocName = L"S12 probe (C++)"; di.lpszOutput = out;
    if (StartDocW(dc, &di) <= 0) { R(label, L"StartDoc DENIED win32=%lu", GetLastError()); DeleteDC(dc); return; }
    StartPage(dc); Rectangle(dc, 200, 200, 2000, 1400); EndPage(dc); int ed = EndDoc(dc); DeleteDC(dc);
    for (int i = 0; i < 100 && GetFileAttributesW(out) == INVALID_FILE_ATTRIBUTES; ++i) Sleep(100);
    R(label, L"completed (EndDoc=%d); file visible to the container: %d", ed, GetFileAttributesW(out) != INVALID_FILE_ATTRIBUTES);
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 4) return 2;
    g = _wfopen(argv[1], L"w, ccs=UTF-8"); if (!g) return 3;
    bool signedOnly = argc > 4;
    HANDLE tok; DWORD isAc = 0, ret = 0; BYTE buf[1024];
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) {
        GetTokenInformation(tok, TokenIsAppContainer, &isAc, sizeof(isAc), &ret);
        DWORD caps = 0; if (GetTokenInformation(tok, TokenCapabilities, buf, sizeof(buf), &ret)) caps = *reinterpret_cast<DWORD*>(buf);
        R(L"process", L"pid %lu IsAppContainer=%lu capability count=%lu", GetCurrentProcessId(), isAc, caps);
    }
    PROCESS_MITIGATION_DYNAMIC_CODE_POLICY dyn{}; if (GetProcessMitigationPolicy(GetCurrentProcess(), ProcessDynamicCodePolicy, &dyn, sizeof(dyn))) R(L"mitigation", L"ProhibitDynamicCode=%lu", dyn.ProhibitDynamicCode);
    if (signedOnly) {
        PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY sp{}; sp.MicrosoftSignedOnly = 1;
        R(L"SetProcessMitigationPolicy(MicrosoftSignedOnly)", SetProcessMitigationPolicy(ProcessSignaturePolicy, &sp, sizeof(sp)) ? L"applied" : L"failed win32=%lu", GetLastError());
    }
    const wchar_t* printer = L"Microsoft Print to PDF";
    HANDLE hp = nullptr; BOOL o = OpenPrinterW(const_cast<LPWSTR>(printer), &hp, nullptr);
    if (o) { R(L"OpenPrinter", L"ALLOWED"); ClosePrinter(hp); } else R(L"OpenPrinter", L"DENIED win32=%lu", GetLastError());
    DWORD need = 0, cnt = 0; EnumPrintersW(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS, nullptr, 4, nullptr, 0, &need, &cnt);
    R(L"EnumPrinters(local)", need ? L"ALLOWED (needs %lu bytes)" : L"DENIED/empty win32=%lu", need ? need : GetLastError());
    LONG sz = DocumentPropertiesW(nullptr, nullptr, const_cast<LPWSTR>(printer), nullptr, nullptr, 0);
    R(L"DocumentProperties(size query)", sz > 0 ? L"ALLOWED (%ld bytes)" : L"DENIED win32=%lu", sz > 0 ? sz : (LONG)GetLastError());
    HDC sdc = GetDC(nullptr); HDC mdc = CreateCompatibleDC(sdc); HBITMAP bmp = mdc ? CreateCompatibleBitmap(sdc, 64, 64) : nullptr;
    R(L"memory DC + bitmap", mdc && bmp ? L"ALLOWED" : L"DENIED win32=%lu", GetLastError());
    Job(L"A: output into a folder the container may write", printer, argv[2]);
    Job(L"B: output into a folder the container may NOT write", printer, argv[3]);
    fclose(g);
    return 0;
}
