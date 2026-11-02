#include "fake_shellhost.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace testing_support {

namespace {
std::atomic<int> g_counter{0};
}

FakeShellHost::FakeShellHost(Script script) : script_(std::move(script)) {
    const std::wstring name = L"mikepdf-shellext-test-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(++g_counter);
    pipe_path_ = L"\\\\.\\pipe\\" + name;
    mutex_name_ = L"Local\\" + name;
    HANDLE handle = CreateNamedPipeW(pipe_path_.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 4,
                                     1 << 16, 1 << 16, 0, nullptr);
    pipe_.reset(handle == INVALID_HANDLE_VALUE ? nullptr : handle);
    stop_.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    thread_ = std::thread([this] { serve(); });
}

FakeShellHost::~FakeShellHost() {
    SetEvent(stop_.get());
    if (thread_.joinable()) {
        thread_.join();
    }
}

shellext::ClientConfig FakeShellHost::config() const {
    shellext::ClientConfig config;
    config.pipe_path = pipe_path_;
    wchar_t self[MAX_PATH]{};
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    config.shellhost_path = self;  // the server is this process
    config.launch_mutex_name = mutex_name_;
    config.require_package_family = false;
    return config;
}

std::vector<std::uint8_t> FakeShellHost::frame(std::uint64_t id, protocol::Status status, protocol::MessageType type, const protocol::ReplyPayload* payload) {
    auto encoded = protocol::encode_response_frame(id, status, type, payload);
    return encoded.ok() ? encoded.value : std::vector<std::uint8_t>{};
}

FakeShellHost::Reply FakeShellHost::default_reply(const protocol::RequestEnvelope& request) {
    using protocol::MessageType;
    using protocol::Status;
    switch (request.message_type) {
        case MessageType::Hello: {
            protocol::ReplyPayload reply = protocol::HelloReply{protocol::kSchemaVersion, ""};
            return frame(request.request_id, Status::Ok, request.message_type, &reply);
        }
        case MessageType::ShellOpen: {
            const auto& open = std::get<protocol::ShellOpen>(request.payload);
            opened_bytes = open.bytes;
            // In this process the client's handle value is a handle of ours too.
            void* view = MapViewOfFile(reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(open.section)), FILE_MAP_READ, 0, 0, open.bytes);
            if (view != nullptr) {
                opened_first_byte = *static_cast<const std::uint8_t*>(view);
                UnmapViewOfFile(view);
            }
            protocol::ShellOpenReply open_reply;
            open_reply.document_id = 1;
            open_reply.page_count = kPageCount;
            open_reply.first_width = kPageWidth;
            open_reply.first_height = kPageHeight;
            protocol::ReplyPayload reply = open_reply;
            return frame(request.request_id, Status::Ok, request.message_type, &reply);
        }
        case MessageType::ShellRenderPage: {
            const auto& render = std::get<protocol::ShellRenderPage>(request.payload);
            const float scale = std::min(static_cast<float>(render.max_width) / kPageWidth, static_cast<float>(render.max_height) / kPageHeight);
            const std::uint32_t width = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(std::lround(kPageWidth * scale)));
            const std::uint32_t height = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(std::lround(kPageHeight * scale)));
            void* view = MapViewOfFile(reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(render.pixels)), FILE_MAP_WRITE, 0, 0, std::uint64_t{width} * height * 4);
            if (view != nullptr) {
                auto* pixels = static_cast<std::uint32_t*>(view);
                std::fill(pixels, pixels + std::size_t{width} * height, kPagePixel);
                UnmapViewOfFile(view);
            }
            protocol::ReplyPayload reply = protocol::ShellRenderPageReply{width, height, width * 4};
            return frame(request.request_id, Status::Ok, request.message_type, &reply);
        }
        case MessageType::ShellClose: {
            protocol::ReplyPayload reply = protocol::ShellCloseReply{};
            return frame(request.request_id, Status::Ok, request.message_type, &reply);
        }
        default:
            return frame(request.request_id, Status::Unsupported, request.message_type, nullptr);
    }
}

bool FakeShellHost::read_exact(std::uint8_t* data, std::size_t size) {
    shellext::UniqueHandle done(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    std::size_t got = 0;
    while (got < size) {
        OVERLAPPED overlapped{};
        overlapped.hEvent = done.get();
        ResetEvent(done.get());
        if (!ReadFile(pipe_.get(), data + got, static_cast<DWORD>(size - got), nullptr, &overlapped) && GetLastError() != ERROR_IO_PENDING) {
            return false;
        }
        HANDLE waits[2] = {done.get(), stop_.get()};
        if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) != WAIT_OBJECT_0) {
            CancelIoEx(pipe_.get(), &overlapped);
            DWORD ignored = 0;
            GetOverlappedResult(pipe_.get(), &overlapped, &ignored, TRUE);
            return false;
        }
        DWORD n = 0;
        if (!GetOverlappedResult(pipe_.get(), &overlapped, &n, FALSE) || n == 0) {
            return false;
        }
        got += n;
    }
    return true;
}

bool FakeShellHost::write_all(const std::vector<std::uint8_t>& bytes) {
    shellext::UniqueHandle done(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    OVERLAPPED overlapped{};
    overlapped.hEvent = done.get();
    if (!WriteFile(pipe_.get(), bytes.data(), static_cast<DWORD>(bytes.size()), nullptr, &overlapped) && GetLastError() != ERROR_IO_PENDING) {
        return false;
    }
    HANDLE waits[2] = {done.get(), stop_.get()};
    if (WaitForMultipleObjects(2, waits, FALSE, 10000) != WAIT_OBJECT_0) {
        CancelIoEx(pipe_.get(), &overlapped);
        DWORD ignored = 0;
        GetOverlappedResult(pipe_.get(), &overlapped, &ignored, TRUE);
        return false;
    }
    DWORD n = 0;
    return GetOverlappedResult(pipe_.get(), &overlapped, &n, FALSE) && n == bytes.size();
}

void FakeShellHost::serve() {
    if (!pipe_) {
        return;
    }
    // One client at a time, as many in a row as the test makes.
    while (WaitForSingleObject(stop_.get(), 0) != WAIT_OBJECT_0) {
        shellext::UniqueHandle connected(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        OVERLAPPED overlapped{};
        overlapped.hEvent = connected.get();
        const BOOL ok = ConnectNamedPipe(pipe_.get(), &overlapped);
        const DWORD error = ok ? ERROR_SUCCESS : GetLastError();
        if (error == ERROR_IO_PENDING) {
            HANDLE waits[2] = {connected.get(), stop_.get()};
            if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) != WAIT_OBJECT_0) {
                CancelIoEx(pipe_.get(), &overlapped);
                return;
            }
        } else if (error != ERROR_SUCCESS && error != ERROR_PIPE_CONNECTED) {
            return;
        }
        ++connections;
        for (;;) {
            std::uint8_t header[protocol::kFrameHeaderBytes];
            if (!read_exact(header, sizeof(header))) {
                break;
            }
            std::size_t length = 0;
            if (protocol::read_frame_length(header, length) != protocol::Status::Ok) {
                break;
            }
            std::vector<std::uint8_t> body(length);
            if (!read_exact(body.data(), body.size())) {
                break;
            }
            auto decoded = protocol::decode_request_body(body);
            if (!decoded.ok()) {
                break;
            }
            const protocol::RequestEnvelope& request = decoded.value;
            last_deadline_ms = request.deadline_ms;
            if (request.message_type == protocol::MessageType::Hello) {
                const auto& hello = std::get<protocol::Hello>(request.payload);
                ++hellos;
                hello_pid = hello.pid;
                hello_schema = hello.schema_version;
                hello_kind = hello.process_kind;
            } else if (request.message_type == protocol::MessageType::ShellOpen) {
                ++opens;
            } else if (request.message_type == protocol::MessageType::ShellRenderPage) {
                ++renders;
                const auto& render = std::get<protocol::ShellRenderPage>(request.payload);
                last_max_width = render.max_width;
                last_max_height = render.max_height;
                last_flags = render.flags;
            }
            Reply reply = script_ ? script_(*this, request) : default_reply(request);
            if (!reply) {
                continue;  // stay silent
            }
            if (reply->empty() || !write_all(*reply)) {
                break;
            }
        }
        DisconnectNamedPipe(pipe_.get());
    }
}

}  // namespace testing_support
