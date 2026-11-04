// GetTextQuads (schema 7, docs/features/text-markup.md 2.3). Criteria TM-01 (the codec half is in protocol_tests.cpp), TM-02 to TM-05.
// The pages are drawn with Helvetica, which PDFium has built in, so the character widths are the font's own (Helvetica AFM units per 1000 em).
#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <deque>
#include <numbers>
#include <string>
#include <vector>

#include "mikepdf/pdfium/document.h"
#include "mikepdf/pdfium/library.h"
#include "test_support.h"

namespace {

using namespace mikepdf;
using pdfium::Document;
using protocol::Status;
using support::PdfBuilder;

struct Point {
    double x, y;
};

Point operator-(Point a, Point b) { return {a.x - b.x, a.y - b.y}; }
double length(Point a) { return std::hypot(a.x, a.y); }
double dot(Point a, Point b) { return a.x * b.x + a.y * b.y; }

// One page of text: `content` is the page's content stream; `page_extra` adds entries to the page dictionary (/Rotate, /CropBox).
std::vector<std::uint8_t> text_page(const std::string& content, const std::string& page_extra = "") {
    PdfBuilder b;
    const int catalog = b.reserve();
    const int pages = b.reserve();
    const int page = b.reserve();
    const int font = b.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    const int stream = b.add_stream(content);
    b.set(catalog, "<< /Type /Catalog /Pages " + std::to_string(pages) + " 0 R >>");
    b.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 /MediaBox [0 0 612 792] >>");
    b.set(page, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /Contents " + std::to_string(stream) + " 0 R /Resources << /Font << /F1 " + std::to_string(font) + " 0 R >> >> " + page_extra + " >>");
    return support::bytes_of(b.build(catalog));
}

class TextQuadsTest : public ::testing::Test {
protected:
    pdfium::Library library_;

    // PDFium reads the bytes in place, so they have to outlive the document: they are kept here until the test ends.
    Document open(std::vector<std::uint8_t> bytes) {
        kept_.push_back(std::move(bytes));
        auto opened = Document::open(kept_.back(), {});
        EXPECT_EQ(opened.status, Status::Ok);
        return std::move(opened.value);
    }
    std::deque<std::vector<std::uint8_t>> kept_;

    // The whole text of page 0 as PDFium numbers it, so that a test can find a word by searching instead of counting.
    static std::u16string text_of(Document& doc) {
        auto text = doc.page_text(0, 0, 2048);
        EXPECT_EQ(text.status, Status::Ok);
        return {text.value.units.begin(), text.value.units.end()};
    }

    struct Range {
        std::uint32_t start;
        std::uint32_t count;
    };

    static Range find(Document& doc, const std::u16string& word) {
        const std::u16string text = text_of(doc);
        const auto at = text.find(word);
        EXPECT_NE(at, std::u16string::npos);
        return {static_cast<std::uint32_t>(at), static_cast<std::uint32_t>(word.size())};
    }

    // All quads of a range, windowed as the broker asks for them.
    static std::vector<std::array<Point, 4>> quads(Document& doc, Range range, bool* truncated = nullptr) {
        std::vector<std::array<Point, 4>> all;
        std::uint32_t first = 0;
        for (;;) {
            auto reply = doc.text_quads(0, range.start, range.count, first);
            EXPECT_EQ(reply.status, Status::Ok);
            if (reply.status != Status::Ok) {
                break;
            }
            EXPECT_EQ(reply.value.xs.size(), reply.value.ys.size());
            EXPECT_EQ(reply.value.xs.size() % 4, 0u);
            for (std::size_t i = 0; i + 3 < reply.value.xs.size(); i += 4) {
                all.push_back({Point{reply.value.xs[i], reply.value.ys[i]}, Point{reply.value.xs[i + 1], reply.value.ys[i + 1]}, Point{reply.value.xs[i + 2], reply.value.ys[i + 2]},
                               Point{reply.value.xs[i + 3], reply.value.ys[i + 3]}});
            }
            first += static_cast<std::uint32_t>(reply.value.xs.size() / 4);
            if (truncated != nullptr) {
                *truncated = reply.value.truncated;
            }
            if (reply.value.xs.empty() || first >= reply.value.total_quads) {
                break;
            }
        }
        return all;
    }
};

TEST_F(TextQuadsTest, ASingleLineIsOneQuadInTheOrderTopLeftTopRightBottomLeftBottomRight) {
    // TM-02. "Hello world" at 24 pt: Helvetica advances 722 556 222 222 556 278 722 556 333 222 556 = 4945 units, 118.68 pt.
    Document doc = open(text_page("BT /F1 24 Tf 72 700 Td (Hello world) Tj ET"));
    const auto range = find(doc, u"Hello world");
    const auto q = quads(doc, range);
    ASSERT_EQ(q.size(), 1u);
    const auto& [tl, tr, bl, br] = q[0];
    EXPECT_NEAR(tl.x, 72.0, 0.6);
    EXPECT_NEAR(tr.x - tl.x, 118.68, 1.0);
    EXPECT_NEAR(bl.x, tl.x, 1e-3);
    EXPECT_NEAR(br.x, tr.x, 1e-3);
    EXPECT_NEAR(tr.y, tl.y, 1e-3);
    EXPECT_NEAR(br.y, bl.y, 1e-3);
    EXPECT_GT(tl.y, bl.y);                 // top above bottom
    EXPECT_LT(bl.y, 700.0);                // the descent is below the baseline
    EXPECT_GT(tl.y, 700.0 + 12.0);         // and the top is a good way above it
    EXPECT_LT(tl.y - bl.y, 24.0 * 1.4);    // the height is the font's, not the page's

    // A reply never holds more than 64 quads and the second window of a one-quad range is empty.
    auto second = doc.text_quads(0, range.start, range.count, 1);
    ASSERT_EQ(second.status, Status::Ok);
    EXPECT_EQ(second.value.total_quads, 1u);
    EXPECT_TRUE(second.value.xs.empty());
    EXPECT_EQ(doc.text_quads(0, range.start, range.count, 2).status, Status::InvalidArgument);
}

TEST_F(TextQuadsTest, WhitespaceAtTheStartOfALineIsNotMarkedAndSpacesInsideStay) {
    // "  Hello world": a space is 278 units, 6.67 pt at 24 pt, so the marked quad starts at the H, not at the margin, and keeps the space between the words.
    Document doc = open(text_page("BT /F1 24 Tf 72 700 Td (  Hello world) Tj ET"));
    const std::u16string text = text_of(doc);
    ASSERT_GE(text.size(), 11u);
    const auto all = quads(doc, {0, static_cast<std::uint32_t>(text.size())});
    ASSERT_EQ(all.size(), 1u);
    EXPECT_GT(all[0][0].x, 72.0 + 5.0);  // at least one of the spaces is left out
    EXPECT_NEAR(all[0][1].x - all[0][0].x, 4945 * 24.0 / 1000.0, 1.0);
}

TEST_F(TextQuadsTest, SeveralLinesAreOneQuadEachAndTheLineBreakPDFiumMakesUpIsNeverMarked) {
    // TM-03.
    Document doc = open(text_page("BT /F1 24 Tf 72 700 Td (First line) Tj 0 -40 Td (Second line) Tj 0 -40 Td (Third) Tj ET"));
    const std::u16string text = text_of(doc);
    const auto all = quads(doc, {0, static_cast<std::uint32_t>(text.size())});
    ASSERT_EQ(all.size(), 3u);
    EXPECT_GT(all[0][0].y, all[1][0].y);
    EXPECT_GT(all[1][0].y, all[2][0].y);
    EXPECT_NEAR(all[0][0].y - all[1][0].y, 40.0, 0.5);
    // "First line" is 10 characters: F 611 i 222 r 333 s 500 t 278 space 278 l 222 i 222 n 556 e 556 = 3778.
    EXPECT_NEAR(all[0][1].x - all[0][0].x, 3778 * 24.0 / 1000.0, 1.0);
    // Starting the range in the middle of the first line and ending in the middle of the second gives two quads, each only as long as its part.
    const auto from = static_cast<std::uint32_t>(text.find(u"line"));
    const auto to = static_cast<std::uint32_t>(text.find(u"Second") + 3);
    const auto part = quads(doc, {from, to - from + 1});
    ASSERT_EQ(part.size(), 2u);
    EXPECT_LT(part[0][1].x - part[0][0].x, all[0][1].x - all[0][0].x);
    EXPECT_NEAR(part[1][0].x, 72.0, 0.6);
}

TEST_F(TextQuadsTest, ALineEndHyphenIsMarked) {
    // PDFium reports a hyphen at the end of a line with its own flag (bit 1 of GetPageText); whatever character it puts there, it is a character of the range and is marked.
    Document doc = open(text_page("BT /F1 24 Tf 72 700 Td (abc-) Tj 0 -40 Td (def) Tj ET"));
    auto text = doc.page_text(0, 0, 2048);
    ASSERT_EQ(text.status, Status::Ok);
    std::uint32_t hyphen = 0;
    bool found = false;
    for (std::uint32_t i = 0; i < text.value.flags.size(); ++i) {
        if ((text.value.flags[i] & 2) != 0) {
            hyphen = i;
            found = true;
            break;
        }
    }
    ASSERT_TRUE(found) << "PDFium found no line-end hyphen";
    const auto line = quads(doc, {0, hyphen + 1});
    ASSERT_EQ(line.size(), 1u);
    const auto without = quads(doc, {0, hyphen});
    ASSERT_EQ(without.size(), 1u);
    EXPECT_GT(line[0][1].x, without[0][1].x + 1.0);  // the hyphen widens the quad
}

TEST_F(TextQuadsTest, RotatedTextGivesRotatedQuadsInTheTextsOwnFrame) {
    // TM-04. 30 degrees: "Rotated text" at 24 pt is 5392 units wide, 129.4 pt.
    const double c = std::cos(std::numbers::pi / 6);
    const double s = std::sin(std::numbers::pi / 6);
    Document doc = open(text_page("BT /F1 24 Tf " + std::to_string(c) + " " + std::to_string(s) + " " + std::to_string(-s) + " " + std::to_string(c) + " 300 300 Tm (Rotated text) Tj ET"));
    auto q = quads(doc, find(doc, u"Rotated text"));
    ASSERT_EQ(q.size(), 1u);
    {
        const auto& [tl, tr, bl, br] = q[0];
        const Point u{c, s};
        const Point v{-s, c};
        EXPECT_NEAR(dot(tr - tl, u), 129.4, 2.5);        // along the baseline
        EXPECT_NEAR(dot(tr - tl, v), 0.0, 0.5);          // and not across it
        EXPECT_LT(dot(bl - tl, v), -15.0);               // the bottom is below the top in the text's frame
        EXPECT_NEAR(dot(bl - tl, u), 0.0, 0.5);
        EXPECT_NEAR(length(br - bl), length(tr - tl), 0.5);  // a rectangle
        EXPECT_NEAR(dot(tr - tl, u) / length(tr - tl), 1.0, 1e-4);
    }

    // 90 degrees, reading upwards: the top-left of the text's own frame is the lower left on the page.
    Document up = open(text_page("BT /F1 24 Tf 0 1 -1 0 500 300 Tm (Upward) Tj ET"));
    q = quads(up, find(up, u"Upward"));
    ASSERT_EQ(q.size(), 1u);
    {
        const auto& [tl, tr, bl, br] = q[0];
        EXPECT_NEAR(tr.x, tl.x, 1e-3);
        EXPECT_GT(tr.y - tl.y, 60.0);                    // up the page: U 722 p 556 w 722 a 556 r 333 d 556 = 3445, 82.7 pt
        EXPECT_NEAR(tr.y - tl.y, 3445 * 24.0 / 1000.0, 1.5);
        EXPECT_GT(bl.x - tl.x, 15.0);                    // up in the text's frame is v = (-1, 0), so its bottom is further right on the page
        EXPECT_NEAR(bl.y, tl.y, 1e-3);
    }
}

TEST_F(TextQuadsTest, AColumnOfVerticalCharactersIsOneQuad) {
    // Vertical writing as PDFium reads it: each character on its own line, stacked, at the same horizontal place.
    Document doc = open(text_page("BT /F1 24 Tf 100 700 Td (A) Tj 0 -26 Td (B) Tj 0 -26 Td (C) Tj ET"));
    const std::u16string text = text_of(doc);
    const auto all = quads(doc, {0, static_cast<std::uint32_t>(text.size())});
    // Three characters stacked closer than a line apart are read as a column when they share a horizontal place; whatever PDFium makes of them, nothing is lost or invented.
    ASSERT_FALSE(all.empty());
    double top = -1e9;
    double bottom = 1e9;
    for (const auto& quad : all) {
        for (const Point& p : quad) {
            top = std::max(top, p.y);
            bottom = std::min(bottom, p.y);
        }
    }
    EXPECT_GT(top, 700.0);
    EXPECT_LT(bottom, 700.0 - 52.0);
}

TEST_F(TextQuadsTest, QuadsAreInUserSpaceWhateverTheRotationAndTheCropBox) {
    // TM-05. The same text on a page rotated by each quarter turn and cropped at (50, 30): the same quads.
    const std::string content = "BT /F1 24 Tf 72 100 Td (Hello) Tj ET";
    Document plain = open(text_page(content));
    const auto want = quads(plain, find(plain, u"Hello"));
    ASSERT_EQ(want.size(), 1u);
    for (const int rotate : {0, 90, 180, 270}) {
        Document doc = open(text_page(content, "/Rotate " + std::to_string(rotate) + " /CropBox [50 30 350 230]"));
        const auto got = quads(doc, find(doc, u"Hello"));
        ASSERT_EQ(got.size(), 1u) << rotate;
        for (int corner = 0; corner < 4; ++corner) {
            EXPECT_NEAR(got[0][corner].x, want[0][corner].x, 0.01) << rotate << " corner " << corner;
            EXPECT_NEAR(got[0][corner].y, want[0][corner].y, 0.01) << rotate << " corner " << corner;
        }
    }
}

TEST_F(TextQuadsTest, ARangeIsCutAtTheEndOfTheTextAndBadRangesAreRefused) {
    Document doc = open(text_page("BT /F1 24 Tf 72 700 Td (Hello) Tj ET"));
    const auto whole = doc.text_quads(0, 0, 200'000, 0);
    ASSERT_EQ(whole.status, Status::Ok);
    EXPECT_EQ(whole.value.total_quads, 1u);
    EXPECT_FALSE(whole.value.truncated);
    EXPECT_EQ(doc.text_quads(0, 5000, 10, 0).status, Status::InvalidArgument);   // a start past the text
    EXPECT_EQ(doc.text_quads(1, 0, 10, 0).status, Status::InvalidArgument);      // no such page
    EXPECT_EQ(doc.text_quads(0, 0, 0, 0).status, Status::InvalidArgument);
    EXPECT_EQ(doc.text_quads(0, 0, 10, 4096).status, Status::InvalidArgument);
    EXPECT_EQ(doc.text_quads(0, 200'000, 1, 0).status, Status::InvalidArgument);
}

TEST_F(TextQuadsTest, AManyLineRangeIsReadInWindowsOf64AndStopsAt4096) {
    // 70 lines are two windows; the second holds the last 6 quads.
    std::string content = "BT /F1 8 Tf 12 TL 10 780 Td\n";
    for (int i = 0; i < 70; ++i) {
        content += "(line) Tj T*\n";
    }
    content += "ET";
    Document doc = open(text_page(content, "/MediaBox [0 0 612 900]"));
    const auto first = doc.text_quads(0, 0, 200'000, 0);
    ASSERT_EQ(first.status, Status::Ok);
    EXPECT_EQ(first.value.total_quads, 70u);
    EXPECT_EQ(first.value.xs.size(), 64u * 4);
    const auto second = doc.text_quads(0, 0, 200'000, 64);
    ASSERT_EQ(second.status, Status::Ok);
    EXPECT_EQ(second.value.xs.size(), 6u * 4);
}

}  // namespace
