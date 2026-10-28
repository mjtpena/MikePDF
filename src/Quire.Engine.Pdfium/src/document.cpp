#include "quire/pdfium/document.h"

#include <algorithm>
#include <cmath>
#include <limits>
#include <string>
#include <utility>
#include <vector>

#include <fpdf_catalog.h>
#include <fpdf_doc.h>
#include <fpdf_edit.h>
#include <fpdf_javascript.h>
#include <fpdf_progressive.h>
#include <fpdf_signature.h>
#include <fpdfview.h>

namespace quire::pdfium {

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

Document::~Document() { reset(); }

Document::Document(Document&& other) noexcept
    : handle_(std::exchange(other.handle_, nullptr)), page_count_(std::exchange(other.page_count_, 0)) {}

Document& Document::operator=(Document&& other) noexcept {
    if (this != &other) {
        reset();
        handle_ = std::exchange(other.handle_, nullptr);
        page_count_ = std::exchange(other.page_count_, 0);
    }
    return *this;
}

void Document::reset() noexcept {
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

}  // namespace quire::pdfium
