// TEST ONLY. This executable must never ship: it deliberately tries everything the sandbox is meant to forbid.
//
// The integration tests launch it through the same SandboxedProcessLauncher profile as Quire.Engine and read what it
// writes to the broker log pipe (QUIRE_LOG_PIPE; stdout when there is none), one tab-separated record per line:
//   INFO <key> <value>
//   RESULT <name> <expect: deny|allow> <outcome: ALLOWED|DENIED|FAILED> <win32 error> <detail>
// With --control it skips the runtime signature policy, so the same checks can be run unsandboxed as a control: every
// "deny" row that is not ALLOWED there would be a vacuous pass inside the sandbox.
//
// ADR-0004 negative-access suite (S3): files in Documents, temp and the repo, UNC, TCP, CreateProcess, OpenProcess on the
// broker, the clipboard, an HKCU write, and an unsigned DLL load after the runtime signature policy.
#include <WinSock2.h>
#include <WS2tcpip.h>
#include <Windows.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

#include "log_pipe.h"
#include "runtime_policy.h"

namespace {

HANDLE g_out = INVALID_HANDLE_VALUE;

bool g_to_log_pipe = false;

void emit(const std::string& line) {
    if (g_to_log_pipe) {
        quire::engine::write_log_line(line);
        return;
    }
    const std::string text = line + "\n";
    DWORD written = 0;
    WriteFile(g_out, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
}

std::string narrow(std::wstring_view wide) {
    if (wide.empty()) {
        return {};
    }
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), out.data(), size, nullptr, nullptr);
    return out;
}

std::string clean(std::string text) {
    for (char& c : text) {
        if (c == '\t' || c == '\n' || c == '\r') {
            c = ' ';
        }
    }
    return text;
}

void info(const std::string& key, const std::string& value) { emit("INFO\t" + key + "\t" + clean(value)); }

bool is_denied(unsigned long error) {
    switch (error) {
        case ERROR_ACCESS_DENIED:           // 5
        case ERROR_NETWORK_ACCESS_DENIED:   // 65
        case ERROR_CHILD_PROCESS_BLOCKED:   // 367
        case ERROR_PRIVILEGE_NOT_HELD:      // 1314
        case ERROR_INVALID_IMAGE_HASH:      // 577
        case ERROR_ACCESS_DISABLED_BY_POLICY:  // 1260
        case WSAEACCES:                      // 10013
        case WSAETIMEDOUT:                   // 10060: loopback SYN silently dropped in an AppContainer
            return true;
        default:
            return false;
    }
}

struct Outcome {
    bool allowed{};
    unsigned long error{};
    std::string detail;
};

void run(const char* name, const char* expect, const Outcome& outcome) {
    const char* result = outcome.allowed ? "ALLOWED" : is_denied(outcome.error) ? "DENIED" : "FAILED";
    emit(std::string("RESULT\t") + name + "\t" + expect + "\t" + result + "\t" + std::to_string(outcome.error) + "\t" + clean(outcome.detail));
}

Outcome open_directory(const std::wstring& path) {
    const HANDLE h = CreateFileW(path.c_str(), FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                 OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        return {false, GetLastError(), "open directory"};
    }
    CloseHandle(h);
    return {true, 0, "directory opened"};
}

Outcome enumerate_directory(const std::wstring& path) {
    WIN32_FIND_DATAW data{};
    const HANDLE h = FindFirstFileW((path + L"\\*").c_str(), &data);
    if (h == INVALID_HANDLE_VALUE) {
        return {false, GetLastError(), "enumerate"};
    }
    FindClose(h);
    return {true, 0, "enumerated"};
}

Outcome write_file(const std::wstring& dir, const wchar_t* leaf) {
    const std::wstring path = dir + L"\\" + leaf + std::to_wstring(GetCurrentProcessId()) + L".tmp";
    const HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        return {false, GetLastError(), "create file"};
    }
    CloseHandle(h);
    DeleteFileW(path.c_str());
    return {true, 0, "file created and deleted"};
}

Outcome read_file(const std::wstring& path) {
    const HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                 FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        return {false, GetLastError(), "open file"};
    }
    CloseHandle(h);
    return {true, 0, "file opened"};
}

Outcome open_unc(const std::wstring& path) {
    const HANDLE h = CreateFileW(path.c_str(), FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                                 FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        return {false, GetLastError(), "open UNC path"};
    }
    CloseHandle(h);
    return {true, 0, "UNC path opened"};
}

Outcome tcp_connect(const std::string& address, unsigned short port) {
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) {
        return {false, static_cast<unsigned long>(WSAGetLastError()), "socket"};
    }
    u_long nonblocking = 1;
    ioctlsocket(s, FIONBIO, &nonblocking);
    sockaddr_in target{};
    target.sin_family = AF_INET;
    target.sin_port = htons(port);
    if (inet_pton(AF_INET, address.c_str(), &target.sin_addr) != 1) {
        closesocket(s);
        return {false, ERROR_INVALID_PARAMETER, "bad address"};
    }
    Outcome outcome{false, 0, address};
    if (connect(s, reinterpret_cast<sockaddr*>(&target), sizeof(target)) == 0) {
        outcome = {true, 0, "connected to " + address};
    } else if (const int error = WSAGetLastError(); error != WSAEWOULDBLOCK) {
        outcome = {false, static_cast<unsigned long>(error), "connect " + address};
    } else {
        fd_set writable, failed;
        FD_ZERO(&writable);
        FD_ZERO(&failed);
        FD_SET(s, &writable);
        FD_SET(s, &failed);
        timeval timeout{3, 0};
        const int ready = select(0, nullptr, &writable, &failed, &timeout);
        if (ready > 0 && FD_ISSET(s, &writable)) {
            outcome = {true, 0, "connected to " + address};
        } else if (ready > 0) {
            int so_error = 0;
            int len = sizeof(so_error);
            getsockopt(s, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&so_error), &len);
            outcome = {false, static_cast<unsigned long>(so_error), "connect failed " + address};
        } else {
            outcome = {false, WSAETIMEDOUT, "no answer from " + address};
        }
    }
    closesocket(s);
    return outcome;
}

Outcome create_cmd() {
    wchar_t command[] = L"C:\\Windows\\System32\\cmd.exe /c exit 0";
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(nullptr, command, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) {
        return {false, GetLastError(), "cmd.exe"};
    }
    WaitForSingleObject(process.hProcess, 2000);
    TerminateProcess(process.hProcess, 0);
    CloseHandle(process.hProcess);
    CloseHandle(process.hThread);
    return {true, 0, "child process started and reaped"};
}

Outcome open_process(DWORD pid, DWORD access, const char* what) {
    const HANDLE h = OpenProcess(access, FALSE, pid);
    if (h == nullptr) {
        return {false, GetLastError(), what};
    }
    CloseHandle(h);
    return {true, 0, what};
}

Outcome read_clipboard() {
    if (!OpenClipboard(nullptr)) {
        return {false, GetLastError(), "OpenClipboard"};
    }
    const bool has_text = IsClipboardFormatAvailable(CF_UNICODETEXT) != 0;
    CloseClipboard();
    return {true, 0, has_text ? "clipboard opened, text present (not read)" : "clipboard opened"};
}

Outcome write_registry() {
    HKEY key = nullptr;
    DWORD disposition = 0;
    const LONG rc = RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\QuireSandboxProbe", 0, nullptr, 0, KEY_WRITE, nullptr, &key, &disposition);
    if (rc != ERROR_SUCCESS) {
        return {false, static_cast<unsigned long>(rc), "RegCreateKeyEx HKCU\\Software\\QuireSandboxProbe"};
    }
    RegCloseKey(key);
    RegDeleteKeyW(HKEY_CURRENT_USER, L"Software\\QuireSandboxProbe");
    return {true, 0, "key created and removed"};
}

Outcome load_dll(const std::wstring& path) {
    const HMODULE module = LoadLibraryExW(path.c_str(), nullptr, 0);
    if (module == nullptr) {
        return {false, GetLastError(), "LoadLibrary"};
    }
    FreeLibrary(module);
    return {true, 0, "image loaded"};
}

void report_process() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        return;
    }
    alignas(8) std::uint8_t buffer[2048];
    DWORD returned = 0;
    DWORD value = 0;
    if (GetTokenInformation(token, static_cast<TOKEN_INFORMATION_CLASS>(29) /* TokenIsAppContainer */, &value, sizeof(value), &returned)) {
        info("isAppContainer", std::to_string(value));
    }
    if (GetTokenInformation(token, TokenIntegrityLevel, buffer, sizeof(buffer), &returned)) {
        const auto* label = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(buffer);
        const SID* sid = static_cast<const SID*>(label->Label.Sid);
        char text[16];
        std::snprintf(text, sizeof(text), "0x%lX", sid->SubAuthority[sid->SubAuthorityCount - 1]);
        info("integrityRid", text);
    }
    if (GetTokenInformation(token, static_cast<TOKEN_INFORMATION_CLASS>(30) /* TokenCapabilities */, buffer, sizeof(buffer), &returned)) {
        info("capabilityCount", std::to_string(*reinterpret_cast<DWORD*>(buffer)));
    }
    if (GetTokenInformation(token, TokenPrivileges, buffer, sizeof(buffer), &returned)) {
        info("privilegeCount", std::to_string(*reinterpret_cast<DWORD*>(buffer)));
    }
    CloseHandle(token);
}

// First DWORD of each PROCESS_MITIGATION_*_POLICY as read back from the OS; the integration test decodes the bits.
void report_mitigations(const char* phase) {
    static const char* const names[] = {"DEP", "ASLR", "DynamicCode", "StrictHandleCheck", "SystemCallDisable", "MitigationOptionsMask",
                                        "ExtensionPointDisable", "ControlFlowGuard", "Signature", "FontDisable", "ImageLoad",
                                        "SystemCallFilter", "PayloadRestriction", "ChildProcess", "SideChannelIsolation",
                                        "UserShadowStack", "RedirectionTrust", "UserPointerAuth", "SEHOP"};
    for (int policy = 0; policy < 19; ++policy) {
        std::uint32_t buffer[8] = {};
        const SIZE_T size = policy == 0 ? 8 : policy == 5 ? 24 : 4;
        char text[32];
        if (GetProcessMitigationPolicy(GetCurrentProcess(), static_cast<PROCESS_MITIGATION_POLICY>(policy), buffer, size)) {
            std::snprintf(text, sizeof(text), "0x%X", buffer[0]);
        } else {
            std::snprintf(text, sizeof(text), "err%lu", GetLastError());
        }
        info(std::string("mit.") + phase + "." + names[policy], text);
    }
}

struct Options {
    std::wstring documents, temp, repo_dir, repo_file, unc, tcp_lan_address, parent_pid, rogue_dll, temp_dll, engine_dir;
    unsigned short tcp_loopback_port{};
    unsigned short tcp_lan_port{};
    DWORD parent{};
    bool control{};
};

bool parse(Options& o) {
    for (int i = 1; i < __argc; ++i) {
        const std::wstring_view arg = __wargv[i];
        if (arg == L"--control") {
            o.control = true;
            continue;
        }
        if (i + 1 >= __argc) {
            return false;
        }
        const std::wstring value = __wargv[++i];
        if (arg == L"--documents") o.documents = value;
        else if (arg == L"--temp") o.temp = value;
        else if (arg == L"--repo-dir") o.repo_dir = value;
        else if (arg == L"--repo-file") o.repo_file = value;
        else if (arg == L"--unc") o.unc = value;
        else if (arg == L"--rogue-dll") o.rogue_dll = value;
        else if (arg == L"--temp-dll") o.temp_dll = value;
        else if (arg == L"--engine-dir") o.engine_dir = value;
        else if (arg == L"--parent") o.parent = static_cast<DWORD>(std::stoul(value));
        else if (arg == L"--tcp-loopback") o.tcp_loopback_port = static_cast<unsigned short>(std::stoul(value));
        else if (arg == L"--tcp-lan") {  // address:port
            const auto colon = value.rfind(L':');
            if (colon == std::wstring::npos) return false;
            o.tcp_lan_address = value.substr(0, colon);
            o.tcp_lan_port = static_cast<unsigned short>(std::stoul(value.substr(colon + 1)));
        } else {
            return false;
        }
    }
    return true;
}

}  // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    // Sandboxed: nothing is inherited, so results go to the broker's log pipe. The unsandboxed control run uses its stdout.
    g_to_log_pipe = quire::engine::connect_log_pipe();
    g_out = GetStdHandle(STD_OUTPUT_HANDLE);
    if (!g_to_log_pipe && (g_out == nullptr || g_out == INVALID_HANDLE_VALUE)) {
        return 10;
    }
    Options o;
    try {
        if (!parse(o)) {
            emit("ERROR\tbad arguments");
            return 2;
        }
    } catch (...) {
        emit("ERROR\tbad arguments");
        return 2;
    }

    WSADATA wsa{};
    WSAStartup(MAKEWORD(2, 2), &wsa);
    report_process();
    report_mitigations("creation");

    // Same order as the engine: everything the process needs is loaded, then the signature policy locks it.
    LoadLibraryW(L"dwrite.dll");
    if (!o.control) {
        const unsigned long error = quire::engine::apply_runtime_signature_policy();
        info("runtimeSignaturePolicy", error == 0 ? "ok" : "err" + std::to_string(error));
        report_mitigations("runtime");
    }

    run("fs.read.documents.open", "deny", open_directory(o.documents));
    run("fs.read.documents.enumerate", "deny", enumerate_directory(o.documents));
    run("fs.write.temp", "deny", write_file(o.temp, L"quire-probe-"));
    run("fs.write.repo-dir", "deny", write_file(o.repo_dir, L"quire-probe-"));
    run("fs.read.repo-file", "deny", read_file(o.repo_file));
    run("fs.read.unc", "deny", open_unc(o.unc));
    run("net.tcp.connect-loopback", "deny", tcp_connect("127.0.0.1", o.tcp_loopback_port));
    if (!o.tcp_lan_address.empty()) {
        run("net.tcp.connect-lan-address", "deny", tcp_connect(narrow(o.tcp_lan_address), o.tcp_lan_port));
    }
    run("proc.create.cmd.exe", "deny", create_cmd());
    run("proc.open.parent.query-limited", "deny", open_process(o.parent, PROCESS_QUERY_LIMITED_INFORMATION, "pid of the broker"));
    run("proc.open.parent.vm-read-dup-handle", "deny", open_process(o.parent, PROCESS_VM_READ | PROCESS_DUP_HANDLE, "pid of the broker"));
    run("ui.clipboard.read", "deny", read_clipboard());
    run("reg.write.hkcu", "deny", write_registry());
    run("dll.load.unsigned-temp-copy", "deny", load_dll(o.temp_dll));
    run("dll.load.unsigned-in-engine-dir", "deny", load_dll(o.rogue_dll));

    // Positive controls: the sandbox is useless if these fail too.
    run("ctl.read.engine-dir", "allow", read_file(o.engine_dir + L"\\Quire.Engine.exe"));
    run("ctl.dll.load.microsoft-signed", "allow", load_dll(L"dwrite.dll"));
    emit("DONE");
    WSACleanup();
    return 0;
}
