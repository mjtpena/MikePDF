// SHELL-11: the handler's validation of ShellHost's replies. The values are built by hand, past the schema's own limits, because the point is what the handler does
// when the peer is not who it says it is.
#include <gtest/gtest.h>

#include <cmath>
#include <limits>

#include "validation.h"

namespace {

using namespace mikepdf::shellext;
using mikepdf::protocol::ShellOpenReply;
using mikepdf::protocol::ShellRenderPageReply;

ShellRenderPageReply reply(std::uint32_t width, std::uint32_t height, std::uint32_t stride) {
    ShellRenderPageReply r;
    r.width = width;
    r.height = height;
    r.stride = stride;
    return r;
}

TEST(ReplyValidation, AnHonestReplyPasses) {
    const auto checked = check_render_reply(reply(171, 256, 171 * 4), 256, 256, pixel_section_bytes(256, 256));
    EXPECT_TRUE(checked.ok) << checked.why;
    EXPECT_EQ(checked.bytes, 171u * 4 * 256);
}

TEST(ReplyValidation, TheLargestHonestReplyPasses) {
    const auto checked = check_render_reply(reply(1024, 1024, 4096), 1024, 1024, kMaxPixelBytes);
    EXPECT_TRUE(checked.ok) << checked.why;
    EXPECT_EQ(checked.bytes, kMaxPixelBytes);
}

TEST(ReplyValidation, ZeroSizesAreRefused) {
    EXPECT_FALSE(check_render_reply(reply(0, 10, 0), 256, 256, 1 << 20).ok);
    EXPECT_FALSE(check_render_reply(reply(10, 0, 40), 256, 256, 1 << 20).ok);
    EXPECT_FALSE(check_render_reply(reply(0, 0, 0), 256, 256, 1 << 20).ok);
}

TEST(ReplyValidation, WidthOrHeightAboveTheRequestIsRefused) {
    EXPECT_FALSE(check_render_reply(reply(257, 10, 257 * 4), 256, 256, 1 << 22).ok);
    EXPECT_FALSE(check_render_reply(reply(10, 257, 40), 256, 256, 1 << 22).ok);
    // The case from the spec: 5000 is above every limit there is.
    EXPECT_FALSE(check_render_reply(reply(5000, 10, 20000), 256, 256, 1 << 22).ok);
    EXPECT_FALSE(check_render_reply(reply(5000, 10, 20000), 1024, 1024, 1 << 22).ok);
}

TEST(ReplyValidation, AStrideThatIsNotFourTimesTheWidthIsRefused) {
    EXPECT_FALSE(check_render_reply(reply(100, 100, 399), 256, 256, 1 << 22).ok);
    EXPECT_FALSE(check_render_reply(reply(100, 100, 401), 256, 256, 1 << 22).ok);
    EXPECT_FALSE(check_render_reply(reply(100, 100, 800), 256, 256, 1 << 22).ok);
    EXPECT_FALSE(check_render_reply(reply(100, 100, 0), 256, 256, 1 << 22).ok);
}

TEST(ReplyValidation, PixelsLargerThanTheSectionAreRefused) {
    const std::uint64_t exactly = std::uint64_t{100} * 4 * 100;
    EXPECT_TRUE(check_render_reply(reply(100, 100, 400), 256, 256, exactly).ok);
    EXPECT_FALSE(check_render_reply(reply(100, 100, 400), 256, 256, exactly - 1).ok);
    EXPECT_FALSE(check_render_reply(reply(100, 100, 400), 256, 256, 0).ok);
}

TEST(ReplyValidation, OverflowSizedValuesAreRefusedWithoutWrapping) {
    constexpr auto max32 = std::numeric_limits<std::uint32_t>::max();
    // 2^32 - 1 squared does not fit in 32 bits; in 64 bits it is still far above any section.
    EXPECT_FALSE(check_render_reply(reply(max32, max32, max32), max32, max32, 1 << 22).ok);
    // A width whose 4x wraps a 32-bit multiply back to a small number (0x40000001 x 4 = 4 mod 2^32): 32-bit arithmetic would accept stride 4.
    EXPECT_FALSE(check_render_reply(reply(0x40000001u, 1, 4), max32, max32, 1 << 22).ok);
    // A height that makes stride x height wrap to 0 in 32 bits (0x4000 x 0x40000 = 2^32).
    EXPECT_FALSE(check_render_reply(reply(1000, 0x40000, 4000), max32, max32, 1 << 22).ok);
    // Still refused when the request's maxima are huge and the section is the largest the schema allows.
    EXPECT_FALSE(check_render_reply(reply(1024, 0x40001, 4096), max32, max32, kMaxPixelBytes).ok);
}

TEST(ReplyValidation, NothingIsAllocatedOrReadByTheCheck) {
    // The function takes numbers only: a refused reply leaves no bytes to free and no pointer to follow.
    const auto checked = check_render_reply(reply(1, 1, 4), 1, 1, 4);
    EXPECT_TRUE(checked.ok);
    EXPECT_EQ(checked.bytes, 4u);
}

TEST(OpenValidation, NeedsASessionPagesAndARealFirstPage) {
    ShellOpenReply ok;
    ok.document_id = 1;
    ok.page_count = 3;
    ok.first_width = 612;
    ok.first_height = 792;
    EXPECT_TRUE(check_open_reply(ok));

    auto broken = ok;
    broken.document_id = 0;
    EXPECT_FALSE(check_open_reply(broken));
    broken = ok;
    broken.page_count = 0;
    EXPECT_FALSE(check_open_reply(broken));
    broken = ok;
    broken.first_width = 0;
    EXPECT_FALSE(check_open_reply(broken));
    broken = ok;
    broken.first_height = std::numeric_limits<float>::quiet_NaN();
    EXPECT_FALSE(check_open_reply(broken));
    broken = ok;
    broken.first_height = std::numeric_limits<float>::infinity();
    EXPECT_FALSE(check_open_reply(broken));
}

TEST(Edges, AreClampedToTheSchemaRange) {
    EXPECT_EQ(clamp_edge(0), 16u);
    EXPECT_EQ(clamp_edge(15), 16u);
    EXPECT_EQ(clamp_edge(16), 16u);
    EXPECT_EQ(clamp_edge(256), 256u);
    EXPECT_EQ(clamp_edge(1024), 1024u);
    EXPECT_EQ(clamp_edge(1025), 1024u);
    EXPECT_EQ(clamp_edge(0xFFFFFFFFFFull), 1024u);
}

}  // namespace
