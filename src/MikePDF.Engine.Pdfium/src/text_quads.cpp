// GetTextQuads (schema 7, docs/features/text-markup.md 2.3): the quadrilaterals that mark a run of characters, one per line, in the order top-left, top-right, bottom-left,
// bottom-right of the text's own frame. PDFium reports each character's loose box (ascent and descent of the font) as an axis-aligned rectangle in user space and the character's
// rotation, so a line is rebuilt in its own frame (u along the baseline, v up) and its quad is the union of its characters' boxes there.
#include <algorithm>
#include <cmath>
#include <numbers>
#include <vector>

#include "document_internal.h"

namespace mikepdf::pdfium {

using protocol::Result;
using protocol::Status;
using namespace detail;

namespace {

constexpr std::uint32_t kMaxChars = 200'000;       // GetPageTextReply.total
constexpr std::size_t kMaxQuads = 4096;            // GetTextQuadsReply.totalQuads
constexpr std::size_t kQuadsPerReply = 64;         // 256 corners
constexpr float kSnapRadians = 0.5f * std::numbers::pi_v<float> / 180.0f;
constexpr float kSameLineRadians = 1.0f * std::numbers::pi_v<float> / 180.0f;

bool is_space(unsigned int cp) { return cp == 0x20 || cp == 0x09 || cp == 0xA0 || (cp >= 0x2000 && cp <= 0x200A) || cp == 0x3000; }

bool is_break(unsigned int cp) { return cp == 0x0A || cp == 0x0D || cp == 0x2028 || cp == 0x2029; }

// A character's angle snapped to an exact quarter turn when it is within half a degree of one (a page rotated by 90 degrees is exact, not nearly exact).
float snapped_angle(float angle) {
    if (!std::isfinite(angle) || angle < 0.0f) {
        return 0.0f;
    }
    constexpr float kQuarter = std::numbers::pi_v<float> / 2.0f;
    const float turns = std::round(angle / kQuarter);
    if (std::fabs(angle - turns * kQuarter) <= kSnapRadians) {
        return std::fmod(turns, 4.0f) * kQuarter;
    }
    return angle;
}

// The direction the character runs in, counter-clockwise from the x axis of user space, in [0, 2 pi). It is the direction of the first row of the character's effective matrix (the
// text matrix and the page's), which for a character turned by 90 degrees to read upwards is (0, 1). FPDFText_GetCharAngle measures the other way round (an upward line reports 270 degrees),
// so it is only the fallback.
float char_angle(FPDF_TEXTPAGE text, int index) {
    FS_MATRIX m{};
    if (FPDFText_GetMatrix(text, index, &m) != 0 && std::isfinite(m.a) && std::isfinite(m.b) && (m.a != 0.0f || m.b != 0.0f)) {
        float angle = std::atan2(m.b, m.a);
        if (angle < 0.0f) {
            angle += 2.0f * std::numbers::pi_v<float>;
        }
        return angle;
    }
    const float reported = FPDFText_GetCharAngle(text, index);
    return std::isfinite(reported) && reported > 0.0f ? 2.0f * std::numbers::pi_v<float> - reported : 0.0f;
}

struct Glyph {
    float cu{}, cv{};  // the centre in the line's frame
    float w{}, h{};    // the size along u and v
    bool trim{};       // whitespace or made up by PDFium: never the first or last character of a quad
};

struct Line {
    float angle{};
    float c{1}, s{0};  // cos and sin of the angle: u = (c, s), v = (-s, c)
    bool vertical{};   // characters stacked along v (vertical writing) instead of laid along u
    std::vector<Glyph> glyphs;
};

struct Placed {
    float cx{}, cy{}, w{}, h{}, angle{};
};

// The size of a character along and across its own baseline, from the axis-aligned loose box. For a quarter turn the box is exact. Otherwise the box is the bounding box of the
// turned rectangle, bw = w|c| + h|s| and bh = w|s| + h|c|, which has a solution unless the angle is near 45 degrees; there the font size stands in for the height.
bool oriented_size(float angle, float bw, float bh, float font_size, float& w, float& h) {
    const float c = std::fabs(std::cos(angle));
    const float s = std::fabs(std::sin(angle));
    if (s < 1e-4f) {
        w = bw;
        h = bh;
    } else if (c < 1e-4f) {
        w = bh;
        h = bw;
    } else {
        const float det = c * c - s * s;
        if (std::fabs(det) > 0.2f) {
            w = (bw * c - bh * s) / det;
            h = (bh * c - bw * s) / det;
        } else {
            h = font_size > 0.0f ? font_size : std::min(bw, bh);
            w = (bw - h * s) / c;
        }
        if (!(w > 0.0f) || !(h > 0.0f) || !std::isfinite(w) || !std::isfinite(h)) {
            w = bw;
            h = bh;
        }
    }
    return w > 0.0f && h > 0.0f && std::isfinite(w) && std::isfinite(h);
}

struct Builder {
    std::vector<float>& xs;
    std::vector<float>& ys;
    bool truncated{false};
    Line line;

    // The glyph in `line`'s frame.
    static Glyph in_frame(const Line& l, const Placed& p, bool trim) {
        Glyph g;
        g.cu = p.cx * l.c + p.cy * l.s;
        g.cv = -p.cx * l.s + p.cy * l.c;
        g.w = p.w;
        g.h = p.h;
        g.trim = trim;
        return g;
    }

    bool joins(const Placed& p) const {
        if (line.glyphs.empty()) {
            return true;
        }
        float turn = std::fabs(p.angle - line.angle);
        turn = std::min(turn, 2.0f * std::numbers::pi_v<float> - turn);
        if (turn > kSameLineRadians) {
            return false;
        }
        const Glyph g = in_frame(line, p, false);
        float umin = line.glyphs.front().cu - line.glyphs.front().w / 2;
        float umax = line.glyphs.front().cu + line.glyphs.front().w / 2;
        float vmin = line.glyphs.front().cv - line.glyphs.front().h / 2;
        float vmax = line.glyphs.front().cv + line.glyphs.front().h / 2;
        for (const Glyph& other : line.glyphs) {
            umin = std::min(umin, other.cu - other.w / 2);
            umax = std::max(umax, other.cu + other.w / 2);
            vmin = std::min(vmin, other.cv - other.h / 2);
            vmax = std::max(vmax, other.cv + other.h / 2);
        }
        const float ref = std::max({g.h, vmax - vmin, 1e-3f});
        if (line.vertical) {
            // A column: the same horizontal place, the next one a little further along v.
            const float column_w = umax - umin;
            const float gap_v = g.cv > (vmin + vmax) / 2 ? (g.cv - g.h / 2) - vmax : vmin - (g.cv + g.h / 2);
            return std::fabs(g.cu - (umin + umax) / 2) <= 0.5f * std::max(g.w, column_w) && gap_v <= 2.0f * ref;
        }
        if (std::fabs(g.cv - (vmin + vmax) / 2) > 0.5f * ref) {
            return false;
        }
        const float gap_u = g.cu > (umin + umax) / 2 ? (g.cu - g.w / 2) - umax : umin - (g.cu + g.w / 2);
        return gap_u <= 4.0f * ref;  // text on one baseline that is further away than this is another run
    }

    void add(const Placed& p, bool trim) {
        if (!line.glyphs.empty() && !joins(p)) {
            // A column of vertical writing starts as two characters that do not sit on one baseline but share a horizontal place.
            bool continues_column = false;
            if (line.glyphs.size() == 1 && !line.vertical) {
                const Glyph g = in_frame(line, p, trim);
                const Glyph& first = line.glyphs.front();
                float turn = std::fabs(p.angle - line.angle);
                turn = std::min(turn, 2.0f * std::numbers::pi_v<float> - turn);
                if (turn <= kSameLineRadians && std::fabs(g.cu - first.cu) <= 0.5f * std::max(g.w, first.w) &&
                    std::fabs(g.cv - first.cv) <= 2.0f * std::max(g.h, first.h) + std::max(g.h, first.h) && std::fabs(g.cv - first.cv) > 0.5f * std::max(g.h, first.h)) {
                    line.vertical = true;
                    continues_column = true;
                }
            }
            if (!continues_column) {
                finish();
            }
        }
        if (line.glyphs.empty()) {
            line.angle = p.angle;
            line.c = std::cos(p.angle);
            line.s = std::sin(p.angle);
            line.vertical = false;
        }
        line.glyphs.push_back(in_frame(line, p, trim));
    }

    void finish() {
        auto& glyphs = line.glyphs;
        std::size_t from = 0;
        std::size_t to = glyphs.size();
        while (from < to && glyphs[from].trim) {
            ++from;
        }
        while (to > from && glyphs[to - 1].trim) {
            --to;
        }
        if (from < to) {
            if (xs.size() / 4 >= kMaxQuads) {
                truncated = true;
            } else {
                float umin = glyphs[from].cu - glyphs[from].w / 2;
                float umax = glyphs[from].cu + glyphs[from].w / 2;
                float vmin = glyphs[from].cv - glyphs[from].h / 2;
                float vmax = glyphs[from].cv + glyphs[from].h / 2;
                for (std::size_t i = from; i < to; ++i) {
                    umin = std::min(umin, glyphs[i].cu - glyphs[i].w / 2);
                    umax = std::max(umax, glyphs[i].cu + glyphs[i].w / 2);
                    vmin = std::min(vmin, glyphs[i].cv - glyphs[i].h / 2);
                    vmax = std::max(vmax, glyphs[i].cv + glyphs[i].h / 2);
                }
                // top-left, top-right, bottom-left, bottom-right of the text's own frame
                for (const auto& [a, b] : {std::pair{umin, vmax}, std::pair{umax, vmax}, std::pair{umin, vmin}, std::pair{umax, vmin}}) {
                    xs.push_back(clamp_coordinate(static_cast<double>(a) * line.c - static_cast<double>(b) * line.s));
                    ys.push_back(clamp_coordinate(static_cast<double>(a) * line.s + static_cast<double>(b) * line.c));
                }
            }
        }
        glyphs.clear();
        line.vertical = false;
    }
};

}  // namespace

Result<protocol::GetTextQuadsReply> Document::text_quads(std::uint32_t page, std::uint32_t start, std::uint32_t count, std::uint32_t first_quad) {
    using Reply = protocol::GetTextQuadsReply;
    if (handle_ == nullptr || page >= page_count_ || start > kMaxChars - 1 || count == 0 || count > kMaxChars || first_quad > kMaxQuads - 1) {
        return failure<Reply>(Status::InvalidArgument, "text quad request out of bounds");
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
    const std::uint32_t total = std::min(static_cast<std::uint32_t>(counted), kMaxChars);
    if (start >= total) {
        return failure<Reply>(Status::InvalidArgument, "the range starts after the text");
    }
    const std::uint32_t end = static_cast<std::uint32_t>(std::min<std::uint64_t>(std::uint64_t{start} + count, total));

    if (quads_.page != page || quads_.start != start || quads_.count != count) {
        quads_ = QuadCache{};
        quads_.page = page;
        quads_.start = start;
        quads_.count = count;
        Builder builder{quads_.xs, quads_.ys};
        for (std::uint32_t i = start; i < end; ++i) {
            const unsigned int cp = FPDFText_GetUnicode(text, static_cast<int>(i));
            if (is_break(cp)) {
                builder.finish();
                continue;
            }
            FS_RECTF box{};
            if (FPDFText_GetLooseCharBox(text, static_cast<int>(i), &box) == 0) {
                continue;
            }
            const double bw = std::fabs(static_cast<double>(box.right) - box.left);
            const double bh = std::fabs(static_cast<double>(box.top) - box.bottom);
            if (!(bw > 0.0) || !(bh > 0.0) || !std::isfinite(bw) || !std::isfinite(bh)) {
                continue;  // a character without a box (a Type 3 glyph with bad metrics, a space PDFium placed nowhere) is skipped
            }
            Placed placed;
            placed.cx = static_cast<float>((static_cast<double>(box.left) + box.right) / 2);
            placed.cy = static_cast<float>((static_cast<double>(box.top) + box.bottom) / 2);
            placed.angle = snapped_angle(char_angle(text, static_cast<int>(i)));
            if (!oriented_size(placed.angle, static_cast<float>(bw), static_cast<float>(bh), static_cast<float>(FPDFText_GetFontSize(text, static_cast<int>(i))), placed.w, placed.h)) {
                continue;
            }
            const bool made_up = FPDFText_IsGenerated(text, static_cast<int>(i)) == 1;
            builder.add(placed, is_space(cp) || made_up);
        }
        builder.finish();
        quads_.truncated = builder.truncated;
    }

    const std::size_t quads = quads_.xs.size() / 4;
    if (first_quad > quads) {
        return failure<Reply>(Status::InvalidArgument, "the first quad is past the end of the range's quads");
    }
    Reply reply;
    reply.total_quads = static_cast<std::uint32_t>(quads);
    reply.truncated = quads_.truncated;
    const std::size_t take = std::min(kQuadsPerReply, quads - first_quad);
    reply.xs.assign(quads_.xs.begin() + static_cast<std::ptrdiff_t>(first_quad) * 4, quads_.xs.begin() + static_cast<std::ptrdiff_t>(first_quad + take) * 4);
    reply.ys.assign(quads_.ys.begin() + static_cast<std::ptrdiff_t>(first_quad) * 4, quads_.ys.begin() + static_cast<std::ptrdiff_t>(first_quad + take) * 4);
    return {Status::Ok, std::move(reply), ""};
}

}  // namespace mikepdf::pdfium
