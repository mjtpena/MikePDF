#include "page_layout.h"

#include <algorithm>
#include <cmath>

namespace mikepdf::shellext {

namespace {
constexpr std::uint32_t kMinWidth = 16;
constexpr std::uint32_t kMaxWidth = 1024;
}  // namespace

PageLayout::PageLayout(std::uint32_t page_count, double first_aspect) noexcept
    : count_(std::min(page_count, kMaxLaidOutPages)), first_aspect_(std::isfinite(first_aspect) && first_aspect > 0.0 ? first_aspect : 1.4142) {}

void PageLayout::set_viewport(int pane_width, int pane_height, int margin, int gap) noexcept {
    pane_width_ = std::max(pane_width, 1);
    pane_height_ = std::max(pane_height, 1);
    margin_ = std::max(margin, 0);
    gap_ = std::max(gap, 0);
    const int content = std::max(pane_width_ - 2 * margin_, 1);
    render_width_ = std::clamp<std::uint32_t>(static_cast<std::uint32_t>(content), kMinWidth, kMaxWidth);
}

double PageLayout::aspect_of(std::uint32_t index) const noexcept {
    const auto it = learned_.find(index);
    return it == learned_.end() ? first_aspect_ : it->second;
}

int PageLayout::height_at(double aspect) const noexcept {
    const double height = std::round(static_cast<double>(render_width_) * aspect);
    return static_cast<int>(std::clamp(height, 1.0, static_cast<double>(render_height())));
}

int PageLayout::page_height(std::uint32_t index) const noexcept { return height_at(aspect_of(index)); }

int PageLayout::page_width(std::uint32_t index) const noexcept {
    const double aspect = aspect_of(index);
    if (static_cast<double>(render_width_) * aspect <= render_height()) {
        return static_cast<int>(render_width_);
    }
    return static_cast<int>(std::max(1.0, std::round(render_height() / aspect)));
}

std::int64_t PageLayout::page_top(std::uint32_t index) const noexcept {
    index = std::min(index, count_);
    const int estimate = height_at(first_aspect_);
    std::int64_t top = margin_ + static_cast<std::int64_t>(index) * (estimate + gap_);
    for (const auto& [page, aspect] : learned_) {
        if (page >= index) {
            break;
        }
        top += height_at(aspect) - estimate;
    }
    return top;
}

std::int64_t PageLayout::total_height() const noexcept {
    if (count_ == 0) {
        return 0;
    }
    return page_top(count_) - gap_ + margin_;
}

namespace {
/// The last page whose top is at or above `y` (0 when `y` is above the first page).
std::uint32_t last_page_at_or_above(const PageLayout& layout, std::int64_t y) noexcept {
    std::uint32_t low = 0;
    std::uint32_t high = layout.page_count() - 1;
    while (low < high) {
        const std::uint32_t middle = low + (high - low + 1) / 2;
        if (layout.page_top(middle) <= y) {
            low = middle;
        } else {
            high = middle - 1;
        }
    }
    return low;
}
}  // namespace

PageLayout::Range PageLayout::visible(std::int64_t scroll) const noexcept {
    if (count_ == 0) {
        return {};
    }
    const std::int64_t bottom = scroll + pane_height_ - 1;
    return {last_page_at_or_above(*this, scroll), last_page_at_or_above(*this, bottom)};
}

std::uint32_t PageLayout::current_page(std::int64_t scroll) const noexcept {
    if (count_ == 0) {
        return 0;
    }
    return last_page_at_or_above(*this, scroll + pane_height_ / 2);
}

std::int64_t PageLayout::learn(std::uint32_t index, std::uint32_t bitmap_width, std::uint32_t bitmap_height, std::int64_t scroll) noexcept {
    if (index >= count_ || bitmap_width == 0 || bitmap_height == 0) {
        return 0;
    }
    const std::int64_t top = page_top(index);
    const int before = page_height(index);
    learned_[index] = static_cast<double>(bitmap_height) / static_cast<double>(bitmap_width);
    const int delta = page_height(index) - before;
    // Only a page that lies wholly above the viewport pushes what the reader sees; one that is in view or below just grows or shrinks.
    return (delta != 0 && top + before <= scroll) ? delta : 0;
}

std::int64_t PageLayout::clamp_scroll(std::int64_t scroll) const noexcept {
    const std::int64_t most = std::max<std::int64_t>(total_height() - pane_height_, 0);
    return std::clamp<std::int64_t>(scroll, 0, most);
}

}  // namespace mikepdf::shellext
