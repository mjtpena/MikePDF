#include "shell_client.h"

#include <appmodel.h>
#include <sddl.h>
#include <shlwapi.h>

#include <algorithm>
#include <cwchar>
#include <cwctype>
#include <variant>

namespace mikepdf::shellext {

namespace {

using protocol::MessageType;
using protocol::Status;

constexpr wchar_t kPipePrefix[] = L"\\\\.\\pipe\\";
constexpr wchar_t kPipeStem[] = L"MikePDF.Shell.";
constexpr wchar_t kLaunchStem[] = L"Local\\MikePDF.Shell.Launch.";

std::wstring current_user_sid() {
    HANDLE raw = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw)) {
        return {};
    }
    UniqueHandle token(raw);
    DWORD size = 0;
    GetTokenInformation(token.get(), TokenUser, nullptr, 0, &size);
    if (size == 0) {
        return {};
    }
    std::vector<std::uint8_t> buffer(size);
    if (!GetTokenInformation(token.get(), TokenUser, buffer.data(), size, &size)) {
        return {};
    }
    LPWSTR text = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &text)) {
        return {};
    }
    std::wstring sid = text;
    LocalFree(text);
    return sid;
}

bool is_packaged() noexcept {
    UINT32 length = 0;
    return GetCurrentPackageFullName(&length, nullptr) != APPMODEL_ERROR_NO_PACKAGE;
}

std::wstring environment_value(const wchar_t* name) {
    wchar_t buffer[MAX_PATH]{};
    const DWORD n = GetEnvironmentVariableW(name, buffer, MAX_PATH);
    return n > 0 && n < MAX_PATH ? std::wstring(buffer, n) : std::wstring();
}

bool is_safe_pipe_name(const std::wstring& name) {
    return !name.empty() && name.size() <= 128 && std::all_of(name.begin(), name.end(), [](wchar_t c) { return iswalnum(c) || c == L'.' || c == L'-' || c == L'_'; });
}

std::wstring full_path(const std::wstring& path) {
    wchar_t buffer[MAX_PATH]{};
    const DWORD n = GetFullPathNameW(path.c_str(), MAX_PATH, buffer, nullptr);
    return n > 0 && n < MAX_PATH ? std::wstring(buffer, n) : path;
}

bool same_file(const std::wstring& a, const std::wstring& b) { return CompareStringOrdinal(full_path(a).c_str(), -1, full_path(b).c_str(), -1, TRUE) == CSTR_EQUAL; }

}  // namespace

std::wstring pipe_name_for_sid(const std::wstring& sid) { return std::wstring(kPipeStem) + sid; }

ClientConfig production_config(HMODULE module, ShellFeature feature) {
    ClientConfig config;
    const std::wstring sid = current_user_sid();
    const std::wstring suffix = feature == ShellFeature::Preview ? L".Preview" : L"";
    std::wstring pipe = pipe_name_for_sid(sid) + suffix;
    wchar_t dll[MAX_PATH]{};
    const DWORD n = GetModuleFileNameW(module, dll, MAX_PATH);
    std::wstring dir(dll, n < MAX_PATH ? n : 0);
    dir = dir.substr(0, dir.find_last_of(L'\\') == std::wstring::npos ? 0 : dir.find_last_of(L'\\'));
    // Package layout: ShellExt\MikePDF.ShellExt.dll beside ShellHost\MikePDF.ShellHost.exe.
    config.shellhost_path = full_path(dir + L"\\..\\ShellHost\\MikePDF.ShellHost.exe");
    config.launch_mutex_name = std::wstring(kLaunchStem) + sid;
    config.require_package_family = is_packaged();
    if (!config.require_package_family) {
        // Unpackaged: tests only. Nothing here weakens a packaged host, which never reads these variables.
        const std::wstring test_pipe = environment_value(L"MIKEPDF_SHELL_TEST_PIPE");
        const std::wstring test_host = environment_value(L"MIKEPDF_SHELL_TEST_HOST");
        if (is_safe_pipe_name(test_pipe)) {
            pipe = test_pipe + suffix;
            config.shellhost_arguments = L"--pipe " + test_pipe;
            config.launch_mutex_name = std::wstring(L"Local\\MikePDF.Shell.Launch.Test.") + test_pipe;
        }
        if (!test_host.empty()) {
            config.shellhost_path = full_path(test_host);
        }
    }
    config.pipe_path = std::wstring(kPipePrefix) + pipe;
    return config;
}

HRESULT hresult_for_status(Status status) noexcept {
    switch (status) {
        case Status::PasswordRequired:
        case Status::PasswordIncorrect:
            return HRESULT_FROM_WIN32(ERROR_INVALID_PASSWORD);
        case Status::Damaged:
        case Status::Unsupported:
        case Status::UnsupportedEncryption:
            return HRESULT_FROM_WIN32(ERROR_BAD_FORMAT);
        case Status::Timeout:
            return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        case Status::Cancelled:
            return HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED);
        case Status::PermissionDenied:
            return E_ACCESSDENIED;
        case Status::EngineCrashed:
            return HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED);
        case Status::InvalidArgument:
        case Status::ProtocolError:
            return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        case Status::Ok:
        case Status::PartiallyRendered:
            break;
    }
    return E_FAIL;
}

bool ShellClient::verify_server() const {
    ULONG pid = 0;
    if (!GetNamedPipeServerProcessId(channel_.native(), &pid) || pid == 0) {
        return false;
    }
    UniqueHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (!process) {
        return false;
    }
    if (config_.require_package_family) {
        wchar_t mine[PACKAGE_FAMILY_NAME_MAX_LENGTH + 1]{};
        wchar_t theirs[PACKAGE_FAMILY_NAME_MAX_LENGTH + 1]{};
        UINT32 mine_length = PACKAGE_FAMILY_NAME_MAX_LENGTH + 1;
        UINT32 theirs_length = PACKAGE_FAMILY_NAME_MAX_LENGTH + 1;
        // Fails closed: a server with no package identity, or one whose family cannot be read, is not ShellHost.
        return GetCurrentPackageFamilyName(&mine_length, mine) == ERROR_SUCCESS && GetPackageFamilyName(process.get(), &theirs_length, theirs) == ERROR_SUCCESS &&
               wcscmp(mine, theirs) == 0;
    }
    wchar_t image[MAX_PATH]{};
    DWORD size = MAX_PATH;
    return QueryFullProcessImageNameW(process.get(), 0, image, &size) && same_file(image, config_.shellhost_path);
}

HRESULT ShellClient::start_shellhost(Deadline deadline, HANDLE cancel) {
    UniqueHandle mutex(CreateMutexW(nullptr, FALSE, config_.launch_mutex_name.c_str()));
    if (!mutex) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    HANDLE waits[2] = {mutex.get(), cancel};
    const DWORD wait = WaitForMultipleObjects(cancel != nullptr ? 2 : 1, waits, FALSE, remaining_ms(deadline));
    if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED_0) {
        return HRESULT_FROM_WIN32(wait == WAIT_OBJECT_0 + 1 ? ERROR_OPERATION_ABORTED : ERROR_TIMEOUT);
    }
    struct Release {
        HANDLE mutex;
        ~Release() { ReleaseMutex(mutex); }
    } release{mutex.get()};

    // Someone else may have started it while this thread waited for the lock: the pipe is the proof.
    if (WaitNamedPipeW(config_.pipe_path.c_str(), 1) || GetLastError() != ERROR_FILE_NOT_FOUND) {
        return S_OK;
    }

    std::wstring command = L"\"" + config_.shellhost_path + L"\"";
    if (!config_.shellhost_arguments.empty()) {
        command += L" " + config_.shellhost_arguments;
    }
    std::wstring directory = config_.shellhost_path.substr(0, config_.shellhost_path.find_last_of(L'\\'));
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION info{};
    // Breakaway first: the host that loaded us (dllhost, prevhost) may sit in a job that would end ShellHost with it. Without it where the job forbids breakaway.
    std::wstring buffer = command;
    BOOL started = CreateProcessW(nullptr, buffer.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB | CREATE_UNICODE_ENVIRONMENT, nullptr,
                                  directory.c_str(), &startup, &info);
    if (!started) {
        buffer = command;
        started = CreateProcessW(nullptr, buffer.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, nullptr, directory.c_str(), &startup, &info);
    }
    if (!started) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    CloseHandle(info.hThread);
    UniqueHandle process(info.hProcess);
    launched_ = true;

    // Hold the lock until the pipe exists (or ShellHost died, or we ran out of time), so that everyone queued behind us finds it.
    while (remaining_ms(deadline) > 0) {
        if (WaitNamedPipeW(config_.pipe_path.c_str(), 1) || GetLastError() != ERROR_FILE_NOT_FOUND) {
            return S_OK;
        }
        HANDLE waits_for[2] = {process.get(), cancel};
        const DWORD result = WaitForMultipleObjects(cancel != nullptr ? 2 : 1, waits_for, FALSE, 5);
        if (result == WAIT_OBJECT_0) {
            return HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED);  // ShellHost exited before it listened
        }
        if (result == WAIT_OBJECT_0 + 1) {
            return HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED);
        }
    }
    return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
}

HRESULT ShellClient::connect(Deadline deadline, HANDLE cancel) {
    channel_.close();
    launched_ = false;
    session_ = 0;
    bool tried_launch = false;
    for (;;) {
        HRESULT hr = channel_.open(config_.pipe_path.c_str());
        if (SUCCEEDED(hr)) {
            break;
        }
        if (remaining_ms(deadline) == 0) {
            last_status_ = Status::Timeout;
            return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        }
        if (hr == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)) {
            if (!tried_launch) {
                tried_launch = true;
                hr = start_shellhost(deadline, cancel);
                if (FAILED(hr)) {
                    return hr;
                }
            } else {
                Sleep(5);
            }
        } else if (hr == HRESULT_FROM_WIN32(ERROR_PIPE_BUSY)) {
            WaitNamedPipeW(config_.pipe_path.c_str(), std::min<std::uint32_t>(20, remaining_ms(deadline)));
        } else {
            return hr;  // for example ERROR_ACCESS_DENIED: the DACL refused us
        }
        if (cancel != nullptr && WaitForSingleObject(cancel, 0) == WAIT_OBJECT_0) {
            return HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED);
        }
    }
    // Nothing is sent to a process that has not been checked.
    if (!verify_server()) {
        channel_.close();
        return HRESULT_FROM_WIN32(ERROR_NOT_AUTHENTICATED);
    }

    protocol::Hello hello;
    hello.schema_version = protocol::kSchemaVersion;
    hello.process_kind = protocol::ProcessKind::ShellHandler;
    hello.pid = GetCurrentProcessId();
    protocol::ReplyPayload reply;
    const HRESULT hr = exchange(hello, 0, deadline, std::min<std::uint32_t>(remaining_ms(deadline), 1700), reply, cancel);
    if (FAILED(hr)) {
        channel_.close();
        return hr;
    }
    const auto* hello_reply = std::get_if<protocol::HelloReply>(&reply);
    if (hello_reply == nullptr || hello_reply->schema_version != protocol::kSchemaVersion) {
        channel_.close();
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }
    return S_OK;
}

HRESULT ShellClient::exchange(const protocol::RequestPayload& request, std::uint32_t document_id, Deadline deadline, std::uint32_t host_ms, protocol::ReplyPayload& reply,
                              HANDLE cancel) {
    last_status_ = Status::ProtocolError;
    const std::uint64_t id = next_request_id_++;
    const MessageType type = protocol::message_type_of(request);
    auto frame = protocol::encode_request_frame(id, document_id, std::clamp<std::uint32_t>(host_ms, 1, 5000), request);
    if (!frame.ok()) {
        return HRESULT_FROM_WIN32(ERROR_INVALID_PARAMETER);
    }
    HRESULT hr = channel_.write_all(frame.value, deadline, cancel);
    if (FAILED(hr)) {
        last_status_ = hr == HRESULT_FROM_WIN32(ERROR_TIMEOUT) ? Status::Timeout : Status::ProtocolError;
        channel_.close();  // a half-written frame leaves the stream unusable
        return hr;
    }
    std::vector<std::uint8_t> body;
    hr = channel_.read_frame_body(body, deadline, cancel);
    if (FAILED(hr)) {
        last_status_ = hr == HRESULT_FROM_WIN32(ERROR_TIMEOUT) ? Status::Timeout : Status::ProtocolError;
        channel_.close();
        return hr;
    }
    auto decoded = protocol::decode_response_body(body);
    // The answer must be for this request and this message type; anything else means the peer is not speaking the protocol, and the connection ends.
    if (!decoded.ok() || decoded.value.request_id != id || decoded.value.message_type != type) {
        channel_.close();
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }
    last_status_ = decoded.value.status;
    if (decoded.value.status != Status::Ok) {
        return hresult_for_status(decoded.value.status);
    }
    if (!decoded.value.payload) {
        channel_.close();
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }
    reply = std::move(*decoded.value.payload);
    return S_OK;
}

HRESULT ShellClient::open(const Section& source, Deadline deadline, std::uint32_t host_ms, OpenedDocument& out, HANDLE cancel) {
    if (!source.handle || source.bytes == 0 || source.bytes > kMaxStreamBytes) {
        return E_INVALIDARG;
    }
    protocol::ShellOpen request;
    request.section = source.handle.value();
    request.bytes = static_cast<std::uint32_t>(source.bytes);
    protocol::ReplyPayload reply;
    const HRESULT hr = exchange(request, 0, deadline, host_ms, reply, cancel);
    if (FAILED(hr)) {
        return hr;
    }
    const auto* opened = std::get_if<protocol::ShellOpenReply>(&reply);
    if (opened == nullptr || !check_open_reply(*opened)) {
        channel_.close();
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }
    session_ = opened->document_id;
    out = {opened->document_id, opened->page_count, opened->first_width, opened->first_height};
    return S_OK;
}

HRESULT ShellClient::render(std::uint32_t page, std::uint32_t max_width, std::uint32_t max_height, const Section& pixels, Deadline deadline, std::uint32_t host_ms,
                            RenderedPage& out, HANDLE cancel) {
    max_width = clamp_edge(max_width);
    max_height = clamp_edge(max_height);
    if (session_ == 0 || !pixels.handle || pixels.bytes < pixel_section_bytes(max_width, max_height) || pixels.bytes > kMaxPixelBytes) {
        return E_INVALIDARG;
    }
    protocol::ShellRenderPage request;
    request.page = page;
    request.max_width = max_width;
    request.max_height = max_height;
    request.flags = 1;  // annotations
    request.pixels = pixels.handle.value();
    request.pixel_bytes = static_cast<std::uint32_t>(pixels.bytes);
    protocol::ReplyPayload reply;
    const HRESULT hr = exchange(request, session_, deadline, host_ms, reply, cancel);
    if (FAILED(hr)) {
        return hr;
    }
    const auto* rendered = std::get_if<protocol::ShellRenderPageReply>(&reply);
    if (rendered == nullptr) {
        channel_.close();
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }
    const CheckedRender checked = check_render_reply(*rendered, max_width, max_height, pixels.bytes);
    if (!checked.ok) {
        channel_.close();  // a peer that sent a wrong reply ends the session
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }
    out = {rendered->width, rendered->height, checked.bytes};
    return S_OK;
}

}  // namespace mikepdf::shellext
