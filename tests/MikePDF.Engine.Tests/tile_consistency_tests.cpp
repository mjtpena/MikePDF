// A page drawn in tiles or bands must be the same pixels as the page drawn at once. PDFium draws an image under a skewed matrix (a slightly crooked scan) with its image
// transformer, and the transformer takes the shear from the corner of the clip rectangle instead of the corner of the image: every tile or band that does not start at the
// page's own corner showed the image shifted by up to several pixels (SSIM 0.73 against the whole-page picture on the corpus's scanned newspaper pages). The engine draws such a
// window from the page's corner and crops it. This file holds the evidence and the guard.
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
using pdfium::BandRequest;
using pdfium::Document;
using pdfium::RenderInterrupt;
using pdfium::TileRequest;
using protocol::Status;

constexpr auto kNever = [] { return RenderInterrupt::None; };

// A 6 x 6 RGB picture with a diagonal and a gradient, as ASCIIHex: enough detail that a shift of one pixel is a different picture.
std::string picture_hex() {
    std::string hex;
    for (int y = 0; y < 6; ++y) {
        for (int x = 0; x < 6; ++x) {
            char pixel[8];
            const int r = (x * 51) & 255;
            const int g = (y * 51) & 255;
            const int b = (x == y) ? 0 : 255;
            std::snprintf(pixel, sizeof pixel, "%02X%02X%02X", r, g, b);
            hex += pixel;
        }
    }
    return hex;
}

// One 600 x 800 pt page: text-like black bars and one image under the matrix `cm`.
std::vector<std::uint8_t> page_with_image(const std::string& cm) {
    std::string content = "0 g 40 40 300 6 re f 40 120 200 6 re f\nq " + cm + " cm\nBI /W 6 /H 6 /CS /RGB /BPC 8 /F /AHx ID\n" + picture_hex() + ">\nEI\nQ\n";
    return support::bytes_of(support::build_pdf(content, "0 0 600 800"));
}

class TileConsistencyTest : public ::testing::Test {
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

    // The page at `scale` drawn at once (one band as tall as the page), in tiles of `side` and in bands of `band_rows`, the last two stitched.
    struct Pictures {
        std::uint32_t width{}, height{};
        std::vector<std::uint8_t> whole, tiled, banded;
    };

    static Pictures draw(Document& doc, float scale, std::uint32_t side, std::uint32_t band_rows) {
        Pictures p;
        p.width = static_cast<std::uint32_t>(600 * scale);
        p.height = static_cast<std::uint32_t>(800 * scale);
        p.whole.assign(std::size_t{p.width} * p.height * 4, 0);
        BandRequest whole;
        whole.page_width_px = p.width;
        whole.page_height_px = p.height;
        whole.band_height = p.height;
        whole.flags = pdfium::kRenderAnnotations;
        EXPECT_EQ(doc.render_band(whole, p.whole, p.width * 4, kNever), Status::Ok);  // band_top 0: the page drawn at once, no window

        p.tiled.assign(p.whole.size(), 0);
        for (std::uint32_t ty = 0; ty < p.height; ty += side) {
            for (std::uint32_t tx = 0; tx < p.width; tx += side) {
                TileRequest request;
                request.scale = scale;
                request.tile_x = static_cast<std::int32_t>(tx);
                request.tile_y = static_cast<std::int32_t>(ty);
                request.tile_width = std::min(side, p.width - tx);
                request.tile_height = std::min(side, p.height - ty);
                request.flags = pdfium::kRenderAnnotations;
                std::vector<std::uint8_t> piece(std::size_t{request.tile_width} * request.tile_height * 4, 0x55);
                EXPECT_EQ(doc.render_tile(request, piece, request.tile_width * 4, kNever), Status::Ok);
                for (std::uint32_t row = 0; row < request.tile_height; ++row) {
                    std::copy_n(piece.begin() + std::size_t{row} * request.tile_width * 4, std::size_t{request.tile_width} * 4,
                                p.tiled.begin() + (std::size_t{ty + row} * p.width + tx) * 4);
                }
            }
        }

        p.banded.assign(p.whole.size(), 0);
        for (std::uint32_t top = 0; top < p.height; top += band_rows) {
            BandRequest band = whole;
            band.band_top = top;
            band.band_height = std::min(band_rows, p.height - top);
            std::vector<std::uint8_t> piece(std::size_t{p.width} * band.band_height * 4, 0x55);
            EXPECT_EQ(doc.render_band(band, piece, p.width * 4, kNever), Status::Ok);
            std::copy(piece.begin(), piece.end(), p.banded.begin() + std::size_t{top} * p.width * 4);
        }
        return p;
    }
};

TEST_F(TileConsistencyTest, ASkewedScanIsTheSamePixelsInTilesAndInBandsAsDrawnAtOnce) {
    // A 0.3 degree skew: 600 x 800 pt with the shear terms 3 and -4 pt.
    Document doc = open(page_with_image("600 3 -4 800 0 0"));
    const auto p = draw(doc, 2.0f, 512, 300);
    EXPECT_EQ(p.tiled, p.whole) << "tiles";
    EXPECT_EQ(p.banded, p.whole) << "bands";
    // And the image is really there and really skewed (guards against a test that compares two blank pages).
    int coloured = 0;
    for (std::size_t i = 0; i < p.whole.size(); i += 4) {
        coloured += (p.whole[i] != p.whole[i + 1] || p.whole[i + 1] != p.whole[i + 2]) ? 1 : 0;
    }
    EXPECT_GT(coloured, 100000);
}

TEST_F(TileConsistencyTest, TheSameHoldsAtOtherScalesAndForADifferentSkew) {
    for (const float scale : {1.0f, 1.7f, 3.0f}) {
        Document doc = open(page_with_image("590 6 -8 790 5 4"));
        const auto p = draw(doc, scale, 300, 250);
        // A 6 x 6 picture stretched to 590 pt is a 300-fold magnification, where PDFium's edge pixels can still differ by rounding when the clip height differs: a handful of
        // anti-aliased edge pixels (24 of 4.3 million at 3x), against the thousands of pixels, shifted by several, that the plain window gave. Exact equality is the first test's.
        const auto differing = [&](const std::vector<std::uint8_t>& other) {
            std::size_t bad = 0;
            for (std::size_t i = 0; i < p.whole.size(); i += 4) {
                bad += (other[i] != p.whole[i] || other[i + 1] != p.whole[i + 1] || other[i + 2] != p.whole[i + 2]) ? 1 : 0;
            }
            return bad;
        };
        EXPECT_LE(differing(p.tiled), p.whole.size() / 4 / 100000) << "tiles, scale " << scale;
        EXPECT_LE(differing(p.banded), p.whole.size() / 4 / 100000) << "bands, scale " << scale;
    }
}

TEST_F(TileConsistencyTest, AnUnskewedImageIsUnaffectedAndStaysConsistent) {
    Document doc = open(page_with_image("600 0 0 800 0 0"));
    const auto p = draw(doc, 2.0f, 512, 300);
    EXPECT_EQ(p.tiled, p.whole);
    EXPECT_EQ(p.banded, p.whole);
}

TEST_F(TileConsistencyTest, AQuarterTurnedImageIsUnaffectedAndStaysConsistent) {
    Document doc = open(page_with_image("0 800 -600 0 600 0"));
    const auto p = draw(doc, 2.0f, 512, 300);
    EXPECT_EQ(p.tiled, p.whole);
    EXPECT_EQ(p.banded, p.whole);
}

TEST_F(TileConsistencyTest, ATileFarFromTheCornerOfAHugePageIsStillDrawnWhenTheWholePageWouldNotFitTheBudget) {
    // 600 x 800 pt at 16x is 9600 x 12800 px, 491 MB: over the budget of the corner-anchored path, so the tile is drawn the plain way (and may differ from the whole page by
    // the shear shift: that is the documented limit). It must still be drawn, never refused or crashed.
    Document doc = open(page_with_image("600 3 -4 800 0 0"));
    TileRequest request;
    request.scale = 16.0f;
    request.tile_x = 8000;
    request.tile_y = 11000;
    request.tile_width = 1024;
    request.tile_height = 1024;
    request.flags = pdfium::kRenderAnnotations;
    std::vector<std::uint8_t> piece(1024 * 1024 * 4, 0x55);
    EXPECT_EQ(doc.render_tile(request, piece, 4096, kNever), Status::Ok);
    EXPECT_NE(piece[0], 0x55);
}

TEST_F(TileConsistencyTest, AnInterruptedWindowAwayFromTheCornerStopsAndWritesNothing) {
    Document doc = open(page_with_image("600 3 -4 800 0 0"));
    TileRequest request;
    request.scale = 2.0f;
    request.tile_x = 512;
    request.tile_y = 512;
    request.tile_width = 512;
    request.tile_height = 512;
    request.flags = pdfium::kRenderAnnotations;
    std::vector<std::uint8_t> piece(512 * 512 * 4, 0x55);
    EXPECT_EQ(doc.render_tile(request, piece, 2048, [] { return RenderInterrupt::Cancel; }), Status::Cancelled);
    EXPECT_EQ(std::count(piece.begin(), piece.end(), std::uint8_t{0x55}), static_cast<std::ptrdiff_t>(piece.size()));
}

}  // namespace
