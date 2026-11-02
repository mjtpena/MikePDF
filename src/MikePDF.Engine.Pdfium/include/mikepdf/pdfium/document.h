// RAII wrapper over one PDFium document. Everything here must run on the engine thread (§5.2 rule 1).
// The bytes of the PDF are owned by the caller (a read-only mapped view, ADR-0005) and must outlive the Document.
// Results use the protocol's status codes (§6.4); nothing throws.
#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <string_view>

#include "mikepdf/protocol/enums.h"
#include "mikepdf/protocol/messages.h"
#include "mikepdf/protocol/msgpack.h"

struct fpdf_document_t__;
struct fpdf_bitmap_t__;
struct fpdf_page_t__;

namespace mikepdf::pdfium {

enum class RenderInterrupt : std::uint8_t { None, Cancel, Timeout };

/// Polled from PDFium's pause callback. It runs inside PDFium, so it must be cheap and must not throw.
using InterruptCheck = std::function<RenderInterrupt()>;

inline constexpr std::uint32_t kRenderAnnotations = 1;
/// Draws the widgets of the page's form fields with their current values. PDFium draws a widget only through its form-fill environment (FPDF_FFLDraw);
/// FPDF_ANNOT skips every one. The environment is made on the first request that asks for this and lives as long as the document. It has no scripting,
/// no callbacks and no field highlight: the pixels are what the file (or a printer) shows.
inline constexpr std::uint32_t kRenderFormFields = 2;
/// RenderBand only: PDFium's printing mode (no screen-only annotations, no LCD text).
inline constexpr std::uint32_t kRenderPrinting = 4;

/// One horizontal band of a page laid out at `page_width_px` x `page_height_px` device pixels (after the rotation), for printing.
struct BandRequest {
    std::uint32_t page{};
    /// Quarter turns clockwise, on top of the page's own /Rotate.
    std::uint16_t rotation{};
    std::uint32_t page_width_px{};
    std::uint32_t page_height_px{};
    /// The band is the rows band_top to band_top + band_height of the laid-out page.
    std::uint32_t band_top{};
    std::uint32_t band_height{};
    std::uint32_t flags{};
};

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

    /// Renders one band of a page for printing as BGRA over opaque white into `destination` (rows `stride` = 4 x page_width_px bytes apart).
    /// The statuses are render_tile's. InvalidArgument also for a band outside the page or one that does not fit `destination`.
    [[nodiscard]] protocol::Status render_band(const BandRequest& request, std::span<std::uint8_t> destination, std::uint32_t stride,
                                               const InterruptCheck& interrupt);

    // ---- schema 5 --------------------------------------------------------------------------------------------

    /// /ViewerPreferences as GetViewerPreferencesReply describes it: every value clamped to its range, anything the file gets wrong read as absent.
    [[nodiscard]] protocol::Result<protocol::GetViewerPreferencesReply> viewer_preferences();

    // ---- reading services (WP-S2) ----------------------------------------------------------------------------
    // Everything below returns data the broker treats as untrusted, and every result obeys the schema limits (the caller encodes it
    // with the generated encoder, which refuses anything else). Coordinates are PDF user space: points, origin bottom-left.

    /// One window of siblings of the bookmark tree. `path` is the sibling index taken at each level from the root, naming the parent.
    /// InvalidArgument when the path leads nowhere. A cycle in the file ends at the scan limit; it never loops.
    [[nodiscard]] protocol::Result<protocol::GetOutlineReply> outline(std::span<const std::uint32_t> path, std::uint32_t first, std::uint32_t count);

    /// One window of the link annotations of a page. InvalidArgument for a page out of range, Damaged when it cannot be loaded.
    [[nodiscard]] protocol::Result<protocol::GetPageLinksReply> page_links(std::uint32_t page, std::uint32_t first, std::uint32_t count);

    /// The address of the external link at `index` (the order page_links uses). InvalidArgument: no such link, or not an external one.
    /// Unsupported: the address is too long, is not UTF-8 or holds control characters.
    [[nodiscard]] protocol::Result<protocol::GetLinkUriReply> link_uri(std::uint32_t page, std::uint32_t index);

    [[nodiscard]] protocol::Result<protocol::GetPageBoxesReply> page_boxes(std::uint32_t page);

    /// One window of the page's characters, one entry per UTF-16 code unit, in PDFium's order.
    [[nodiscard]] protocol::Result<protocol::GetPageTextReply> page_text(std::uint32_t page, std::uint32_t first, std::uint32_t count);

    /// Matches of `query` (UTF-8, not empty) in one page. `expired` is polled between matches; when it returns true the result is Timeout.
    [[nodiscard]] protocol::Result<protocol::SearchPageReply> search_page(std::uint32_t page, std::string_view query, std::uint32_t flags,
                                                                          const std::function<bool()>& expired);

    // ---- the properties dialog (schema 4) ----------------------------------------------------------------------

    /// Dates, the security handler revision, the user permissions, the form type and the fonts of the first pages. PDFium has no document-level font
    /// list, so the fonts are found by walking the page objects of up to `max_font_pages` pages, stopping when `expired` says the time is up (checked
    /// between pages and every few hundred objects); `fonts_complete` says whether every page was walked. Never fails for a document that opened.
    [[nodiscard]] protocol::Result<protocol::GetDocumentDetailsReply> details(std::uint32_t max_font_pages, const std::function<bool()>& expired);

private:
    struct PageCache;
    struct PageCacheDeleter {
        void operator()(PageCache* cache) const noexcept;
    };
    struct FormFill;
    struct FormFillDeleter {
        void operator()(FormFill* form) const noexcept;
    };

    Document(fpdf_document_t__* handle, std::uint32_t page_count) noexcept : handle_(handle), page_count_(page_count) {}
    void reset() noexcept;

    /// The parsed page (and, on demand, its text) of the last page a reading service touched: the services come in windows of the same
    /// page, so keeping one avoids parsing it for every window. Null when the page cannot be loaded. Engine thread only.
    [[nodiscard]] PageCache* cached_page(std::uint32_t page);

    /// The form-fill environment of this document, made on first use. Null when PDFium refuses to make one.
    [[nodiscard]] FormFill* form_fill();

    /// Draws a loaded page into `bitmap` and, when `with_forms`, the widgets of its form fields on top of it with the same transform. Shared by tiles and bands so that
    /// both draw a widget the same way. Unsupported when PDFium cannot make the form-fill environment.
    [[nodiscard]] protocol::Status render_page(fpdf_bitmap_t__* bitmap, fpdf_page_t__* page, int start_x, int start_y, int size_x, int size_y, int rotate,
                                               bool with_forms, int flags, const InterruptCheck& interrupt);

    fpdf_document_t__* handle_{nullptr};
    std::uint32_t page_count_{0};
    std::unique_ptr<PageCache, PageCacheDeleter> cache_;
    std::unique_ptr<FormFill, FormFillDeleter> form_;
};

}  // namespace mikepdf::pdfium
