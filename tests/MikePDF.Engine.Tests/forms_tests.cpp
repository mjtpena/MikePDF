// The form layer of RenderTile and RenderBand (flag bit 1). PDFium draws a widget only through its form-fill environment (FPDF_FFLDraw); FPDF_ANNOT skips every one,
// so without the bit a filled form shows empty fields on screen and on paper. The fixture (acroform_filled.pdf, 400 x 300 pt) has four widgets whose places and colours the
// tests know: a text field with a red appearance at x 20..180 and y 20..60 of the 1x picture (the PDF's y is 240..280), a checked checkbox with a green one at x 20..60, y 80..120,
// a combo box with a blue one at x 20..180, y 140..180, and a text field with a value but no appearance at all at x 200..380, y 20..60, which only the form-fill environment can draw.
#include <gtest/gtest.h>

#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

#include "mikepdf/pdfium/document.h"
#include "mikepdf/pdfium/library.h"
#include "test_support.h"

namespace {

using namespace mikepdf;
using pdfium::BandRequest;
using pdfium::Document;
using pdfium::RenderInterrupt;
using pdfium::TileRequest;
using protocol::Status;
using support::Bgra;
using support::pixel;

constexpr auto kNever = [] { return RenderInterrupt::None; };
constexpr std::uint32_t kAnnotations = pdfium::kRenderAnnotations;
constexpr std::uint32_t kForms = pdfium::kRenderFormFields;
constexpr std::uint32_t kPrinting = pdfium::kRenderPrinting;
constexpr std::uint32_t kWidth = 400;
constexpr std::uint32_t kHeight = 300;
constexpr std::uint32_t kStride = kWidth * 4;

bool is_white(const Bgra& p) { return p.b == 255 && p.g == 255 && p.r == 255; }
bool is(const Bgra& p, int r, int g, int b) { return p.r == r && p.g == g && p.b == b; }

class FormsTest : public ::testing::Test {
protected:
    pdfium::Library library_;
    std::vector<std::uint8_t> bytes_ = support::read_fixture("acroform_filled.pdf");

    Document open(const std::vector<std::uint8_t>& bytes) const {
        auto opened = Document::open(bytes, {});
        EXPECT_EQ(opened.status, Status::Ok);
        return std::move(opened.value);
    }

    // The whole page as one tile at 1x.
    static std::vector<std::uint8_t> tile(Document& doc, std::uint32_t flags, std::uint16_t rotation = 0, Status expected = Status::Ok) {
        const bool turned = (rotation & 1) != 0;
        TileRequest request;
        request.scale = 1.0f;
        request.rotation = rotation;
        request.tile_width = turned ? kHeight : kWidth;
        request.tile_height = turned ? kWidth : kHeight;
        request.flags = flags;
        std::vector<std::uint8_t> buffer(std::size_t{request.tile_width} * request.tile_height * 4, 0x55);
        EXPECT_EQ(doc.render_tile(request, buffer, request.tile_width * 4, kNever), expected);
        return buffer;
    }

    static std::vector<std::uint8_t> band(Document& doc, std::uint32_t flags, std::uint32_t top = 0, std::uint32_t height = kHeight) {
        BandRequest request;
        request.page_width_px = kWidth;
        request.page_height_px = kHeight;
        request.band_top = top;
        request.band_height = height;
        request.flags = flags;
        std::vector<std::uint8_t> buffer(std::size_t{kWidth} * height * 4, 0x55);
        EXPECT_EQ(doc.render_band(request, buffer, kStride, kNever), Status::Ok);
        return buffer;
    }

    // Whether any pixel of the rectangle (device pixels) is not white.
    static bool has_ink(const std::vector<std::uint8_t>& buffer, std::uint32_t stride, int left, int top, int right, int bottom) {
        for (int y = top; y < bottom; ++y) {
            for (int x = left; x < right; ++x) {
                if (!is_white(pixel(buffer, stride, static_cast<std::uint32_t>(x), static_cast<std::uint32_t>(y)))) {
                    return true;
                }
            }
        }
        return false;
    }
};

TEST_F(FormsTest, WithoutTheFormLayerNoWidgetIsDrawn) {
    Document doc = open(bytes_);
    const auto picture = tile(doc, kAnnotations);  // what the viewer drew before: the page, annotations, no widgets
    EXPECT_TRUE(is_white(pixel(picture, kStride, 100, 40)));
    EXPECT_TRUE(is_white(pixel(picture, kStride, 40, 100)));
    EXPECT_TRUE(is_white(pixel(picture, kStride, 100, 160)));
    EXPECT_FALSE(has_ink(picture, kStride, 200, 20, 380, 60));
    EXPECT_TRUE(is(pixel(picture, kStride, 200, 295), 230, 230, 230)) << "the page's own content is there";
}

TEST_F(FormsTest, TheFormLayerDrawsATextFieldACheckboxAndAComboBox) {
    Document doc = open(bytes_);
    const auto picture = tile(doc, kAnnotations | kForms);
    EXPECT_TRUE(is(pixel(picture, kStride, 100, 40), 255, 0, 0)) << "the text field's appearance";
    EXPECT_TRUE(is(pixel(picture, kStride, 40, 100), 0, 153, 0)) << "the checked box";
    EXPECT_TRUE(is(pixel(picture, kStride, 100, 160), 0, 0, 255)) << "the combo box's appearance";
    EXPECT_TRUE(is(pixel(picture, kStride, 200, 295), 230, 230, 230)) << "the page content is still under it";
    EXPECT_TRUE(is_white(pixel(picture, kStride, 300, 150))) << "and nothing is drawn where there is no widget";
}

TEST_F(FormsTest, TheBitAloneDrawsTheWidgetsToo) {
    Document doc = open(bytes_);
    EXPECT_TRUE(is(pixel(tile(doc, kForms), kStride, 100, 40), 255, 0, 0)) << "FFLDraw draws widgets whether or not annotations were asked for";
}

TEST_F(FormsTest, AFieldWithAValueAndNoAppearanceIsDrawnFromItsValue) {
    Document doc = open(bytes_);
    const auto with = tile(doc, kAnnotations | kForms);
    EXPECT_TRUE(has_ink(with, kStride, 200, 20, 380, 60)) << "the form-fill environment made an appearance for the value 'Hi'";
    // The ink is dark text on white, not a fill or a tint of the whole field.
    EXPECT_TRUE(is_white(pixel(with, kStride, 205, 25)));
    int dark = 0;
    for (std::uint32_t y = 20; y < 60; ++y) {
        for (std::uint32_t x = 200; x < 380; ++x) {
            dark += pixel(with, kStride, x, y).r < 100 ? 1 : 0;
        }
    }
    EXPECT_GT(dark, 30);
}

TEST_F(FormsTest, NoFieldHighlightIsEverDrawn) {
    Document doc = open(bytes_);
    const auto picture = tile(doc, kAnnotations | kForms);
    // A highlight tints the whole widget rectangle. The generated field has white around its text; the checkbox and the others have their own colours, untinted.
    EXPECT_TRUE(is_white(pixel(picture, kStride, 378, 58)));
    EXPECT_TRUE(is_white(pixel(picture, kStride, 202, 58)));
    EXPECT_TRUE(is(pixel(picture, kStride, 21, 21), 255, 0, 0));
    EXPECT_TRUE(is(pixel(picture, kStride, 178, 58), 255, 0, 0));
}

TEST_F(FormsTest, AskingTwiceGivesTheSamePixelsAndTheEnvironmentIsReused) {
    Document doc = open(bytes_);
    const auto first = tile(doc, kAnnotations | kForms);
    const auto plain = tile(doc, kAnnotations);
    const auto second = tile(doc, kAnnotations | kForms);
    EXPECT_EQ(first, second);
    EXPECT_NE(first, plain);
    EXPECT_EQ(plain, tile(doc, kAnnotations)) << "a request without the bit is not affected by an earlier one with it";
}

TEST_F(FormsTest, TilesOfAPageAddUpToTheWholePageWithTheFormLayer) {
    Document doc = open(bytes_);
    const auto whole = tile(doc, kAnnotations | kForms);
    // Four 200 x 150 tiles with the origin at the corners: the widget rectangles cross the seams (x 200 is inside the second text field's rectangle's left edge).
    std::vector<std::uint8_t> stitched(whole.size(), 0);
    for (std::uint32_t ty = 0; ty < kHeight; ty += 150) {
        for (std::uint32_t tx = 0; tx < kWidth; tx += 200) {
            TileRequest request;
            request.scale = 1.0f;
            request.tile_x = static_cast<std::int32_t>(tx);
            request.tile_y = static_cast<std::int32_t>(ty);
            request.tile_width = 200;
            request.tile_height = 150;
            request.flags = kAnnotations | kForms;
            std::vector<std::uint8_t> piece(200 * 150 * 4, 0x55);
            ASSERT_EQ(doc.render_tile(request, piece, 800, kNever), Status::Ok);
            for (std::uint32_t y = 0; y < 150; ++y) {
                std::copy_n(piece.begin() + y * 800, 800, stitched.begin() + (ty + y) * kStride + tx * 4);
            }
        }
    }
    EXPECT_EQ(stitched, whole);
}

TEST_F(FormsTest, ATileAndABandOfTheSameRowsAreTheSameWithTheFormLayer) {
    Document doc = open(bytes_);
    EXPECT_EQ(band(doc, kAnnotations | kForms), tile(doc, kAnnotations | kForms));
    // Bands add up too, and the band that holds only part of a widget draws that part.
    std::vector<std::uint8_t> stitched;
    for (std::uint32_t top = 0; top < kHeight; top += 50) {
        const auto piece = band(doc, kAnnotations | kForms, top, 50);
        stitched.insert(stitched.end(), piece.begin(), piece.end());
    }
    EXPECT_EQ(stitched, band(doc, kAnnotations | kForms));
}

TEST_F(FormsTest, PrintingModeDrawsTheWidgetsAndLeavesTheRestOfTheBandAlone) {
    Document doc = open(bytes_);
    const auto printed = band(doc, kAnnotations | kForms | kPrinting);
    EXPECT_TRUE(is(pixel(printed, kStride, 100, 40), 255, 0, 0));
    EXPECT_TRUE(is(pixel(printed, kStride, 40, 100), 0, 153, 0));
    EXPECT_TRUE(is(pixel(printed, kStride, 100, 160), 0, 0, 255));
    EXPECT_TRUE(has_ink(printed, kStride, 200, 20, 380, 60));
    EXPECT_FALSE(has_ink(printed, kStride, 190, 80, 400, 285)) << "no highlight, no stray marks";
    const auto without = band(doc, kAnnotations | kPrinting);
    EXPECT_TRUE(is_white(pixel(without, kStride, 100, 40)));
}

TEST_F(FormsTest, TheWidgetsTurnWithThePage) {
    Document doc = open(bytes_);
    // A quarter turn clockwise maps the 1x point (x, y) to (300 - y, x) in the 300 x 400 picture.
    const auto turned = tile(doc, kAnnotations | kForms, 1);
    EXPECT_TRUE(is(pixel(turned, kHeight * 4, 260, 100), 255, 0, 0));
    EXPECT_TRUE(is(pixel(turned, kHeight * 4, 200, 40), 0, 153, 0));
    EXPECT_TRUE(is(pixel(turned, kHeight * 4, 140, 100), 0, 0, 255));
    const auto upside_down = tile(doc, kAnnotations | kForms, 2);
    EXPECT_TRUE(is(pixel(upside_down, kStride, 300, 260), 255, 0, 0));
}

TEST_F(FormsTest, ADocumentWithoutFormsIsDrawnTheSameWithAndWithoutTheBit) {
    const auto plain = support::bytes_of(support::build_pdf("0 g 20 20 100 100 re f"));
    Document doc = open(plain);
    TileRequest request;
    request.tile_width = 200;
    request.tile_height = 200;
    request.flags = kAnnotations;
    std::vector<std::uint8_t> a(200 * 200 * 4);
    std::vector<std::uint8_t> b(200 * 200 * 4);
    ASSERT_EQ(doc.render_tile(request, a, 800, kNever), Status::Ok);
    request.flags = kAnnotations | kForms;
    ASSERT_EQ(doc.render_tile(request, b, 800, kNever), Status::Ok);
    EXPECT_EQ(a, b);
}

TEST_F(FormsTest, AnInterruptedRenderStillStopsAndLeavesTheDocumentUsable) {
    const auto many = support::bytes_of(support::build_pdf(support::many_rectangles(5000)));
    Document busy = open(many);
    TileRequest request;
    request.tile_width = 200;
    request.tile_height = 200;
    request.flags = kAnnotations | kForms;
    std::vector<std::uint8_t> buffer(200 * 200 * 4);
    EXPECT_EQ(busy.render_tile(request, buffer, 800, [] { return RenderInterrupt::Cancel; }), Status::Cancelled);
    EXPECT_EQ(busy.render_tile(request, buffer, 800, [] { return RenderInterrupt::Timeout; }), Status::Timeout);
    EXPECT_EQ(busy.render_tile(request, buffer, 800, kNever), Status::Ok);

    Document form = open(bytes_);
    request.tile_width = kWidth;
    request.tile_height = kHeight;
    std::vector<std::uint8_t> picture(std::size_t{kWidth} * kHeight * 4);
    EXPECT_EQ(form.render_tile(request, picture, kStride, [] { return RenderInterrupt::Cancel; }), Status::Cancelled);
    EXPECT_TRUE(is(pixel(tile(form, kAnnotations | kForms), kStride, 100, 40), 255, 0, 0));
}

TEST_F(FormsTest, ManyRendersAndAMovedDocumentKeepWorking) {
    Document doc = open(bytes_);
    std::vector<std::uint8_t> reference = tile(doc, kAnnotations | kForms);
    for (int i = 0; i < 25; ++i) {
        ASSERT_EQ(tile(doc, kAnnotations | kForms), reference) << i;
    }
    Document moved = std::move(doc);
    EXPECT_EQ(tile(moved, kAnnotations | kForms), reference) << "the environment moves with the document";
    // Reading services on the same document do not disturb it, and it closes cleanly with the environment alive.
    EXPECT_EQ(moved.page_text(0, 0, 10).status, Status::Ok);
    EXPECT_EQ(tile(moved, kAnnotations | kForms), reference);
}

TEST_F(FormsTest, AHostileFieldTreeIsBoundedNotAHang) {
    // A field that is its own kid, a /Fields entry that is not a dictionary, and a widget whose parent is itself.
    support::PdfBuilder b;
    const int catalog = b.reserve();
    const int pages = b.reserve();
    const int page = b.reserve();
    const int loop = b.reserve();
    b.set(catalog, "<< /Type /Catalog /Pages " + std::to_string(pages) + " 0 R /AcroForm << /Fields [" + std::to_string(loop) + " 0 R 7 /Name (text)] /NeedAppearances true >> >>");
    b.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 /MediaBox [0 0 100 100] >>");
    b.set(page, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /Annots [" + std::to_string(loop) + " 0 R] >>");
    b.set(loop, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (x) /V (loop) /Rect [10 10 90 40] /Parent " + std::to_string(loop) + " 0 R /Kids [" + std::to_string(loop) + " 0 R] >>");
    const auto hostile = support::bytes_of(b.build(catalog));
    Document doc = open(hostile);
    TileRequest request;
    request.tile_width = 100;
    request.tile_height = 100;
    request.flags = kAnnotations | kForms;
    std::vector<std::uint8_t> buffer(100 * 100 * 4);
    const Status status = doc.render_tile(request, buffer, 400, kNever);
    EXPECT_TRUE(status == Status::Ok || status == Status::PartiallyRendered || status == Status::Unsupported) << static_cast<int>(status);
}

}  // namespace
