#include "engine_host.h"

#include <Windows.h>

#include <array>
#include <chrono>
#include <cstring>
#include <utility>
#include <variant>
#include <vector>

#include "log_pipe.h"
#include "mikepdf/pdfium/library.h"
#include "runtime_policy.h"

namespace mikepdf::engine {

using protocol::MessageType;
using protocol::Status;

namespace {
constexpr std::size_t kMaxQueuedRequests = 256;
constexpr auto kReadyTimeout = std::chrono::seconds(30);
constexpr auto kStopGrace = std::chrono::seconds(2);
}  // namespace

void log_line(const char* text) noexcept { write_log_line(text); }

void die(int exit_code, const char* reason) noexcept {
    log_line(reason);
    TerminateProcess(GetCurrentProcess(), static_cast<UINT>(exit_code));
    ExitProcess(static_cast<UINT>(exit_code));
}

EngineHost::EngineHost(EngineKind kind, SlotMemory slots, std::wstring pipe_name, std::uint32_t parent_pid)
    : core_(kind, slots), pipe_name_(std::move(pipe_name)), parent_pid_(parent_pid) {}

void EngineHost::engine_thread_main() {
    try {
        pdfium::Library library;
        // Everything the process will ever load is loaded now; lock it before any document byte can arrive.
        const unsigned long policy_error = apply_runtime_signature_policy();
        {
            std::lock_guard lock(mutex_);
            readiness_ = policy_error == 0 ? Readiness::Ready : Readiness::Failed;
        }
        cv_.notify_all();

        while (policy_error == 0) {
            Work work;
            {
                std::unique_lock lock(mutex_);
                cv_.wait(lock, [this] { return stopping_ || !queue_.empty(); });
                if (stopping_) {
                    break;
                }
                work = std::move(queue_.front());
                queue_.pop_front();
            }
            const auto deadline = work.request.deadline_ms == 0
                                      ? Clock::time_point::max()
                                      : work.received + std::chrono::milliseconds(work.request.deadline_ms);
            const Reply reply = core_.execute(work.request, deadline);
            send_reply(work.request.request_id, work.request.message_type, reply);
        }
        core_.close_all();  // PDFium documents must not outlive the library
    } catch (...) {
        die(kExitInternalError, "unhandled exception on the engine thread");
    }
    {
        std::lock_guard lock(mutex_);
        engine_thread_done_ = true;
    }
    cv_.notify_all();
}

void EngineHost::send_reply(std::uint64_t request_id, MessageType type, const Reply& reply) {
    auto frame = protocol::encode_response_frame(request_id, reply.status, type, reply.payload ? &*reply.payload : nullptr);
    if (!frame.ok()) {
        // Our own reply broke a schema limit. Tell the broker something went wrong rather than going silent.
        frame = protocol::encode_response_frame(request_id, Status::Unsupported, type, nullptr);
        if (!frame.ok()) {
            die(kExitInternalError, "cannot encode a reply");
        }
    }
    if (!pipe_.write_all(frame.value)) {
        die(kExitBrokerGone, "pipe write failed");
    }
}

bool EngineHost::wait_until_ready() {
    std::unique_lock lock(mutex_);
    if (!cv_.wait_for(lock, kReadyTimeout, [this] { return readiness_ != Readiness::Starting; })) {
        return false;
    }
    return readiness_ == Readiness::Ready;
}

void EngineHost::handle_frame(protocol::RequestEnvelope request, Clock::time_point received, bool& shutting_down) {
    switch (request.message_type) {
        case MessageType::Hello: {
            // Handled by run(), which owns the sequencing.
            die(kExitInternalError, "unexpected Hello");
        }
        case MessageType::Ping: {
            protocol::Pong pong;
            pong.nonce = std::get<protocol::Ping>(request.payload).nonce;
            pong.progress = core_.progress();  // read on this lane, written by the engine thread: how the broker tells a busy engine from a stuck one
            send_reply(request.request_id, request.message_type, {Status::Ok, protocol::ReplyPayload{pong}});
            return;
        }
        case MessageType::CancelBefore: {
            const auto& cancel = std::get<protocol::CancelBefore>(request.payload);
            send_reply(request.request_id, request.message_type, core_.cancel_before(request.document_id, cancel.generation));
            return;
        }
        case MessageType::AttachSlotRing: {
            send_reply(request.request_id, request.message_type, core_.attach_slot_ring(std::get<protocol::AttachSlotRing>(request.payload)));
            return;
        }
        case MessageType::Shutdown: {
            send_reply(request.request_id, request.message_type, {Status::Ok, protocol::ReplyPayload{protocol::ShutdownReply{}}});
            shutting_down = true;
            return;
        }
        case MessageType::OpenDocument:
        case MessageType::CloseDocument:
        case MessageType::GetDocumentInfo:
        case MessageType::GetPageSizes:
        case MessageType::GetOutline:
        case MessageType::GetPageLinks:
        case MessageType::GetLinkUri:
        case MessageType::GetPageBoxes:
        case MessageType::GetPageText:
        case MessageType::SearchPage:
        case MessageType::GetDocumentDetails:
        case MessageType::GetViewerPreferences:
        case MessageType::GetTextQuads:
        case MessageType::ListAnnotations:
        case MessageType::GetAnnotation:
        case MessageType::GetInkStroke:
        case MessageType::SetAnnotationHidden:
        case MessageType::CountAnnotations:
        case MessageType::GetFormFields:
        case MessageType::GetFormFieldDetail:
        case MessageType::SetFormFieldValue:
        case MessageType::FormAction:
        case MessageType::SaveDocument:
        case MessageType::RenderBand:
        case MessageType::RenderTile: {
            {
                std::lock_guard lock(mutex_);
                if (queue_.size() >= kMaxQueuedRequests) {
                    die(kExitProtocolViolation, "request queue overflow");
                }
                queue_.push_back(Work{std::move(request), received});
            }
            cv_.notify_one();
            return;
        }
        case MessageType::ValidateStructure:  // the WriterHost's
        case MessageType::AppendAnnotations:
        case MessageType::NormalizeTail:
        case MessageType::RewriteDocument:
        case MessageType::VerifyAnnotations:
        case MessageType::ShellOpen:          // ShellHost's
        case MessageType::ShellRenderPage:
        case MessageType::ShellClose:
            break;
    }
    die(kExitProtocolViolation, "message not served by this host");
}

int EngineHost::run() {
    engine_thread_ = std::thread([this] { engine_thread_main(); });

    if (const unsigned long error = pipe_.connect(pipe_name_, parent_pid_); error != 0) {
        die(kExitPipeFailed, "cannot connect to the broker pipe");
    }

    bool hello_done = false;
    bool shutting_down = false;
    int exit_code = kExitOk;
    std::vector<std::uint8_t> body;
    while (!shutting_down) {
        std::array<std::uint8_t, protocol::kFrameHeaderBytes> header{};
        if (!pipe_.read_exact(header)) {
            exit_code = kExitBrokerGone;
            break;
        }
        const Clock::time_point received = Clock::now();
        std::size_t length = 0;
        if (protocol::read_frame_length(header, length) != Status::Ok) {
            die(kExitProtocolViolation, "bad frame length");
        }
        body.resize(length);
        if (!pipe_.read_exact(body)) {
            exit_code = kExitBrokerGone;
            break;
        }
        auto decoded = protocol::decode_request_body(body);
        if (!decoded.ok()) {
            die(kExitProtocolViolation, "malformed request");
        }
        protocol::RequestEnvelope request = std::move(decoded.value);

        if (request.message_type == MessageType::Hello) {
            const auto& hello = std::get<protocol::Hello>(request.payload);
            if (hello_done || hello.process_kind != protocol::ProcessKind::Broker) {
                die(kExitProtocolViolation, "unexpected Hello");
            }
            if (!wait_until_ready()) {
                die(kExitPolicyFailed, "engine did not become ready");
            }
            if (hello.schema_version != protocol::kSchemaVersion) {
                send_reply(request.request_id, request.message_type, {Status::Unsupported, std::nullopt});
                die(kExitProtocolViolation, "schema version mismatch");
            }
            protocol::HelloReply reply;
            reply.schema_version = protocol::kSchemaVersion;
            reply.pdfium_version = std::string(pdfium::library_version());
            send_reply(request.request_id, request.message_type, {Status::Ok, protocol::ReplyPayload{reply}});
            hello_done = true;
            continue;
        }
        if (!hello_done) {
            die(kExitProtocolViolation, "request before Hello");
        }
        handle_frame(std::move(request), received, shutting_down);
    }

    {
        std::lock_guard lock(mutex_);
        stopping_ = true;
    }
    cv_.notify_all();
    {
        std::unique_lock lock(mutex_);
        if (!cv_.wait_for(lock, kStopGrace, [this] { return engine_thread_done_; })) {
            die(exit_code, "engine thread did not stop");
        }
    }
    engine_thread_.join();
    return exit_code;
}

}  // namespace mikepdf::engine
