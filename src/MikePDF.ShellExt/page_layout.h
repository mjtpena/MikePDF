// The preview's geometry and memory, without a window (spec 3.4, SHELL-19 and SHELL-21): a vertical continuous column of pages at fit-width, and a cache that keeps
// at most four rendered pages. ShellHost has no "page sizes" message for a handler, so every page's height is an estimate (the first page's aspect ratio) until that page
// has been rendered; the layout learns the real aspect ratio then and keeps the page the reader is looking at where it is.
#pragma once

#include <cstdint>
#include <map>
#include <vector>

namespace mikepdf::shellext {

/// Pages beyond this are not laid out: the scroll range then stays far inside 32 bits (20,000 x 1,036 px), and nobody scrolls through more in a preview pane.
inline constexpr std::uint32_t kMaxLaidOutPages = 20000;
/// The most rendered pages kept in memory (SHELL-19).
inline constexpr std::size_t kMaxCachedPages = 4;

class PageLayout {
public:
    /// `first_aspect` is the first page's height divided by its width (from ShellOpen's points). `page_count` is the document's.
    PageLayout(std::uint32_t page_count, double first_aspect) noexcept;

    /// The width pages are rendered at: the pane's width less its margins, 16 to 1024 (the schema's limit; a wider pane centres the page). `gap` separates pages.
    void set_viewport(int pane_width, int pane_height, int margin, int gap) noexcept;

    [[nodiscard]] std::uint32_t page_count() const noexcept { return count_; }
    [[nodiscard]] int pane_width() const noexcept { return pane_width_; }
    [[nodiscard]] int pane_height() const noexcept { return pane_height_; }
    [[nodiscard]] int margin() const noexcept { return margin_; }
    /// The width a page is requested at (fit-width, within the schema's limit).
    [[nodiscard]] std::uint32_t render_width() const noexcept { return render_width_; }
    /// The tallest a page may be (the schema's limit).
    [[nodiscard]] static constexpr std::uint32_t render_height() noexcept { return 1024; }

    /// The height of page `index` as drawn: its learned aspect ratio at the render width, or the estimate, never above the schema's limit.
    [[nodiscard]] int page_height(std::uint32_t index) const noexcept;
    /// The width of page `index` as drawn (narrower than render_width() only for a page that is taller than it is wide by more than the height limit allows).
    [[nodiscard]] int page_width(std::uint32_t index) const noexcept;
    [[nodiscard]] std::int64_t page_top(std::uint32_t index) const noexcept;
    [[nodiscard]] std::int64_t total_height() const noexcept;

    struct Range {
        std::uint32_t first{0};
        std::uint32_t last{0};  // inclusive; first > last never happens, an empty document has no layout
    };
    /// The pages that touch the viewport [scroll, scroll + pane height).
    [[nodiscard]] Range visible(std::int64_t scroll) const noexcept;
    /// The page the reader is on: the one that holds the middle of the viewport.
    [[nodiscard]] std::uint32_t current_page(std::int64_t scroll) const noexcept;

    /// Records the real shape of a rendered page. Returns how far the pages below the top of the viewport moved, for the caller to add to its scroll position so that
    /// the page it was looking at does not jump: the change in height of every page that lies above `scroll` (0 if none, or if the aspect ratio did not change).
    [[nodiscard]] std::int64_t learn(std::uint32_t index, std::uint32_t bitmap_width, std::uint32_t bitmap_height, std::int64_t scroll) noexcept;

    [[nodiscard]] std::int64_t clamp_scroll(std::int64_t scroll) const noexcept;

private:
    [[nodiscard]] double aspect_of(std::uint32_t index) const noexcept;
    [[nodiscard]] int height_at(double aspect) const noexcept;

    std::uint32_t count_;
    double first_aspect_;
    int pane_width_{0}, pane_height_{0}, margin_{0}, gap_{0};
    std::uint32_t render_width_{16};
    std::map<std::uint32_t, double> learned_;  // page -> height / width
};

/// A small least-recently-used set of rendered pages. It never holds more than kMaxCachedPages; when it is full, the page farthest from `anchor` goes. The values are
/// opaque to it: an owner gives a `release` callback to free what an evicted or cleared entry holds.
template <class Value>
class PageCache {
public:
    struct Entry {
        std::uint32_t page;
        Value value;
        std::uint32_t render_width;  // the width this was rendered at, so a stale one can be drawn scaled until its replacement arrives
    };

    /// The pages dropped to make room are appended to `evicted` so the owner can free them.
    void put(std::uint32_t page, Value value, std::uint32_t render_width, std::uint32_t anchor, std::vector<Value>& evicted) {
        for (auto it = entries_.begin(); it != entries_.end(); ++it) {
            if (it->page == page) {
                evicted.push_back(it->value);
                entries_.erase(it);
                break;
            }
        }
        while (entries_.size() >= kMaxCachedPages) {
            auto farthest = entries_.begin();
            for (auto it = entries_.begin(); it != entries_.end(); ++it) {
                if (distance(it->page, anchor) > distance(farthest->page, anchor)) {
                    farthest = it;
                }
            }
            evicted.push_back(farthest->value);
            entries_.erase(farthest);
        }
        entries_.push_back({page, value, render_width});
    }

    [[nodiscard]] const Entry* find(std::uint32_t page) const noexcept {
        for (const Entry& entry : entries_) {
            if (entry.page == page) {
                return &entry;
            }
        }
        return nullptr;
    }

    [[nodiscard]] std::size_t size() const noexcept { return entries_.size(); }

    void take_all(std::vector<Value>& out) {
        for (const Entry& entry : entries_) {
            out.push_back(entry.value);
        }
        entries_.clear();
    }

private:
    static std::uint32_t distance(std::uint32_t a, std::uint32_t b) noexcept { return a > b ? a - b : b - a; }

    std::vector<Entry> entries_;
};

}  // namespace mikepdf::shellext
