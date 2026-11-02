// Schema 5 in the engine: /ViewerPreferences, print bands, and the progress counter that tells a busy engine from a stuck one.
#include <gtest/gtest.h>

#include <algorithm>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "engine_core.h"
#include "mapped_file.h"
#include "mikepdf/pdfium/document.h"
#include "mikepdf/pdfium/library.h"
#include "test_support.h"

namespace {

using namespace mikepdf;
using engine::Clock;
using engine::EngineCore;
using engine::EngineKind;
using engine::SlotMemory;
using pdfium::BandRequest;
using pdfium::Document;
using pdfium::RenderInterrupt;
using protocol::MessageType;
using protocol::RequestEnvelope;
using protocol::Status;
using support::PdfBuilder;

constexpr auto kNever = [] { return RenderInterrupt::None; };

// A one-page document whose catalog carries the given /ViewerPreferences body (none when empty).
std::vector<std::uint8_t> with_preferences(const std::string& preferences, int pages = 1) {
    PdfBuilder b;
    const int catalog = b.reserve();
    const int tree = b.reserve();
    std::string kids;
    std::vector<int> page_objects;
    for (int i = 0; i < pages; ++i) {
        page_objects.push_back(b.reserve());
        kids += std::to_string(page_objects.back()) + " 0 R ";
    }
    b.set(catalog, "<< /Type /Catalog /Pages " + std::to_string(tree) + " 0 R" + (preferences.empty() ? "" : " /ViewerPreferences << " + preferences + " >>") + " >>");
    b.set(tree, "<< /Type /Pages /Kids [" + kids + "] /Count " + std::to_string(pages) + " /MediaBox [0 0 200 200] >>");
    for (const int page : page_objects) {
        b.set(page, "<< /Type /Page /Parent " + std::to_string(tree) + " 0 R >>");
    }
    return support::bytes_of(b.build(catalog));
}

class Schema5Test : public ::testing::Test {
protected:
    pdfium::Library library_;

    static Document open(const std::vector<std::uint8_t>& bytes) {
        auto opened = Document::open(bytes, {});
        EXPECT_EQ(opened.status, Status::Ok);
        return std::move(opened.value);
    }

    static protocol::GetViewerPreferencesReply preferences(const std::string& body, int pages = 1) {
        const auto bytes = with_preferences(body, pages);
        Document doc = open(bytes);
        auto reply = doc.viewer_preferences();
        EXPECT_EQ(reply.status, Status::Ok);
        EXPECT_EQ(reply.value.validate(), Status::Ok) << body;  // whatever the file says, the reply obeys the schema
        return std::move(reply.value);
    }
};

TEST_F(Schema5Test, AFileWithoutPreferencesReadsAsAbsent) {
    const auto none = preferences("");
    EXPECT_EQ(none.direction, 2);
    EXPECT_EQ(none.print_scaling, 0);
    EXPECT_EQ(none.num_copies, 0);
    EXPECT_EQ(none.duplex, 0);
    EXPECT_TRUE(none.range_firsts.empty());
}

TEST_F(Schema5Test, EveryPreferenceIsReadWithTheMeaningThePrintDialogNeeds) {
    const auto p = preferences("/Direction /R2L /PrintScaling /None /NumCopies 3 /Duplex /DuplexFlipLongEdge /PrintPageRange [1 2 5 7]", 8);
    EXPECT_EQ(p.direction, 1);
    EXPECT_EQ(p.print_scaling, 1);
    EXPECT_EQ(p.num_copies, 3);
    EXPECT_EQ(p.duplex, 3);
    ASSERT_EQ(p.range_firsts.size(), 2u);
    EXPECT_EQ(p.range_firsts, (std::vector<std::uint32_t>{0, 4}));  // the file counts from 1, the reply from 0
    EXPECT_EQ(p.range_lasts, (std::vector<std::uint32_t>{1, 6}));

    EXPECT_EQ(preferences("/Direction /L2R").direction, 0);
    EXPECT_EQ(preferences("/PrintScaling /AppDefault").print_scaling, 2);
    EXPECT_EQ(preferences("/Duplex /Simplex").duplex, 1);
    EXPECT_EQ(preferences("/Duplex /DuplexFlipShortEdge").duplex, 2);
}

TEST_F(Schema5Test, HostileValuesAreClampedOrAbsentNeverPassedOn) {
    EXPECT_EQ(preferences("/NumCopies 2147483647").num_copies, 99);  // never more than 99 copies (PRINT-24)
    EXPECT_EQ(preferences("/NumCopies 100").num_copies, 99);
    EXPECT_EQ(preferences("/NumCopies -5").num_copies, 0);
    EXPECT_EQ(preferences("/NumCopies 0").num_copies, 0);
    EXPECT_EQ(preferences("/NumCopies (many)").num_copies, 0);
    EXPECT_EQ(preferences("/Direction /Sideways").direction, 2);
    EXPECT_EQ(preferences("/Direction 7").direction, 2);
    EXPECT_EQ(preferences("/PrintScaling /Huge").print_scaling, 0);
    EXPECT_EQ(preferences("/Duplex /Triplex").duplex, 0);
    EXPECT_EQ(preferences("/Duplex 3").duplex, 0);

    // Pairs that are not pages of this document are dropped; the good ones around them are kept.
    const auto ranges = preferences("/PrintPageRange [0 3 3 2 9 12 2 3 -1 4 4]", 5);
    EXPECT_EQ(ranges.range_firsts, (std::vector<std::uint32_t>{1}));
    EXPECT_EQ(ranges.range_lasts, (std::vector<std::uint32_t>{2}));
    EXPECT_TRUE(preferences("/PrintPageRange 5").range_firsts.empty());
    EXPECT_TRUE(preferences("/PrintPageRange [1]").range_firsts.empty());
    EXPECT_TRUE(preferences("/PrintPageRange [(a) (b)]").range_firsts.empty());
}

TEST_F(Schema5Test, ATooLongRangeListIsCutAtTheSchemaLimit) {
    std::string body = "/PrintPageRange [";
    for (int i = 0; i < 200; ++i) {
        body += "1 1 ";
    }
    body += "]";
    const auto p = preferences(body);
    EXPECT_EQ(p.range_firsts.size(), 64u);
    EXPECT_EQ(p.range_lasts.size(), 64u);
}

// ---- RenderBand ---------------------------------------------------------------------------------------

class BandTest : public Schema5Test {
protected:
    // A 200 x 200 pt page with a black square at its bottom-left (0..100 in both directions), which is the lower left quarter of the picture.
    std::vector<std::uint8_t> bytes_ = support::bytes_of(support::build_pdf("0 g 0 0 100 100 re f"));

    BandRequest band(std::uint32_t width, std::uint32_t height, std::uint32_t top, std::uint32_t band_height, std::uint32_t flags = 0) const {
        BandRequest r;
        r.page = 0;
        r.page_width_px = width;
        r.page_height_px = height;
        r.band_top = top;
        r.band_height = band_height;
        r.flags = flags;
        return r;
    }
};

TEST_F(BandTest, TheBandsOfAPageAddUpToTheWholePageDrawnAtOnce) {
    Document doc = open(bytes_);
    constexpr std::uint32_t kSide = 400;  // 200 pt at 144 dpi
    std::vector<std::uint8_t> whole(kSide * kSide * 4);
    ASSERT_EQ(doc.render_band(band(kSide, kSide, 0, kSide), whole, kSide * 4, kNever), Status::Ok);

    std::vector<std::uint8_t> stitched;
    for (std::uint32_t top = 0; top < kSide; top += 150) {
        const std::uint32_t height = std::min<std::uint32_t>(150, kSide - top);
        std::vector<std::uint8_t> piece(kSide * height * 4, 0xCD);
        ASSERT_EQ(doc.render_band(band(kSide, kSide, top, height), piece, kSide * 4, kNever), Status::Ok) << top;
        stitched.insert(stitched.end(), piece.begin(), piece.end());
    }
    EXPECT_EQ(stitched, whole);  // PRINT-06 in miniature: a band is exactly its rows of the whole page

    // And the picture is the right way up: the square is at the bottom left.
    EXPECT_EQ(support::pixel(whole, kSide * 4, 50, kSide - 50).r, 0);
    EXPECT_EQ(support::pixel(whole, kSide * 4, 50, 50).r, 255);
    EXPECT_EQ(support::pixel(whole, kSide * 4, kSide - 50, kSide - 50).r, 255);
}

TEST_F(BandTest, ABandThatDoesNotFitOrDoesNotExistIsRefusedAndNothingIsWritten) {
    Document doc = open(bytes_);
    std::vector<std::uint8_t> buffer(400 * 100 * 4, 0xCD);
    EXPECT_EQ(doc.render_band(band(400, 400, 350, 100), buffer, 1600, kNever), Status::InvalidArgument);   // reaches past the page
    EXPECT_EQ(doc.render_band(band(400, 400, 0, 101), buffer, 1600, kNever), Status::InvalidArgument);     // bigger than the buffer
    EXPECT_EQ(doc.render_band(band(400, 400, 0, 100), buffer, 1604, kNever), Status::InvalidArgument);     // a stride that is not a row
    EXPECT_EQ(doc.render_band(band(400, 400, 0, 0), buffer, 1600, kNever), Status::InvalidArgument);
    EXPECT_EQ(doc.render_band(band(0, 400, 0, 100), buffer, 1600, kNever), Status::InvalidArgument);
    EXPECT_EQ(doc.render_band(band(65537, 400, 0, 100), buffer, 262148, kNever), Status::InvalidArgument);
    EXPECT_EQ(doc.render_band(band(400, 400, 0, 16385), buffer, 1600, kNever), Status::InvalidArgument);
    auto flags = band(400, 400, 0, 100);
    flags.flags = 8;  // the reserved bit
    EXPECT_EQ(doc.render_band(flags, buffer, 1600, kNever), Status::InvalidArgument);
    auto rotated = band(400, 400, 0, 100);
    rotated.rotation = 4;
    EXPECT_EQ(doc.render_band(rotated, buffer, 1600, kNever), Status::InvalidArgument);
    auto missing = band(400, 400, 0, 100);
    missing.page = 1;
    EXPECT_EQ(doc.render_band(missing, buffer, 1600, kNever), Status::InvalidArgument);
    EXPECT_EQ(std::count(buffer.begin(), buffer.end(), std::uint8_t{0xCD}), static_cast<std::ptrdiff_t>(buffer.size()));
}

TEST_F(BandTest, FormFieldsAreNotDrawnYetAndPrintingModeIsAccepted) {
    Document doc = open(bytes_);
    std::vector<std::uint8_t> buffer(400 * 100 * 4);
    EXPECT_EQ(doc.render_band(band(400, 400, 0, 100, 2), buffer, 1600, kNever), Status::Unsupported);
    EXPECT_EQ(doc.render_band(band(400, 400, 0, 100, 1 | 4), buffer, 1600, kNever), Status::Ok);
}

TEST_F(BandTest, ABandIsInterruptedLikeATile) {
    Document doc = open(bytes_);
    std::vector<std::uint8_t> buffer(400 * 100 * 4);
    std::vector<std::uint8_t> big;
    const auto many = support::bytes_of(support::build_pdf(support::many_rectangles(5000)));
    Document busy = open(many);
    EXPECT_EQ(busy.render_band(band(400, 400, 0, 100), buffer, 1600, [] { return RenderInterrupt::Cancel; }), Status::Cancelled);
    EXPECT_EQ(busy.render_band(band(400, 400, 0, 100), buffer, 1600, [] { return RenderInterrupt::Timeout; }), Status::Timeout);
}

// ---- the progress counter -------------------------------------------------------------------------------

TEST(ProgressTest, AnIdleEngineStandsStillAndEveryRequestMovesIt) {
    pdfium::Library library;
    std::vector<std::uint8_t> slots(64 * 1024 * 2, 0);
    EngineCore core(EngineKind::Document, SlotMemory{slots.data(), 2, 64 * 1024});
    const auto idle = core.progress();
    EXPECT_EQ(core.progress(), idle);

    protocol::RequestEnvelope open;
    open.request_id = 1;
    open.message_type = MessageType::OpenDocument;
    protocol::OpenDocument payload;
    payload.handle = support::open_handle(support::fixture_path("rectangles.pdf"));
    open.payload = payload;
    const auto opened = core.execute(open, Clock::time_point::max());
    ASSERT_EQ(opened.status, Status::Ok);
    EXPECT_GT(core.progress(), idle);

    // A render polls for interruption, and each poll counts: a long render keeps moving the counter, a hung one does not.
    const auto before = core.progress();
    protocol::RequestEnvelope render;
    render.request_id = 2;
    render.document_id = std::get<protocol::OpenDocumentReply>(*opened.payload).document_id;
    render.message_type = MessageType::RenderTile;
    protocol::RenderTile tile;
    tile.scale = 1.0f;
    tile.tile_width = 32;
    tile.tile_height = 32;
    render.payload = tile;
    ASSERT_EQ(core.execute(render, Clock::time_point::max()).status, Status::Ok);
    EXPECT_GE(core.progress(), before + 3);  // the request starting, at least one poll, the request ending
    core.close_all();
}

TEST(ProgressTest, ALongRenderKeepsMovingTheCounterWhileItRuns) {
    // The counter is moved from inside PDFium's pause callback, about every 100 drawing operations: a page of 6,000 of them polls dozens of times.
    pdfium::Library library;
    const auto path = std::filesystem::temp_directory_path() / ("mikepdf-progress-" + std::to_string(GetCurrentProcessId()) + ".pdf");
    {
        const std::string pdf = support::build_pdf(support::many_rectangles(6000));
        std::ofstream(path, std::ios::binary) << pdf;
    }
    std::vector<std::uint8_t> slots(64 * 1024, 0);
    EngineCore core(EngineKind::Document, SlotMemory{slots.data(), 1, 64 * 1024});
    protocol::RequestEnvelope open;
    open.request_id = 1;
    open.message_type = MessageType::OpenDocument;
    protocol::OpenDocument payload;
    payload.handle = support::open_handle(path);
    open.payload = payload;
    const auto opened = core.execute(open, Clock::time_point::max());
    ASSERT_EQ(opened.status, Status::Ok);

    const auto before = core.progress();
    protocol::RequestEnvelope render;
    render.request_id = 2;
    render.document_id = std::get<protocol::OpenDocumentReply>(*opened.payload).document_id;
    render.message_type = MessageType::RenderTile;
    protocol::RenderTile tile;
    tile.scale = 1.0f;
    tile.tile_x = 0;
    tile.tile_y = 72;  // the rectangles are in the bottom 32 points of the 200-point page; this tile is its bottom 128 rows
    tile.tile_width = 128;
    tile.tile_height = 128;
    render.payload = tile;
    ASSERT_EQ(core.execute(render, Clock::time_point::max()).status, Status::Ok);
    EXPECT_GE(core.progress(), before + 30);
    core.close_all();
    std::error_code ignored;
    std::filesystem::remove(path, ignored);
}

TEST(ProgressTest, AFailedRequestCountsToo) {
    pdfium::Library library;
    EngineCore core(EngineKind::Pool);
    const auto before = core.progress();
    protocol::RequestEnvelope request;
    request.request_id = 1;
    request.document_id = 99;
    request.message_type = MessageType::GetDocumentInfo;
    request.payload = protocol::GetDocumentInfo{};
    EXPECT_EQ(core.execute(request, Clock::time_point::max()).status, Status::InvalidArgument);
    EXPECT_GE(core.progress(), before + 2);
}

}  // namespace
