#include "mikepdf/pdfium/document.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <string>
#include <unordered_set>
#include <utility>
#include <vector>

#include <fpdf_catalog.h>
#include <fpdf_doc.h>
#include <fpdf_edit.h>
#include <fpdf_javascript.h>
#include <fpdf_progressive.h>
#include <fpdf_signature.h>
#include <fpdf_text.h>
#include <fpdf_transformpage.h>
#include <fpdfview.h>

namespace mikepdf::pdfium {

using protocol::Result;
using protocol::Status;

namespace {

constexpr std::uint32_t kMaxPages = 10'000'000;      // schema limit of pageCount
constexpr float kMinPageSide = 1.0f;
constexpr float kMaxPageSide = 14400.0f;              // schema limit of GetPageSizesReply
constexpr std::size_t kMaxMetaStringBytes = 1024;     // schema limit of the info strings
constexpr unsigned long kMaxMetaRawBytes = 64 * 1024;  // refuse to copy a larger raw string out of PDFium
constexpr std::uint32_t kMaxSignatures = 65535;
constexpr std::int64_t kMaxDeviceSide = std::int64_t{1} << 24;
constexpr std::uint32_t kMaxTileSide = 1024;

// Reading services. Each limit is the schema's: the generated encoder refuses anything beyond it, so these keep the engine from building a
// reply it could not send, and bound the work one request can cause.
constexpr std::uint32_t kMaxOutlineSiblings = 100'000;  // GetOutlineReply.total
constexpr std::size_t kMaxOutlineTitleBytes = 256;
constexpr std::uint32_t kMaxLinksPerPage = 100'000;     // GetPageLinksReply.total
constexpr std::size_t kMaxUriBytes = 2048;
constexpr std::uint32_t kMaxPageChars = 200'000;        // GetPageTextReply.total
constexpr std::size_t kMaxSearchMatches = 2048;
constexpr std::uint32_t kSearchPollEvery = 64;
constexpr double kMaxCoordinate = 1'000'000.0;          // the schema's range for every coordinate

// The kinds of a destination (GetOutlineReply.kinds).
constexpr std::uint16_t kDestNone = 0;
constexpr std::uint16_t kDestPage = 1;
constexpr std::uint16_t kDestPageY = 2;
constexpr std::uint16_t kDestPageXY = 3;
constexpr std::uint16_t kDestExternal = 4;
constexpr std::uint16_t kDestOther = 5;

template <class T>
Result<T> failure(Status status, const char* detail) {
    return {status, T{}, detail};
}

// PDFium reports both "no password" and "wrong password" as FPDF_ERR_PASSWORD.
Status status_from_load_error(unsigned long error, bool password_given) {
    switch (error) {
        case FPDF_ERR_PASSWORD:
            return password_given ? Status::PasswordIncorrect : Status::PasswordRequired;
        case FPDF_ERR_SECURITY:
            return Status::UnsupportedEncryption;
        case FPDF_ERR_FILE:
        case FPDF_ERR_FORMAT:
        case FPDF_ERR_PAGE:
        case FPDF_ERR_UNKNOWN:
        default:
            return Status::Damaged;
    }
}

void append_utf8(std::string& out, char32_t cp) {
    if (cp < 0x80) {
        out += static_cast<char>(cp);
    } else if (cp < 0x800) {
        out += static_cast<char>(0xC0 | (cp >> 6));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
        out += static_cast<char>(0xE0 | (cp >> 12));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
        out += static_cast<char>(0xF0 | (cp >> 18));
        out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

std::size_t utf8_length(char32_t cp) { return cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4; }

// UTF-16LE to strict UTF-8: stops at the first NUL, replaces unpaired surrogates with U+FFFD, and stops before a code
// point that would not fit in `max_bytes`, so the result always satisfies the schema's text rule.
std::string utf16_to_utf8(const std::vector<char16_t>& units, std::size_t max_bytes) {
    std::string out;
    for (std::size_t i = 0; i < units.size();) {
        char32_t cp = units[i++];
        if (cp == 0) {
            break;
        }
        if (cp >= 0xD800 && cp <= 0xDBFF && i < units.size() && units[i] >= 0xDC00 && units[i] <= 0xDFFF) {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (units[i++] - 0xDC00);
        } else if (cp >= 0xD800 && cp <= 0xDFFF) {
            cp = 0xFFFD;
        }
        if (out.size() + utf8_length(cp) > max_bytes) {
            break;
        }
        append_utf8(out, cp);
    }
    return out;
}

std::string meta_text(FPDF_DOCUMENT doc, const char* tag) {
    const unsigned long needed = FPDF_GetMetaText(doc, tag, nullptr, 0);  // bytes, including the 2-byte terminator
    if (needed <= 2 || needed > kMaxMetaRawBytes || needed % 2 != 0) {
        return {};
    }
    std::vector<char16_t> buffer(needed / 2);
    if (FPDF_GetMetaText(doc, tag, buffer.data(), needed) != needed) {
        return {};
    }
    return utf16_to_utf8(buffer, kMaxMetaStringBytes);
}

float clamp_page_side(float value) {
    if (!std::isfinite(value) || value < kMinPageSide) {
        return kMinPageSide;
    }
    return std::min(value, kMaxPageSide);
}

struct PageHandle {
    FPDF_PAGE page{nullptr};
    PageHandle(FPDF_DOCUMENT doc, int index) : page(FPDF_LoadPage(doc, index)) {}
    ~PageHandle() {
        if (page != nullptr) {
            FPDF_ClosePage(page);
        }
    }
    PageHandle(const PageHandle&) = delete;
    PageHandle& operator=(const PageHandle&) = delete;
};

struct BitmapHandle {
    FPDF_BITMAP bitmap{nullptr};
    ~BitmapHandle() {
        if (bitmap != nullptr) {
            FPDFBitmap_Destroy(bitmap);  // an external buffer is not freed
        }
    }
};

struct PauseState {
    const InterruptCheck* check{nullptr};
    RenderInterrupt reason{RenderInterrupt::None};
};

FPDF_BOOL NeedToPauseNow(IFSDK_PAUSE* self) {
    auto* state = static_cast<PauseState*>(self->user);
    if (state->reason == RenderInterrupt::None && state->check != nullptr && *state->check) {
        try {
            state->reason = (*state->check)();
        } catch (...) {
            state->reason = RenderInterrupt::Cancel;
        }
    }
    return state->reason != RenderInterrupt::None ? 1 : 0;
}

}  // namespace

struct Document::PageCache {
    std::uint32_t index{};
    FPDF_PAGE page{nullptr};
    FPDF_TEXTPAGE text{nullptr};
    bool text_failed{false};

    PageCache() = default;
    PageCache(const PageCache&) = delete;
    PageCache& operator=(const PageCache&) = delete;
    // The text page must go before the page it was made from, and both before the document.
    ~PageCache() {
        if (text != nullptr) {
            FPDFText_ClosePage(text);
        }
        if (page != nullptr) {
            FPDF_ClosePage(page);
        }
    }

    FPDF_TEXTPAGE text_page() {
        if (text == nullptr && !text_failed) {
            text = FPDFText_LoadPage(page);
            text_failed = text == nullptr;
        }
        return text;
    }
};

void Document::PageCacheDeleter::operator()(PageCache* cache) const noexcept { delete cache; }

Document::~Document() { reset(); }

Document::Document(Document&& other) noexcept
    : handle_(std::exchange(other.handle_, nullptr)),
      page_count_(std::exchange(other.page_count_, 0)),
      cache_(std::move(other.cache_)) {}

Document& Document::operator=(Document&& other) noexcept {
    if (this != &other) {
        reset();
        handle_ = std::exchange(other.handle_, nullptr);
        page_count_ = std::exchange(other.page_count_, 0);
        cache_ = std::move(other.cache_);
    }
    return *this;
}

void Document::reset() noexcept {
    cache_.reset();  // before the document: the page and its text belong to it
    if (handle_ != nullptr) {
        FPDF_CloseDocument(handle_);
        handle_ = nullptr;
    }
    page_count_ = 0;
}

Result<Document> Document::open(std::span<const std::uint8_t> bytes, std::string_view password) {
    if (bytes.empty()) {
        return failure<Document>(Status::Damaged, "empty file");
    }
    const std::string password_z(password);  // FPDF_BYTESTRING must be NUL-terminated
    FPDF_DOCUMENT doc = FPDF_LoadMemDocument64(bytes.data(), bytes.size(), password_z.c_str());
    if (doc == nullptr) {
        return failure<Document>(status_from_load_error(FPDF_GetLastError(), !password.empty()), "PDFium could not load the document");
    }
    const int pages = FPDF_GetPageCount(doc);
    if (pages < 0 || static_cast<std::uint32_t>(pages) > kMaxPages) {
        FPDF_CloseDocument(doc);
        return failure<Document>(pages < 0 ? Status::Damaged : Status::Unsupported, "page count out of range");
    }
    return {Status::Ok, Document(doc, static_cast<std::uint32_t>(pages)), ""};
}

Result<protocol::GetDocumentInfoReply> Document::info() {
    if (handle_ == nullptr) {
        return failure<protocol::GetDocumentInfoReply>(Status::InvalidArgument, "no document");
    }
    protocol::GetDocumentInfoReply reply;
    reply.page_count = page_count_;
    int version = 0;
    if (FPDF_GetFileVersion(handle_, &version) != 0 && version > 0 && version <= 99) {
        reply.pdf_version = static_cast<std::uint32_t>(version);
    }
    reply.title = meta_text(handle_, "Title");
    reply.author = meta_text(handle_, "Author");
    reply.subject = meta_text(handle_, "Subject");
    reply.keywords = meta_text(handle_, "Keywords");
    reply.creator = meta_text(handle_, "Creator");
    reply.producer = meta_text(handle_, "Producer");
    reply.permissions = static_cast<std::uint32_t>(FPDF_GetDocPermissions(handle_));
    reply.is_tagged = FPDFCatalog_IsTagged(handle_) != 0;
    reply.has_xfa = FPDF_GetXFAPacketCount(handle_) > 0;
    reply.has_java_script = FPDFDoc_GetJavaScriptActionCount(handle_) > 0;
    const int signatures = FPDF_GetSignatureCount(handle_);
    reply.signature_count = signatures < 0 ? 0 : std::min(static_cast<std::uint32_t>(signatures), kMaxSignatures);
    return {Status::Ok, std::move(reply), ""};
}

Result<protocol::GetPageSizesReply> Document::page_sizes(std::uint32_t first, std::uint32_t count) {
    using Reply = protocol::GetPageSizesReply;
    if (handle_ == nullptr) {
        return failure<Reply>(Status::InvalidArgument, "no document");
    }
    if (count == 0 || count > 5000 || static_cast<std::uint64_t>(first) + count > page_count_) {
        return failure<Reply>(Status::InvalidArgument, "page range out of bounds");
    }
    Reply reply;
    reply.widths.reserve(count);
    reply.heights.reserve(count);
    reply.rotations.reserve(count);
    for (std::uint32_t i = 0; i < count; ++i) {
        PageHandle page(handle_, static_cast<int>(first + i));
        if (page.page == nullptr) {
            return failure<Reply>(Status::Damaged, "page cannot be loaded");
        }
        reply.widths.push_back(clamp_page_side(FPDF_GetPageWidthF(page.page)));
        reply.heights.push_back(clamp_page_side(FPDF_GetPageHeightF(page.page)));
        const int rotation = FPDFPage_GetRotation(page.page);
        reply.rotations.push_back(rotation >= 0 && rotation <= 3 ? static_cast<std::uint16_t>(rotation) : std::uint16_t{0});
    }
    return {Status::Ok, std::move(reply), ""};
}

Status Document::render_tile(const TileRequest& request, std::span<std::uint8_t> destination, std::uint32_t stride,
                             const InterruptCheck& interrupt) {
    if (handle_ == nullptr || request.page >= page_count_ || !std::isfinite(request.scale) || request.scale <= 0.0f ||
        request.rotation > 3 || request.tile_width == 0 || request.tile_width > kMaxTileSide || request.tile_height == 0 ||
        request.tile_height > kMaxTileSide || (request.flags & ~(kRenderAnnotations | kRenderFormFields)) != 0) {
        return Status::InvalidArgument;
    }
    if ((request.flags & kRenderFormFields) != 0) {
        return Status::Unsupported;  // no form-fill environment until forms arrive
    }
    const std::uint64_t row_bytes = std::uint64_t{request.tile_width} * 4;
    if (stride < row_bytes || stride > static_cast<std::uint32_t>(std::numeric_limits<int>::max()) ||
        std::uint64_t{stride} * request.tile_height > destination.size()) {
        return Status::InvalidArgument;
    }

    PageHandle page(handle_, static_cast<int>(request.page));
    if (page.page == nullptr) {
        return Status::Damaged;
    }
    double width = clamp_page_side(FPDF_GetPageWidthF(page.page));
    double height = clamp_page_side(FPDF_GetPageHeightF(page.page));
    if ((request.rotation & 1) != 0) {
        std::swap(width, height);
    }
    const std::int64_t device_width = std::llround(width * static_cast<double>(request.scale));
    const std::int64_t device_height = std::llround(height * static_cast<double>(request.scale));
    if (device_width < 1 || device_height < 1 || device_width > kMaxDeviceSide || device_height > kMaxDeviceSide) {
        return Status::InvalidArgument;
    }

    BitmapHandle bitmap{FPDFBitmap_CreateEx(static_cast<int>(request.tile_width), static_cast<int>(request.tile_height),
                                            FPDFBitmap_BGRA, destination.data(), static_cast<int>(stride))};
    if (bitmap.bitmap == nullptr) {
        return Status::InvalidArgument;
    }
    FPDFBitmap_FillRect(bitmap.bitmap, 0, 0, static_cast<int>(request.tile_width), static_cast<int>(request.tile_height), 0xFFFFFFFFu);

    PauseState state{&interrupt, RenderInterrupt::None};
    IFSDK_PAUSE pause{};
    pause.version = 1;
    pause.NeedToPauseNow = NeedToPauseNow;
    pause.user = &state;

    const int flags = (request.flags & kRenderAnnotations) != 0 ? FPDF_ANNOT : 0;
    int progress = FPDF_RenderPageBitmap_Start(bitmap.bitmap, page.page, -request.tile_x, -request.tile_y,
                                               static_cast<int>(device_width), static_cast<int>(device_height),
                                               request.rotation, flags, &pause);
    while (progress == FPDF_RENDER_TOBECONTINUED && state.reason == RenderInterrupt::None) {
        progress = FPDF_RenderPage_Continue(page.page, &pause);
    }
    FPDF_RenderPage_Close(page.page);

    if (progress == FPDF_RENDER_DONE) {
        return Status::Ok;
    }
    if (state.reason == RenderInterrupt::Cancel) {
        return Status::Cancelled;
    }
    if (state.reason == RenderInterrupt::Timeout) {
        return Status::Timeout;
    }
    return Status::PartiallyRendered;
}

// ---- reading services (WP-S2) ----------------------------------------------------------------------------------------------

namespace {

float clamp_coordinate(double value) {
    if (!std::isfinite(value)) {
        return 0.0f;
    }
    return static_cast<float>(std::clamp(value, -kMaxCoordinate, kMaxCoordinate));
}

struct Destination {
    std::uint16_t kind{kDestNone};
    std::uint32_t page{0};
    float x{0};
    float y{0};
};

// Where a destination lands. A destination whose page does not exist is no destination at all.
Destination describe_dest(FPDF_DOCUMENT doc, FPDF_DEST dest, std::uint32_t page_count) {
    Destination out;
    if (dest == nullptr) {
        return out;
    }
    const int page = FPDFDest_GetDestPageIndex(doc, dest);
    if (page < 0 || static_cast<std::uint32_t>(page) >= page_count) {
        return out;
    }
    out.page = static_cast<std::uint32_t>(page);
    out.kind = kDestPage;
    FPDF_BOOL has_x = 0;
    FPDF_BOOL has_y = 0;
    FPDF_BOOL has_zoom = 0;
    FS_FLOAT x = 0;
    FS_FLOAT y = 0;
    FS_FLOAT zoom = 0;
    unsigned long parameter_count = 0;
    FS_FLOAT parameters[4] = {0, 0, 0, 0};
    switch (FPDFDest_GetView(dest, &parameter_count, parameters)) {
        case PDFDEST_VIEW_XYZ:
            // [page /XYZ left top zoom]: either number may be null.
            if (FPDFDest_GetLocationInPage(dest, &has_x, &has_y, &has_zoom, &x, &y, &zoom) != 0 && has_y != 0) {
                out.y = clamp_coordinate(y);
                out.kind = kDestPageY;
                if (has_x != 0) {
                    out.x = clamp_coordinate(x);
                    out.kind = kDestPageXY;
                }
            }
            break;
        case PDFDEST_VIEW_FITH:
        case PDFDEST_VIEW_FITBH:
            // [page /FitH top]: the line at the top of the window.
            if (parameter_count >= 1) {
                out.y = clamp_coordinate(parameters[0]);
                out.kind = kDestPageY;
            }
            break;
        case PDFDEST_VIEW_FITR:
            // [page /FitR left bottom right top]: the corner at the top left of the window.
            if (parameter_count >= 4) {
                out.x = clamp_coordinate(parameters[0]);
                out.y = clamp_coordinate(parameters[3]);
                out.kind = kDestPageXY;
            }
            break;
        default:
            break;  // /Fit, /FitV, /FitB, /FitBV: the page, from its top
    }
    return out;
}

Destination describe_action(FPDF_DOCUMENT doc, FPDF_ACTION action, std::uint32_t page_count) {
    Destination out;
    if (action == nullptr) {
        return out;
    }
    switch (FPDFAction_GetType(action)) {
        case PDFACTION_GOTO:
            return describe_dest(doc, FPDFAction_GetDest(doc, action), page_count);
        case PDFACTION_URI:
            out.kind = kDestExternal;
            return out;
        default:
            // Remote and embedded go-to, launch, JavaScript, named actions: MikePDF does not follow them.
            out.kind = kDestOther;
            return out;
    }
}

Destination describe_link(FPDF_DOCUMENT doc, FPDF_LINK link, std::uint32_t page_count) {
    if (FPDF_DEST dest = FPDFLink_GetDest(doc, link); dest != nullptr) {
        return describe_dest(doc, dest, page_count);
    }
    return describe_action(doc, FPDFLink_GetAction(link), page_count);
}

std::string bookmark_title(FPDF_BOOKMARK bookmark) {
    const unsigned long needed = FPDFBookmark_GetTitle(bookmark, nullptr, 0);
    if (needed <= 2 || needed > kMaxMetaRawBytes || needed % 2 != 0) {
        return {};
    }
    std::vector<char16_t> buffer(needed / 2);
    if (FPDFBookmark_GetTitle(bookmark, buffer.data(), needed) != needed) {
        return {};
    }
    return utf16_to_utf8(buffer, kMaxOutlineTitleBytes);
}

// The input has passed protocol::is_valid_text, so it is well-formed UTF-8.
std::u16string utf8_to_utf16(std::string_view text) {
    std::u16string out;
    out.reserve(text.size());
    for (std::size_t i = 0; i < text.size();) {
        const auto lead = static_cast<unsigned char>(text[i]);
        char32_t cp = 0;
        std::size_t extra = 0;
        if (lead < 0x80) {
            cp = lead;
        } else if (lead < 0xE0) {
            cp = lead & 0x1Fu;
            extra = 1;
        } else if (lead < 0xF0) {
            cp = lead & 0x0Fu;
            extra = 2;
        } else {
            cp = lead & 0x07u;
            extra = 3;
        }
        ++i;
        for (std::size_t k = 0; k < extra && i < text.size(); ++k, ++i) {
            cp = (cp << 6) | (static_cast<unsigned char>(text[i]) & 0x3Fu);
        }
        if (cp >= 0x10000) {
            cp -= 0x10000;
            out += static_cast<char16_t>(0xD800 + (cp >> 10));
            out += static_cast<char16_t>(0xDC00 + (cp & 0x3FF));
        } else {
            out += static_cast<char16_t>(cp);
        }
    }
    return out;
}

bool has_control_character(std::string_view text) {
    return std::any_of(text.begin(), text.end(), [](char c) {
        const auto u = static_cast<unsigned char>(c);
        return u < 0x20 || u == 0x7F;
    });
}

}  // namespace

Document::PageCache* Document::cached_page(std::uint32_t page) {
    if (cache_ && cache_->index == page) {
        return cache_.get();
    }
    cache_.reset();
    if (handle_ == nullptr || page >= page_count_) {
        return nullptr;
    }
    FPDF_PAGE loaded = FPDF_LoadPage(handle_, static_cast<int>(page));
    if (loaded == nullptr) {
        return nullptr;
    }
    auto entry = std::unique_ptr<PageCache, PageCacheDeleter>(new PageCache());
    entry->index = page;
    entry->page = loaded;
    cache_ = std::move(entry);
    return cache_.get();
}

Result<protocol::GetOutlineReply> Document::outline(std::span<const std::uint32_t> path, std::uint32_t first, std::uint32_t count) {
    using Reply = protocol::GetOutlineReply;
    if (handle_ == nullptr || count == 0 || count > 64 || first > 99'999 || path.size() > 16) {
        return failure<Reply>(Status::InvalidArgument, "outline request out of bounds");
    }
    FPDF_BOOKMARK parent = nullptr;
    for (const std::uint32_t step : path) {
        FPDF_BOOKMARK at = FPDFBookmark_GetFirstChild(handle_, parent);
        for (std::uint32_t i = 0; at != nullptr && i < step; ++i) {
            at = FPDFBookmark_GetNextSibling(handle_, at);
        }
        if (at == nullptr) {
            return failure<Reply>(Status::InvalidArgument, "no such bookmark");
        }
        parent = at;
    }

    Reply reply;
    std::unordered_set<FPDF_BOOKMARK> seen;  // a file can make its siblings a ring
    std::uint32_t index = 0;
    for (FPDF_BOOKMARK at = FPDFBookmark_GetFirstChild(handle_, parent); at != nullptr && index < kMaxOutlineSiblings;
         at = FPDFBookmark_GetNextSibling(handle_, at)) {
        if (!seen.insert(at).second) {
            break;
        }
        if (index >= first && reply.titles.size() < count) {
            Destination where = describe_action(handle_, FPDFBookmark_GetAction(at), page_count_);
            if (where.kind == kDestNone) {
                where = describe_dest(handle_, FPDFBookmark_GetDest(handle_, at), page_count_);
            }
            reply.titles.push_back(bookmark_title(at));
            reply.kinds.push_back(where.kind);
            reply.pages.push_back(where.page);
            reply.xs.push_back(where.x);
            reply.ys.push_back(where.y);
            reply.has_children.push_back(FPDFBookmark_GetFirstChild(handle_, at) != nullptr ? 1 : 0);
        }
        ++index;
    }
    reply.total = index;
    return {Status::Ok, std::move(reply), ""};
}

Result<protocol::GetPageLinksReply> Document::page_links(std::uint32_t page, std::uint32_t first, std::uint32_t count) {
    using Reply = protocol::GetPageLinksReply;
    if (handle_ == nullptr || page >= page_count_ || count == 0 || count > 512 || first > 99'999) {
        return failure<Reply>(Status::InvalidArgument, "link request out of bounds");
    }
    PageCache* cached = cached_page(page);
    if (cached == nullptr) {
        return failure<Reply>(Status::Damaged, "page cannot be loaded");
    }
    Reply reply;
    int position = 0;
    FPDF_LINK link = nullptr;
    std::uint32_t index = 0;
    while (index < kMaxLinksPerPage && FPDFLink_Enumerate(cached->page, &position, &link) != 0) {
        if (index >= first && reply.lefts.size() < count) {
            FS_RECTF rect{};
            if (FPDFLink_GetAnnotRect(link, &rect) == 0) {
                rect = FS_RECTF{};
            }
            const Destination where = describe_link(handle_, link, page_count_);
            const float a = clamp_coordinate(rect.bottom);
            const float b = clamp_coordinate(rect.top);
            const float c = clamp_coordinate(rect.left);
            const float d = clamp_coordinate(rect.right);
            reply.lefts.push_back(std::min(c, d));
            reply.rights.push_back(std::max(c, d));
            reply.bottoms.push_back(std::min(a, b));
            reply.tops.push_back(std::max(a, b));
            reply.kinds.push_back(where.kind);
            reply.pages.push_back(where.page);
            reply.xs.push_back(where.x);
            reply.ys.push_back(where.y);
        }
        ++index;
    }
    reply.total = index;
    return {Status::Ok, std::move(reply), ""};
}

Result<protocol::GetLinkUriReply> Document::link_uri(std::uint32_t page, std::uint32_t index) {
    using Reply = protocol::GetLinkUriReply;
    if (handle_ == nullptr || page >= page_count_ || index >= kMaxLinksPerPage) {
        return failure<Reply>(Status::InvalidArgument, "link request out of bounds");
    }
    PageCache* cached = cached_page(page);
    if (cached == nullptr) {
        return failure<Reply>(Status::Damaged, "page cannot be loaded");
    }
    int position = 0;
    FPDF_LINK link = nullptr;
    for (std::uint32_t i = 0; i <= index; ++i) {
        if (FPDFLink_Enumerate(cached->page, &position, &link) == 0) {
            return failure<Reply>(Status::InvalidArgument, "no such link");
        }
    }
    if (FPDFLink_GetDest(handle_, link) != nullptr) {
        return failure<Reply>(Status::InvalidArgument, "not an external link");
    }
    FPDF_ACTION action = FPDFLink_GetAction(link);
    if (action == nullptr || FPDFAction_GetType(action) != PDFACTION_URI) {
        return failure<Reply>(Status::InvalidArgument, "not an external link");
    }
    const unsigned long needed = FPDFAction_GetURIPath(handle_, action, nullptr, 0);  // bytes, with the terminator
    if (needed <= 1 || needed > kMaxUriBytes + 1) {
        return failure<Reply>(Status::Unsupported, "the address is empty or too long");
    }
    std::string buffer(needed, '\0');
    if (FPDFAction_GetURIPath(handle_, action, buffer.data(), needed) != needed) {
        return failure<Reply>(Status::Unsupported, "the address could not be read");
    }
    buffer.resize(std::strlen(buffer.c_str()));
    if (buffer.empty() || !protocol::is_valid_text(buffer) || has_control_character(buffer)) {
        return failure<Reply>(Status::Unsupported, "the address is not plain text");
    }
    Reply reply;
    reply.uri = std::move(buffer);
    return {Status::Ok, std::move(reply), ""};
}

Result<protocol::GetPageBoxesReply> Document::page_boxes(std::uint32_t page) {
    using Reply = protocol::GetPageBoxesReply;
    if (handle_ == nullptr || page >= page_count_) {
        return failure<Reply>(Status::InvalidArgument, "page out of range");
    }
    PageCache* cached = cached_page(page);
    if (cached == nullptr) {
        return failure<Reply>(Status::Damaged, "page cannot be loaded");
    }
    // PDFium's own idea of the visible box (CropBox clipped to the MediaBox, inheritance applied) is what it draws, so it is the
    // CropBox reported here; the MediaBox is widened to hold it, so the broker's clipping can never disagree with the renderer.
    FS_RECTF visible{};
    if (FPDF_GetPageBoundingBox(cached->page, &visible) == 0) {
        visible = FS_RECTF{0.f, 792.f, 612.f, 0.f};
    }
    const double v_left = std::min<double>(visible.left, visible.right);
    const double v_right = std::max<double>(visible.left, visible.right);
    const double v_bottom = std::min<double>(visible.bottom, visible.top);
    const double v_top = std::max<double>(visible.bottom, visible.top);
    float m_left = 0;
    float m_bottom = 0;
    float m_right = 0;
    float m_top = 0;
    double media_left = v_left;
    double media_bottom = v_bottom;
    double media_right = v_right;
    double media_top = v_top;
    if (FPDFPage_GetMediaBox(cached->page, &m_left, &m_bottom, &m_right, &m_top) != 0) {
        media_left = std::min<double>(media_left, std::min(m_left, m_right));
        media_right = std::max<double>(media_right, std::max(m_left, m_right));
        media_bottom = std::min<double>(media_bottom, std::min(m_bottom, m_top));
        media_top = std::max<double>(media_top, std::max(m_bottom, m_top));
    }
    Reply reply;
    reply.media_left = clamp_coordinate(media_left);
    reply.media_bottom = clamp_coordinate(media_bottom);
    reply.media_right = clamp_coordinate(media_right);
    reply.media_top = clamp_coordinate(media_top);
    reply.has_crop_box = true;
    reply.crop_left = clamp_coordinate(v_left);
    reply.crop_bottom = clamp_coordinate(v_bottom);
    reply.crop_right = clamp_coordinate(v_right);
    reply.crop_top = clamp_coordinate(v_top);
    const int rotation = FPDFPage_GetRotation(cached->page);
    reply.rotation = rotation >= 0 && rotation <= 3 ? static_cast<std::uint16_t>(rotation) : std::uint16_t{0};
    return {Status::Ok, std::move(reply), ""};
}

Result<protocol::GetPageTextReply> Document::page_text(std::uint32_t page, std::uint32_t first, std::uint32_t count) {
    using Reply = protocol::GetPageTextReply;
    if (handle_ == nullptr || page >= page_count_ || count == 0 || count > 2048 || first > kMaxPageChars - 1) {
        return failure<Reply>(Status::InvalidArgument, "text request out of bounds");
    }
    PageCache* cached = cached_page(page);
    if (cached == nullptr) {
        return failure<Reply>(Status::Damaged, "page cannot be loaded");
    }
    FPDF_TEXTPAGE text = cached->text_page();
    if (text == nullptr) {
        return failure<Reply>(Status::Damaged, "text cannot be loaded");
    }
    const int counted = FPDFText_CountChars(text);
    if (counted < 0) {
        return failure<Reply>(Status::Damaged, "text cannot be counted");
    }
    Reply reply;
    reply.total = std::min(static_cast<std::uint32_t>(counted), kMaxPageChars);
    reply.truncated = static_cast<std::uint32_t>(counted) > kMaxPageChars;
    if (first > reply.total) {
        return failure<Reply>(Status::InvalidArgument, "window starts after the text");
    }
    const auto end = static_cast<std::uint32_t>(std::min<std::uint64_t>(std::uint64_t{first} + count, reply.total));
    reply.units.reserve(end - first);
    for (std::uint32_t i = first; i < end; ++i) {
        const unsigned int unicode = FPDFText_GetUnicode(text, static_cast<int>(i));
        // One entry per PDFium character keeps the numbering the search results use; a character beyond the BMP (which PDFium
        // reports as one unit on Windows) cannot be told apart from a lone surrogate, so both become U+FFFD.
        const bool plain = unicode <= 0xFFFF && !(unicode >= 0xD800 && unicode <= 0xDFFF);
        reply.units.push_back(plain ? static_cast<std::uint16_t>(unicode) : std::uint16_t{0xFFFD});
        FS_RECTF box{};
        if (FPDFText_GetLooseCharBox(text, static_cast<int>(i), &box) == 0) {
            double left = 0;
            double right = 0;
            double bottom = 0;
            double top = 0;
            if (FPDFText_GetCharBox(text, static_cast<int>(i), &left, &right, &bottom, &top) != 0) {
                box = FS_RECTF{static_cast<float>(left), static_cast<float>(top), static_cast<float>(right), static_cast<float>(bottom)};
            } else {
                box = FS_RECTF{};
            }
        }
        const float c = clamp_coordinate(box.left);
        const float d = clamp_coordinate(box.right);
        const float a = clamp_coordinate(box.bottom);
        const float b = clamp_coordinate(box.top);
        reply.lefts.push_back(std::min(c, d));
        reply.rights.push_back(std::max(c, d));
        reply.bottoms.push_back(std::min(a, b));
        reply.tops.push_back(std::max(a, b));
        std::uint16_t flags = 0;
        if (FPDFText_IsGenerated(text, static_cast<int>(i)) == 1) {
            flags |= 1;
        }
        if (FPDFText_IsHyphen(text, static_cast<int>(i)) == 1) {
            flags |= 2;
        }
        reply.flags.push_back(flags);
    }
    return {Status::Ok, std::move(reply), ""};
}

Result<protocol::SearchPageReply> Document::search_page(std::uint32_t page, std::string_view query, std::uint32_t flags,
                                                        const std::function<bool()>& expired) {
    using Reply = protocol::SearchPageReply;
    if (handle_ == nullptr || page >= page_count_ || query.empty() || query.size() > 512 || !protocol::is_valid_text(query) || (flags & ~3u) != 0) {
        return failure<Reply>(Status::InvalidArgument, "search request out of bounds");
    }
    PageCache* cached = cached_page(page);
    if (cached == nullptr) {
        return failure<Reply>(Status::Damaged, "page cannot be loaded");
    }
    FPDF_TEXTPAGE text = cached->text_page();
    if (text == nullptr) {
        return failure<Reply>(Status::Damaged, "text cannot be loaded");
    }
    const std::u16string wide = utf8_to_utf16(query);
    unsigned long options = 0;
    if ((flags & 1u) != 0) {
        options |= FPDF_MATCHCASE;
    }
    if ((flags & 2u) != 0) {
        options |= FPDF_MATCHWHOLEWORD;
    }
    FPDF_SCHHANDLE search = FPDFText_FindStart(text, reinterpret_cast<FPDF_WIDESTRING>(wide.c_str()), options, 0);
    if (search == nullptr) {
        return failure<Reply>(Status::Damaged, "the search could not start");
    }
    Reply reply;
    Status status = Status::Ok;
    std::uint32_t polled = 0;
    while (FPDFText_FindNext(search) != 0) {
        const int start = FPDFText_GetSchResultIndex(search);
        const int length = FPDFText_GetSchCount(search);
        if (start >= 0 && length >= 1 && static_cast<std::uint64_t>(start) + static_cast<std::uint64_t>(length) <= kMaxPageChars) {
            if (reply.starts.size() >= kMaxSearchMatches) {
                reply.truncated = true;
                break;
            }
            reply.starts.push_back(static_cast<std::uint32_t>(start));
            reply.lengths.push_back(static_cast<std::uint32_t>(length));
        }
        if (++polled % kSearchPollEvery == 0 && expired && expired()) {
            status = Status::Timeout;
            break;
        }
    }
    FPDFText_FindClose(search);
    if (status != Status::Ok) {
        return failure<Reply>(status, "the search ran out of time");
    }
    return {Status::Ok, std::move(reply), ""};
}

}  // namespace mikepdf::pdfium
