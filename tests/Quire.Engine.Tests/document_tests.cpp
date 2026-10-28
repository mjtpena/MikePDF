// Unit tests for the PDFium wrapper (src/Quire.Engine.Pdfium). PDFium is process-wide state, so every test holds a Library.
#include <gtest/gtest.h>

#include <cstdint>
#include <string>
#include <vector>

#include "quire/pdfium/document.h"
#include "quire/pdfium/library.h"
#include "quire/protocol/msgpack.h"
#include "test_support.h"

namespace {

using namespace quire;
using protocol::Status;
using pdfium::Document;
using pdfium::RenderInterrupt;
using pdfium::TileRequest;
using support::build_pdf;
using support::pixel;
using support::read_fixture;

constexpr auto kNever = [] { return RenderInterrupt::None; };

class DocumentTest : public ::testing::Test {
protected:
    pdfium::Library library_;

    static Document open_fixture(const std::string& name, const std::vector<std::uint8_t>& storage, std::string_view password = {}) {
        (void)name;
        auto opened = Document::open(storage, password);
        EXPECT_EQ(opened.status, Status::Ok) << name;
        return std::move(opened.value);
    }
};

TEST_F(DocumentTest, VersionComesFromThePinnedPackage) {
    EXPECT_FALSE(pdfium::library_version().empty());
    EXPECT_NE(pdfium::library_version().find('.'), std::string_view::npos);
}

TEST_F(DocumentTest, OpensAndReportsPageCountAndInfo) {
    const auto bytes = read_fixture("hello_world.pdf");
    Document doc = open_fixture("hello_world.pdf", bytes);
    ASSERT_TRUE(doc);
    EXPECT_EQ(doc.page_count(), 1u);

    const auto info = doc.info();
    ASSERT_EQ(info.status, Status::Ok);
    EXPECT_EQ(info.value.page_count, 1u);
    EXPECT_EQ(info.value.pdf_version, 17u);
    EXPECT_TRUE(info.value.title.empty());
    EXPECT_EQ(info.value.permissions, 0xFFFFFFFFu);  // not encrypted
    EXPECT_FALSE(info.value.is_tagged);
    EXPECT_FALSE(info.value.has_xfa);
    EXPECT_FALSE(info.value.has_java_script);
    EXPECT_EQ(info.value.signature_count, 0u);
    EXPECT_EQ(info.value.validate(), Status::Ok);
}

TEST_F(DocumentTest, DetectsJavaScript) {
    const auto bytes = read_fixture("js.pdf");
    Document doc = open_fixture("js.pdf", bytes);
    ASSERT_TRUE(doc);
    const auto info = doc.info();
    ASSERT_EQ(info.status, Status::Ok);
    EXPECT_TRUE(info.value.has_java_script);
}

TEST_F(DocumentTest, ReadsMetadataAndBoundsIt) {
    // Title: "é" then 700 emoji (4 UTF-8 bytes each): longer than the 1024-byte limit, so it is cut on a code point boundary.
    std::string title = "FEFF00E9";
    for (int i = 0; i < 700; ++i) {
        title += "D83DDE00";
    }
    const auto bytes = support::bytes_of(build_pdf("", "0 0 200 200", "/Title <" + title + "> /Author (Ada) /Producer <FEFF004100000042>"));
    Document doc = open_fixture("generated", bytes);
    ASSERT_TRUE(doc);
    const auto info = doc.info();
    ASSERT_EQ(info.status, Status::Ok);
    EXPECT_LE(info.value.title.size(), 1024u);
    EXPECT_GT(info.value.title.size(), 1000u);
    EXPECT_TRUE(protocol::is_valid_text(info.value.title));
    EXPECT_EQ(info.value.title.substr(0, 2), "\xC3\xA9");
    EXPECT_EQ(info.value.author, "Ada");
    EXPECT_EQ(info.value.producer, "A");  // cut at the embedded NUL
    EXPECT_EQ(info.value.validate(), Status::Ok);
}

TEST_F(DocumentTest, PageSizesAreInPointsWithRotation) {
    const auto bytes = read_fixture("hello_world_2_pages.pdf");
    Document doc = open_fixture("hello_world_2_pages.pdf", bytes);
    ASSERT_TRUE(doc);
    ASSERT_EQ(doc.page_count(), 2u);
    const auto sizes = doc.page_sizes(0, 2);
    ASSERT_EQ(sizes.status, Status::Ok);
    ASSERT_EQ(sizes.value.widths.size(), 2u);
    EXPECT_EQ(sizes.value.widths[0], 200.0f);
    EXPECT_EQ(sizes.value.heights[0], 200.0f);
    EXPECT_EQ(sizes.value.rotations[0], 0);
    EXPECT_EQ(sizes.value.validate(), Status::Ok);

    // /Rotate 90 swaps the displayed size and is reported as one quarter turn.
    const auto rotated_pdf = support::bytes_of(build_pdf("", "0 0 200 300", "", "/Rotate 90"));
    Document rotated_doc = open_fixture("rotated", rotated_pdf);
    ASSERT_TRUE(rotated_doc);
    const auto rotated_sizes = rotated_doc.page_sizes(0, 1);
    ASSERT_EQ(rotated_sizes.status, Status::Ok);
    EXPECT_EQ(rotated_sizes.value.widths[0], 300.0f);
    EXPECT_EQ(rotated_sizes.value.heights[0], 200.0f);
    EXPECT_EQ(rotated_sizes.value.rotations[0], 1);
}

TEST_F(DocumentTest, PageSizesRejectsBadRanges) {
    const auto bytes = read_fixture("hello_world_2_pages.pdf");
    Document doc = open_fixture("hello_world_2_pages.pdf", bytes);
    ASSERT_TRUE(doc);
    EXPECT_EQ(doc.page_sizes(0, 0).status, Status::InvalidArgument);
    EXPECT_EQ(doc.page_sizes(1, 2).status, Status::InvalidArgument);
    EXPECT_EQ(doc.page_sizes(2, 1).status, Status::InvalidArgument);
    EXPECT_EQ(doc.page_sizes(0xFFFFFFFFu, 5000).status, Status::InvalidArgument);
    EXPECT_EQ(doc.page_sizes(1, 1).status, Status::Ok);
}

TEST_F(DocumentTest, OversizedPagesAreClampedToTheSchemaRange) {
    const auto bytes = support::bytes_of(build_pdf("", "0 0 30000 0.5"));
    Document doc = open_fixture("generated", bytes);
    ASSERT_TRUE(doc);
    const auto sizes = doc.page_sizes(0, 1);
    ASSERT_EQ(sizes.status, Status::Ok);
    EXPECT_EQ(sizes.value.widths[0], 14400.0f);
    EXPECT_EQ(sizes.value.heights[0], 1.0f);
    EXPECT_EQ(sizes.value.validate(), Status::Ok);
}

TEST_F(DocumentTest, EncryptedDocumentNeedsTheRightPassword) {
    const auto bytes = read_fixture("encrypted.pdf");
    EXPECT_EQ(Document::open(bytes, "").status, Status::PasswordRequired);
    EXPECT_EQ(Document::open(bytes, "definitely-wrong").status, Status::PasswordIncorrect);
    EXPECT_EQ(Document::open(bytes, "5678").status, Status::Ok) << "5678 is the user password";
    EXPECT_EQ(Document::open(bytes, "1234").status, Status::Ok) << "1234 is the owner password";
}

TEST_F(DocumentTest, GarbageIsDamagedNotACrash) {
    EXPECT_EQ(Document::open({}, "").status, Status::Damaged);
    EXPECT_EQ(Document::open(support::bytes_of("not a pdf at all"), "").status, Status::Damaged);
    auto truncated = read_fixture("hello_world.pdf");
    truncated.resize(truncated.size() / 3);
    EXPECT_EQ(Document::open(truncated, "").status, Status::Damaged);
    std::vector<std::uint8_t> noise(4096);
    for (std::size_t i = 0; i < noise.size(); ++i) {
        noise[i] = static_cast<std::uint8_t>(i * 131 + 7);
    }
    EXPECT_EQ(Document::open(noise, "").status, Status::Damaged);
}

TEST_F(DocumentTest, RendersKnownPixels) {
    const auto bytes = read_fixture("rectangles.pdf");  // 200 x 300 pt: black top-left, blue top-right, green bottom-right, red bottom-left
    Document doc = open_fixture("rectangles.pdf", bytes);
    ASSERT_TRUE(doc);
    std::vector<std::uint8_t> buffer(200 * 4 * 300, 0x55);
    TileRequest request;
    request.scale = 1.0f;
    request.tile_width = 200;
    request.tile_height = 300;
    ASSERT_EQ(doc.render_tile(request, buffer, 800, kNever), Status::Ok);

    const auto black = pixel(buffer, 800, 5, 5);
    EXPECT_EQ(std::vector<int>({black.b, black.g, black.r, black.a}), std::vector<int>({0, 0, 0, 255}));
    const auto blue = pixel(buffer, 800, 195, 5);
    EXPECT_EQ(std::vector<int>({blue.b, blue.g, blue.r, blue.a}), std::vector<int>({255, 0, 0, 255}));
    const auto green = pixel(buffer, 800, 195, 295);
    EXPECT_EQ(std::vector<int>({green.b, green.g, green.r, green.a}), std::vector<int>({0, 255, 0, 255}));
    const auto red = pixel(buffer, 800, 5, 295);
    EXPECT_EQ(std::vector<int>({red.b, red.g, red.r, red.a}), std::vector<int>({0, 0, 255, 255}));
    const auto paper = pixel(buffer, 800, 100, 5);
    EXPECT_EQ(std::vector<int>({paper.b, paper.g, paper.r, paper.a}), std::vector<int>({255, 255, 255, 255}));
}

TEST_F(DocumentTest, RendersATileAtAnOffsetAndScale) {
    const auto bytes = read_fixture("rectangles.pdf");
    Document doc = open_fixture("rectangles.pdf", bytes);
    ASSERT_TRUE(doc);
    // At 2x the blue square covers x 380..400, y 0..20. Take the 64 x 64 tile at (336, 0): its pixel (60, 10) is inside it.
    std::vector<std::uint8_t> buffer(64 * 4 * 64);
    TileRequest request;
    request.scale = 2.0f;
    request.tile_x = 336;
    request.tile_y = 0;
    request.tile_width = 64;
    request.tile_height = 64;
    ASSERT_EQ(doc.render_tile(request, buffer, 256, kNever), Status::Ok);
    const auto blue = pixel(buffer, 256, 60, 10);
    EXPECT_EQ(std::vector<int>({blue.b, blue.g, blue.r}), std::vector<int>({255, 0, 0}));
    const auto paper = pixel(buffer, 256, 5, 40);
    EXPECT_EQ(std::vector<int>({paper.b, paper.g, paper.r}), std::vector<int>({255, 255, 255}));
}

TEST_F(DocumentTest, RotationTurnsThePageClockwise) {
    const auto bytes = read_fixture("rectangles.pdf");
    Document doc = open_fixture("rectangles.pdf", bytes);
    ASSERT_TRUE(doc);
    std::vector<std::uint8_t> buffer(300 * 4 * 200);
    TileRequest request;
    request.rotation = 1;
    request.scale = 1.0f;
    request.tile_width = 300;
    request.tile_height = 200;
    ASSERT_EQ(doc.render_tile(request, buffer, 1200, kNever), Status::Ok);
    const auto top_left = pixel(buffer, 1200, 5, 5);   // was bottom-left: red
    EXPECT_EQ(std::vector<int>({top_left.b, top_left.g, top_left.r}), std::vector<int>({0, 0, 255}));
    const auto top_right = pixel(buffer, 1200, 295, 5);  // was top-left: black
    EXPECT_EQ(std::vector<int>({top_right.b, top_right.g, top_right.r}), std::vector<int>({0, 0, 0}));
}

TEST_F(DocumentTest, TheCancelCheckInterruptsAHeavyPage) {
    const auto bytes = support::bytes_of(build_pdf(support::many_rectangles(5000)));
    Document doc = open_fixture("generated", bytes);
    ASSERT_TRUE(doc);
    std::vector<std::uint8_t> buffer(200 * 4 * 200);
    TileRequest request;
    request.tile_width = 200;
    request.tile_height = 200;

    int polls = 0;
    EXPECT_EQ(doc.render_tile(request, buffer, 800, [&] { ++polls; return RenderInterrupt::Cancel; }), Status::Cancelled);
    EXPECT_GE(polls, 1);
    EXPECT_EQ(doc.render_tile(request, buffer, 800, [] { return RenderInterrupt::Timeout; }), Status::Timeout);
    // Not interrupted: the same page renders to completion afterwards.
    EXPECT_EQ(doc.render_tile(request, buffer, 800, kNever), Status::Ok);
    const auto filled = pixel(buffer, 800, 2, 197);
    EXPECT_EQ(std::vector<int>({filled.b, filled.g, filled.r}), std::vector<int>({204, 102, 51}));
}

TEST_F(DocumentTest, RenderRejectsBadArguments) {
    const auto bytes = read_fixture("rectangles.pdf");
    Document doc = open_fixture("rectangles.pdf", bytes);
    ASSERT_TRUE(doc);
    std::vector<std::uint8_t> buffer(64 * 4 * 64);
    TileRequest good;
    good.tile_width = 64;
    good.tile_height = 64;
    ASSERT_EQ(doc.render_tile(good, buffer, 256, kNever), Status::Ok);

    TileRequest page = good;
    page.page = 1;
    EXPECT_EQ(doc.render_tile(page, buffer, 256, kNever), Status::InvalidArgument);
    TileRequest scale = good;
    scale.scale = 0.0f;
    EXPECT_EQ(doc.render_tile(scale, buffer, 256, kNever), Status::InvalidArgument);
    TileRequest huge = good;
    huge.scale = 64.0f;  // 200 x 300 pt at 64x is 12800 x 19200 px: fine, but a 14400 pt page would not be
    EXPECT_EQ(doc.render_tile(huge, buffer, 256, kNever), Status::Ok);
    TileRequest zero = good;
    zero.tile_width = 0;
    EXPECT_EQ(doc.render_tile(zero, buffer, 256, kNever), Status::InvalidArgument);
    TileRequest wide = good;
    wide.tile_width = 2000;
    EXPECT_EQ(doc.render_tile(wide, buffer, 256, kNever), Status::InvalidArgument);
    EXPECT_EQ(doc.render_tile(good, buffer, 100, kNever), Status::InvalidArgument) << "stride below width * 4";
    EXPECT_EQ(doc.render_tile(good, std::span(buffer).first(buffer.size() - 1), 256, kNever), Status::InvalidArgument) << "buffer too small";
    TileRequest forms = good;
    forms.flags = pdfium::kRenderFormFields;
    EXPECT_EQ(doc.render_tile(forms, buffer, 256, kNever), Status::Unsupported);
    TileRequest unknown_flag = good;
    unknown_flag.flags = 4;
    EXPECT_EQ(doc.render_tile(unknown_flag, buffer, 256, kNever), Status::InvalidArgument);
}

TEST_F(DocumentTest, DocumentsMoveAndClose) {
    const auto bytes = read_fixture("hello_world.pdf");
    Document a = open_fixture("hello_world.pdf", bytes);
    Document b = std::move(a);
    EXPECT_FALSE(a);  // NOLINT(bugprone-use-after-move): moved-from is defined to be empty
    EXPECT_TRUE(b);
    Document c;
    c = std::move(b);
    EXPECT_TRUE(c);
    EXPECT_EQ(c.page_count(), 1u);
    Document empty;
    EXPECT_EQ(empty.info().status, Status::InvalidArgument);
    EXPECT_EQ(empty.page_sizes(0, 1).status, Status::InvalidArgument);
}

}  // namespace
