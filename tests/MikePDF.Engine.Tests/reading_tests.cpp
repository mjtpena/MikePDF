// Tests for the reading services of src/MikePDF.Engine.Pdfium (outline, links, page boxes, page text, search) and for the way EngineCore
// serves them. Every reply is also checked against its schema limits: the encoder would refuse anything else, and a refused reply is a
// failed request for the person reading.
#include <gtest/gtest.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "engine_core.h"
#include "mikepdf/pdfium/document.h"
#include "mikepdf/pdfium/library.h"
#include "test_support.h"

namespace {

using namespace mikepdf;
using engine::Clock;
using engine::EngineCore;
using engine::EngineKind;
using pdfium::Document;
using protocol::MessageType;
using protocol::Status;
using support::PdfBuilder;

std::string text_of(const protocol::GetPageTextReply& reply) {
    std::string out;
    for (std::size_t i = 0; i < reply.units.size(); ++i) {
        if ((reply.flags[i] & 1) == 0 && reply.units[i] < 0x80) {
            out += static_cast<char>(reply.units[i]);
        }
    }
    return out;
}

// Three pages. Page 1 (A) has text and seven links, page 2 (B) is small, page 3 (C) has a CropBox, /Rotate 90 and text.
// The outline: "Chapter One" (two children), "Chapter Two", and a title in UTF-16 that points at a web address.
std::string reader_pdf() {
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int page_a = pdf.reserve();
    const int page_b = pdf.reserve();
    const int page_c = pdf.reserve();
    const int font = pdf.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
    const int content_a = pdf.add_stream("BT /F1 12 Tf 72 700 Td (Hello World) Tj ET\nBT /F1 12 Tf 72 680 Td (second line of text) Tj ET");
    const int content_b = pdf.add_stream("BT /F1 12 Tf 20 350 Td (Page two here) Tj ET");
    const int content_c = pdf.add_stream("BT /F1 20 Tf 150 300 Td (Rotated) Tj ET");
    const int outlines = pdf.reserve();
    const int one = pdf.reserve();
    const int two = pdf.reserve();
    const int three = pdf.reserve();
    const int one_a = pdf.reserve();
    const int one_b = pdf.reserve();

    const std::string b = std::to_string(page_b) + " 0 R";
    const std::string c = std::to_string(page_c) + " 0 R";
    const auto link = [&](const std::string& rect, const std::string& rest) {
        return pdf.add("<< /Type /Annot /Subtype /Link /Rect [" + rect + "] /Border [0 0 0] " + rest + " >>");
    };
    const int l1 = link("72 695 160 712", "/A << /S /GoTo /D [" + b + " /XYZ 20 350 0] >>");
    const int l2 = link("72 660 200 676", "/A << /S /URI /URI (https://example.org/a%20b) >>");
    const int l3 = link("10 10 50 30", "/Dest [" + c + " /FitH 500]");
    const int l4 = link("60 10 90 30", "/A << /S /Launch /F (calc.exe) >>");
    const int l5 = link("100 10 130 30", "/Dest [" + b + " /Fit]");
    const int l6 = link("140 10 170 30", "/A << /S /URI /URI (https://example.org/\\001x) >>");
    const int l7 = link("180 10 210 30", "/Dest [999 0 R /Fit]");
    const std::string annots = std::to_string(l1) + " 0 R " + std::to_string(l2) + " 0 R " + std::to_string(l3) + " 0 R " + std::to_string(l4) + " 0 R " +
                               std::to_string(l5) + " 0 R " + std::to_string(l6) + " 0 R " + std::to_string(l7) + " 0 R";

    pdf.set(catalog, "<< /Type /Catalog /Pages " + std::to_string(pages) + " 0 R /Outlines " + std::to_string(outlines) + " 0 R >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page_a) + " 0 R " + b + " " + c + "] /Count 3 >>");
    const std::string resources = "/Resources << /Font << /F1 " + std::to_string(font) + " 0 R >> >>";
    pdf.set(page_a, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /MediaBox [0 0 612 792] " + resources + " /Contents " +
                        std::to_string(content_a) + " 0 R /Annots [" + annots + "] >>");
    pdf.set(page_b, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /MediaBox [0 0 300 400] " + resources + " /Contents " +
                        std::to_string(content_b) + " 0 R >>");
    pdf.set(page_c, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /MediaBox [0 0 612 792] /CropBox [100 150 500 650] /Rotate 90 " + resources +
                        " /Contents " + std::to_string(content_c) + " 0 R >>");

    const std::string a = std::to_string(page_a) + " 0 R";
    pdf.set(outlines, "<< /Type /Outlines /First " + std::to_string(one) + " 0 R /Last " + std::to_string(three) + " 0 R /Count 3 >>");
    pdf.set(one, "<< /Title (Chapter One) /Parent " + std::to_string(outlines) + " 0 R /Next " + std::to_string(two) + " 0 R /First " +
                     std::to_string(one_a) + " 0 R /Last " + std::to_string(one_b) + " 0 R /Count 2 /Dest [" + a + " /XYZ 72 700 0] >>");
    pdf.set(two, "<< /Title (Chapter Two) /Parent " + std::to_string(outlines) + " 0 R /Prev " + std::to_string(one) + " 0 R /Next " +
                     std::to_string(three) + " 0 R /A << /S /GoTo /D [" + b + " /Fit] >> >>");
    pdf.set(three, "<< /Title <FEFF00DC006E00690063006F00640065002027 13> /Parent " + std::to_string(outlines) + " 0 R /Prev " + std::to_string(two) +
                       " 0 R /A << /S /URI /URI (https://example.org/) >> >>");
    pdf.set(one_a, "<< /Title (Section 1.1) /Parent " + std::to_string(one) + " 0 R /Next " + std::to_string(one_b) + " 0 R /Dest [" + b + " /FitH 300] >>");
    pdf.set(one_b, "<< /Title (Section 1.2) /Parent " + std::to_string(one) + " 0 R /Prev " + std::to_string(one_a) + " 0 R /Dest [" + c +
                       " /XYZ null 400 null] >>");
    return pdf.build(catalog);
}

std::vector<std::uint8_t> bytes(const std::string& text) { return {text.begin(), text.end()}; }

class ReadingTest : public ::testing::Test {
protected:
    ReadingTest() : storage_(bytes(reader_pdf())) {
        auto opened = Document::open(storage_, {});
        EXPECT_EQ(opened.status, Status::Ok);
        doc_ = std::move(opened.value);
    }

    pdfium::Library library_;
    std::vector<std::uint8_t> storage_;
    Document doc_;
};

// ---- outline -------------------------------------------------------------------------------------------------------------

TEST_F(ReadingTest, TheTopLevelOfTheOutlineHasTitlesDestinationsAndWhetherThereAreChildren) {
    const auto r = doc_.outline({}, 0, 64);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    EXPECT_EQ(r.value.total, 3u);
    ASSERT_EQ(r.value.titles.size(), 3u);
    EXPECT_EQ(r.value.titles[0], "Chapter One");
    EXPECT_EQ(r.value.titles[1], "Chapter Two");
    EXPECT_EQ(r.value.titles[2], "\xC3\x9Cnicode \xE2\x9C\x93");  // U+00DC, 'nicode', U+2713 from a UTF-16 title
    EXPECT_EQ((std::vector<std::uint16_t>{3, 1, 4}), r.value.kinds);
    EXPECT_EQ((std::vector<std::uint16_t>{1, 0, 0}), r.value.has_children);
    EXPECT_EQ(r.value.pages[0], 0u);
    EXPECT_EQ(r.value.pages[1], 1u);
    EXPECT_FLOAT_EQ(r.value.xs[0], 72.f);
    EXPECT_FLOAT_EQ(r.value.ys[0], 700.f);
}

TEST_F(ReadingTest, AWindowOfSiblingsStillReportsTheTotal) {
    const auto r = doc_.outline({}, 1, 1);
    ASSERT_EQ(r.status, Status::Ok);
    ASSERT_EQ(r.value.titles.size(), 1u);
    EXPECT_EQ(r.value.titles[0], "Chapter Two");
    EXPECT_EQ(r.value.total, 3u);

    const auto past = doc_.outline({}, 50, 10);
    ASSERT_EQ(past.status, Status::Ok);
    EXPECT_TRUE(past.value.titles.empty());
    EXPECT_EQ(past.value.total, 3u);
}

TEST_F(ReadingTest, ChildrenAreAddressedByTheirPositionAndReportPositionsWithinThePage) {
    const std::vector<std::uint32_t> path{0};
    const auto r = doc_.outline(path, 0, 64);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    ASSERT_EQ(r.value.titles.size(), 2u);
    EXPECT_EQ(r.value.titles[0], "Section 1.1");
    EXPECT_EQ(r.value.titles[1], "Section 1.2");
    EXPECT_EQ(r.value.pages[0], 1u);
    EXPECT_EQ(r.value.pages[1], 2u);
    // [/FitH 300] gives a y; [/XYZ null 400 null] gives a y and no x.
    EXPECT_EQ(r.value.kinds[0], 2);
    EXPECT_FLOAT_EQ(r.value.ys[0], 300.f);
    EXPECT_EQ(r.value.kinds[1], 2);
    EXPECT_FLOAT_EQ(r.value.ys[1], 400.f);
}

TEST_F(ReadingTest, APathThatLeadsNowhereIsInvalid) {
    EXPECT_EQ(doc_.outline(std::vector<std::uint32_t>{7}, 0, 1).status, Status::InvalidArgument);
    EXPECT_EQ(doc_.outline(std::vector<std::uint32_t>{0, 5}, 0, 1).status, Status::InvalidArgument);
    // A leaf has an empty list of children, not an error.
    const auto leaf = doc_.outline(std::vector<std::uint32_t>{1}, 0, 1);
    ASSERT_EQ(leaf.status, Status::Ok);
    EXPECT_EQ(leaf.value.total, 0u);
    EXPECT_EQ(doc_.outline({}, 0, 0).status, Status::InvalidArgument);
    EXPECT_EQ(doc_.outline({}, 0, 65).status, Status::InvalidArgument);
    EXPECT_EQ(doc_.outline(std::vector<std::uint32_t>(17, 0), 0, 1).status, Status::InvalidArgument);
}

TEST_F(ReadingTest, ADocumentWithoutAnOutlineHasAnEmptyTopLevel) {
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int page = pdf.add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
    pdf.set(catalog, "<< /Type /Catalog /Pages 2 0 R >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 >>");
    const auto data = bytes(pdf.build(catalog));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.outline({}, 0, 64);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.total, 0u);
    EXPECT_TRUE(r.value.titles.empty());
}

TEST_F(ReadingTest, ARingOfSiblingsEndsInsteadOfLooping) {
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int page = pdf.add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
    const int outlines = pdf.reserve();
    const int a = pdf.reserve();
    const int b = pdf.reserve();
    pdf.set(catalog, "<< /Type /Catalog /Pages 2 0 R /Outlines " + std::to_string(outlines) + " 0 R >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 >>");
    pdf.set(outlines, "<< /Type /Outlines /First " + std::to_string(a) + " 0 R /Last " + std::to_string(b) + " 0 R /Count 2 >>");
    pdf.set(a, "<< /Title (A) /Parent " + std::to_string(outlines) + " 0 R /Next " + std::to_string(b) + " 0 R >>");
    pdf.set(b, "<< /Title (B) /Parent " + std::to_string(outlines) + " 0 R /Next " + std::to_string(a) + " 0 R >>");
    const auto data = bytes(pdf.build(catalog));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.outline({}, 0, 64);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.total, 2u);
    EXPECT_EQ(r.value.validate(), Status::Ok);
}

TEST_F(ReadingTest, AnOutlineTitleIsCutToTheSchemaLimitOnACharacterBoundary) {
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int page = pdf.add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
    const int outlines = pdf.reserve();
    const int a = pdf.reserve();
    // 300 copies of U+00E9 (two bytes in UTF-8) as UTF-16BE: 600 bytes of UTF-8 would be too long.
    std::string title = "<FEFF";
    for (int i = 0; i < 300; ++i) {
        title += "00E9";
    }
    title += ">";
    pdf.set(catalog, "<< /Type /Catalog /Pages 2 0 R /Outlines " + std::to_string(outlines) + " 0 R >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 >>");
    pdf.set(outlines, "<< /Type /Outlines /First " + std::to_string(a) + " 0 R /Last " + std::to_string(a) + " 0 R /Count 1 >>");
    pdf.set(a, "<< /Title " + title + " /Parent " + std::to_string(outlines) + " 0 R >>");
    const auto data = bytes(pdf.build(catalog));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.outline({}, 0, 1);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.titles[0].size(), 256u);
    EXPECT_EQ(r.value.validate(), Status::Ok);
}

// ---- links ---------------------------------------------------------------------------------------------------------------

TEST_F(ReadingTest, LinksReportTheirRectangleAndWhereTheyGo) {
    const auto r = doc_.page_links(0, 0, 512);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    EXPECT_EQ(r.value.total, 7u);
    ASSERT_EQ(r.value.lefts.size(), 7u);
    // Link order is the order PDFium enumerates the page's annotations.
    EXPECT_EQ((std::vector<std::uint16_t>{3, 4, 2, 5, 1, 4, 0}), r.value.kinds);
    EXPECT_EQ(r.value.pages[0], 1u);
    EXPECT_FLOAT_EQ(r.value.xs[0], 20.f);
    EXPECT_FLOAT_EQ(r.value.ys[0], 350.f);
    EXPECT_FLOAT_EQ(r.value.lefts[0], 72.f);
    EXPECT_FLOAT_EQ(r.value.bottoms[0], 695.f);
    EXPECT_FLOAT_EQ(r.value.rights[0], 160.f);
    EXPECT_FLOAT_EQ(r.value.tops[0], 712.f);
    EXPECT_EQ(r.value.pages[2], 2u);
    EXPECT_FLOAT_EQ(r.value.ys[2], 500.f);
}

TEST_F(ReadingTest, AWindowOfLinksStillReportsTheTotal) {
    const auto r = doc_.page_links(0, 5, 10);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.total, 7u);
    EXPECT_EQ(r.value.lefts.size(), 2u);
    const auto none = doc_.page_links(1, 0, 10);
    ASSERT_EQ(none.status, Status::Ok);
    EXPECT_EQ(none.value.total, 0u);
}

TEST_F(ReadingTest, TheAddressOfAnExternalLinkIsReturnedOnlyWhenItIsPlainText) {
    const auto ok = doc_.link_uri(0, 1);
    ASSERT_EQ(ok.status, Status::Ok);
    EXPECT_EQ(ok.value.uri, "https://example.org/a%20b");
    EXPECT_EQ(ok.value.validate(), Status::Ok);
    EXPECT_EQ(doc_.link_uri(0, 0).status, Status::InvalidArgument);   // an internal link
    EXPECT_EQ(doc_.link_uri(0, 3).status, Status::InvalidArgument);   // a launch action
    EXPECT_EQ(doc_.link_uri(0, 5).status, Status::Unsupported);       // a control character in the address
    EXPECT_EQ(doc_.link_uri(0, 99).status, Status::InvalidArgument);  // no such link
    EXPECT_EQ(doc_.link_uri(9, 0).status, Status::InvalidArgument);   // no such page
}

TEST_F(ReadingTest, AnAddressLongerThanTheSchemaAllowsIsRefused) {
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int annot = pdf.add("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (https://example.org/" + std::string(3000, 'a') + ") >> >>");
    const int page = pdf.add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [" + std::to_string(annot) + " 0 R] >>");
    pdf.set(catalog, "<< /Type /Catalog /Pages 2 0 R >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 >>");
    const auto data = bytes(pdf.build(catalog));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    EXPECT_EQ(opened.value.link_uri(0, 0).status, Status::Unsupported);
}

// ---- boxes ---------------------------------------------------------------------------------------------------------------

TEST_F(ReadingTest, PageBoxesGiveTheVisibleBoxAndTheRotation) {
    const auto plain = doc_.page_boxes(1);
    ASSERT_EQ(plain.status, Status::Ok);
    EXPECT_EQ(plain.value.validate(), Status::Ok);
    EXPECT_FLOAT_EQ(plain.value.media_right, 300.f);
    EXPECT_FLOAT_EQ(plain.value.media_top, 400.f);
    EXPECT_EQ(plain.value.rotation, 0);

    const auto cropped = doc_.page_boxes(2);
    ASSERT_EQ(cropped.status, Status::Ok);
    EXPECT_TRUE(cropped.value.has_crop_box);
    EXPECT_FLOAT_EQ(cropped.value.media_right, 612.f);
    EXPECT_FLOAT_EQ(cropped.value.crop_left, 100.f);
    EXPECT_FLOAT_EQ(cropped.value.crop_bottom, 150.f);
    EXPECT_FLOAT_EQ(cropped.value.crop_right, 500.f);
    EXPECT_FLOAT_EQ(cropped.value.crop_top, 650.f);
    EXPECT_EQ(cropped.value.rotation, 1);
    EXPECT_EQ(doc_.page_boxes(3).status, Status::InvalidArgument);
}

TEST_F(ReadingTest, AMediaBoxInheritedFromThePagesNodeIsFound) {
    // support::build_pdf puts the MediaBox on /Pages, not on the page.
    const auto data = support::bytes_of(support::build_pdf("0 0 10 10 re f", "0 0 321 123"));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.page_boxes(0);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_FLOAT_EQ(r.value.crop_right, 321.f);
    EXPECT_FLOAT_EQ(r.value.crop_top, 123.f);
}

// ---- text ----------------------------------------------------------------------------------------------------------------

TEST_F(ReadingTest, PageTextHasOneEntryPerCharacterWithABoxAroundIt) {
    const auto r = doc_.page_text(0, 0, 2048);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    EXPECT_FALSE(r.value.truncated);
    EXPECT_EQ(r.value.units.size(), r.value.total);
    EXPECT_NE(text_of(r.value).find("Hello World"), std::string::npos);
    EXPECT_NE(text_of(r.value).find("second line of text"), std::string::npos);
    // "H" is the first character: its box starts at the text origin (x 72) and straddles the baseline (y 700).
    EXPECT_EQ(r.value.units[0], static_cast<std::uint16_t>('H'));
    EXPECT_NEAR(r.value.lefts[0], 72.f, 1.f);
    EXPECT_LT(r.value.bottoms[0], 700.f);
    EXPECT_GT(r.value.tops[0], 700.f);
    EXPECT_GT(r.value.rights[0], r.value.lefts[0]);
    // The line break PDFium makes up between the two lines is marked as generated.
    const auto generated = std::count_if(r.value.flags.begin(), r.value.flags.end(), [](std::uint16_t f) { return (f & 1) != 0; });
    EXPECT_GE(generated, 1);
}

TEST_F(ReadingTest, WindowsOfTextAddUpToTheWhole) {
    const auto whole = doc_.page_text(0, 0, 2048);
    ASSERT_EQ(whole.status, Status::Ok);
    protocol::GetPageTextReply joined;
    for (std::uint32_t first = 0; first < whole.value.total; first += 7) {
        const auto part = doc_.page_text(0, first, 7);
        ASSERT_EQ(part.status, Status::Ok);
        EXPECT_EQ(part.value.total, whole.value.total);
        joined.units.insert(joined.units.end(), part.value.units.begin(), part.value.units.end());
        joined.lefts.insert(joined.lefts.end(), part.value.lefts.begin(), part.value.lefts.end());
        joined.flags.insert(joined.flags.end(), part.value.flags.begin(), part.value.flags.end());
    }
    EXPECT_EQ(joined.units, whole.value.units);
    EXPECT_EQ(joined.lefts, whole.value.lefts);
    EXPECT_EQ(joined.flags, whole.value.flags);

    const auto at_end = doc_.page_text(0, whole.value.total, 5);
    ASSERT_EQ(at_end.status, Status::Ok);
    EXPECT_TRUE(at_end.value.units.empty());
    EXPECT_EQ(doc_.page_text(0, whole.value.total + 1, 5).status, Status::InvalidArgument);
    EXPECT_EQ(doc_.page_text(0, 0, 0).status, Status::InvalidArgument);
    EXPECT_EQ(doc_.page_text(0, 0, 2049).status, Status::InvalidArgument);
    EXPECT_EQ(doc_.page_text(3, 0, 5).status, Status::InvalidArgument);
}

TEST_F(ReadingTest, TextBoxesAreInPdfUserSpaceEvenOnARotatedCroppedPage) {
    const auto r = doc_.page_text(2, 0, 64);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_NE(text_of(r.value).find("Rotated"), std::string::npos);
    EXPECT_NEAR(r.value.lefts[0], 150.f, 1.f);
    EXPECT_LT(r.value.bottoms[0], 300.f);
    EXPECT_GT(r.value.tops[0], 300.f);
}

TEST_F(ReadingTest, ABlankPageHasNoText) {
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int page = pdf.add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
    pdf.set(catalog, "<< /Type /Catalog /Pages 2 0 R >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 >>");
    const auto data = bytes(pdf.build(catalog));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.page_text(0, 0, 10);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.total, 0u);
    EXPECT_TRUE(r.value.units.empty());
}

// ---- search --------------------------------------------------------------------------------------------------------------

TEST_F(ReadingTest, SearchFindsPhrasesWithTheOptionsAsked) {
    const auto never = [] { return false; };
    auto r = doc_.search_page(0, "world", 0, never);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    ASSERT_EQ(r.value.starts.size(), 1u);
    EXPECT_EQ(r.value.starts[0], 6u);
    EXPECT_EQ(r.value.lengths[0], 5u);

    EXPECT_TRUE(doc_.search_page(0, "world", 1, never).value.starts.empty());       // match case
    EXPECT_EQ(doc_.search_page(0, "World", 1, never).value.starts.size(), 1u);
    EXPECT_TRUE(doc_.search_page(0, "Hell", 2, never).value.starts.empty());        // whole words
    EXPECT_EQ(doc_.search_page(0, "Hello", 2, never).value.starts.size(), 1u);
    EXPECT_TRUE(doc_.search_page(0, "no such phrase", 0, never).value.starts.empty());
    EXPECT_EQ(doc_.search_page(0, "l", 0, never).value.starts.size(), 4u);           // He-l-l-o, Wor-l-d, and the l of "line"
}

TEST_F(ReadingTest, SearchRefusesWhatItCannotRun) {
    const auto never = [] { return false; };
    EXPECT_EQ(doc_.search_page(0, "", 0, never).status, Status::InvalidArgument);
    EXPECT_EQ(doc_.search_page(0, std::string(513, 'a'), 0, never).status, Status::InvalidArgument);
    EXPECT_EQ(doc_.search_page(0, "a\xFF", 0, never).status, Status::InvalidArgument);  // not UTF-8
    EXPECT_EQ(doc_.search_page(0, std::string("a\0b", 3), 0, never).status, Status::InvalidArgument);
    EXPECT_EQ(doc_.search_page(0, "a", 4, never).status, Status::InvalidArgument);
    EXPECT_EQ(doc_.search_page(3, "a", 0, never).status, Status::InvalidArgument);
}

TEST_F(ReadingTest, SearchMatchesNonAsciiText) {
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int font = pdf.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    // WinAnsi 0xE9 is e-acute.
    const int content = pdf.add_stream("BT /F1 12 Tf 10 50 Td (caf\\351 society) Tj ET");
    const int page = pdf.add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 " + std::to_string(font) + " 0 R >> >> /Contents " +
                             std::to_string(content) + " 0 R >>");
    pdf.set(catalog, "<< /Type /Catalog /Pages 2 0 R >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 >>");
    const auto data = bytes(pdf.build(catalog));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.search_page(0, "caf\xC3\xA9", 0, [] { return false; });  // UTF-8 for cafe-acute
    ASSERT_EQ(r.status, Status::Ok);
    ASSERT_EQ(r.value.starts.size(), 1u);
    EXPECT_EQ(r.value.starts[0], 0u);
    EXPECT_EQ(r.value.lengths[0], 4u);
}

TEST_F(ReadingTest, SearchStopsAtTheMatchLimitAndSaysSo) {
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int font = pdf.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
    std::string text;
    for (int i = 0; i < 3000; ++i) {
        text += "a ";
    }
    const int content = pdf.add_stream("BT /F1 1 Tf 10 50 Td (" + text + ") Tj ET");
    const int page = pdf.add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 " + std::to_string(font) + " 0 R >> >> /Contents " +
                             std::to_string(content) + " 0 R >>");
    pdf.set(catalog, "<< /Type /Catalog /Pages 2 0 R >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 >>");
    const auto data = bytes(pdf.build(catalog));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.search_page(0, "a", 0, [] { return false; });
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.starts.size(), 2048u);
    EXPECT_TRUE(r.value.truncated);
    EXPECT_EQ(r.value.validate(), Status::Ok);

    // A page of 6000 characters is read in three windows of 2048.
    const auto text_reply = opened.value.page_text(0, 0, 2048);
    ASSERT_EQ(text_reply.status, Status::Ok);
    EXPECT_GE(text_reply.value.total, 6000u);

    // The same search with a clock that has run out ends in Timeout, not in a half-finished list.
    const auto timed_out = opened.value.search_page(0, "a", 0, [] { return true; });
    EXPECT_EQ(timed_out.status, Status::Timeout);
}

TEST_F(ReadingTest, TheServicesShareOnePageCacheAcrossPagesAndSurviveClosing) {
    for (const std::uint32_t page : {0u, 1u, 0u, 2u, 2u, 1u, 0u}) {
        EXPECT_EQ(doc_.page_text(page, 0, 8).status, Status::Ok);
        EXPECT_EQ(doc_.page_links(page, 0, 8).status, Status::Ok);
        EXPECT_EQ(doc_.page_boxes(page).status, Status::Ok);
    }
    Document moved = std::move(doc_);  // the cache goes with the document
    EXPECT_EQ(moved.page_text(0, 0, 8).status, Status::Ok);
    EXPECT_EQ(doc_.page_text(0, 0, 8).status, Status::InvalidArgument);  // a moved-from document has no pages
}

// ---- as served by EngineCore ----------------------------------------------------------------------------------------------

struct TempFile {
    std::filesystem::path path;
    explicit TempFile(const std::string& content)
        : path(std::filesystem::temp_directory_path() / ("mikepdf-reading-test-" + std::to_string(GetCurrentProcessId()) + ".pdf")) {
        std::ofstream out(path, std::ios::binary);
        out.write(content.data(), static_cast<std::streamsize>(content.size()));
    }
    ~TempFile() {
        std::error_code ignored;
        std::filesystem::remove(path, ignored);
    }
    TempFile(const TempFile&) = delete;
    TempFile& operator=(const TempFile&) = delete;
};

class ReadingCoreTest : public ::testing::Test {
protected:
    ReadingCoreTest() : file_(reader_pdf()), core_(EngineKind::Pool) {
        protocol::OpenDocument open;
        open.handle = support::open_handle(file_.path);
        protocol::RequestEnvelope request;
        request.request_id = 1;
        request.message_type = MessageType::OpenDocument;
        request.payload = open;
        const auto reply = core_.execute(request, Clock::time_point::max());
        EXPECT_EQ(reply.status, Status::Ok);
        document_ = std::get<protocol::OpenDocumentReply>(*reply.payload).document_id;
    }
    ~ReadingCoreTest() override { core_.close_all(); }

    engine::Reply ask(MessageType type, protocol::RequestPayload payload, std::uint32_t document, Clock::time_point deadline = Clock::time_point::max()) {
        protocol::RequestEnvelope request;
        request.request_id = 2;
        request.document_id = document;
        request.message_type = type;
        request.payload = std::move(payload);
        return core_.execute(request, deadline);
    }

    struct Ask {
        MessageType type;
        protocol::RequestPayload payload;
    };

    static std::vector<Ask> everything() {
        protocol::GetOutline outline;
        outline.count = 8;
        protocol::GetPageLinks links;
        links.count = 8;
        protocol::GetLinkUri uri;
        uri.index = 1;
        protocol::GetPageBoxes boxes;
        protocol::GetPageText text;
        text.count = 64;
        protocol::SearchPage search;
        search.query = "Hello";
        return {{MessageType::GetOutline, outline}, {MessageType::GetPageLinks, links}, {MessageType::GetLinkUri, uri},
                {MessageType::GetPageBoxes, boxes}, {MessageType::GetPageText, text},   {MessageType::SearchPage, search}};
    }

    TempFile file_;
    pdfium::Library library_;
    EngineCore core_;
    std::uint32_t document_{0};
};

TEST_F(ReadingCoreTest, EveryServiceAnswersWithAReplyThatEncodes) {
    for (const auto& question : everything()) {
        const auto reply = ask(question.type, question.payload, document_);
        ASSERT_EQ(reply.status, Status::Ok) << static_cast<int>(question.type);
        ASSERT_TRUE(reply.payload.has_value());
        EXPECT_EQ(protocol::message_type_of(*reply.payload), question.type);
        EXPECT_TRUE(protocol::encode_response_frame(1, reply.status, question.type, &*reply.payload).ok()) << static_cast<int>(question.type);
    }
}

TEST_F(ReadingCoreTest, AnUnknownDocumentIsInvalidAndAPassedDeadlineIsATimeout) {
    const auto past = Clock::now() - std::chrono::milliseconds(1);
    for (const auto& question : everything()) {
        EXPECT_EQ(ask(question.type, question.payload, document_ + 40).status, Status::InvalidArgument) << static_cast<int>(question.type);
        EXPECT_EQ(ask(question.type, question.payload, document_, past).status, Status::Timeout) << static_cast<int>(question.type);
    }
}

TEST_F(ReadingCoreTest, ARequestOutsideTheDocumentIsInvalidNotFatal) {
    protocol::GetPageText text;
    text.page = 99;
    text.count = 8;
    EXPECT_EQ(ask(MessageType::GetPageText, text, document_).status, Status::InvalidArgument);
    protocol::GetOutline outline;
    outline.path = {9};
    outline.count = 1;
    EXPECT_EQ(ask(MessageType::GetOutline, outline, document_).status, Status::InvalidArgument);
    protocol::SearchPage search;
    search.page = 0;
    search.query = "";
    EXPECT_EQ(ask(MessageType::SearchPage, search, document_).status, Status::InvalidArgument);
    // Still serving afterwards.
    protocol::GetPageBoxes boxes;
    EXPECT_EQ(ask(MessageType::GetPageBoxes, boxes, document_).status, Status::Ok);
}

}  // namespace
