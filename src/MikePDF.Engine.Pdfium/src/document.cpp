#include "mikepdf/pdfium/document.h"

#include "document_internal.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <string>
#include <unordered_set>
#include <utility>
#include <vector>

#include <fpdf_catalog.h>
#include <fpdf_doc.h>
#include <fpdf_edit.h>
#include <fpdf_formfill.h>
#include <fpdf_javascript.h>
#include <fpdf_progressive.h>
#include <fpdf_signature.h>
#include <fpdf_text.h>
#include <fpdf_transformpage.h>
#include <fpdfview.h>

namespace mikepdf::pdfium {

using protocol::Result;
using protocol::Status;
using namespace detail;

namespace {

constexpr std::uint32_t kMaxPages = 10'000'000;      // schema limit of pageCount
constexpr float kMinPageSide = 1.0f;
constexpr float kMaxPageSide = 14400.0f;              // schema limit of GetPageSizesReply
constexpr std::size_t kMaxMetaStringBytes = 1024;     // schema limit of the info strings
constexpr std::size_t kMaxDateBytes = 64;             // schema limit of the two dates in GetDocumentDetailsReply
constexpr std::size_t kMaxFonts = 64;
constexpr std::size_t kMaxFontNameBytes = 128;
constexpr int kMaxFormDepth = 4;                      // forms inside forms: a hostile file can nest them far deeper than any real one
constexpr std::uint32_t kMaxObjectsPerPage = 50'000;  // page objects visited per page, nested ones included
constexpr std::uint32_t kExpiredPollEvery = 256;
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

// The kinds of a destination (GetOutlineReply.kinds).
constexpr std::uint16_t kDestNone = 0;
constexpr std::uint16_t kDestPage = 1;
constexpr std::uint16_t kDestPageY = 2;
constexpr std::uint16_t kDestPageXY = 3;
constexpr std::uint16_t kDestExternal = 4;
constexpr std::uint16_t kDestOther = 5;

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

std::string meta_text(FPDF_DOCUMENT doc, const char* tag, std::size_t max_bytes = kMaxMetaStringBytes) {
    const unsigned long needed = FPDF_GetMetaText(doc, tag, nullptr, 0);  // bytes, including the 2-byte terminator
    if (needed <= 2 || needed > kMaxMetaRawBytes || needed % 2 != 0) {
        return {};
    }
    std::vector<char16_t> buffer(needed / 2);
    if (FPDF_GetMetaText(doc, tag, buffer.data(), needed) != needed) {
        return {};
    }
    return utf16_to_utf8(buffer, max_bytes);
}

float clamp_page_side(float value) {
    if (!std::isfinite(value) || value < kMinPageSide) {
        return kMinPageSide;
    }
    return std::min(value, kMaxPageSide);
}

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

// Draws a page into a bitmap in slices, asking `interrupt` between them (PDFium polls it about every 100 drawing operations). Ok, Cancelled / Timeout (the check
// stopped it) or PartiallyRendered (PDFium gave up part-way).
Status render_progressive(FPDF_BITMAP bitmap, FPDF_PAGE page, int start_x, int start_y, int size_x, int size_y, int rotate, int flags,
                          const InterruptCheck& interrupt) {
    PauseState state{&interrupt, RenderInterrupt::None};
    IFSDK_PAUSE pause{};
    pause.version = 1;
    pause.NeedToPauseNow = NeedToPauseNow;
    pause.user = &state;

    int progress = FPDF_RenderPageBitmap_Start(bitmap, page, start_x, start_y, size_x, size_y, rotate, flags, &pause);
    while (progress == FPDF_RENDER_TOBECONTINUED && state.reason == RenderInterrupt::None) {
        progress = FPDF_RenderPage_Continue(page, &pause);
    }
    FPDF_RenderPage_Close(page);

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

// ---- fonts (GetDocumentDetails) ------------------------------------------------------------------------------------------------------

// A font's base name as printable ASCII: names are PDF names, whose bytes can be anything, and the schema wants text.
std::string font_name(FPDF_FONT font) {
    const std::size_t needed = FPDFFont_GetBaseFontName(font, nullptr, 0);  // bytes, with the terminator
    if (needed <= 1 || needed > 4096) {
        return {};
    }
    std::string buffer(needed, '\0');
    if (FPDFFont_GetBaseFontName(font, buffer.data(), needed) != needed) {
        return {};
    }
    std::string name;
    for (std::size_t i = 0; i + 1 < needed && name.size() < kMaxFontNameBytes; ++i) {
        const auto c = static_cast<unsigned char>(buffer[i]);
        name += (c >= 0x20 && c < 0x7F) ? static_cast<char>(c) : '?';
    }
    return name;
}

struct FontCollector {
    std::vector<std::string> names;
    std::vector<std::uint16_t> flags;
    bool overflowed{false};  // more fonts than the reply holds

    void add(FPDF_FONT font) {
        if (font == nullptr) {
            return;
        }
        std::string name = font_name(font);
        if (name.empty()) {
            return;
        }
        const std::uint16_t embedded = FPDFFont_GetIsEmbedded(font) == 1 ? 1 : 0;
        for (std::size_t i = 0; i < names.size(); ++i) {
            if (names[i] == name) {
                flags[i] |= embedded;
                return;
            }
        }
        if (names.size() >= kMaxFonts) {
            overflowed = true;
            return;
        }
        names.push_back(std::move(name));
        flags.push_back(embedded);
    }
};

// Walks the objects of a page (or of a form inside it) for text. False when it stopped early: out of time, or out of objects.
bool collect_fonts(FPDF_PAGE page, FPDF_PAGEOBJECT form, int depth, std::uint32_t& visited, FontCollector& fonts, const std::function<bool()>& expired) {
    const int count = form == nullptr ? FPDFPage_CountObjects(page) : FPDFFormObj_CountObjects(form);
    for (int i = 0; i < count; ++i) {
        if (++visited > kMaxObjectsPerPage || (visited % kExpiredPollEvery == 0 && expired && expired())) {
            return false;
        }
        FPDF_PAGEOBJECT object = form == nullptr ? FPDFPage_GetObject(page, i) : FPDFFormObj_GetObject(form, static_cast<unsigned long>(i));
        if (object == nullptr) {
            continue;
        }
        const int type = FPDFPageObj_GetType(object);
        if (type == FPDF_PAGEOBJ_TEXT) {
            fonts.add(FPDFTextObj_GetFont(object));
        } else if (type == FPDF_PAGEOBJ_FORM && depth < kMaxFormDepth && !collect_fonts(page, object, depth + 1, visited, fonts, expired)) {
            return false;
        }
    }

    return true;
}

}  // namespace

void Document::PageCacheDeleter::operator()(PageCache* cache) const noexcept { delete cache; }

void Document::FormFillDeleter::operator()(FormFill* form) const noexcept { delete form; }

Document::~Document() { reset(); }

Document::Document(Document&& other) noexcept
    : handle_(std::exchange(other.handle_, nullptr)),
      page_count_(std::exchange(other.page_count_, 0)),
      cache_(std::move(other.cache_)),
      form_(std::move(other.form_)),
      mirror_(std::move(other.mirror_)),
      widgets_(std::move(other.widgets_)),
      widgets_scanned_pages_(std::exchange(other.widgets_scanned_pages_, 0)),
      widgets_ready_(std::exchange(other.widgets_ready_, false)),
      quads_(std::move(other.quads_)) {}

Document& Document::operator=(Document&& other) noexcept {
    if (this != &other) {
        reset();
        handle_ = std::exchange(other.handle_, nullptr);
        page_count_ = std::exchange(other.page_count_, 0);
        cache_ = std::move(other.cache_);
        form_ = std::move(other.form_);
        mirror_ = std::move(other.mirror_);
        widgets_ = std::move(other.widgets_);
        widgets_scanned_pages_ = std::exchange(other.widgets_scanned_pages_, 0);
        widgets_ready_ = std::exchange(other.widgets_ready_, false);
        quads_ = std::move(other.quads_);
    }
    return *this;
}

void Document::reset() noexcept {
    cache_.reset();  // before the document: the page and its text belong to it
    form_.reset();   // the form-fill environment too, and before the document it was made for
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

namespace {

constexpr std::uint64_t kMaxAnchoredBytes = std::uint64_t{144} << 20;  // a whole A4 page at 600 dpi is 133 MiB

struct Affine {
    float a{1}, b{0}, c{0}, d{1}, e{0}, f{0};
};

// The matrix of a page object, in the space of the form it is in. `inner` first, then `outer` (row vectors, as PDF does).
Affine compose(const Affine& inner, const Affine& outer) {
    return {inner.a * outer.a + inner.b * outer.c, inner.a * outer.b + inner.b * outer.d, inner.c * outer.a + inner.d * outer.c, inner.c * outer.b + inner.d * outer.d,
            inner.e * outer.a + inner.f * outer.c + outer.e, inner.e * outer.b + inner.f * outer.d + outer.f};
}

// True when an image under `matrix` is drawn by PDFium's image transformer for good: not axis-aligned and not a quarter turn (those two it stretches or flips exactly).
bool is_skewed(const Affine& m) {
    const float n = std::max({std::fabs(m.a), std::fabs(m.b), std::fabs(m.c), std::fabs(m.d)});
    if (!(n > 0.0f) || !std::isfinite(n)) {
        return false;
    }
    const float eps = 1e-4f * n;
    const bool axis_aligned = std::fabs(m.b) <= eps && std::fabs(m.c) <= eps;
    const bool quarter_turn = std::fabs(m.a) <= eps && std::fabs(m.d) <= eps;
    return !axis_aligned && !quarter_turn;
}

// Walks the page objects (the forms in them too, as deep as collect_fonts goes) for a skewed image. Stops, answering no, after kMaxObjectsPerPage objects.
bool find_skewed_image(FPDF_PAGE page, FPDF_PAGEOBJECT form, const Affine& outer, int depth, std::uint32_t& visited) {
    const int count = form == nullptr ? FPDFPage_CountObjects(page) : FPDFFormObj_CountObjects(form);
    for (int i = 0; i < count; ++i) {
        if (++visited > kMaxObjectsPerPage) {
            return false;
        }
        FPDF_PAGEOBJECT object = form == nullptr ? FPDFPage_GetObject(page, i) : FPDFFormObj_GetObject(form, static_cast<unsigned long>(i));
        if (object == nullptr) {
            continue;
        }
        const int type = FPDFPageObj_GetType(object);
        if (type != FPDF_PAGEOBJ_IMAGE && type != FPDF_PAGEOBJ_FORM) {
            continue;
        }
        FS_MATRIX raw{1, 0, 0, 1, 0, 0};
        if (FPDFPageObj_GetMatrix(object, &raw) == 0) {
            continue;
        }
        const Affine total = compose({raw.a, raw.b, raw.c, raw.d, raw.e, raw.f}, outer);
        if (type == FPDF_PAGEOBJ_IMAGE) {
            if (is_skewed(total)) {
                return true;
            }
        } else if (depth < kMaxFormDepth && find_skewed_image(page, object, total, depth + 1, visited)) {
            return true;
        }
    }
    return false;
}

}  // namespace

bool Document::has_skewed_image(FPDF_PAGE page, std::uint32_t index) {
    if (skew_page_ != index) {
        std::uint32_t visited = 0;
        skew_value_ = find_skewed_image(page, nullptr, Affine{}, 0, visited);
        skew_page_ = index;
    }
    return skew_value_;
}

Status Document::render_window(FPDF_PAGE page, std::uint32_t index, int x, int y, int width, int height, int size_x, int size_y, int rotate, FormDraw forms,
                               int flags, std::uint8_t* destination, std::uint32_t stride, const InterruptCheck& interrupt) {
    const bool away_from_corner = x > 0 || y > 0;
    if (away_from_corner) {
        const int origin_x = x > 0 ? 0 : x;
        const int origin_y = y > 0 ? 0 : y;
        constexpr int kMargin = 8;  // the transformer also rounds at the far edges of its clip: keep the window's own edges away from them
        const int margin_x = std::int64_t{x} + width < size_x ? kMargin : 0;  // but never past the page's own edge: the whole page ends there
        const int margin_y = std::int64_t{y} + height < size_y ? kMargin : 0;
        const std::uint64_t scratch_width = static_cast<std::uint64_t>(std::int64_t{x} + width + margin_x - origin_x);
        const std::uint64_t scratch_height = static_cast<std::uint64_t>(std::int64_t{y} + height + margin_y - origin_y);
        const std::uint64_t bytes = scratch_width * scratch_height * 4;
        if (scratch_width <= static_cast<std::uint64_t>(std::numeric_limits<int>::max() / 4) && bytes <= kMaxAnchoredBytes && has_skewed_image(page, index)) {
            std::unique_ptr<std::uint8_t[]> scratch(new (std::nothrow) std::uint8_t[bytes]);
            if (scratch != nullptr) {
                BitmapHandle bitmap{FPDFBitmap_CreateEx(static_cast<int>(scratch_width), static_cast<int>(scratch_height), FPDFBitmap_BGRA, scratch.get(),
                                                        static_cast<int>(scratch_width * 4))};
                if (bitmap.bitmap == nullptr) {
                    return Status::InvalidArgument;
                }
                FPDFBitmap_FillRect(bitmap.bitmap, 0, 0, static_cast<int>(scratch_width), static_cast<int>(scratch_height), 0xFFFFFFFFu);
                const Status status = render_page(bitmap.bitmap, page, -origin_x, -origin_y, size_x, size_y, rotate, forms, flags, interrupt);
                if (status == Status::Ok || status == Status::PartiallyRendered) {
                    const std::uint8_t* from = scratch.get() + (static_cast<std::uint64_t>(y - origin_y) * scratch_width + static_cast<std::uint64_t>(x - origin_x)) * 4;
                    for (int row = 0; row < height; ++row) {
                        std::memcpy(destination + static_cast<std::uint64_t>(row) * stride, from + static_cast<std::uint64_t>(row) * scratch_width * 4,
                                    static_cast<std::size_t>(width) * 4);
                    }
                }
                return status;
            }
        }
    }

    BitmapHandle bitmap{FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRA, destination, static_cast<int>(stride))};
    if (bitmap.bitmap == nullptr) {
        return Status::InvalidArgument;
    }
    FPDFBitmap_FillRect(bitmap.bitmap, 0, 0, width, height, 0xFFFFFFFFu);
    return render_page(bitmap.bitmap, page, -x, -y, size_x, size_y, rotate, forms, flags, interrupt);
}

namespace {

constexpr std::uint32_t kMaxImagesAnalysed = 32;
constexpr std::uint64_t kMaxAnalysedImagePixels = std::uint64_t{64} << 20;

struct ImageRef {
    FPDF_PAGEOBJECT object;
    Affine matrix;
};

// Every image object of the page, forms included, with its matrix in page space. Stops after kMaxObjectsPerPage objects.
void collect_images(FPDF_PAGE page, FPDF_PAGEOBJECT form, const Affine& outer, int depth, std::uint32_t& visited, std::vector<ImageRef>& out) {
    const int count = form == nullptr ? FPDFPage_CountObjects(page) : FPDFFormObj_CountObjects(form);
    for (int i = 0; i < count; ++i) {
        if (++visited > kMaxObjectsPerPage) {
            return;
        }
        FPDF_PAGEOBJECT object = form == nullptr ? FPDFPage_GetObject(page, i) : FPDFFormObj_GetObject(form, static_cast<unsigned long>(i));
        if (object == nullptr) {
            continue;
        }
        const int type = FPDFPageObj_GetType(object);
        if (type != FPDF_PAGEOBJ_IMAGE && type != FPDF_PAGEOBJ_FORM) {
            continue;
        }
        FS_MATRIX raw{1, 0, 0, 1, 0, 0};
        if (FPDFPageObj_GetMatrix(object, &raw) == 0) {
            continue;
        }
        const Affine total = compose({raw.a, raw.b, raw.c, raw.d, raw.e, raw.f}, outer);
        if (type == FPDF_PAGEOBJ_IMAGE) {
            out.push_back({object, total});
        } else if (depth < kMaxFormDepth) {
            collect_images(page, object, total, depth + 1, visited, out);
        }
    }
}

// A colour photograph, as opposed to a scan, line art or a diagram, which are mostly white paper: more than one bit a pixel and fewer than 55% near-white pixels in a sample.
bool looks_like_photo(FPDF_PAGE page, FPDF_PAGEOBJECT image) {
    FPDF_IMAGEOBJ_METADATA meta{};
    if (FPDFImageObj_GetImageMetadata(image, page, &meta) == 0 || meta.width < 16 || meta.height < 16 || meta.bits_per_pixel <= 1 ||
        std::uint64_t{meta.width} * meta.height > kMaxAnalysedImagePixels) {
        return false;
    }
    BitmapHandle bitmap{FPDFImageObj_GetBitmap(image)};
    if (bitmap.bitmap == nullptr) {
        return false;
    }
    const int width = FPDFBitmap_GetWidth(bitmap.bitmap);
    const int height = FPDFBitmap_GetHeight(bitmap.bitmap);
    const int stride = FPDFBitmap_GetStride(bitmap.bitmap);
    const int format = FPDFBitmap_GetFormat(bitmap.bitmap);
    const auto* pixels = static_cast<const std::uint8_t*>(FPDFBitmap_GetBuffer(bitmap.bitmap));
    const int bytes = format == FPDFBitmap_Gray ? 1 : format == FPDFBitmap_BGR ? 3 : (format == FPDFBitmap_BGRx || format == FPDFBitmap_BGRA) ? 4 : 0;
    if (pixels == nullptr || bytes == 0 || width < 1 || height < 1 || stride < width * bytes) {
        return false;
    }
    constexpr int kGrid = 48;
    int sampled = 0;
    int white = 0;
    for (int gy = 0; gy < kGrid; ++gy) {
        const int y = std::min(height - 1, (gy * height + height / 2) / kGrid);
        for (int gx = 0; gx < kGrid; ++gx) {
            const int x = std::min(width - 1, (gx * width + width / 2) / kGrid);
            const std::uint8_t* p = pixels + static_cast<std::size_t>(y) * static_cast<std::size_t>(stride) + static_cast<std::size_t>(x) * static_cast<std::size_t>(bytes);
            const bool transparent = format == FPDFBitmap_BGRA && p[3] < 16;
            const bool near_white = bytes == 1 ? p[0] >= 235 : (p[0] >= 235 && p[1] >= 235 && p[2] >= 235);
            white += (transparent || near_white) ? 1 : 0;
            ++sampled;
        }
    }
    return white * 100 < sampled * 55;
}

// The app's PageColourMatrix.Inverted (src/MikePDF.Core/Colour/PageColour.cs): CSS invert(1) hue-rotate(180deg), eased into [0.07, 0.95]. Same constants, same clamp.
void darken_pixels(std::uint8_t* pixels, std::uint32_t stride, int width, int height, int tile_x, int tile_y, const std::vector<std::array<int, 4>>& keep) {
    constexpr float kLow = 0.07f;
    constexpr float kHigh = 0.95f;
    constexpr float kHue[3][3] = {{-0.574f, 1.430f, 0.144f}, {0.426f, 0.430f, 0.144f}, {0.426f, 1.430f, -0.856f}};
    // Lookup for the channel sums is not worth it: a tile is at most a million pixels. Photographs are skipped by rectangle.
    for (int y = 0; y < height; ++y) {
        std::uint8_t* row = pixels + static_cast<std::size_t>(y) * stride;
        const int page_y = tile_y + y;
        for (int x = 0; x < width; ++x) {
            const int page_x = tile_x + x;
            bool kept = false;
            for (const auto& r : keep) {
                if (page_x >= r[0] && page_x < r[2] && page_y >= r[1] && page_y < r[3]) {
                    kept = true;
                    break;
                }
            }
            if (kept) {
                continue;
            }
            std::uint8_t* p = row + static_cast<std::size_t>(x) * 4;
            const float b = p[0] / 255.0f;
            const float g = p[1] / 255.0f;
            const float r = p[2] / 255.0f;
            for (int o = 0; o < 3; ++o) {
                const float v = -(kHigh - kLow) * (kHue[o][0] * r + kHue[o][1] * g + kHue[o][2] * b) + kLow + (kHigh - kLow);
                p[2 - o] = static_cast<std::uint8_t>(std::lround(std::clamp(v, 0.0f, 1.0f) * 255.0f));
            }
        }
    }
}

}  // namespace

const std::vector<Document::PhotoRect>& Document::photo_rects(FPDF_PAGE page, std::uint32_t index) {
    if (photo_page_ != index) {
        photos_.clear();
        photo_page_ = index;
        std::vector<ImageRef> images;
        std::uint32_t visited = 0;
        collect_images(page, nullptr, Affine{}, 0, visited, images);
        const double page_area = double{FPDF_GetPageWidthF(page)} * double{FPDF_GetPageHeightF(page)};
        std::uint32_t analysed = 0;
        for (const ImageRef& image : images) {
            float left = std::numeric_limits<float>::max();
            float bottom = left;
            float right = std::numeric_limits<float>::lowest();
            float top = right;
            for (const auto& [u, v] : {std::pair{0.0f, 0.0f}, std::pair{1.0f, 0.0f}, std::pair{0.0f, 1.0f}, std::pair{1.0f, 1.0f}}) {
                const float x = image.matrix.a * u + image.matrix.c * v + image.matrix.e;
                const float y = image.matrix.b * u + image.matrix.d * v + image.matrix.f;
                left = std::min(left, x);
                right = std::max(right, x);
                bottom = std::min(bottom, y);
                top = std::max(top, y);
            }
            if (!std::isfinite(left) || !std::isfinite(right) || !std::isfinite(bottom) || !std::isfinite(top)) {
                continue;
            }
            const double area = double{right - left} * double{top - bottom};
            if (page_area > 0 && area >= 0.9 * page_area) {
                continue;  // a scan of a page: text, so it is recoloured like text
            }
            if (analysed++ >= kMaxImagesAnalysed) {
                break;
            }
            if (looks_like_photo(page, image.object)) {
                photos_.push_back({left, bottom, right, top});
            }
        }
    }
    return photos_;
}

Document::FormFill* Document::form_fill() {
    if (form_ == nullptr && handle_ != nullptr) {
        auto form = std::make_unique<FormFill>();
        form->info.version = 1;  // the stable interface only; every callback stays null
        form->handle = FPDFDOC_InitFormFillEnvironment(handle_, &form->info);
        if (form->handle == nullptr) {
            return nullptr;  // not remembered: the next request tries again
        }
        form_ = std::unique_ptr<FormFill, FormFillDeleter>(form.release());
    }
    return form_.get();
}

// Draws the page and, when asked, its widgets. A widget is drawn only by FPDF_FFLDraw, which always draws every widget (the FPDF_ANNOT flag only decides about the
// other interactive annotations, which a still image has no use for); it has to follow the page's own drawing and uses the same transform, so the widgets land on
// the pixels the page content does, in every tile and every band. FFLDraw cannot be paused, so it runs only for a page that was drawn to the end.
Status Document::render_page(FPDF_BITMAP bitmap, FPDF_PAGE page, int start_x, int start_y, int size_x, int size_y, int rotate, FormDraw forms, int flags,
                             const InterruptCheck& interrupt) {
    FormFill* form = nullptr;
    if (forms != FormDraw::None) {
        form = form_fill();
        if (form == nullptr) {
            return Status::Unsupported;
        }
        FORM_OnAfterLoadPage(page, form->handle);
    }
    const Status status = render_progressive(bitmap, page, start_x, start_y, size_x, size_y, rotate, flags, interrupt);
    if (form != nullptr) {
        if (status == Status::Ok || status == Status::PartiallyRendered) {
            if (forms == FormDraw::Highlighted) {
                // PDFium's own tint over the fillable widgets: a light blue, translucent. It is switched on for this draw only, so no other render of the document ever shows it.
                FPDF_SetFormFieldHighlightColor(form->handle, FPDF_FORMFIELD_UNKNOWN, 0x00A8C8FFul);
                FPDF_SetFormFieldHighlightAlpha(form->handle, 100);
            }
            FPDF_FFLDraw(form->handle, bitmap, page, start_x, start_y, size_x, size_y, rotate, flags);
            if (forms == FormDraw::Highlighted) {
                FPDF_RemoveFormFieldHighlight(form->handle);
            }
        }
        FORM_OnBeforeClosePage(page, form->handle);  // before the caller closes the page
    }
    return status;
}

Status Document::render_tile(const TileRequest& request, std::span<std::uint8_t> destination, std::uint32_t stride,
                             const InterruptCheck& interrupt) {
    if (handle_ == nullptr || request.page >= page_count_ || !std::isfinite(request.scale) || request.scale <= 0.0f ||
        request.rotation > 3 || request.tile_width == 0 || request.tile_width > kMaxTileSide || request.tile_height == 0 ||
        request.tile_height > kMaxTileSide ||
        (request.flags & ~(kRenderAnnotations | kRenderFormFields | kRenderDarkPage | kRenderFieldHighlight)) != 0 ||
        ((request.flags & kRenderFieldHighlight) != 0 && (request.flags & kRenderFormFields) == 0)) {
        return Status::InvalidArgument;
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

    const int flags = (request.flags & kRenderAnnotations) != 0 ? FPDF_ANNOT : 0;
    const FormDraw forms = (request.flags & kRenderFormFields) == 0 ? FormDraw::None : (request.flags & kRenderFieldHighlight) != 0 ? FormDraw::Highlighted : FormDraw::Fields;
    const Status status = render_window(page.page, request.page, request.tile_x, request.tile_y, static_cast<int>(request.tile_width),
                                        static_cast<int>(request.tile_height), static_cast<int>(device_width), static_cast<int>(device_height), request.rotation,
                                        forms, flags, destination.data(), stride, interrupt);
    if ((request.flags & kRenderDarkPage) != 0 && (status == Status::Ok || status == Status::PartiallyRendered)) {
        // The photographs' rectangles in the device pixels of the whole page, from the same transform the page was drawn with.
        std::vector<std::array<int, 4>> keep;
        for (const PhotoRect& photo : photo_rects(page.page, request.page)) {
            int left = std::numeric_limits<int>::max();
            int top = left;
            int right = std::numeric_limits<int>::min();
            int bottom = right;
            for (const auto& [px, py] : {std::pair{photo.left, photo.bottom}, std::pair{photo.right, photo.bottom}, std::pair{photo.left, photo.top},
                                         std::pair{photo.right, photo.top}}) {
                int dx = 0;
                int dy = 0;
                FPDF_PageToDevice(page.page, 0, 0, static_cast<int>(device_width), static_cast<int>(device_height), request.rotation, px, py, &dx, &dy);
                left = std::min(left, dx);
                right = std::max(right, dx);
                top = std::min(top, dy);
                bottom = std::max(bottom, dy);
            }
            keep.push_back({left, top, right + 1, bottom + 1});
        }
        darken_pixels(destination.data(), stride, static_cast<int>(request.tile_width), static_cast<int>(request.tile_height), request.tile_x, request.tile_y, keep);
    }
    return status;
}

Status Document::render_band(const BandRequest& request, std::span<std::uint8_t> destination, std::uint32_t stride,
                             const InterruptCheck& interrupt) {
    constexpr std::uint32_t kMaxBandPageSide = 65536;
    constexpr std::uint32_t kMaxBandHeight = 16384;
    constexpr std::uint32_t kKnownFlags = kRenderAnnotations | kRenderFormFields | kRenderPrinting;
    if (handle_ == nullptr || request.page >= page_count_ || request.rotation > 3 || request.page_width_px == 0 ||
        request.page_width_px > kMaxBandPageSide || request.page_height_px == 0 || request.page_height_px > kMaxBandPageSide ||
        request.band_height == 0 || request.band_height > kMaxBandHeight ||
        std::uint64_t{request.band_top} + request.band_height > request.page_height_px || (request.flags & ~kKnownFlags) != 0) {
        return Status::InvalidArgument;
    }
    const std::uint64_t row_bytes = std::uint64_t{request.page_width_px} * 4;
    if (stride != row_bytes || stride > static_cast<std::uint32_t>(std::numeric_limits<int>::max()) ||
        std::uint64_t{stride} * request.band_height > destination.size()) {
        return Status::InvalidArgument;
    }

    PageHandle page(handle_, static_cast<int>(request.page));
    if (page.page == nullptr) {
        return Status::Damaged;
    }
    int flags = (request.flags & kRenderAnnotations) != 0 ? FPDF_ANNOT : 0;
    if ((request.flags & kRenderPrinting) != 0) {
        flags |= FPDF_PRINTING;
    }
    return render_window(page.page, request.page, 0, static_cast<int>(request.band_top), static_cast<int>(request.page_width_px), static_cast<int>(request.band_height),
                         static_cast<int>(request.page_width_px), static_cast<int>(request.page_height_px), request.rotation,
                         (request.flags & kRenderFormFields) != 0 ? FormDraw::Fields : FormDraw::None, flags,
                         destination.data(), stride, interrupt);
}

// ---- schema 5: /ViewerPreferences -------------------------------------------------------------------------------------------

namespace {

// The value of a name entry of /ViewerPreferences ("R2L"), empty when the entry is absent or not a name. Names longer than 32 bytes are not any we look for.
std::string viewer_pref_name(FPDF_DOCUMENT doc, const char* key) {
    char buffer[33]{};
    const unsigned long needed = FPDF_VIEWERREF_GetName(doc, key, nullptr, 0);
    if (needed <= 1 || needed > sizeof(buffer) || FPDF_VIEWERREF_GetName(doc, key, buffer, sizeof(buffer)) != needed) {
        return {};
    }
    return std::string(buffer, needed - 1);
}

}  // namespace

Result<protocol::GetViewerPreferencesReply> Document::viewer_preferences() {
    constexpr std::size_t kMaxRanges = 64;
    constexpr int kMaxCopies = 99;
    if (handle_ == nullptr) {
        return failure<protocol::GetViewerPreferencesReply>(Status::InvalidArgument, "no document");
    }
    protocol::GetViewerPreferencesReply reply;

    const std::string direction = viewer_pref_name(handle_, "Direction");
    reply.direction = direction == "L2R" ? 0 : direction == "R2L" ? 1 : 2;

    // GetPrintScaling cannot tell an entry that says AppDefault from none at all; the name can.
    const std::string scaling = viewer_pref_name(handle_, "PrintScaling");
    reply.print_scaling = scaling == "None" ? 1 : scaling == "AppDefault" ? 2 : 0;

    // PDFium answers 1 for an absent entry, so 1 is "nothing asked for" here too; an absurd value is the most the dialog may take, never more.
    const int copies = FPDF_VIEWERREF_GetNumCopies(handle_);
    reply.num_copies = copies >= 2 ? static_cast<std::uint16_t>(std::min(copies, kMaxCopies)) : std::uint16_t{0};

    const int duplex = static_cast<int>(FPDF_VIEWERREF_GetDuplex(handle_));
    reply.duplex = duplex >= 1 && duplex <= 3 ? static_cast<std::uint16_t>(duplex) : std::uint16_t{0};

    // /PrintPageRange counts pages from 1 (ISO 32000-1, table 150); the reply counts from 0, and a pair that does not describe pages of this document is dropped.
    if (const FPDF_PAGERANGE range = FPDF_VIEWERREF_GetPrintPageRange(handle_); range != nullptr) {
        const std::size_t count = std::min<std::size_t>(FPDF_VIEWERREF_GetPrintPageRangeCount(range), kMaxRanges * 2);
        for (std::size_t i = 0; i + 1 < count && reply.range_firsts.size() < kMaxRanges; i += 2) {
            const int first = FPDF_VIEWERREF_GetPrintPageRangeElement(range, i);
            const int last = FPDF_VIEWERREF_GetPrintPageRangeElement(range, i + 1);
            if (first >= 1 && last >= first && static_cast<std::uint32_t>(last) <= page_count_) {
                reply.range_firsts.push_back(static_cast<std::uint32_t>(first) - 1);
                reply.range_lasts.push_back(static_cast<std::uint32_t>(last) - 1);
            }
        }
    }
    return {Status::Ok, std::move(reply), ""};
}

// ---- reading services (WP-S2) ----------------------------------------------------------------------------------------------

namespace {

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
    // PDFium's FPDFLink_GetDest looks through the link's action as well, and resolves a go-to in ANOTHER file (GoToR, GoToE) against this document's
    // own names: a link to "alpha" in other.pdf would land on this file's "alpha". Those actions name a place MikePDF cannot show, so they stay actions.
    if (FPDF_ACTION action = FPDFLink_GetAction(link); action != nullptr) {
        const auto type = FPDFAction_GetType(action);
        if (type == PDFACTION_REMOTEGOTO || type == PDFACTION_EMBEDDEDGOTO) {
            return describe_action(doc, action, page_count);
        }
    }
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

Result<protocol::GetDocumentDetailsReply> Document::details(std::uint32_t max_font_pages, const std::function<bool()>& expired) {
    using Reply = protocol::GetDocumentDetailsReply;
    if (handle_ == nullptr) {
        return failure<Reply>(Status::InvalidArgument, "no document");
    }
    Reply reply;
    reply.creation_date = meta_text(handle_, "CreationDate", kMaxDateBytes);
    reply.modification_date = meta_text(handle_, "ModDate", kMaxDateBytes);
    const int revision = FPDF_GetSecurityHandlerRevision(handle_);
    reply.security_revision = revision < 0 ? 0 : static_cast<std::uint32_t>(std::min(revision, 255));
    reply.user_permissions = static_cast<std::uint32_t>(FPDF_GetDocUserPermissions(handle_));
    const int form = FPDF_GetFormType(handle_);
    reply.form_type = form < 0 ? 0 : static_cast<std::uint32_t>(std::min(form, 3));

    FontCollector fonts;
    const std::uint32_t pages = std::min(page_count_, max_font_pages);
    bool complete = true;
    std::uint32_t scanned = 0;
    for (; scanned < pages; ++scanned) {
        if (expired && expired()) {
            complete = false;
            break;
        }
        PageHandle page(handle_, static_cast<int>(scanned));
        if (page.page == nullptr) {
            continue;  // a page that cannot be loaded has no fonts to list; the page itself reports its damage when drawn
        }
        std::uint32_t visited = 0;
        if (!collect_fonts(page.page, nullptr, 0, visited, fonts, expired)) {
            complete = false;
            break;
        }
    }

    reply.fonts_complete = complete && !fonts.overflowed && scanned >= page_count_;
    reply.font_pages_scanned = scanned;
    reply.font_names = std::move(fonts.names);
    reply.font_flags = std::move(fonts.flags);
    return {Status::Ok, std::move(reply), ""};
}

}  // namespace mikepdf::pdfium
