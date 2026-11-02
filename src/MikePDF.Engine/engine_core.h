// The engine's request handling without any pipe or thread of its own, so it can be unit tested.
//
// Threading (ADR-0005 rule 2):
//   - execute() calls PDFium and must only ever run on the one engine thread.
//   - cancel_before() is the only call the I/O lane makes into the core. It touches an atomic under a short lock and
//     never PDFium.
#pragma once

#include <atomic>
#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <optional>
#include <unordered_map>

#include "mapped_file.h"
#include "mikepdf/pdfium/document.h"
#include "mikepdf/protocol/codec.h"
#include "slot_memory.h"

namespace mikepdf::engine {

using Clock = std::chrono::steady_clock;

enum class EngineKind : std::uint8_t {
    Document,  // one open document at a time
    Pool,      // thumbnails and search: several documents
    Shell,     // ShellHost's engine for one Explorer thumbnail or preview (ADR-0018): one document, and only the messages in is_served_by_shell_kind()
};

inline constexpr std::size_t kMaxDocumentsDocumentKind = 1;
inline constexpr std::size_t kMaxDocumentsPoolKind = 32;
inline constexpr std::size_t kMaxDocumentsShellKind = 1;

/// The allow-list of the Shell kind (docs/features/shell-extensions.md section 11): the session and handshake messages, opening and closing the one document,
/// its size and page sizes, one tile at a time, and cancelling. Outlines, links, boxes, text, search, details, bands and everything else are not served: a
/// shell host never asks for them, so an engine that answers them would be attack surface for nothing.
[[nodiscard]] constexpr bool is_served_by_shell_kind(protocol::MessageType type) noexcept {
    switch (type) {
        case protocol::MessageType::Hello:
        case protocol::MessageType::AttachSlotRing:
        case protocol::MessageType::Ping:
        case protocol::MessageType::Shutdown:
        case protocol::MessageType::OpenDocument:
        case protocol::MessageType::CloseDocument:
        case protocol::MessageType::GetDocumentInfo:
        case protocol::MessageType::GetPageSizes:
        case protocol::MessageType::RenderTile:
        case protocol::MessageType::CancelBefore:
            return true;
        default:
            return false;
    }
}

struct Reply {
    protocol::Status status{protocol::Status::Ok};
    /// Present exactly when carries_payload(status).
    std::optional<protocol::ReplyPayload> payload{};
};

class EngineCore {
public:
    /// `slots` may be empty; the ring is then attached later with attach_slot_ring() (RenderTile is refused until it is).
    explicit EngineCore(EngineKind kind, SlotMemory slots = {}) : kind_(kind), slots_(slots) {}

    /// AttachSlotRing: maps the section the broker duplicated into this process. Safe from the I/O lane (no PDFium). Attaching twice is
    /// an InvalidArgument. The handle is consumed either way.
    [[nodiscard]] Reply attach_slot_ring(const protocol::AttachSlotRing& request);

    /// OpenDocument, CloseDocument, GetDocumentInfo, GetPageSizes, RenderTile, RenderBand and the reading services (GetOutline, GetPageLinks,
    /// GetLinkUri, GetPageBoxes, GetPageText, SearchPage), GetDocumentDetails and GetViewerPreferences. Anything else is InvalidArgument.
    /// `deadline` is when the request's budget runs out (time_point::max() for none). The OpenDocument password is wiped
    /// from `request` once it has been used.
    [[nodiscard]] Reply execute(protocol::RequestEnvelope& request, Clock::time_point deadline);

    /// Raises the generation below which this document's renders are cancelled. Safe from any thread.
    [[nodiscard]] Reply cancel_before(std::uint32_t document_id, std::uint64_t generation);

    [[nodiscard]] std::size_t open_document_count() const;

    /// How much work the engine thread has done: it goes up when a request starts, when PDFium or a long loop polls for an interrupt, and when a request ends.
    /// The I/O lane puts it in every Pong, so the broker can tell an engine that is busy with a heavy page from one whose work thread is stuck: a hang is a
    /// counter that stands still while a request is overdue, not a request that took longer than its deadline (PDFium only looks at the deadline about every 100
    /// drawing operations). Safe from any thread.
    [[nodiscard]] std::uint64_t progress() const noexcept { return progress_.load(std::memory_order_relaxed); }

    /// Closes every document (engine thread only). Must run before the PDFium library is destroyed.
    void close_all();

private:
    struct Entry {
        MappedFile file;  // declared first: destroyed after the document that reads it
        pdfium::Document document;
        std::atomic<std::uint64_t> cancel_before{0};
    };

    [[nodiscard]] Reply open_document(protocol::OpenDocument& request, Clock::time_point deadline);
    [[nodiscard]] Reply close_document(std::uint32_t id);
    [[nodiscard]] Reply get_info(Entry& entry);
    [[nodiscard]] Reply get_details(Entry& entry, Clock::time_point deadline);
    [[nodiscard]] Reply get_page_sizes(Entry& entry, const protocol::GetPageSizes& request);
    [[nodiscard]] Reply read_page_data(Entry& entry, protocol::RequestEnvelope& request, Clock::time_point deadline);
    [[nodiscard]] Reply render_tile(Entry& entry, const protocol::RenderTile& request, Clock::time_point deadline);
    [[nodiscard]] Reply render_band(Entry& entry, const protocol::RenderBand& request, Clock::time_point deadline);
    [[nodiscard]] Entry* find(std::uint32_t id);
    void tick() noexcept { progress_.fetch_add(1, std::memory_order_relaxed); }

    EngineKind kind_;
    [[nodiscard]] SlotMemory slots() const;

    SlotMemory slots_;  // guarded by mutex_
    std::uint32_t next_id_{1};  // engine thread only
    std::atomic<std::uint64_t> progress_{0};
    mutable std::mutex mutex_;
    std::unordered_map<std::uint32_t, std::unique_ptr<Entry>> documents_;
};

}  // namespace mikepdf::engine
