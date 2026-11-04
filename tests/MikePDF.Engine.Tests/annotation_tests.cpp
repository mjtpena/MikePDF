// The annotation read side (schema 7, docs/features/annotation-core.md 11: ListAnnotations, GetAnnotation, GetInkStroke, CountAnnotations), the display mirror
// (SetAnnotationHidden, invariant 4a) and the engine's own save (Document::save, which SaveDocument calls). Criteria: AC-11 (the engine half), AC-14, AC-17.
//
// The test document is one page, 612 x 792 pt, with nine entries in /Annots. Their places in the array (the index everything below names them by):
//   0 highlight with an appearance (a yellow 200 x 20 box at x 100..300, y 600..620, which is device y 172..192 at 1x), comment, author, dates, id, /CA 0.5
//   1 text note        2 reply to the note (/IRT, /RT, /StateModel, /State)      3 link        4 ink with two strokes       5 polygon      6 line
//   7 null (an entry that is not a dictionary)       8 free text with /DA and /Q
#include <gtest/gtest.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <deque>
#include <string>
#include <vector>

#include "mikepdf/pdfium/document.h"
#include "mikepdf/pdfium/library.h"
#include "test_support.h"

namespace {

using namespace mikepdf;
using pdfium::Document;
using pdfium::RenderInterrupt;
using pdfium::TileRequest;
using protocol::Status;
using support::PdfBuilder;
using support::pixel;

constexpr auto kNever = [] { return RenderInterrupt::None; };
constexpr auto kNotExpired = [] { return false; };

// A PDF text string in UTF-16BE with a byte order mark, as a hex string: how a file spells a comment that is not plain ASCII.
std::string hex_text(const std::u16string& text) {
    std::string out = "<FEFF";
    char buffer[8];
    for (const char16_t unit : text) {
        std::snprintf(buffer, sizeof buffer, "%04X", static_cast<unsigned>(unit));
        out += buffer;
    }
    return out + ">";
}

std::string build_annotated(int extra_entries = 0) {
    PdfBuilder b;
    const int catalog = b.reserve();
    const int pages = b.reserve();
    const int page = b.reserve();
    const int content = b.add_stream("0.95 g 0 0 612 20 re f");
    const int appearance = b.add_stream("1 1 0 rg 0 0 200 20 re f", "/Type /XObject /Subtype /Form /BBox [0 0 200 20]");
    const int highlight = b.add(
        "<< /Type /Annot /Subtype /Highlight /Rect [100 600 300 620] /QuadPoints [100 620 300 620 100 600 300 600] /C [1 1 0] /CA 0.5 /F 4 /NM (MikePDF:0123456789abcdef0123456789abcdef) /T (Alice) "
        "/Subj (Highlight) /M (D:20241103123456+10'00') /CreationDate (D:20241103120000+10'00') /Contents " + hex_text(u"Café ✓ comment") + " /AP << /N " + std::to_string(appearance) + " 0 R >> >>");
    const int note = b.add("<< /Type /Annot /Subtype /Text /Rect [20 700 44 724] /Contents (Reply target) /Name /Comment /F 28 /C [1 0 0] /NM (note-1) /T (Bob) >>");
    const int reply = b.add("<< /Type /Annot /Subtype /Text /Rect [20 650 44 674] /IRT " + std::to_string(note) + " 0 R /RT /R /StateModel (Review) /State (Accepted) /Contents (A reply) /F 28 /NM (note-2) >>");
    const int link = b.add("<< /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Border [0 0 0] /A << /S /URI /URI (http://example.org/) >> >>");
    const int ink = b.add("<< /Type /Annot /Subtype /Ink /Rect [300 300 400 400] /InkList [[310 310 320 330 330 310] [350 350 360 360]] /C [0 0 1] /F 4 /NM (ink-1) >>");
    const int polygon = b.add("<< /Type /Annot /Subtype /Polygon /Rect [100 100 200 200] /Vertices [100 100 200 100 150 200] /C [0 1 0] /IC [1 0 0] /F 4 /NM (poly-1) >>");
    const int line = b.add("<< /Type /Annot /Subtype /Line /Rect [50 50 150 150] /L [50 50 150 150] /C [0 0 0] /F 4 /NM (line-1) >>");
    const int free_text = b.add("<< /Type /Annot /Subtype /FreeText /Rect [300 500 500 540] /DA (0 g /Helv 12 Tf) /Q 1 /Contents (Text box) /F 4 /NM (free-1) >>");
    std::string annots = "[" + std::to_string(highlight) + " 0 R " + std::to_string(note) + " 0 R " + std::to_string(reply) + " 0 R " + std::to_string(link) + " 0 R " + std::to_string(ink) +
                         " 0 R " + std::to_string(polygon) + " 0 R " + std::to_string(line) + " 0 R null " + std::to_string(free_text) + " 0 R";
    for (int i = 0; i < extra_entries; ++i) {
        annots += " null";
    }
    annots += "]";
    b.set(catalog, "<< /Type /Catalog /Pages " + std::to_string(pages) + " 0 R >>");
    b.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 /MediaBox [0 0 612 792] >>");
    b.set(page, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /Contents " + std::to_string(content) + " 0 R /Resources << >> /Annots " + annots + " >>");
    return b.build(catalog);
}

class AnnotationTest : public ::testing::Test {
protected:
    pdfium::Library library_;
    std::vector<std::uint8_t> bytes_ = support::bytes_of(build_annotated());

    // PDFium reads the bytes in place, so they have to outlive the document: they are kept here until the test ends.
    Document open(std::vector<std::uint8_t> bytes) {
        kept_.push_back(std::move(bytes));
        auto opened = Document::open(kept_.back(), {});
        EXPECT_EQ(opened.status, Status::Ok);
        return std::move(opened.value);
    }
    std::deque<std::vector<std::uint8_t>> kept_;

    static std::vector<std::uint8_t> tile(Document& doc) {
        TileRequest request;
        request.scale = 1.0f;
        request.tile_width = 612;
        request.tile_height = 792;
        request.flags = pdfium::kRenderAnnotations;
        std::vector<std::uint8_t> buffer(std::size_t{612} * 792 * 4, 0x55);
        EXPECT_EQ(doc.render_tile(request, buffer, 612 * 4, kNever), Status::Ok);
        return buffer;
    }

    static bool is_yellow(const support::Bgra& p) { return p.r > 200 && p.g > 200 && p.b < 80; }

    // Saves the document the way SaveDocument does and returns what PDFium wrote.
    static std::vector<std::uint8_t> save(Document& doc, bool incremental) {
        std::vector<std::uint8_t> out;
        std::uint64_t written = 0;
        EXPECT_EQ(doc.save(incremental, [&out](const void* data, unsigned long size) {
            const auto* p = static_cast<const std::uint8_t*>(data);
            out.insert(out.end(), p, p + size);
            return true;
        }, written), Status::Ok);
        EXPECT_EQ(written, out.size());
        return out;
    }
};

TEST_F(AnnotationTest, ListingGivesEveryEntryItsPlaceAndTheFactsOfTheFile) {
    Document doc = open(bytes_);
    auto list = doc.list_annotations(0, 0, 64);
    ASSERT_EQ(list.status, Status::Ok);
    const auto& r = list.value;
    EXPECT_EQ(r.total, 9u);
    ASSERT_EQ(r.subtypes.size(), 9u);
    EXPECT_EQ((std::vector<std::uint16_t>(r.subtypes.begin(), r.subtypes.end())), (std::vector<std::uint16_t>{9, 1, 1, 2, 15, 7, 4, 0, 3}));  // highlight, text, text, link, ink, polygon, line, null, freetext
    EXPECT_EQ(r.flags[0], 4u);
    EXPECT_EQ(r.flags[1], 28u);
    EXPECT_EQ(r.flags[7], 0u);  // the null entry keeps its place
    EXPECT_FLOAT_EQ(r.lefts[0], 100.f);
    EXPECT_FLOAT_EQ(r.bottoms[0], 600.f);
    EXPECT_FLOAT_EQ(r.rights[0], 300.f);
    EXPECT_FLOAT_EQ(r.tops[0], 620.f);
    EXPECT_EQ(r.colours[0], 0u);  // it has an appearance stream, and the pinned PDFium reads /C only from an annotation without one
    EXPECT_EQ(r.colours[1] & 0x00FFFFFFu, 0x00FF0000u);
    EXPECT_EQ(r.colours[3], 0u);  // a link has no /C
    EXPECT_EQ(r.nms[0], "MikePDF:0123456789abcdef0123456789abcdef");
    EXPECT_EQ(r.nms[3], "");
    EXPECT_EQ(r.authors[0], "Alice");
    EXPECT_EQ(r.modified[0], "D:20241103123456+10'00'");
    EXPECT_EQ(r.snippets[0], "Caf\xC3\xA9 \xE2\x9C\x93 comment");
    EXPECT_EQ(r.snippets[1], "Reply target");
    EXPECT_EQ(r.parents[0], -1);
    EXPECT_EQ(r.parents[2], 1);   // the reply names the note
    EXPECT_EQ(r.parents[1], -1);
    EXPECT_EQ(r.parents[7], -1);
}

TEST_F(AnnotationTest, AListIsReadInWindowsAndAWindowPastTheEndIsEmpty) {
    Document doc = open(bytes_);
    auto first = doc.list_annotations(0, 0, 4);
    ASSERT_EQ(first.status, Status::Ok);
    EXPECT_EQ(first.value.total, 9u);
    EXPECT_EQ(first.value.subtypes.size(), 4u);
    auto tail = doc.list_annotations(0, 8, 64);
    ASSERT_EQ(tail.status, Status::Ok);
    EXPECT_EQ(tail.value.subtypes.size(), 1u);
    EXPECT_EQ(tail.value.subtypes[0], 3);
    auto past = doc.list_annotations(0, 50, 4);
    ASSERT_EQ(past.status, Status::Ok);
    EXPECT_EQ(past.value.total, 9u);
    EXPECT_TRUE(past.value.subtypes.empty());
    EXPECT_EQ(doc.list_annotations(1, 0, 4).status, Status::InvalidArgument);      // no such page
    EXPECT_EQ(doc.list_annotations(0, 0, 0).status, Status::InvalidArgument);
    EXPECT_EQ(doc.list_annotations(0, 0, 65).status, Status::InvalidArgument);
    EXPECT_EQ(doc.list_annotations(0, 100'000, 1).status, Status::InvalidArgument);
}

TEST_F(AnnotationTest, AnAnnotationIsReadInFull) {
    Document doc = open(bytes_);
    auto highlight = doc.annotation(0, 0);
    ASSERT_EQ(highlight.status, Status::Ok);
    const auto& h = highlight.value;
    EXPECT_EQ(h.subtype, 9);
    EXPECT_EQ(h.flags, 4u);
    EXPECT_EQ(h.contents, "Caf\xC3\xA9 \xE2\x9C\x93 comment");
    EXPECT_FALSE(h.contents_truncated);
    EXPECT_EQ(h.author, "Alice");
    EXPECT_EQ(h.subject, "Highlight");
    EXPECT_EQ(h.nm, "MikePDF:0123456789abcdef0123456789abcdef");
    EXPECT_EQ(h.colour, 0x80FFFF00u);  // from the stream (/C is out of reach), with /CA 0.5 as the alpha
    EXPECT_EQ(h.created, "D:20241103120000+10'00'");
    EXPECT_EQ(h.modified, "D:20241103123456+10'00'");
    EXPECT_NEAR(h.opacity, 0.5f, 1e-6);
    EXPECT_EQ(h.parent_index, -1);
    EXPECT_TRUE(h.has_appearance);
    EXPECT_EQ(h.quad_points, (std::vector<float>{100.f, 620.f, 300.f, 620.f, 100.f, 600.f, 300.f, 600.f}));

    auto reply = doc.annotation(0, 2);
    ASSERT_EQ(reply.status, Status::Ok);
    EXPECT_EQ(reply.value.parent_index, 1);
    EXPECT_EQ(reply.value.reply_type, 1);
    EXPECT_EQ(reply.value.state_model, "Review");
    EXPECT_EQ(reply.value.state, "Accepted");
    EXPECT_FALSE(reply.value.has_appearance);

    auto note = doc.annotation(0, 1);
    ASSERT_EQ(note.status, Status::Ok);
    EXPECT_EQ(note.value.name, "Comment");

    auto polygon = doc.annotation(0, 5);
    ASSERT_EQ(polygon.status, Status::Ok);
    EXPECT_EQ(polygon.value.vertices, (std::vector<float>{100.f, 100.f, 200.f, 100.f, 150.f, 200.f}));
    EXPECT_EQ(polygon.value.colour & 0x00FFFFFFu, 0x0000FF00u);
    EXPECT_EQ(polygon.value.interior_colour & 0x00FFFFFFu, 0x00FF0000u);

    auto line = doc.annotation(0, 6);
    ASSERT_EQ(line.status, Status::Ok);
    EXPECT_EQ(line.value.vertices, (std::vector<float>{50.f, 50.f, 150.f, 150.f}));

    auto ink = doc.annotation(0, 4);
    ASSERT_EQ(ink.status, Status::Ok);
    EXPECT_EQ(ink.value.stroke_count, 2u);
    EXPECT_EQ(ink.value.point_count, 5u);  // 3 + 2 points

    auto free_text = doc.annotation(0, 8);
    ASSERT_EQ(free_text.status, Status::Ok);
    EXPECT_EQ(free_text.value.default_appearance, "0 g /Helv 12 Tf");
    EXPECT_EQ(free_text.value.alignment, 1);

    EXPECT_EQ(doc.annotation(0, 7).status, Status::InvalidArgument);   // the null entry has no dictionary
    EXPECT_EQ(doc.annotation(0, 9).status, Status::InvalidArgument);   // one past the end
    EXPECT_EQ(doc.annotation(1, 0).status, Status::InvalidArgument);
    EXPECT_EQ(doc.annotation(0, 100'000).status, Status::InvalidArgument);
}

TEST_F(AnnotationTest, AnInkStrokeIsReadInWindows) {
    Document doc = open(bytes_);
    auto first = doc.ink_stroke(0, 4, 0, 0, 2);
    ASSERT_EQ(first.status, Status::Ok);
    EXPECT_EQ(first.value.point_count, 3u);
    EXPECT_EQ(first.value.xs, (std::vector<float>{310.f, 320.f}));
    EXPECT_EQ(first.value.ys, (std::vector<float>{310.f, 330.f}));
    EXPECT_FALSE(first.value.has_widths);
    EXPECT_EQ(first.value.widths, (std::vector<float>{0.f, 0.f}));
    auto rest = doc.ink_stroke(0, 4, 0, 2, 1024);
    ASSERT_EQ(rest.status, Status::Ok);
    EXPECT_EQ(rest.value.xs, (std::vector<float>{330.f}));
    auto second = doc.ink_stroke(0, 4, 1, 0, 1024);
    ASSERT_EQ(second.status, Status::Ok);
    EXPECT_EQ(second.value.point_count, 2u);
    auto past = doc.ink_stroke(0, 4, 0, 3, 5);
    ASSERT_EQ(past.status, Status::Ok);
    EXPECT_TRUE(past.value.xs.empty());
    EXPECT_EQ(doc.ink_stroke(0, 4, 2, 0, 1).status, Status::InvalidArgument);   // no third stroke
    EXPECT_EQ(doc.ink_stroke(0, 0, 0, 0, 1).status, Status::InvalidArgument);   // the highlight is not ink
    EXPECT_EQ(doc.ink_stroke(0, 7, 0, 0, 1).status, Status::InvalidArgument);   // nor is a null
}

TEST_F(AnnotationTest, CountingLeavesOutLinksPopupsAndWidgets) {
    Document doc = open(bytes_);
    auto counts = doc.count_annotations(0, 1, kNotExpired);
    ASSERT_EQ(counts.status, Status::Ok);
    EXPECT_EQ(counts.value.counts, (std::vector<std::uint16_t>{7}));  // 9 entries, less the link and the null
    EXPECT_EQ(doc.count_annotations(0, 2, kNotExpired).status, Status::InvalidArgument);  // the document has one page
    EXPECT_EQ(doc.count_annotations(0, 0, kNotExpired).status, Status::InvalidArgument);
    EXPECT_EQ(doc.count_annotations(0, 1, [] { return true; }).status, Status::Timeout);
}

TEST_F(AnnotationTest, HidingAnAnnotationHidesItInTheTileAndTheFileKeepsItsOwnFlags) {
    Document doc = open(bytes_);
    const auto shown = tile(doc);
    EXPECT_TRUE(is_yellow(pixel(shown, 612 * 4, 200, 182)));  // the highlight's appearance

    ASSERT_EQ(doc.set_annotation_hidden(0, 0, true), Status::Ok);
    EXPECT_EQ(doc.mirrored_annotation_count(), 1u);
    const auto hidden = tile(doc);
    EXPECT_FALSE(is_yellow(pixel(hidden, 612 * 4, 200, 182)));  // gone from the picture

    // The listing and the full record still say what the file says, not what the screen shows.
    EXPECT_EQ(doc.list_annotations(0, 0, 1).value.flags[0], 4u);
    EXPECT_EQ(doc.annotation(0, 0).value.flags, 4u);

    // Hiding twice is the same as hiding once; showing puts the file's flag back and forgets the entry.
    ASSERT_EQ(doc.set_annotation_hidden(0, 0, true), Status::Ok);
    EXPECT_EQ(doc.mirrored_annotation_count(), 1u);
    ASSERT_EQ(doc.set_annotation_hidden(0, 0, false), Status::Ok);
    EXPECT_EQ(doc.mirrored_annotation_count(), 0u);
    EXPECT_TRUE(is_yellow(pixel(tile(doc), 612 * 4, 200, 182)));
    ASSERT_EQ(doc.set_annotation_hidden(0, 0, false), Status::Ok);  // already shown: nothing to do

    EXPECT_EQ(doc.set_annotation_hidden(0, 7, true), Status::InvalidArgument);    // the null entry
    EXPECT_EQ(doc.set_annotation_hidden(0, 9, true), Status::InvalidArgument);
    EXPECT_EQ(doc.set_annotation_hidden(1, 0, true), Status::InvalidArgument);
}

TEST_F(AnnotationTest, AMirrorFlagNeverReachesASavedFile) {
    // AC-14: hide, then save both ways. A fresh PDFium reading the output must find every annotation with the flags the file started with.
    for (const bool incremental : {true, false}) {
        Document doc = open(bytes_);
        for (const std::uint32_t index : {0u, 1u, 4u, 5u}) {
            ASSERT_EQ(doc.set_annotation_hidden(0, index, true), Status::Ok);
        }
        ASSERT_EQ(doc.mirrored_annotation_count(), 4u);
        const auto saved = save(doc, incremental);
        ASSERT_FALSE(saved.empty());

        // The screen is as it was: the flags were put back for the save and set again after it.
        EXPECT_EQ(doc.mirrored_annotation_count(), 4u);
        EXPECT_FALSE(is_yellow(pixel(tile(doc), 612 * 4, 200, 182)));

        Document reopened = open(saved);
        Document original = open(bytes_);
        auto before = original.list_annotations(0, 0, 64);
        auto after = reopened.list_annotations(0, 0, 64);
        ASSERT_EQ(before.status, Status::Ok);
        ASSERT_EQ(after.status, Status::Ok);
        EXPECT_EQ(after.value.total, before.value.total) << (incremental ? "incremental" : "full");
        EXPECT_EQ(after.value.flags, before.value.flags) << (incremental ? "incremental" : "full");
        EXPECT_TRUE(is_yellow(pixel(tile(reopened), 612 * 4, 200, 182))) << "the saved file still shows the highlight";
    }
}

TEST_F(AnnotationTest, AnAnnotationTheFileHidesAlreadyIsLeftAlone) {
    std::string text(bytes_.begin(), bytes_.end());
    const auto at = text.find("/NM (note-1)");
    ASSERT_NE(at, std::string::npos);
    // The note is flagged Hidden in the file (28 = print + nozoom + norotate, plus 2 = hidden is 30): same length of text, so the xref offsets still hold.
    const auto flags = text.rfind("/F 28", at);
    ASSERT_NE(flags, std::string::npos);
    text.replace(flags, 5, "/F 30");
    Document doc = open(support::bytes_of(text));
    ASSERT_EQ(doc.set_annotation_hidden(0, 1, true), Status::Ok);
    EXPECT_EQ(doc.mirrored_annotation_count(), 0u);   // nothing was changed, so nothing is remembered
    ASSERT_EQ(doc.set_annotation_hidden(0, 1, false), Status::Ok);
    EXPECT_EQ(doc.list_annotations(0, 1, 1).value.flags[0], 30u);  // and showing it does not override the file
}

TEST_F(AnnotationTest, ABigListIsBoundedAndTheNullEntriesKeepTheirIndex) {
    // 100,000 entries and a few more: the engine counts at most 100,000.
    Document doc = open(support::bytes_of(build_annotated(100'100)));
    auto list = doc.list_annotations(0, 99'990, 64);
    ASSERT_EQ(list.status, Status::Ok);
    EXPECT_EQ(list.value.total, 100'000u);
    EXPECT_EQ(list.value.subtypes.size(), 10u);  // 99,990 to 99,999
    EXPECT_EQ(doc.annotation(0, 99'999).status, Status::InvalidArgument);  // a null entry
    EXPECT_EQ(doc.set_annotation_hidden(0, 100'000 - 1, true), Status::InvalidArgument);
}

TEST_F(AnnotationTest, HostileValuesAreClampedAndBounded) {
    PdfBuilder b;
    const int catalog = b.reserve();
    const int pages = b.reserve();
    const int page = b.reserve();
    const int content = b.add_stream("q Q");
    std::string huge(4 * 1024 * 1024 + 100, 'x');
    const int a = b.add("<< /Type /Annot /Subtype /Square /Rect [99999999999.0 5 -99999999999.0 7] /C [2 -1 0.5] /CA 7 /NM (" + std::string(200, 'n') + ") /Contents (" + huge + ") /T (" + std::string(1000, 't') + ") >>");
    const int c = b.add("<< /Type /Annot /Subtype /Polygon /Rect [0 0 1 1] /Vertices [" + [] { std::string v; for (int i = 0; i < 3000; ++i) v += std::to_string(i) + " " + std::to_string(i) + " "; return v; }() + "] >>");
    b.set(catalog, "<< /Type /Catalog /Pages " + std::to_string(pages) + " 0 R >>");
    b.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 /MediaBox [0 0 612 792] >>");
    b.set(page, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /Contents " + std::to_string(content) + " 0 R /Annots [" + std::to_string(a) + " 0 R " + std::to_string(c) + " 0 R] >>");
    Document doc = open(support::bytes_of(b.build(catalog)));

    auto list = doc.list_annotations(0, 0, 64);
    ASSERT_EQ(list.status, Status::Ok);
    EXPECT_FLOAT_EQ(list.value.lefts[0], -1'000'000.f);   // clamped, and normalised: left <= right
    EXPECT_FLOAT_EQ(list.value.rights[0], 1'000'000.f);
    EXPECT_EQ(list.value.nms[0], "");                      // an identifier of 200 bytes is not one of ours
    EXPECT_EQ(list.value.snippets[0], "");                 // too large to read for a list
    EXPECT_EQ(list.value.authors[0].size(), 128u);

    auto full = doc.annotation(0, 0);
    ASSERT_EQ(full.status, Status::Ok);
    EXPECT_TRUE(full.value.contents_truncated);
    EXPECT_EQ(full.value.contents, "");
    EXPECT_LE(full.value.opacity, 1.0f);
    EXPECT_EQ(full.value.author.size(), 256u);

    auto polygon = doc.annotation(0, 1);
    ASSERT_EQ(polygon.status, Status::Ok);
    EXPECT_EQ(polygon.value.vertices.size(), 2048u);
    EXPECT_TRUE(polygon.value.vertices_truncated);
}

}  // namespace
