// libFuzzer target for the engine's document-open and render path (master prompt 12.6: "document load, render page, text extraction,
// annotation enumeration"). It drives mikepdf::pdfium::Document, the wrapper the sandboxed engine uses, exactly as EngineCore does.
//
// What this does and does not test. It tests MikePDF's wrapper and its limits: that every status is one the protocol knows, that every
// reply the wrapper builds satisfies the schema (the generated encoder would refuse it otherwise, so a reply that fails validate() is a
// broken engine), that nothing the wrapper allocates is overrun, and that the wall-clock guards hold. It does NOT fuzz PDFium: the pinned
// PDFium is a prebuilt, uninstrumented DLL, so libFuzzer gets no coverage feedback from inside it and AddressSanitizer cannot see its
// heap. A crash inside pdfium.dll is still a crash here (and is triaged as a PDFium finding, docs/fuzzing.md), but this target is not
// how PDFium is expected to be hardened: the sandbox is (ADR-0004).
#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "mikepdf/pdfium/document.h"
#include "mikepdf/pdfium/library.h"

namespace {

using namespace mikepdf;
using protocol::Status;
using Clock = std::chrono::steady_clock;

[[noreturn]] void broken(const char* what) {
    std::fputs("INVARIANT BROKEN: ", stderr);
    std::fputs(what, stderr);
    std::fputc('\n', stderr);
    std::abort();
}

void require(bool condition, const char* what) {
    if (!condition) {
        broken(what);
    }
}

/// With MIKEPDF_FUZZ_TRACE=1 in the environment, the time spent in each phase is printed when the run ends: where a slow target spends it.
enum Phase : int { kOpen, kInfoSizesOutline, kBoxesLinks, kText, kSearch, kRender, kPhaseCount };
double g_phase_seconds[kPhaseCount];
std::uint64_t g_inputs;

Clock::time_point g_last_lap;

/// Charges the time since the previous lap to `phase`.
void lap(Phase phase) {
    const auto now = Clock::now();
    g_phase_seconds[phase] += std::chrono::duration<double>(now - g_last_lap).count();
    g_last_lap = now;
}

struct TraceReport {
    ~TraceReport() {
        char* value = nullptr;
        std::size_t length = 0;
        const bool enabled = _dupenv_s(&value, &length, "MIKEPDF_FUZZ_TRACE") == 0 && value != nullptr;
        std::free(value);
        if (!enabled) {
            return;
        }
        std::fprintf(stderr, "TRACE inputs=%llu open=%.2fs info=%.2fs boxes+links=%.2fs text=%.2fs search=%.2fs render=%.2fs\n",
                     static_cast<unsigned long long>(g_inputs), g_phase_seconds[kOpen], g_phase_seconds[kInfoSizesOutline],
                     g_phase_seconds[kBoxesLinks], g_phase_seconds[kText], g_phase_seconds[kSearch], g_phase_seconds[kRender]);
    }
} g_trace_report;

/// A small deterministic generator, seeded from the input, so a given input always exercises the same requests.
struct Picker {
    std::uint64_t state;
    std::uint32_t next(std::uint32_t bound) {
        state = state * 6364136223846793005ULL + 1442695040888963407ULL;
        return bound == 0 ? 0 : static_cast<std::uint32_t>((state >> 33) % bound);
    }
};

std::uint64_t seed_of(const std::uint8_t* data, std::size_t size) {
    std::uint64_t h = 1469598103934665603ULL;
    for (std::size_t i = 0; i < size; ++i) {
        h = (h ^ data[i]) * 1099511628211ULL;
    }
    return h;
}

/// A reply is either not Ok, or valid. (The engine encodes with the generated encoder, which validates; an invalid reply would be refused.)
template <class R>
void check_reply(const protocol::Result<R>& result, const char* what) {
    if (result.status == Status::Ok) {
        require(result.value.validate() == Status::Ok, what);
    }
}

bool allowed_open_status(Status s) {
    switch (s) {
        case Status::Ok:
        case Status::PasswordRequired:
        case Status::PasswordIncorrect:
        case Status::UnsupportedEncryption:
        case Status::Damaged:
        case Status::Unsupported:
            return true;
        default:
            return false;
    }
}

bool allowed_read_status(Status s) {
    switch (s) {
        case Status::Ok:
        case Status::InvalidArgument:
        case Status::Damaged:
        case Status::Unsupported:
        case Status::Timeout:
            return true;
        default:
            return false;
    }
}

bool allowed_render_status(Status s) {
    switch (s) {
        case Status::Ok:
        case Status::PartiallyRendered:
        case Status::Cancelled:
        case Status::Timeout:
        case Status::InvalidArgument:
        case Status::Damaged:
        case Status::Unsupported:  // form-field rendering has no environment yet
            return true;
        default:
            return false;
    }
}

constexpr float kScales[] = {0.01f, 0.25f, 1.0f, 1.5f, 3.0f, 16.0f, 64.0f};
constexpr std::uint32_t kTileSides[] = {1, 7, 64, 256, 1000, 1024};
constexpr std::int32_t kTileOrigins[] = {0, 0, 0, 13, 256, 4096, -17, -1024, 16777216 - 1024, -16777216};

void exercise(pdfium::Document& doc, Picker& pick) {
    // The deadline every engine request carries (EngineCore polls it the same way). A short one keeps a pathological page from stalling a run.
    const auto deadline = Clock::now() + std::chrono::seconds(8);

    const auto info = doc.info();
    require(info.status == Status::Ok || info.status == Status::Damaged, "info status");
    check_reply(info, "GetDocumentInfoReply fails its schema");

    const std::uint32_t pages = doc.page_count();
    if (pages == 0) {
        // Legal: the schema's pageCount has no minimum, and PDFium opens a page tree with no leaves. Nothing else can be asked of it.
        return;
    }

    const auto sizes = doc.page_sizes(0, std::min<std::uint32_t>(pages, 100));
    require(allowed_read_status(sizes.status), "page_sizes status");
    check_reply(sizes, "GetPageSizesReply fails its schema");

    const auto outline = doc.outline({}, 0, 64);
    require(allowed_read_status(outline.status), "outline status");
    check_reply(outline, "GetOutlineReply fails its schema");
    if (outline.status == Status::Ok && !outline.value.titles.empty()) {
        const std::uint32_t path[] = {pick.next(static_cast<std::uint32_t>(outline.value.titles.size()))};
        const auto child = doc.outline(path, 0, 64);
        require(allowed_read_status(child.status), "outline child status");
        check_reply(child, "GetOutlineReply (child) fails its schema");
    }

    lap(kInfoSizesOutline);

    const std::uint32_t rounds = std::min<std::uint32_t>(pages, 3);
    for (std::uint32_t round = 0; round < rounds; ++round) {
        const std::uint32_t page = round == 0 ? 0 : pick.next(pages);

        const auto boxes = doc.page_boxes(page);
        require(allowed_read_status(boxes.status), "page_boxes status");
        check_reply(boxes, "GetPageBoxesReply fails its schema");

        const auto links = doc.page_links(page, 0, 512);
        require(allowed_read_status(links.status), "page_links status");
        check_reply(links, "GetPageLinksReply fails its schema");
        if (links.status == Status::Ok) {
            const std::uint32_t listed = static_cast<std::uint32_t>(links.value.kinds.size());
            for (std::uint32_t i = 0; i < std::min<std::uint32_t>(listed, 4); ++i) {
                const auto uri = doc.link_uri(page, i);
                require(allowed_read_status(uri.status), "link_uri status");
                check_reply(uri, "GetLinkUriReply fails its schema");
            }
        }

        lap(kBoxesLinks);

        const auto text = doc.page_text(page, 0, 2048);
        require(allowed_read_status(text.status), "page_text status");
        check_reply(text, "GetPageTextReply fails its schema");
        if (text.status == Status::Ok && text.value.total > 2048) {
            const auto more = doc.page_text(page, 2048, 2048);
            require(allowed_read_status(more.status), "page_text window status");
            check_reply(more, "GetPageTextReply (second window) fails its schema");
        }

        lap(kText);

        static constexpr const char* kQueries[] = {"a", "the", "PDF", "\xC3\xA9", "0"};
        const auto expired = [&] { return Clock::now() >= deadline; };
        const auto found = doc.search_page(page, kQueries[pick.next(5)], pick.next(4), expired);
        require(allowed_read_status(found.status), "search_page status");
        check_reply(found, "SearchPageReply fails its schema");
        lap(kSearch);

        pdfium::TileRequest request;
        request.page = page;
        // Mostly ordinary zoom levels and small tiles, so a run is fast; the extremes (scale 0.01 and 64, 1024 px tiles, origins at the
        // schema's edges) come up often enough to be exercised many times per minute.
        request.scale = pick.next(4) == 0 ? kScales[pick.next(7)] : kScales[2 + pick.next(2)];
        request.rotation = static_cast<std::uint16_t>(pick.next(4));
        request.tile_x = kTileOrigins[pick.next(10)];
        request.tile_y = kTileOrigins[pick.next(10)];
        request.tile_width = pick.next(4) == 0 ? kTileSides[pick.next(6)] : kTileSides[pick.next(3)];
        request.tile_height = pick.next(4) == 0 ? kTileSides[pick.next(6)] : kTileSides[pick.next(3)];
        // Bit 1 (form fields) is refused as Unsupported until forms arrive; ask for it now and then to keep that refusal honest.
        request.flags = pick.next(16) == 0 ? 2 + pick.next(2) : pick.next(2);
        const std::uint32_t stride = request.tile_width * 4;
        std::vector<std::uint8_t> pixels(static_cast<std::size_t>(stride) * request.tile_height);
        const auto interrupt = [&]() -> pdfium::RenderInterrupt {
            return Clock::now() >= deadline ? pdfium::RenderInterrupt::Timeout : pdfium::RenderInterrupt::None;
        };
        require(allowed_render_status(doc.render_tile(request, pixels, stride, interrupt)), "render_tile status");

        // A buffer that is too small must be refused, never written past.
        if (pixels.size() > 4) {
            std::vector<std::uint8_t> small(pixels.size() - 4);
            const Status refused = doc.render_tile(request, small, stride, interrupt);
            require(refused != Status::Ok && refused != Status::PartiallyRendered, "a too-small render buffer was accepted");
        }
        lap(kRender);
    }
}

}  // namespace

extern "C" int LLVMFuzzerInitialize(int*, char***) {
    // PDFium is process-wide state: initialise it once and keep it for the whole run (the engine does the same on its one thread).
    static pdfium::Library library;
    return 0;
}

extern "C" int LLVMFuzzerTestOneInput(const std::uint8_t* data, std::size_t size) {
    if (size == 0) {
        return 0;
    }
    // PDFium reads the bytes in place and may be handed a view of them for the document's whole life: give it a private copy.
    const std::vector<std::uint8_t> bytes(data, data + size);
    Picker pick{seed_of(data, size)};

    ++g_inputs;
    g_last_lap = Clock::now();
    auto opened = pdfium::Document::open(bytes, "");
    require(allowed_open_status(opened.status), "open status");
    if (opened.status == Status::PasswordRequired || opened.status == Status::PasswordIncorrect) {
        // The password path hands the bytes to PDFium's security handlers.
        opened = pdfium::Document::open(bytes, "secret");
        require(allowed_open_status(opened.status), "open status (with a password)");
    }
    lap(kOpen);
    if (opened.status != Status::Ok) {
        return 0;
    }
    exercise(opened.value, pick);
    return 0;
}
