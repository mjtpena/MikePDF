// Dark pages (RenderTile flag bit 2): the page recoloured the way the app's inverted page colour does it, except for colour photographs. Today's inverted mode runs on the GPU
// and inverts everything, photographs included (PDFium's FPDF_COLORSCHEME was evaluated against it and is not used: see docs/perf or the completion note of W2-engine-2: it
// replaces every path and text colour with one colour, so blue headings, links and coloured fills all turn white; it leaves images alone).
#include <gtest/gtest.h>

#include <algorithm>
#include <cstdint>
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
using support::Bgra;
using support::pixel;

constexpr auto kNever = [] { return RenderInterrupt::None; };
constexpr std::uint32_t kAnnotations = pdfium::kRenderAnnotations;
constexpr std::uint32_t kDark = pdfium::kRenderDarkPage;
constexpr std::uint32_t kSide = 400;

// A 16 x 16 RGB picture as ASCIIHex. Colour: every pixel a saturated, unequal colour (no white at all). Line art: white paper with a black cross.
std::string picture_hex(bool colour) {
    std::string hex;
    for (int y = 0; y < 16; ++y) {
        for (int x = 0; x < 16; ++x) {
            char pixel[8];
            if (colour) {
                std::snprintf(pixel, sizeof pixel, "%02X%02X%02X", 40 + x * 12, 200 - y * 10, 60 + ((x + y) * 7) % 150);
            } else {
                const bool ink = x == 8 || y == 8;
                std::snprintf(pixel, sizeof pixel, ink ? "000000" : "FFFFFF");
            }
            hex += pixel;
        }
    }
    return hex;
}

std::string image(const std::string& cm, bool colour) {
    return "q " + cm + " cm\nBI /W 16 /H 16 /CS /RGB /BPC 8 /F /AHx ID\n" + picture_hex(colour) + ">\nEI\nQ\n";
}

// A 400 x 400 pt page: a black square at (20,20), a red one at (120,20), a colour photograph at (220,20) 100 pt square, line art at (20,220) 100 pt square.
std::vector<std::uint8_t> page() {
    const std::string content = "0 g 20 20 80 80 re f 1 0 0 rg 120 20 80 80 re f\n" + image("100 0 0 100 220 20", true) + image("100 0 0 100 20 220", false);
    return support::bytes_of(support::build_pdf(content, "0 0 400 400"));
}

class DarkPageTest : public ::testing::Test {
protected:
    pdfium::Library library_;

    // PDFium reads the bytes in place: they have to outlive the Document, so the fixture keeps every file it was given.
    std::deque<std::vector<std::uint8_t>> files_;

    Document open(std::vector<std::uint8_t> bytes) {
        files_.push_back(std::move(bytes));
        auto opened = Document::open(files_.back(), {});
        EXPECT_EQ(opened.status, Status::Ok);
        return std::move(opened.value);
    }

    static std::vector<std::uint8_t> tile(Document& doc, std::uint32_t flags, std::int32_t x = 0, std::int32_t y = 0, std::uint32_t w = kSide, std::uint32_t h = kSide) {
        TileRequest request;
        request.scale = 1.0f;
        request.tile_x = x;
        request.tile_y = y;
        request.tile_width = w;
        request.tile_height = h;
        request.flags = flags;
        std::vector<std::uint8_t> buffer(std::size_t{w} * h * 4, 0x55);
        EXPECT_EQ(doc.render_tile(request, buffer, w * 4, kNever), Status::Ok);
        return buffer;
    }

    static void expect_near(const Bgra& p, int r, int g, int b, const char* what) {
        EXPECT_NEAR(p.r, r, 1) << what;
        EXPECT_NEAR(p.g, g, 1) << what;
        EXPECT_NEAR(p.b, b, 1) << what;
    }
};

TEST_F(DarkPageTest, PaperTextAndColourFollowTheAppsInvertedMatrixWithHueKept) {
    Document doc = open(page());
    const auto dark = tile(doc, kAnnotations | kDark);
    // PageColourMatrix.Inverted maps white to 7%, black to 95%, red (255,0,0) to (255,147,147): the same numbers are asserted on the C# side (PageColourTests).
    expect_near(pixel(dark, kSide * 4, 380, 10), 18, 18, 18, "white paper");
    expect_near(pixel(dark, kSide * 4, 60, 340), 242, 242, 242, "black");
    expect_near(pixel(dark, kSide * 4, 160, 340), 255, 147, 147, "red keeps its hue and gets lighter");
}

TEST_F(DarkPageTest, AColourPhotographKeepsItsColoursAndLineArtIsRecolouredWithThePage) {
    Document doc = open(page());
    const auto normal = tile(doc, kAnnotations);
    const auto dark = tile(doc, kAnnotations | kDark);
    // The photograph really is there (a guard against comparing two pictures without it) and is colourful.
    const auto centre = pixel(normal, kSide * 4, 270, 330);
    EXPECT_GT(std::max({centre.r, centre.g, centre.b}) - std::min({centre.r, centre.g, centre.b}), 40);
    // The photograph: every pixel of its rectangle is as the file draws it.
    int differing = 0;
    for (std::uint32_t y = 282; y < 378; ++y) {
        for (std::uint32_t x = 222; x < 318; ++x) {
            const auto a = pixel(normal, kSide * 4, x, y);
            const auto b = pixel(dark, kSide * 4, x, y);
            differing += (a.r != b.r || a.g != b.g || a.b != b.b) ? 1 : 0;
        }
    }
    EXPECT_EQ(differing, 0) << "the photograph is not touched";
    // The line art (mostly white paper): white is the dark paper, the cross is light.
    EXPECT_EQ(pixel(normal, kSide * 4, 30, 90).r, 255) << "the line art is there: white paper";
    expect_near(pixel(dark, kSide * 4, 30, 90), 18, 18, 18, "the line art's paper");
    expect_near(pixel(dark, kSide * 4, 72, 100), 242, 242, 242, "the cross of the line art");
    // Around the photograph is the page's paper.
    expect_near(pixel(dark, kSide * 4, 210, 330), 18, 18, 18, "paper beside the photograph");
}

TEST_F(DarkPageTest, WithoutTheBitNothingChangesAndTheBitIsNotAnAnnotationFlag) {
    Document doc = open(page());
    const auto normal = tile(doc, kAnnotations);
    EXPECT_EQ(normal, tile(doc, kAnnotations));
    expect_near(pixel(normal, kSide * 4, 380, 10), 255, 255, 255, "white paper as the file draws it");
    // With the bit and without annotations it is dark all the same.
    expect_near(pixel(tile(doc, kDark), kSide * 4, 380, 10), 18, 18, 18, "dark without annotations");
}

TEST_F(DarkPageTest, TilesOfADarkPageAddUpToTheWholeDarkPageAcrossAPhotograph) {
    Document doc = open(page());
    const auto whole = tile(doc, kAnnotations | kDark);
    std::vector<std::uint8_t> stitched(whole.size(), 0);
    // 130 x 130 tiles: the photograph's rectangle (220..320 x 280..380) crosses the seams at 260 and 390 / 260.
    for (std::uint32_t ty = 0; ty < kSide; ty += 130) {
        for (std::uint32_t tx = 0; tx < kSide; tx += 130) {
            const std::uint32_t w = std::min<std::uint32_t>(130, kSide - tx);
            const std::uint32_t h = std::min<std::uint32_t>(130, kSide - ty);
            const auto piece = tile(doc, kAnnotations | kDark, static_cast<std::int32_t>(tx), static_cast<std::int32_t>(ty), w, h);
            for (std::uint32_t row = 0; row < h; ++row) {
                std::copy_n(piece.begin() + std::size_t{row} * w * 4, std::size_t{w} * 4, stitched.begin() + (std::size_t{ty + row} * kSide + tx) * 4);
            }
        }
    }
    EXPECT_EQ(stitched, whole);
}

TEST_F(DarkPageTest, ARotatedPageKeepsThePhotographInPlace) {
    Document doc = open(page());
    TileRequest request;
    request.scale = 1.0f;
    request.rotation = 1;  // a quarter turn clockwise: the 1x point (x, y) is at (400 - y, x)
    request.tile_width = kSide;
    request.tile_height = kSide;
    request.flags = kAnnotations;
    std::vector<std::uint8_t> normal(kSide * kSide * 4);
    ASSERT_EQ(doc.render_tile(request, normal, kSide * 4, kNever), Status::Ok);
    request.flags = kAnnotations | kDark;
    std::vector<std::uint8_t> dark(kSide * kSide * 4);
    ASSERT_EQ(doc.render_tile(request, dark, kSide * 4, kNever), Status::Ok);
    // Every colourful pixel that is not the red square's own red is photograph: the same in both, and there is a good deal of it.
    int photograph = 0;
    int differing = 0;
    for (std::uint32_t y = 0; y < kSide; ++y) {
        for (std::uint32_t x = 0; x < kSide; ++x) {
            const auto a = pixel(normal, kSide * 4, x, y);
            const bool colourful = std::max({a.r, a.g, a.b}) - std::min({a.r, a.g, a.b}) > 40 && !(a.r == 255 && a.g == 0 && a.b == 0);
            if (colourful) {
                ++photograph;
                const auto b = pixel(dark, kSide * 4, x, y);
                differing += (a.r != b.r || a.g != b.g || a.b != b.b) ? 1 : 0;
            }
        }
    }
    EXPECT_GT(photograph, 5000);
    EXPECT_EQ(differing, 0);
    expect_near(pixel(dark, kSide * 4, 5, 5), 18, 18, 18, "paper");
}

TEST_F(DarkPageTest, ADocumentWithoutImagesAndAnInterruptedRenderBehave) {
    const auto plain = support::bytes_of(support::build_pdf("0 g 20 20 100 100 re f", "0 0 200 200"));
    Document doc = open(plain);
    TileRequest request;
    request.tile_width = 200;
    request.tile_height = 200;
    request.flags = kDark;
    std::vector<std::uint8_t> buffer(200 * 200 * 4, 0x55);
    EXPECT_EQ(doc.render_tile(request, buffer, 800, kNever), Status::Ok);
    expect_near(pixel(buffer, 800, 190, 5), 18, 18, 18, "paper");
    std::vector<std::uint8_t> cancelled(200 * 200 * 4, 0x55);
    EXPECT_EQ(doc.render_tile(request, cancelled, 800, [] { return RenderInterrupt::Cancel; }), Status::Cancelled);
    EXPECT_EQ(pixel(cancelled, 800, 190, 5).r, 255) << "a cancelled tile is left as drawn (white paper), not recoloured";
}

TEST_F(DarkPageTest, AFullPageScanAndAnImageTooSmallToJudgeAreRecolouredLikeText) {
    // A colour picture over the whole page (a scan) and a colour 4 x 4 picture: neither is kept as a photograph.
    const std::string big = image("400 0 0 400 0 0", true);
    Document scan = open(support::bytes_of(support::build_pdf(big, "0 0 400 400")));
    const auto normal = tile(scan, kAnnotations);
    const auto dark = tile(scan, kAnnotations | kDark);
    EXPECT_NE(normal, dark);

    const std::string tiny = "q 100 0 0 100 20 20 cm\nBI /W 4 /H 4 /CS /RGB /BPC 8 /F /AHx ID\n" + std::string(4 * 4 * 6, 'A') + ">\nEI\nQ\n";
    Document small = open(support::bytes_of(support::build_pdf(tiny, "0 0 400 400")));
    EXPECT_NE(tile(small, kAnnotations), tile(small, kAnnotations | kDark));
}

TEST_F(DarkPageTest, TheBitIsRefusedNowhereAndUnknownBitsStillAre) {
    Document doc = open(page());
    TileRequest request;
    request.tile_width = 10;
    request.tile_height = 10;
    std::vector<std::uint8_t> buffer(10 * 10 * 4);
    request.flags = 7;
    EXPECT_EQ(doc.render_tile(request, buffer, 40, kNever), Status::Ok);
    request.flags = 8;
    EXPECT_EQ(doc.render_tile(request, buffer, 40, kNever), Status::InvalidArgument);
}

}  // namespace
