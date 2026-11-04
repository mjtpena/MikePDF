// The annotation read side of the engine (schema 7, docs/features/annotation-core.md 11), the display mirror (SetAnnotationHidden) and the engine's own save (SaveDocument).
// Every PDFium call here is Experimental (fpdf_annot.h) and listed in EXPERIMENTAL.md. An annotation is named by (page, index): its position in the page's /Annots array.
#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <memory>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include <fpdf_edit.h>
#include <fpdf_save.h>

#include "document_internal.h"

namespace mikepdf::pdfium {

using protocol::Result;
using protocol::Status;
using namespace detail;

namespace {

constexpr std::uint32_t kMaxAnnotationsPerPage = 100'000;  // the schema's limit of a page's entries
constexpr std::uint16_t kMaxSubtype = 28;                   // FPDF_ANNOT_REDACT
constexpr std::size_t kMaxQuads = 64;                       // GetAnnotationReply.quadPoints holds 512 numbers
constexpr std::size_t kMaxVertexNumbers = 2048;
constexpr unsigned long kMaxVertexPointsRead = 262'144;     // a polygon with more points than this is reported as truncated, empty
constexpr std::uint32_t kMaxStrokes = 100'000;
constexpr std::uint64_t kMaxInkPoints = 10'000'000;
constexpr unsigned long kMaxStrokePointsRead = 1'000'000;   // GetInkStroke copies at most this many points of one stroke out of PDFium
constexpr unsigned long kSnippetRawBytes = 1u << 20;        // a /Contents larger than this reads as empty in a list
constexpr unsigned long kContentsRawBytes = 4u << 20;       // and larger than this in GetAnnotation
constexpr std::uint32_t kNoParent = 0xFFFFFFFFu;

std::uint64_t mirror_key(std::uint32_t page, std::uint32_t index) { return (std::uint64_t{page} << 32) | index; }

struct Rect {
    float left{0}, bottom{0}, right{0}, top{0};
};

// The rectangle normalised (left <= right, bottom <= top) and clamped to the schema range; an annotation PDFium cannot give a rectangle for has an empty one.
Rect annot_rect(FPDF_ANNOTATION annot) {
    FS_RECTF raw{};
    Rect out;
    if (FPDFAnnot_GetRect(annot, &raw) == 0) {
        return out;
    }
    const float a = clamp_coordinate(raw.left);
    const float b = clamp_coordinate(raw.right);
    const float c = clamp_coordinate(raw.bottom);
    const float d = clamp_coordinate(raw.top);
    out.left = std::min(a, b);
    out.right = std::max(a, b);
    out.bottom = std::min(c, d);
    out.top = std::max(c, d);
    return out;
}

// 0xAARRGGBB from /C (or /IC), with the opacity /CA as alpha; 0 when the entry is absent. PDFium answers black (or yellow for a highlight) for an absent entry, so the key is looked at first.
std::uint32_t annot_colour(FPDF_ANNOTATION annot, const char* key, FPDFANNOT_COLORTYPE type) {
    if (FPDFAnnot_HasKey(annot, key) == 0) {
        return 0;
    }
    unsigned int r = 0;
    unsigned int g = 0;
    unsigned int b = 0;
    unsigned int a = 255;
    if (FPDFAnnot_GetColor(annot, type, &r, &g, &b, &a) == 0) {
        return 0;
    }
    a = std::clamp(a, 1u, 255u);  // 0 is "none"
    return (a << 24) | (std::min(r, 255u) << 16) | (std::min(g, 255u) << 8) | std::min(b, 255u);
}

// FPDFAnnot_GetColor gives up on an annotation that has an appearance stream (PDFium says the stream's own colours win), which is nearly every annotation a reader wrote. For GetAnnotation
// the colour is then taken from the stream: the first object that paints, filled or else stroked. This parses the stream, so it is not done for a list of 64.
std::uint32_t appearance_colour(FPDF_ANNOTATION annot) {
    constexpr int kObjectsLooked = 8;
    const int objects = std::min(FPDFAnnot_GetObjectCount(annot), kObjectsLooked);
    for (int i = 0; i < objects; ++i) {
        FPDF_PAGEOBJECT object = FPDFAnnot_GetObject(annot, i);
        if (object == nullptr) {
            continue;
        }
        unsigned int r = 0;
        unsigned int g = 0;
        unsigned int b = 0;
        unsigned int a = 0;
        const bool filled = FPDFPageObj_GetFillColor(object, &r, &g, &b, &a) != 0 && a > 0;
        if (filled || (FPDFPageObj_GetStrokeColor(object, &r, &g, &b, &a) != 0 && a > 0)) {
            return 0xFF000000u | (std::min(r, 255u) << 16) | (std::min(g, 255u) << 8) | std::min(b, 255u);
        }
    }
    return 0;
}

float annot_opacity(FPDF_ANNOTATION annot) {
    float value = 1.0f;
    if (FPDFAnnot_GetNumberValue(annot, "CA", &value) == 0 || !std::isfinite(value)) {
        return 1.0f;
    }
    return std::clamp(value, 0.0f, 1.0f);
}

// The index of the annotation `annot` replies to (/IRT), or -1. Never the annotation's own index and never one the page does not have.
std::int32_t reply_parent(FPDF_PAGE page, FPDF_ANNOTATION annot, std::uint32_t own_index, std::uint32_t total) {
    if (FPDFAnnot_HasKey(annot, "IRT") == 0) {
        return -1;
    }
    AnnotHandle linked(FPDFAnnot_GetLinkedAnnot(annot, "IRT"));
    if (linked.annot == nullptr) {
        return -1;
    }
    const int index = FPDFPage_GetAnnotIndex(page, linked.annot);
    if (index < 0 || static_cast<std::uint32_t>(index) >= total || static_cast<std::uint32_t>(index) == own_index) {
        return -1;
    }
    return index;
}

std::uint16_t clamp_subtype(int subtype) { return subtype < 0 || subtype > kMaxSubtype ? std::uint16_t{0} : static_cast<std::uint16_t>(subtype); }

}  // namespace

int Document::file_flags(std::uint32_t page, std::uint32_t index, int current) const {
    const auto it = mirror_.find(mirror_key(page, index));
    return it == mirror_.end() ? current : it->second.original;
}

// ---- reading -----------------------------------------------------------------------------------------------------------------------------

Result<protocol::ListAnnotationsReply> Document::list_annotations(std::uint32_t page, std::uint32_t first, std::uint32_t count) {
    using Reply = protocol::ListAnnotationsReply;
    if (handle_ == nullptr || page >= page_count_ || count == 0 || count > 64 || first > 99'999) {
        return failure<Reply>(Status::InvalidArgument, "annotation request out of bounds");
    }
    PageCache* cached = cached_page(page);
    if (cached == nullptr) {
        return failure<Reply>(Status::Damaged, "page cannot be loaded");
    }
    const int counted = FPDFPage_GetAnnotCount(cached->page);
    Reply reply;
    reply.total = counted < 0 ? 0 : std::min(static_cast<std::uint32_t>(counted), kMaxAnnotationsPerPage);
    const std::uint32_t end = static_cast<std::uint32_t>(std::min<std::uint64_t>(std::uint64_t{first} + count, reply.total));
    for (std::uint32_t i = first; i < end; ++i) {
        AnnotHandle annot(cached->page, static_cast<int>(i));
        if (annot.annot == nullptr) {  // an entry that is not a dictionary keeps its place in the list
            reply.subtypes.push_back(0);
            reply.flags.push_back(0);
            reply.lefts.push_back(0.f);
            reply.bottoms.push_back(0.f);
            reply.rights.push_back(0.f);
            reply.tops.push_back(0.f);
            reply.colours.push_back(0);
            reply.nms.emplace_back();
            reply.authors.emplace_back();
            reply.modified.emplace_back();
            reply.snippets.emplace_back();
            reply.parents.push_back(-1);
            continue;
        }
        const Rect rect = annot_rect(annot.annot);
        reply.subtypes.push_back(clamp_subtype(FPDFAnnot_GetSubtype(annot.annot)));
        reply.flags.push_back(static_cast<std::uint32_t>(file_flags(page, i, FPDFAnnot_GetFlags(annot.annot))));
        reply.lefts.push_back(rect.left);
        reply.bottoms.push_back(rect.bottom);
        reply.rights.push_back(rect.right);
        reply.tops.push_back(rect.top);
        reply.colours.push_back(annot_colour(annot.annot, "C", FPDFANNOT_COLORTYPE_Color));
        bool nm_truncated = false;
        std::string nm = annot_text(annot.annot, "NM", 64, 4096, &nm_truncated);
        if (nm_truncated) {
            nm.clear();  // an identifier cut short would name another annotation
        }
        reply.nms.push_back(std::move(nm));
        reply.authors.push_back(annot_text(annot.annot, "T", 128));
        reply.modified.push_back(annot_text(annot.annot, "M", 32, 4096));
        reply.snippets.push_back(annot_text(annot.annot, "Contents", 192, kSnippetRawBytes));
        reply.parents.push_back(reply_parent(cached->page, annot.annot, i, reply.total));
    }
    return {Status::Ok, std::move(reply), ""};
}

Result<protocol::GetAnnotationReply> Document::annotation(std::uint32_t page, std::uint32_t index) {
    using Reply = protocol::GetAnnotationReply;
    if (handle_ == nullptr || page >= page_count_ || index > 99'999) {
        return failure<Reply>(Status::InvalidArgument, "annotation request out of bounds");
    }
    PageCache* cached = cached_page(page);
    if (cached == nullptr) {
        return failure<Reply>(Status::Damaged, "page cannot be loaded");
    }
    const int counted = FPDFPage_GetAnnotCount(cached->page);
    const std::uint32_t total = counted < 0 ? 0 : std::min(static_cast<std::uint32_t>(counted), kMaxAnnotationsPerPage);
    if (index >= total) {
        return failure<Reply>(Status::InvalidArgument, "no such annotation");
    }
    AnnotHandle handle(cached->page, static_cast<int>(index));
    FPDF_ANNOTATION annot = handle.annot;
    if (annot == nullptr) {
        return failure<Reply>(Status::InvalidArgument, "no annotation dictionary at that position");
    }

    Reply reply;
    const int subtype = FPDFAnnot_GetSubtype(annot);
    reply.subtype = clamp_subtype(subtype);
    reply.flags = static_cast<std::uint32_t>(file_flags(page, index, FPDFAnnot_GetFlags(annot)));
    const Rect rect = annot_rect(annot);
    reply.left = rect.left;
    reply.bottom = rect.bottom;
    reply.right = rect.right;
    reply.top = rect.top;
    reply.opacity = annot_opacity(annot);
    reply.colour = annot_colour(annot, "C", FPDFANNOT_COLORTYPE_Color);
    reply.interior_colour = annot_colour(annot, "IC", FPDFANNOT_COLORTYPE_InteriorColor);
    if (reply.colour == 0 && FPDFAnnot_HasKey(annot, "AP") != 0 && subtype != FPDF_ANNOT_LINK) {
        reply.colour = appearance_colour(annot);
        if (reply.colour != 0) {
            const auto alpha = static_cast<std::uint32_t>(std::clamp(std::lround(reply.opacity * 255.0f), 1L, 255L));
            reply.colour = (alpha << 24) | (reply.colour & 0x00FFFFFFu);
        }
    }

    // The width of /Border [h v w] when there is one. /BS is a dictionary and the pinned PDFium has no call into it, so a style and a /BS width cannot be read: the style stays 0.
    if (FPDFAnnot_HasKey(annot, "Border") != 0) {
        float h = 0;
        float v = 0;
        float w = 0;
        if (FPDFAnnot_GetBorder(annot, &h, &v, &w) != 0 && std::isfinite(w)) {
            reply.border_width = std::clamp(w, 0.0f, 1000.0f);
        }
    }

    bool truncated = false;
    bool too_large = false;
    reply.contents = annot_text(annot, "Contents", 16'384, kContentsRawBytes, &truncated, &too_large);
    reply.contents_truncated = truncated || too_large;
    reply.author = annot_text(annot, "T", 256);
    reply.subject = annot_text(annot, "Subj", 256);
    bool nm_truncated = false;
    reply.nm = annot_text(annot, "NM", 64, 4096, &nm_truncated);
    if (nm_truncated) {
        reply.nm.clear();
    }
    reply.created = annot_text(annot, "CreationDate", 32, 4096);
    reply.modified = annot_text(annot, "M", 32, 4096);
    reply.parent_index = reply_parent(cached->page, annot, index, total);
    const std::string reply_type = annot_text(annot, "RT", 16, 4096);
    reply.reply_type = reply_type == "R" ? 1 : reply_type == "Group" ? 2 : 0;
    reply.state_model = annot_text(annot, "StateModel", 16, 4096);
    reply.state = annot_text(annot, "State", 16, 4096);
    reply.popup_open = false;  // /Open is a boolean and the pinned PDFium has no boolean getter
    reply.name = annot_text(annot, "Name", 64, 4096);
    reply.default_appearance = annot_text(annot, "DA", 256, 8192);
    float alignment = 0;
    if (FPDFAnnot_GetNumberValue(annot, "Q", &alignment) != 0 && alignment >= 1.0f) {
        reply.alignment = alignment >= 2.0f ? 2 : 1;
    }
    reply.has_appearance = FPDFAnnot_HasKey(annot, "AP") != 0;  // looking inside the stream would decode it, which a hostile file can make expensive

    if (FPDFAnnot_HasAttachmentPoints(annot) != 0) {
        const std::size_t quads = std::min<std::size_t>(FPDFAnnot_CountAttachmentPoints(annot), kMaxQuads);
        for (std::size_t q = 0; q < quads; ++q) {
            FS_QUADPOINTSF points{};
            if (FPDFAnnot_GetAttachmentPoints(annot, q, &points) == 0) {
                continue;
            }
            for (const float value : {points.x1, points.y1, points.x2, points.y2, points.x3, points.y3, points.x4, points.y4}) {
                reply.quad_points.push_back(clamp_coordinate(value));
            }
        }
    }

    if (subtype == FPDF_ANNOT_POLYGON || subtype == FPDF_ANNOT_POLYLINE) {
        const unsigned long points = FPDFAnnot_GetVertices(annot, nullptr, 0);
        if (points > kMaxVertexPointsRead) {
            reply.vertices_truncated = true;
        } else if (points > 0) {
            std::vector<FS_POINTF> buffer(points);
            const unsigned long got = FPDFAnnot_GetVertices(annot, buffer.data(), points);
            for (unsigned long i = 0; i < got && i < points; ++i) {
                if (reply.vertices.size() + 2 > kMaxVertexNumbers) {
                    reply.vertices_truncated = true;
                    break;
                }
                reply.vertices.push_back(clamp_coordinate(buffer[i].x));
                reply.vertices.push_back(clamp_coordinate(buffer[i].y));
            }
        }
    } else if (subtype == FPDF_ANNOT_LINE) {
        FS_POINTF start{};
        FS_POINTF end{};
        if (FPDFAnnot_GetLine(annot, &start, &end) != 0) {
            reply.vertices = {clamp_coordinate(start.x), clamp_coordinate(start.y), clamp_coordinate(end.x), clamp_coordinate(end.y)};
        }
    } else if (subtype == FPDF_ANNOT_INK) {
        const unsigned long strokes = std::min<unsigned long>(FPDFAnnot_GetInkListCount(annot), kMaxStrokes);
        std::uint64_t points = 0;
        for (unsigned long s = 0; s < strokes && points < kMaxInkPoints; ++s) {
            points += FPDFAnnot_GetInkListPath(annot, s, nullptr, 0);
        }
        reply.stroke_count = static_cast<std::uint32_t>(strokes);
        reply.point_count = static_cast<std::uint32_t>(std::min(points, kMaxInkPoints));
    }
    return {Status::Ok, std::move(reply), ""};
}

Result<protocol::GetInkStrokeReply> Document::ink_stroke(std::uint32_t page, std::uint32_t index, std::uint32_t stroke, std::uint32_t first, std::uint32_t count) {
    using Reply = protocol::GetInkStrokeReply;
    if (handle_ == nullptr || page >= page_count_ || index > 99'999 || stroke > 99'999 || first > 9'999'999 || count == 0 || count > 1024) {
        return failure<Reply>(Status::InvalidArgument, "ink request out of bounds");
    }
    PageCache* cached = cached_page(page);
    if (cached == nullptr) {
        return failure<Reply>(Status::Damaged, "page cannot be loaded");
    }
    AnnotHandle handle(cached->page, static_cast<int>(index));
    if (handle.annot == nullptr || FPDFAnnot_GetSubtype(handle.annot) != FPDF_ANNOT_INK) {
        return failure<Reply>(Status::InvalidArgument, "not an ink annotation");
    }
    const unsigned long strokes = FPDFAnnot_GetInkListCount(handle.annot);
    if (stroke >= strokes) {
        return failure<Reply>(Status::InvalidArgument, "no such stroke");
    }
    const unsigned long points = FPDFAnnot_GetInkListPath(handle.annot, stroke, nullptr, 0);
    if (points > kMaxStrokePointsRead) {
        return failure<Reply>(Status::Unsupported, "the stroke is too long to read");
    }
    Reply reply;
    reply.point_count = static_cast<std::uint32_t>(std::min<unsigned long>(points, 10'000'000));
    if (first < points) {
        std::vector<FS_POINTF> buffer(points);
        const unsigned long got = FPDFAnnot_GetInkListPath(handle.annot, stroke, buffer.data(), points);
        const unsigned long end = std::min<unsigned long>(std::min<unsigned long>(got, points), static_cast<unsigned long>(first) + count);
        for (unsigned long i = first; i < end; ++i) {
            reply.xs.push_back(clamp_coordinate(buffer[i].x));
            reply.ys.push_back(clamp_coordinate(buffer[i].y));
        }
    }
    reply.has_widths = false;  // a PDF ink list has no per-point width (ink-pen OQ-INK-3)
    reply.widths.assign(reply.xs.size(), 0.0f);
    return {Status::Ok, std::move(reply), ""};
}

Result<protocol::CountAnnotationsReply> Document::count_annotations(std::uint32_t first_page, std::uint32_t count, const std::function<bool()>& expired) {
    using Reply = protocol::CountAnnotationsReply;
    if (handle_ == nullptr || count == 0 || count > 5000 || std::uint64_t{first_page} + count > page_count_) {
        return failure<Reply>(Status::InvalidArgument, "page range out of bounds");
    }
    Reply reply;
    reply.counts.reserve(count);
    for (std::uint32_t p = 0; p < count; ++p) {
        if (expired && expired()) {
            return failure<Reply>(Status::Timeout, "the count ran out of time");
        }
        PageHandle page(handle_, static_cast<int>(first_page + p));
        if (page.page == nullptr) {
            return failure<Reply>(Status::Damaged, "page cannot be loaded");
        }
        const int counted = FPDFPage_GetAnnotCount(page.page);
        const std::uint32_t entries = counted < 0 ? 0 : std::min(static_cast<std::uint32_t>(counted), kMaxAnnotationsPerPage);
        std::uint32_t visible = 0;
        for (std::uint32_t i = 0; i < entries; ++i) {
            AnnotHandle annot(page.page, static_cast<int>(i));
            if (annot.annot == nullptr) {
                continue;
            }
            const int subtype = FPDFAnnot_GetSubtype(annot.annot);
            if (subtype != FPDF_ANNOT_LINK && subtype != FPDF_ANNOT_POPUP && subtype != FPDF_ANNOT_WIDGET) {
                ++visible;
            }
        }
        reply.counts.push_back(static_cast<std::uint16_t>(std::min<std::uint32_t>(visible, 65'535)));
    }
    return {Status::Ok, std::move(reply), ""};
}

// ---- the display mirror ------------------------------------------------------------------------------------------------------------------

Status Document::set_annotation_hidden(std::uint32_t page, std::uint32_t index, bool hidden) {
    if (handle_ == nullptr || page >= page_count_ || index > 99'999) {
        return Status::InvalidArgument;
    }
    PageCache* cached = cached_page(page);
    if (cached == nullptr) {
        return Status::Damaged;
    }
    const int counted = FPDFPage_GetAnnotCount(cached->page);
    if (counted < 0 || index >= std::min(static_cast<std::uint32_t>(counted), kMaxAnnotationsPerPage)) {
        return Status::InvalidArgument;
    }
    AnnotHandle handle(cached->page, static_cast<int>(index));
    if (handle.annot == nullptr) {
        return Status::InvalidArgument;
    }
    const auto it = mirror_.find(mirror_key(page, index));
    if (hidden) {
        const int original = it != mirror_.end() ? it->second.original : FPDFAnnot_GetFlags(handle.annot);
        if ((original & FPDF_ANNOT_FLAG_HIDDEN) != 0) {
            return Status::Ok;  // the file hides it already; the mirror has nothing to add
        }
        const int current = original | FPDF_ANNOT_FLAG_HIDDEN;
        if (FPDFAnnot_SetFlags(handle.annot, current) == 0) {
            return Status::Damaged;
        }
        mirror_[mirror_key(page, index)] = {original, current};
    } else if (it != mirror_.end()) {
        if (FPDFAnnot_SetFlags(handle.annot, it->second.original) == 0) {
            return Status::Damaged;
        }
        mirror_.erase(it);
    }
    return Status::Ok;
}

bool Document::restore_mirrored_flags() {
    std::vector<std::uint64_t> keys;
    keys.reserve(mirror_.size());
    for (const auto& entry : mirror_) {
        keys.push_back(entry.first);
    }
    std::sort(keys.begin(), keys.end());
    bool all = true;
    std::uint32_t open_page = 0xFFFFFFFFu;
    std::unique_ptr<PageHandle> page;
    for (const std::uint64_t key : keys) {
        const auto p = static_cast<std::uint32_t>(key >> 32);
        const auto index = static_cast<std::uint32_t>(key & 0xFFFFFFFFu);
        if (page == nullptr || open_page != p) {
            page = std::make_unique<PageHandle>(handle_, static_cast<int>(p));
            open_page = p;
        }
        AnnotHandle annot(page->page == nullptr ? nullptr : FPDFPage_GetAnnot(page->page, static_cast<int>(index)));
        if (annot.annot == nullptr || FPDFAnnot_SetFlags(annot.annot, mirror_.at(key).original) == 0) {
            all = false;
        }
    }
    return all;
}

void Document::reapply_mirrored_flags() {
    std::vector<std::uint64_t> keys;
    keys.reserve(mirror_.size());
    for (const auto& entry : mirror_) {
        keys.push_back(entry.first);
    }
    std::sort(keys.begin(), keys.end());
    std::uint32_t open_page = 0xFFFFFFFFu;
    std::unique_ptr<PageHandle> page;
    for (const std::uint64_t key : keys) {
        const auto p = static_cast<std::uint32_t>(key >> 32);
        const auto index = static_cast<std::uint32_t>(key & 0xFFFFFFFFu);
        if (page == nullptr || open_page != p) {
            page = std::make_unique<PageHandle>(handle_, static_cast<int>(p));
            open_page = p;
        }
        AnnotHandle annot(page->page == nullptr ? nullptr : FPDFPage_GetAnnot(page->page, static_cast<int>(index)));
        if (annot.annot != nullptr) {
            (void)FPDFAnnot_SetFlags(annot.annot, mirror_.at(key).current);
        }
    }
}

// ---- saving ------------------------------------------------------------------------------------------------------------------------------

namespace {

struct SinkWriter : FPDF_FILEWRITE {
    const Document::SaveSink* sink{nullptr};
    std::uint64_t total{0};
    bool failed{false};

    SinkWriter() {
        version = 1;
        WriteBlock = &SinkWriter::write;
    }

    static int write(FPDF_FILEWRITE* self, const void* data, unsigned long size) {
        auto* writer = static_cast<SinkWriter*>(self);
        if (writer->failed) {
            return 0;
        }
        bool ok = false;
        try {
            ok = writer->sink != nullptr && *writer->sink && (*writer->sink)(data, size);
        } catch (...) {
            ok = false;
        }
        if (!ok) {
            writer->failed = true;
            return 0;
        }
        writer->total += size;
        return 1;
    }
};

}  // namespace

Status Document::save(bool incremental, const SaveSink& sink, std::uint64_t& bytes_written) {
    bytes_written = 0;
    if (handle_ == nullptr || !sink) {
        return Status::InvalidArgument;
    }
    // A value being typed into a field is committed by taking the focus away; without that it would be left out of the file.
    if (form_ != nullptr && form_->handle != nullptr) {
        (void)FORM_ForceToKillFocus(form_->handle);
    }
    // A flag set for the screen must never reach a file: put the file's own back before PDFium looks at anything, and the mirror again afterwards, success or not.
    if (!restore_mirrored_flags()) {
        reapply_mirrored_flags();
        return Status::Damaged;
    }
    SinkWriter writer;
    writer.sink = &sink;
    const FPDF_BOOL saved = FPDF_SaveAsCopy(handle_, &writer, incremental ? FPDF_INCREMENTAL : FPDF_NO_INCREMENTAL);
    reapply_mirrored_flags();
    bytes_written = writer.total;
    return saved != 0 && !writer.failed ? Status::Ok : Status::Damaged;
}

}  // namespace mikepdf::pdfium
