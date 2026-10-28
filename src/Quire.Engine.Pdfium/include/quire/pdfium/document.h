// RAII wrapper over one PDFium document. Everything here must run on the engine thread (§5.2 rule 1).
// The bytes of the PDF are owned by the caller (a read-only mapped view, ADR-0005) and must outlive the Document.
// Results use the protocol's status codes (§6.4); nothing throws.
#pragma once

#include <cstdint>
#include <functional>
#include <span>
#include <string_view>

#include "quire/protocol/enums.h"
#include "quire/protocol/messages.h"
#include "quire/protocol/msgpack.h"

struct fpdf_document_t__;

namespace quire::pdfium {

enum class RenderInterrupt : std::uint8_t { None, Cancel, Timeout };

/// Polled from PDFium's pause callback. It runs inside PDFium, so it must be cheap and must not throw.
using InterruptCheck = std::function<RenderInterrupt()>;

inline constexpr std::uint32_t kRenderAnnotations = 1;
inline constexpr std::uint32_t kRenderFormFields = 2;

struct TileRequest {
    std::uint32_t page{};
    float scale{1.0f};
    /// Quarter turns clockwise, on top of the page's own /Rotate.
    std::uint16_t rotation{};
    /// Origin of the tile in device pixels of the whole scaled, rotated page.
    std::int32_t tile_x{};
    std::int32_t tile_y{};
    std::uint32_t tile_width{};
    std::uint32_t tile_height{};
    std::uint32_t flags{};
};

class Document {
public:
    Document() noexcept = default;
    ~Document();
    Document(Document&& other) noexcept;
    Document& operator=(Document&& other) noexcept;
    Document(const Document&) = delete;
    Document& operator=(const Document&) = delete;

    /// FPDF_LoadMemDocument64: PDFium reads `bytes` in place, it does not copy them.
    /// Failure statuses: PasswordRequired, PasswordIncorrect, UnsupportedEncryption, Damaged, Unsupported (too many pages).
    [[nodiscard]] static protocol::Result<Document> open(std::span<const std::uint8_t> bytes, std::string_view password);

    [[nodiscard]] explicit operator bool() const noexcept { return handle_ != nullptr; }
    [[nodiscard]] std::uint32_t page_count() const noexcept { return page_count_; }

    /// Metadata strings are cut to the schema limit on a code point boundary; a string longer than 64 KiB is reported empty.
    [[nodiscard]] protocol::Result<protocol::GetDocumentInfoReply> info();

    /// Sizes are in points as displayed (the page's /Rotate applied) and clamped to the schema range [1, 14400],
    /// so the layout the broker computes from them and the scale used by render_tile agree.
    [[nodiscard]] protocol::Result<protocol::GetPageSizesReply> page_sizes(std::uint32_t first, std::uint32_t count);

    /// Renders one tile as BGRA over opaque white into `destination` (rows `stride` bytes apart).
    /// Ok, PartiallyRendered (PDFium reported a failure part-way), Cancelled / Timeout (the check interrupted PDFium),
    /// InvalidArgument (page, size or buffer out of range) or Damaged (the page cannot be loaded).
    [[nodiscard]] protocol::Status render_tile(const TileRequest& request, std::span<std::uint8_t> destination,
                                               std::uint32_t stride, const InterruptCheck& interrupt);

private:
    Document(fpdf_document_t__* handle, std::uint32_t page_count) noexcept : handle_(handle), page_count_(page_count) {}
    void reset() noexcept;

    fpdf_document_t__* handle_{nullptr};
    std::uint32_t page_count_{0};
};

}  // namespace quire::pdfium
