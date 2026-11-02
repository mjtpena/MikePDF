// SHELL-19 and SHELL-21, the parts that need no window: the preview's geometry, the way it learns real page shapes, and the four-page memory rule.
#include <gtest/gtest.h>

#include <algorithm>
#include <limits>
#include <vector>

#include "page_layout.h"

namespace {

using namespace mikepdf::shellext;

constexpr double kLetter = 792.0 / 612.0;

PageLayout make(std::uint32_t pages, int width = 636, int height = 800) {
    PageLayout layout(pages, kLetter);
    layout.set_viewport(width, height, 12, 8);
    return layout;
}

TEST(Layout, PagesAreRenderedAtTheFitWidthWithinTheSchemaLimit) {
    EXPECT_EQ(make(3, 636).render_width(), 612u);   // 636 less two 12 px margins
    EXPECT_EQ(make(3, 5000).render_width(), 1024u);  // a wide pane: the schema's limit, the page is centred
    EXPECT_EQ(make(3, 20).render_width(), 16u);      // a sliver
    EXPECT_EQ(make(3, 636).page_height(0), 792);
}

TEST(Layout, ATallPageIsLimitedByTheSchemaHeight) {
    PageLayout layout(2, 4.0);  // four times taller than wide
    layout.set_viewport(636, 800, 12, 8);
    EXPECT_EQ(layout.page_height(0), 1024);
    EXPECT_EQ(layout.page_width(0), 256);  // 1024 / 4: narrower than the pane, centred by the painter
}

TEST(Layout, PagesStackWithAGap) {
    const PageLayout layout = make(5);
    EXPECT_EQ(layout.page_top(0), 12);
    EXPECT_EQ(layout.page_top(1), 12 + 792 + 8);
    EXPECT_EQ(layout.page_top(2), 12 + 2 * (792 + 8));
    EXPECT_EQ(layout.total_height(), 12 + 5 * 792 + 4 * 8 + 12);
}

TEST(Layout, VisiblePagesAndTheCurrentPageFollowTheScrollPosition) {
    const PageLayout layout = make(10);
    const auto top = layout.visible(0);
    EXPECT_EQ(top.first, 0u);
    EXPECT_EQ(top.last, 0u) << "the pane is 800 px: page 1 starts at 812, below it";
    EXPECT_EQ(layout.visible(100).last, 1u);
    EXPECT_EQ(layout.current_page(0), 0u) << "the middle of the view, 400, is on page 0";

    const auto later = layout.visible(layout.page_top(4) + 600);
    EXPECT_EQ(later.first, 4u);
    EXPECT_EQ(later.last, 5u);
    EXPECT_EQ(layout.current_page(layout.page_top(4) + 600), 5u) << "page 5 holds the middle of the view";
}

TEST(Layout, ScrollIsClampedToTheContent) {
    const PageLayout layout = make(3);
    EXPECT_EQ(layout.clamp_scroll(-50), 0);
    EXPECT_EQ(layout.clamp_scroll(layout.total_height()), layout.total_height() - 800);
    const PageLayout short_document = make(1, 636, 5000);
    EXPECT_EQ(short_document.clamp_scroll(300), 0) << "a document shorter than the pane does not scroll";
}

TEST(Layout, LearningARealShapeMovesThePagesBelowOnly) {
    PageLayout layout = make(10);
    const std::int64_t before = layout.page_top(5);
    // Page 2 turns out to be a landscape page: 612 wide, 459 tall.
    const std::int64_t shift = layout.learn(2, 612, 459, /*scroll*/ 0);
    EXPECT_EQ(shift, 0) << "the viewer was looking at the top: nothing above it moved";
    EXPECT_EQ(layout.page_height(2), 459);
    EXPECT_EQ(layout.page_top(5), before - (792 - 459));
    EXPECT_EQ(layout.page_top(2), 12 + 2 * (792 + 8)) << "pages above are unchanged";
}

TEST(Layout, APageAboveTheViewportKeepsTheReadersPlace) {
    PageLayout layout = make(10);
    const std::int64_t scroll = layout.page_top(6) + 100;  // reading page 6
    const std::int64_t anchor = layout.page_top(6);
    const std::int64_t shift = layout.learn(2, 612, 459, scroll);  // a page above turns out shorter
    EXPECT_EQ(shift, 459 - 792);
    // The reader adds the shift to the scroll position, and page 6 stays exactly where it was on screen.
    EXPECT_EQ((layout.page_top(6)) - (scroll + shift), anchor - scroll);
}

TEST(Layout, APageInViewDoesNotShiftTheScrollPosition) {
    PageLayout layout = make(10);
    const std::int64_t scroll = layout.page_top(3) + 100;
    EXPECT_EQ(layout.learn(3, 612, 459, scroll), 0);
    EXPECT_EQ(layout.learn(7, 612, 459, scroll), 0);
}

TEST(Layout, LearningTheSameShapeTwiceChangesNothing) {
    PageLayout layout = make(10);
    (void)layout.learn(2, 612, 459, 0);
    EXPECT_EQ(layout.learn(2, 612, 459, 100000), 0);
}

TEST(Layout, ResizingKeepsWhatWasLearnedAsAnAspectRatio) {
    PageLayout layout = make(4, 636);
    (void)layout.learn(1, 612, 459, 0);
    layout.set_viewport(336, 800, 12, 8);  // a narrower pane: render width 312
    EXPECT_EQ(layout.render_width(), 312u);
    EXPECT_EQ(layout.page_height(1), 234) << "459/612 of 312, rounded";
    EXPECT_EQ(layout.page_height(0), 404) << "unlearned pages keep the first page's shape";
}

TEST(Layout, DocumentsBeyondTheCapAreLaidOutOnlyToTheCap) {
    const PageLayout layout = make(10'000'000);
    EXPECT_EQ(layout.page_count(), kMaxLaidOutPages);
    EXPECT_LT(layout.total_height(), 0x7FFFFFFF) << "the scroll bar range must fit 32 bits";
    EXPECT_EQ(layout.visible(layout.total_height()).last, kMaxLaidOutPages - 1);
}

TEST(Layout, ANonsenseFirstShapeFallsBackToAPageShape) {
    PageLayout layout(2, 0.0);
    layout.set_viewport(636, 800, 12, 8);
    EXPECT_GT(layout.page_height(0), 1);
    PageLayout nan_layout(2, std::numeric_limits<double>::quiet_NaN());
    nan_layout.set_viewport(636, 800, 12, 8);
    EXPECT_GT(nan_layout.page_height(0), 1);
}

TEST(Cache, NeverHoldsMoreThanFourPages) {
    PageCache<int> cache;
    std::vector<int> evicted;
    for (int page = 0; page < 200; ++page) {
        cache.put(static_cast<std::uint32_t>(page), page + 1000, 612, static_cast<std::uint32_t>(page), evicted);
        EXPECT_LE(cache.size(), kMaxCachedPages);
    }
    EXPECT_EQ(cache.size(), kMaxCachedPages);
    EXPECT_EQ(evicted.size(), 200u - kMaxCachedPages) << "every page that left was handed back to be freed";
}

TEST(Cache, EvictsTheFarthestPageFromTheAnchor) {
    PageCache<int> cache;
    std::vector<int> evicted;
    for (std::uint32_t page : {10u, 11u, 12u, 13u}) {
        cache.put(page, static_cast<int>(page), 612, 11, evicted);
    }
    cache.put(40, 40, 612, 12, evicted);  // the reader is around page 12: page 10 is the farthest
    EXPECT_EQ(cache.find(10), nullptr);
    EXPECT_NE(cache.find(11), nullptr);
    EXPECT_NE(cache.find(40), nullptr);
    ASSERT_EQ(evicted.size(), 1u);
    EXPECT_EQ(evicted[0], 10);
}

TEST(Cache, ReplacingAPageReturnsTheOldValueAndKeepsOneEntry) {
    PageCache<int> cache;
    std::vector<int> evicted;
    cache.put(3, 1, 612, 3, evicted);
    cache.put(3, 2, 312, 3, evicted);
    EXPECT_EQ(cache.size(), 1u);
    ASSERT_EQ(evicted.size(), 1u);
    EXPECT_EQ(evicted[0], 1);
    EXPECT_EQ(cache.find(3)->value, 2);
    EXPECT_EQ(cache.find(3)->render_width, 312u);
}

TEST(Cache, TakeAllEmptiesItAndReturnsEverything) {
    PageCache<int> cache;
    std::vector<int> evicted;
    cache.put(1, 10, 612, 1, evicted);
    cache.put(2, 20, 612, 1, evicted);
    std::vector<int> all;
    cache.take_all(all);
    std::sort(all.begin(), all.end());
    EXPECT_EQ(all, (std::vector<int>{10, 20}));
    EXPECT_EQ(cache.size(), 0u);
}

}  // namespace
