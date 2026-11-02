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
#include "quire/pdfium/document.h"
#include "quire/protocol/codec.h"
#include "slot_memory.h"

namespace quire::engine {

using Clock = std::chrono::steady_clock;

enum class EngineKind : std::uint8_t {
    Document,  // one open document at a time
    Pool,      // thumbnails and search: several documents
};

inline constexpr std::size_t kMaxDocumentsDocumentKind = 1;
inline constexpr std::size_t kMaxDocumentsPoolKind = 32;

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

    /// OpenDocument, CloseDocument, GetDocumentInfo, GetPageSizes, RenderTile and the reading services (GetOutline, GetPageLinks,
    /// GetLinkUri, GetPageBoxes, GetPageText, SearchPage). Anything else is InvalidArgument.
    /// `deadline` is when the request's budget runs out (time_point::max() for none). The OpenDocument password is wiped
    /// from `request` once it has been used.
    [[nodiscard]] Reply execute(protocol::RequestEnvelope& request, Clock::time_point deadline);

    /// Raises the generation below which this document's renders are cancelled. Safe from any thread.
    [[nodiscard]] Reply cancel_before(std::uint32_t document_id, std::uint64_t generation);

    [[nodiscard]] std::size_t open_document_count() const;

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
    [[nodiscard]] Reply get_page_sizes(Entry& entry, const protocol::GetPageSizes& request);
    [[nodiscard]] Reply read_page_data(Entry& entry, protocol::RequestEnvelope& request, Clock::time_point deadline);
    [[nodiscard]] Reply render_tile(Entry& entry, const protocol::RenderTile& request, Clock::time_point deadline);
    [[nodiscard]] Entry* find(std::uint32_t id);

    EngineKind kind_;
    [[nodiscard]] SlotMemory slots() const;

    SlotMemory slots_;  // guarded by mutex_
    std::uint32_t next_id_{1};  // engine thread only
    mutable std::mutex mutex_;
    std::unordered_map<std::uint32_t, std::unique_ptr<Entry>> documents_;
};

}  // namespace quire::engine
