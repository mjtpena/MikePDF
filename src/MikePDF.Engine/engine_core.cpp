#include "engine_core.h"

#include "slot_section.h"

#include <Windows.h>

#include <algorithm>
#include <string>
#include <utility>
#include <variant>

namespace mikepdf::engine {

using protocol::Status;

namespace {

Reply status_only(Status status) { return {status, std::nullopt}; }

template <class Payload>
Reply ok_with(Payload&& payload) {
    return {Status::Ok, protocol::ReplyPayload{std::forward<Payload>(payload)}};
}

std::size_t max_documents(EngineKind kind) {
    return kind == EngineKind::Document ? kMaxDocumentsDocumentKind : kMaxDocumentsPoolKind;
}

}  // namespace

SlotMemory EngineCore::slots() const {
    std::lock_guard lock(mutex_);
    return slots_;
}

Reply EngineCore::attach_slot_ring(const protocol::AttachSlotRing& request) {
    {
        std::lock_guard lock(mutex_);
        if (slots_.base != nullptr) {
            MappedFile::discard_handle(request.handle);
            return status_only(Status::InvalidArgument);
        }
    }
    const auto mapped = map_slot_section(request.handle, request.slot_count, request.slot_size);
    if (!mapped.ok()) {
        return status_only(mapped.status);
    }
    {
        std::lock_guard lock(mutex_);
        slots_ = mapped.value;
    }
    return ok_with(protocol::AttachSlotRingReply{});
}

std::size_t EngineCore::open_document_count() const {
    std::lock_guard lock(mutex_);
    return documents_.size();
}

EngineCore::Entry* EngineCore::find(std::uint32_t id) {
    std::lock_guard lock(mutex_);
    const auto it = documents_.find(id);
    return it == documents_.end() ? nullptr : it->second.get();
}

Reply EngineCore::cancel_before(std::uint32_t document_id, std::uint64_t generation) {
    std::lock_guard lock(mutex_);
    const auto it = documents_.find(document_id);
    if (it == documents_.end()) {
        return status_only(Status::InvalidArgument);
    }
    std::atomic<std::uint64_t>& floor = it->second->cancel_before;
    std::uint64_t current = floor.load(std::memory_order_relaxed);
    while (current < generation &&
           !floor.compare_exchange_weak(current, generation, std::memory_order_release, std::memory_order_relaxed)) {
    }
    return ok_with(protocol::CancelBeforeReply{});
}

Reply EngineCore::execute(protocol::RequestEnvelope& request, Clock::time_point deadline) {
    switch (request.message_type) {
        case protocol::MessageType::OpenDocument:
            return open_document(std::get<protocol::OpenDocument>(request.payload), deadline);
        case protocol::MessageType::CloseDocument:
            return close_document(request.document_id);
        case protocol::MessageType::GetDocumentInfo:
        case protocol::MessageType::GetPageSizes:
        case protocol::MessageType::GetOutline:
        case protocol::MessageType::GetPageLinks:
        case protocol::MessageType::GetLinkUri:
        case protocol::MessageType::GetPageBoxes:
        case protocol::MessageType::GetPageText:
        case protocol::MessageType::SearchPage:
        case protocol::MessageType::GetDocumentDetails:
        case protocol::MessageType::RenderTile: {
            if (Clock::now() >= deadline) {
                return status_only(Status::Timeout);
            }
            Entry* entry = find(request.document_id);
            if (entry == nullptr) {
                return status_only(Status::InvalidArgument);
            }
            if (request.message_type == protocol::MessageType::GetDocumentInfo) {
                return get_info(*entry);
            }
            if (request.message_type == protocol::MessageType::GetDocumentDetails) {
                return get_details(*entry, deadline);
            }
            if (request.message_type == protocol::MessageType::GetPageSizes) {
                return get_page_sizes(*entry, std::get<protocol::GetPageSizes>(request.payload));
            }
            if (request.message_type == protocol::MessageType::RenderTile) {
                return render_tile(*entry, std::get<protocol::RenderTile>(request.payload), deadline);
            }
            return read_page_data(*entry, request, deadline);
        }
        default:
            return status_only(Status::InvalidArgument);
    }
}

Reply EngineCore::open_document(protocol::OpenDocument& request, Clock::time_point deadline) {
    // The handle is the broker's gift to us: it is consumed (closed) on every path.
    const auto refuse = [&](Status status) {
        MappedFile::discard_handle(request.handle);
        return status_only(status);
    };
    if (Clock::now() >= deadline) {
        return refuse(Status::Timeout);
    }
    if (open_document_count() >= max_documents(kind_) || next_id_ == 0) {
        return refuse(Status::Unsupported);
    }

    auto mapped = MappedFile::from_handle(request.handle);
    if (!mapped.ok()) {
        return status_only(mapped.status);
    }
    auto entry = std::make_unique<Entry>();
    entry->file = std::move(mapped.value);

    std::string password = request.password.value_or(std::string{});
    if (request.password) {
        SecureZeroMemory(request.password->data(), request.password->size());
        request.password.reset();
    }
    auto opened = pdfium::Document::open(entry->file.bytes(), password);
    SecureZeroMemory(password.data(), password.size());
    if (!opened.ok()) {
        return status_only(opened.status);
    }
    entry->document = std::move(opened.value);

    protocol::OpenDocumentReply reply;
    reply.page_count = entry->document.page_count();
    reply.document_id = next_id_++;
    {
        std::lock_guard lock(mutex_);
        documents_.emplace(reply.document_id, std::move(entry));
    }
    return ok_with(reply);
}

void EngineCore::close_all() {
    std::unordered_map<std::uint32_t, std::unique_ptr<Entry>> doomed;
    {
        std::lock_guard lock(mutex_);
        doomed.swap(documents_);
    }
}

Reply EngineCore::close_document(std::uint32_t id) {
    std::unique_ptr<Entry> removed;
    {
        std::lock_guard lock(mutex_);
        const auto it = documents_.find(id);
        if (it == documents_.end()) {
            return status_only(Status::InvalidArgument);
        }
        removed = std::move(it->second);
        documents_.erase(it);
    }
    removed.reset();  // closes the PDFium document, then unmaps the file, here on the engine thread
    return ok_with(protocol::CloseDocumentReply{});
}

Reply EngineCore::get_info(Entry& entry) {
    auto info = entry.document.info();
    if (!info.ok()) {
        return status_only(info.status);
    }
    return ok_with(std::move(info.value));
}

// The fonts are found by walking the first pages, which can be slow for a heavy file: stop at the request's deadline or after 1.5 s, whichever is first.
Reply EngineCore::get_details(Entry& entry, Clock::time_point deadline) {
    constexpr std::uint32_t kFontPages = 100;
    const Clock::time_point stop = std::min(deadline, Clock::now() + std::chrono::milliseconds(1500));
    auto details = entry.document.details(kFontPages, [stop] { return Clock::now() >= stop; });
    if (!details.ok()) {
        return status_only(details.status);
    }
    return ok_with(std::move(details.value));
}

Reply EngineCore::get_page_sizes(Entry& entry, const protocol::GetPageSizes& request) {
    auto sizes = entry.document.page_sizes(request.first, request.count);
    if (!sizes.ok()) {
        return status_only(sizes.status);
    }
    return ok_with(std::move(sizes.value));
}

// The reading services: every one answers with a typed status and, on success, a reply that obeys the schema limits (the encoder checks).
Reply EngineCore::read_page_data(Entry& entry, protocol::RequestEnvelope& request, Clock::time_point deadline) {
    switch (request.message_type) {
        case protocol::MessageType::GetOutline: {
            const auto& r = std::get<protocol::GetOutline>(request.payload);
            auto result = entry.document.outline(r.path, r.first, r.count);
            return result.ok() ? ok_with(std::move(result.value)) : status_only(result.status);
        }
        case protocol::MessageType::GetPageLinks: {
            const auto& r = std::get<protocol::GetPageLinks>(request.payload);
            auto result = entry.document.page_links(r.page, r.first, r.count);
            return result.ok() ? ok_with(std::move(result.value)) : status_only(result.status);
        }
        case protocol::MessageType::GetLinkUri: {
            const auto& r = std::get<protocol::GetLinkUri>(request.payload);
            auto result = entry.document.link_uri(r.page, r.index);
            return result.ok() ? ok_with(std::move(result.value)) : status_only(result.status);
        }
        case protocol::MessageType::GetPageBoxes: {
            auto result = entry.document.page_boxes(std::get<protocol::GetPageBoxes>(request.payload).page);
            return result.ok() ? ok_with(std::move(result.value)) : status_only(result.status);
        }
        case protocol::MessageType::GetPageText: {
            const auto& r = std::get<protocol::GetPageText>(request.payload);
            auto result = entry.document.page_text(r.page, r.first, r.count);
            return result.ok() ? ok_with(std::move(result.value)) : status_only(result.status);
        }
        case protocol::MessageType::SearchPage: {
            const auto& r = std::get<protocol::SearchPage>(request.payload);
            auto result = entry.document.search_page(r.page, r.query, r.flags, [deadline] { return Clock::now() >= deadline; });
            return result.ok() ? ok_with(std::move(result.value)) : status_only(result.status);
        }
        default:
            return status_only(Status::InvalidArgument);
    }
}

Reply EngineCore::render_tile(Entry& entry, const protocol::RenderTile& request, Clock::time_point deadline) {
    const auto interrupt = [&]() noexcept {
        if (request.generation < entry.cancel_before.load(std::memory_order_acquire)) {
            return pdfium::RenderInterrupt::Cancel;
        }
        return Clock::now() >= deadline ? pdfium::RenderInterrupt::Timeout : pdfium::RenderInterrupt::None;
    };
    if (interrupt() == pdfium::RenderInterrupt::Cancel) {
        return status_only(Status::Cancelled);
    }

    // The slot is shared memory the broker reads: check the index and that the whole tile fits before PDFium is given a pointer.
    const std::span<std::uint8_t> slot = slots().slot(request.slot_index);
    const std::uint64_t stride = std::uint64_t{request.tile_width} * 4;
    if (slot.empty() || stride * request.tile_height > slot.size()) {
        return status_only(Status::InvalidArgument);
    }

    pdfium::TileRequest tile;
    tile.page = request.page;
    tile.scale = request.scale;
    tile.rotation = request.rotation;
    tile.tile_x = request.tile_x;
    tile.tile_y = request.tile_y;
    tile.tile_width = request.tile_width;
    tile.tile_height = request.tile_height;
    tile.flags = request.flags;
    const Status status = entry.document.render_tile(tile, slot, static_cast<std::uint32_t>(stride), interrupt);
    if (status != Status::Ok && status != Status::PartiallyRendered) {
        return status_only(status);
    }
    protocol::RenderTileReply reply;
    reply.slot_index = request.slot_index;
    reply.width = request.tile_width;
    reply.height = request.tile_height;
    reply.stride = static_cast<std::uint32_t>(stride);
    reply.generation = request.generation;
    return {status, protocol::ReplyPayload{reply}};
}

}  // namespace mikepdf::engine
