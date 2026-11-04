// RAII wrapper over one PDFium document. Everything here must run on the engine thread (§5.2 rule 1).
// The bytes of the PDF are owned by the caller (a read-only mapped view, ADR-0005) and must outlive the Document.
// Results use the protocol's status codes (§6.4); nothing throws.
#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "mikepdf/protocol/enums.h"
#include "mikepdf/protocol/messages.h"
#include "mikepdf/protocol/msgpack.h"

struct fpdf_document_t__;
struct fpdf_bitmap_t__;
struct fpdf_page_t__;
struct fpdf_annotation_t__;

namespace mikepdf::pdfium {

enum class RenderInterrupt : std::uint8_t { None, Cancel, Timeout };

/// Polled from PDFium's pause callback. It runs inside PDFium, so it must be cheap and must not throw.
using InterruptCheck = std::function<RenderInterrupt()>;

inline constexpr std::uint32_t kRenderAnnotations = 1;
/// Draws the widgets of the page's form fields with their current values. PDFium draws a widget only through its form-fill environment (FPDF_FFLDraw);
/// FPDF_ANNOT skips every one. The environment is made on the first request that asks for this and lives as long as the document. It has no scripting,
/// no callbacks and no field highlight: the pixels are what the file (or a printer) shows.
inline constexpr std::uint32_t kRenderFormFields = 2;
/// RenderTile only: dark pages. The tile is drawn as the file draws it and then recoloured the way the app's inverted page colour does it (lightness turned over, hue kept:
/// white paper becomes about 7% grey, black text about 95% white), except for colour photographs, which keep their own colours. Which images are photographs: a picture
/// of at least 16 x 16 pixels, with more than one bit a pixel, that covers less than 90% of the page and in which fewer than 55% of a sample of its pixels are near white. Scans,
/// line art and diagrams (mostly white paper) are recoloured with the rest of the page. The meaning of bit 2 of RenderBand is another one (below).
inline constexpr std::uint32_t kRenderDarkPage = 4;
/// RenderBand only: PDFium's printing mode (no screen-only annotations, no LCD text).
inline constexpr std::uint32_t kRenderPrinting = 4;
/// RenderTile only (schema 7), and only together with kRenderFormFields: PDFium's own form field highlight, a translucent tint on every fillable widget, drawn by FPDF_FFLDraw with
/// the page. It is never part of a print, a snapshot or a flattened copy (RenderBand has no such bit), and it is switched off again as soon as the tile is drawn.
inline constexpr std::uint32_t kRenderFieldHighlight = 8;

/// What is drawn of a page's form fields on top of the page itself.
enum class FormDraw : std::uint8_t { None, Fields, Highlighted };

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

    // ---- schema 7: text quads, the annotation read side, forms and the engine's own save -------------------------------------------------
    // An annotation is named by (page, index): its position in the page's /Annots array, which does not change while the document is open (nothing here adds or removes
    // one). Every text value is a PDF text string decoded to UTF-8 and cut at a code point boundary at the schema limit.

    /// The quadrilaterals of the characters [start, start + count) of a page, one per line of text, 64 from `first_quad` on (GetTextQuads in the schema).
    /// InvalidArgument: page out of range, start at or past the end of the text, first_quad past the end of the quads. Damaged: the page or its text cannot be loaded.
    [[nodiscard]] protocol::Result<protocol::GetTextQuadsReply> text_quads(std::uint32_t page, std::uint32_t start, std::uint32_t count, std::uint32_t first_quad);

    [[nodiscard]] protocol::Result<protocol::ListAnnotationsReply> list_annotations(std::uint32_t page, std::uint32_t first, std::uint32_t count);

    /// InvalidArgument: page or index out of range, or no dictionary at that position.
    [[nodiscard]] protocol::Result<protocol::GetAnnotationReply> annotation(std::uint32_t page, std::uint32_t index);

    /// InvalidArgument: not an ink annotation, or no such stroke. Unsupported: the stroke has more points than the engine copies out of PDFium (1,000,000).
    [[nodiscard]] protocol::Result<protocol::GetInkStrokeReply> ink_stroke(std::uint32_t page, std::uint32_t index, std::uint32_t stroke, std::uint32_t first, std::uint32_t count);

    /// The display mirror (annotation-core 3.3): sets or puts back the Hidden flag in the in-memory document. The file's own flags are remembered the first time an annotation is
    /// changed, reported by list_annotations and annotation, and put back by save() before PDFium writes anything.
    [[nodiscard]] protocol::Status set_annotation_hidden(std::uint32_t page, std::uint32_t index, bool hidden);

    /// How many annotations each of `count` pages from `first_page` has that a person sees (not links, popups or widgets). `expired` is polled between pages; when it returns true the result is Timeout.
    [[nodiscard]] protocol::Result<protocol::CountAnnotationsReply> count_annotations(std::uint32_t first_page, std::uint32_t count, const std::function<bool()>& expired);

    /// One window of the document's form widgets in tab order (document order within each page). The list is built once, page by page, with `expired` polled between pages: when it
    /// returns true the result is Timeout and the next call carries on from the page it reached. Unsupported: PDFium cannot make a form-fill environment.
    [[nodiscard]] protocol::Result<protocol::GetFormFieldsReply> form_fields(std::uint32_t first, std::uint32_t count, const std::function<bool()>& expired);

    [[nodiscard]] protocol::Result<protocol::GetFormFieldDetailReply> form_field_detail(std::uint32_t field_id, std::uint32_t first_option, const std::function<bool()>& expired);

    /// Changes one field through PDFium's form-fill events (SetFormFieldValue in the schema has the statuses).
    [[nodiscard]] protocol::Result<protocol::SetFormFieldValueReply> set_form_field_value(const protocol::SetFormFieldValue& request, const std::function<bool()>& expired);

    /// Reset one field, reset every field, or press a push button (FormAction in the schema has the statuses). `expired` is polled between the fields of a reset of all.
    [[nodiscard]] protocol::Result<protocol::FormActionReply> form_action(std::uint32_t field_id, std::uint16_t action, const std::function<bool()>& expired);

    /// Receives what PDFium writes, a block at a time. Returns false to stop the save (the disk is full, the handle was closed).
    using SaveSink = std::function<bool(const void* data, unsigned long size)>;

    /// FPDF_SaveAsCopy of the in-memory document. Ends a form edit in progress first (so a typed value is kept), then puts the file's own annotation flags back for the duration of the
    /// save (a display mirror flag must never reach a saved file) and sets the mirror again afterwards, whether or not the save worked. `incremental` is FPDF_INCREMENTAL, otherwise
    /// FPDF_NO_INCREMENTAL. Ok: `bytes_written` is what the sink received. Damaged: PDFium or the sink failed.
    [[nodiscard]] protocol::Status save(bool incremental, const SaveSink& sink, std::uint64_t& bytes_written);

    /// How many annotations have a Hidden flag that differs from the file's (a test hook for the save invariant).
    [[nodiscard]] std::size_t mirrored_annotation_count() const noexcept { return mirror_.size(); }

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

    /// Whether the page draws an image through a matrix that is neither axis-aligned nor a quarter turn (a skewed scan). PDFium draws such an image with its image
    /// transformer, which takes the shear from the corner of the clip rectangle instead of the corner of the image, so a window of the page that does not start at the
    /// page's own corner shows the image shifted by up to a few pixels against the whole-page picture. The answer for the last page asked about is remembered.
    [[nodiscard]] bool has_skewed_image(fpdf_page_t__* page, std::uint32_t index);

    /// The rectangles of the page's colour photographs in page space (left, bottom, right, top), for dark pages. Found once per page (the last page asked about is remembered):
    /// the images are decoded to look at a sample of their pixels, which costs about as much as drawing them.
    struct PhotoRect {
        float left, bottom, right, top;
    };
    [[nodiscard]] const std::vector<PhotoRect>& photo_rects(fpdf_page_t__* page, std::uint32_t index);

    /// Draws the window [x, x + width) x [y, y + height) of a page laid out at `size_x` x `size_y` device pixels into `destination` (rows `stride` bytes apart): a tile or a band.
    /// A window that does not start at the page's corner of a page with a skewed image is drawn from the corner instead, into a scratch bitmap that is cropped (when it fits
    /// the budget), so that it is the same pixels as that part of the whole page drawn at once.
    [[nodiscard]] protocol::Status render_window(fpdf_page_t__* page, std::uint32_t index, int x, int y, int width, int height, int size_x, int size_y, int rotate,
                                                 FormDraw forms, int flags, std::uint8_t* destination, std::uint32_t stride, const InterruptCheck& interrupt);

    /// Draws a loaded page into `bitmap` and, unless `forms` is None, the widgets of its form fields on top of it with the same transform. Shared by tiles and bands so that
    /// both draw a widget the same way. Unsupported when PDFium cannot make the form-fill environment.
    [[nodiscard]] protocol::Status render_page(fpdf_bitmap_t__* bitmap, fpdf_page_t__* page, int start_x, int start_y, int size_x, int size_y, int rotate,
                                               FormDraw forms, int flags, const InterruptCheck& interrupt);

    /// Display mirror: (page << 32 | index) of every annotation whose /F this engine changed, with the file's value and the value it set.
    struct MirroredFlags {
        int original;
        int current;
    };
    std::unordered_map<std::uint64_t, MirroredFlags> mirror_;

    /// The form widgets of the document in tab order, built page by page. A fieldId is a position in this list.
    struct Widget {
        std::uint32_t page;
        std::uint32_t index;
        int type;          // FPDF_FORMFIELD_*; 0 when PDFium does not know the widget as part of a field
        std::string name;  // the fully qualified field name, cut at 256 bytes; widgets that share a name share a value
    };
    std::vector<Widget> widgets_;
    std::uint32_t widgets_scanned_pages_{0};
    bool widgets_ready_{false};

    /// The quads of the last range asked about, so that the 64-quad windows of one range cost one pass over its characters.
    struct QuadCache {
        std::uint32_t page{0xFFFFFFFFu};
        std::uint32_t start{};
        std::uint32_t count{};
        std::vector<float> xs;  // four corners a quad
        std::vector<float> ys;
        bool truncated{false};
    };
    QuadCache quads_;

    /// Annotations and the display mirror (annotations.cpp).
    [[nodiscard]] bool restore_mirrored_flags();
    void reapply_mirrored_flags();
    [[nodiscard]] int file_flags(std::uint32_t page, std::uint32_t index, int current) const;

    /// Forms (forms.cpp). Both Timeout-able: the widget list is built page by page.
    [[nodiscard]] protocol::Status ensure_widgets(const std::function<bool()>& expired);

    fpdf_document_t__* handle_{nullptr};
    std::uint32_t page_count_{0};
    std::unique_ptr<PageCache, PageCacheDeleter> cache_;
    std::unique_ptr<FormFill, FormFillDeleter> form_;
    std::uint32_t photo_page_{0xFFFFFFFFu};
    std::vector<PhotoRect> photos_;
    std::uint32_t skew_page_{0xFFFFFFFFu};
    bool skew_value_{false};
};

}  // namespace mikepdf::pdfium
