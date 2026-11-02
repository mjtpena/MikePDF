// Tests for the C++ half of MikePDF.Protocol: the MessagePack core, the generated messages and the framing codec.
// The golden frames are written by the C# tests (tests/MikePDF.Protocol.Tests/golden); both sides must read them alike.
#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <functional>
#include <iterator>
#include <limits>
#include <map>
#include <optional>
#include <random>
#include <sstream>
#include <string>
#include <vector>

#include "mikepdf/protocol/codec.h"
#include "mikepdf/protocol/messages.h"
#include "mikepdf/protocol/msgpack.h"

#ifndef MIKEPDF_GOLDEN_DIR
#error "MIKEPDF_GOLDEN_DIR must point at tests/MikePDF.Protocol.Tests/golden"
#endif

namespace mikepdf::protocol {
// Lets gtest print a Status by number in failure messages.
void PrintTo(Status status, std::ostream* os) { *os << "Status(" << static_cast<int>(status) << ")"; }
}  // namespace mikepdf::protocol

namespace {

using namespace mikepdf::protocol;
using Bytes = std::vector<std::uint8_t>;

constexpr float kInf = std::numeric_limits<float>::infinity();
constexpr float kNan = std::numeric_limits<float>::quiet_NaN();

// ---- building hand-made input ----------------------------------------------------------------

// Appends MessagePack, including things the real writer refuses to produce.
class Raw {
public:
    Raw& raw(std::initializer_list<std::uint8_t> bytes) {
        bytes_.insert(bytes_.end(), bytes);
        return *this;
    }
    Raw& raw(const Bytes& bytes) {
        bytes_.insert(bytes_.end(), bytes.begin(), bytes.end());
        return *this;
    }
    Raw& arr(std::size_t n) { return with([n](Writer& w) { w.write_array_header(n); }); }
    Raw& u(std::uint64_t v) { return with([v](Writer& w) { w.write_uint(v); }); }
    Raw& i(std::int64_t v) { return with([v](Writer& w) { w.write_int(v); }); }
    Raw& f(float v) { return with([v](Writer& w) { w.write_f32(v); }); }
    Raw& b(bool v) { return with([v](Writer& w) { w.write_bool(v); }); }
    Raw& nil() { return with([](Writer& w) { w.write_nil(); }); }
    Raw& str(const std::string& s) {
        return with([&s](Writer& w) { w.write_string(s); });
    }
    // A string header followed by exactly these bytes, valid UTF-8 or not.
    Raw& str_bytes(const Bytes& bytes) {
        Writer w;
        w.write_string(std::string(bytes.size(), 'x'));
        const auto written = w.bytes();
        bytes_.insert(bytes_.end(), written.begin(), written.end() - static_cast<std::ptrdiff_t>(bytes.size()));
        bytes_.insert(bytes_.end(), bytes.begin(), bytes.end());
        return *this;
    }
    [[nodiscard]] const Bytes& bytes() const { return bytes_; }

private:
    template <class Fn>
    Raw& with(Fn fn) {
        Writer w(1 << 20);
        fn(w);
        const auto written = w.bytes();
        bytes_.insert(bytes_.end(), written.begin(), written.end());
        return *this;
    }
    Bytes bytes_;
};

Bytes prefixed(const Bytes& body, std::optional<std::uint32_t> declared = std::nullopt) {
    const std::uint32_t n = declared.value_or(static_cast<std::uint32_t>(body.size()));
    Bytes frame{static_cast<std::uint8_t>(n), static_cast<std::uint8_t>(n >> 8), static_cast<std::uint8_t>(n >> 16), static_cast<std::uint8_t>(n >> 24)};
    frame.insert(frame.end(), body.begin(), body.end());
    return frame;
}

Bytes request_frame(const Raw& payload, MessageType type, std::uint64_t id = 1, std::uint32_t doc = 0, std::uint32_t deadline = 0) {
    Raw body;
    body.arr(5).u(id).u(doc).u(deadline).u(static_cast<std::uint16_t>(type)).raw(payload.bytes());
    return prefixed(body.bytes());
}

Bytes response_frame(const Raw* payload, MessageType type, Status status = Status::Ok, std::uint64_t id = 1) {
    Raw body;
    body.arr(4).u(id).u(static_cast<std::uint16_t>(status)).u(static_cast<std::uint16_t>(type));
    if (payload != nullptr) {
        body.raw(payload->bytes());
    } else {
        body.nil();
    }
    return prefixed(body.bytes());
}

Status request_status(const Bytes& frame) { return decode_request_frame(frame).status; }
Status response_status(const Bytes& frame) { return decode_response_frame(frame).status; }

Raw tile(std::uint32_t page, float scale, std::uint16_t rotation, std::int32_t x, std::int32_t y, std::uint32_t w, std::uint32_t h,
         std::uint32_t flags, std::uint32_t slot, std::uint64_t generation) {
    Raw r;
    r.arr(10).u(page).f(scale).u(rotation).i(x).i(y).u(w).u(h).u(flags).u(slot).u(generation);
    return r;
}

Raw good_tile() { return tile(3, 1.5f, 1, -256, 512, 256, 256, 3, 12, 9'000'000'000ull); }

Bytes tile_request(const Raw& payload) { return request_frame(payload, MessageType::RenderTile, 9, 7, 250); }

// ---- the samples (mirror tests/MikePDF.Protocol.Tests/Samples.cs) --------------------------------

struct RequestSample {
    std::string name;
    std::uint64_t id;
    std::uint32_t document;
    std::uint32_t deadline;
    RequestPayload payload;
};

struct ResponseSample {
    std::string name;
    std::uint64_t id;
    Status status;
    MessageType type;
    std::optional<ReplyPayload> payload;
};

std::vector<RequestSample> request_samples() {
    std::vector<RequestSample> v;
    {
        Hello m;
        m.schema_version = kSchemaVersion;
        m.process_kind = ProcessKind::Broker;
        m.pid = 4242;
        v.push_back({"req_hello", 1, 0, 5000, m});
    }
    {
        Ping m;
        m.nonce = 0x1122334455667788ull;
        v.push_back({"req_ping", 2, 0, 0, m});
    }
    v.push_back({"req_shutdown", 3, 0, 1000, Shutdown{}});
    {
        AttachSlotRing m;
        m.handle = 0x2F4;
        m.slot_count = 8;
        m.slot_size = 4194304;
        v.push_back({"req_attach_slot_ring", 12, 0, 5000, m});
    }
    {
        OpenDocument m;
        m.handle = 0x1F4;
        m.password = "p" "\xC3\xA4" "ssw" "\xC3\xB6" "rd" "\xE2\x9C\x93";
        v.push_back({"req_open_document", 4, 0, 30000, m});
    }
    {
        OpenDocument m;
        m.handle = 0xFFFFFFFFFFFFFFFFull;
        v.push_back({"req_open_document_no_password", 5, 0, 30000, m});
    }
    v.push_back({"req_close_document", 6, 7, 1000, CloseDocument{}});
    v.push_back({"req_get_document_info", 7, 7, 1000, GetDocumentInfo{}});
    {
        GetPageSizes m;
        m.first = 100;
        m.count = 5000;
        v.push_back({"req_get_page_sizes", 8, 7, 1000, m});
    }
    {
        RenderTile m;
        m.page = 3;
        m.scale = 1.5f;
        m.rotation = 1;
        m.tile_x = -256;
        m.tile_y = 512;
        m.tile_width = 256;
        m.tile_height = 256;
        m.flags = 3;
        m.slot_index = 12;
        m.generation = 9'000'000'000ull;
        v.push_back({"req_render_tile", 9, 7, 250, m});
    }
    {
        CancelBefore m;
        m.generation = 77;
        v.push_back({"req_cancel_before", 10, 7, 0, m});
    }
    {
        ValidateStructure m;
        m.handle = 1234;
        v.push_back({"req_validate_structure", 11, 0, 60000, m});
    }
    {
        GetOutline m;
        m.path = {2, 0, 5};
        m.first = 64;
        m.count = 64;
        v.push_back({"req_get_outline", 13, 7, 1000, m});
        m.path = {};
        m.first = 0;
        m.count = 1;
        v.push_back({"req_get_outline_top_level", 14, 7, 1000, m});
    }
    {
        GetPageLinks m;
        m.page = 41;
        m.first = 512;
        m.count = 512;
        v.push_back({"req_get_page_links", 15, 7, 1000, m});
    }
    {
        GetLinkUri m;
        m.page = 41;
        m.index = 99999;
        v.push_back({"req_get_link_uri", 16, 7, 1000, m});
    }
    {
        GetPageBoxes m;
        m.page = 9999999;
        v.push_back({"req_get_page_boxes", 17, 7, 1000, m});
    }
    {
        GetPageText m;
        m.page = 3;
        m.first = 2048;
        m.count = 2048;
        v.push_back({"req_get_page_text", 18, 7, 1000, m});
    }
    {
        SearchPage m;
        m.page = 3;
        m.query = "fa" "\xC3\xA7" "ade " "\xE2\x9C\x93";
        m.flags = 3;
        v.push_back({"req_search_page", 19, 7, 1000, m});
    }
    v.push_back({"req_get_document_details", 20, 7, 1500, GetDocumentDetails{}});

    // Schema 5.
    v.push_back({"req_get_viewer_preferences", 21, 7, 1000, GetViewerPreferences{}});
    {
        RenderBand m;
        m.page = 3;
        m.dpi = 600;
        m.rotation = 1;
        m.page_width_px = 65536;
        m.page_height_px = 65536;
        m.band_top = 49152;
        m.band_height = 16384;
        m.flags = 7;
        m.slot_index = 12;
        m.generation = 9'000'000'000ull;
        v.push_back({"req_render_band", 22, 7, 30000, m});
    }
    {
        RenderBand m;
        m.page = 0;
        m.dpi = 150;
        m.rotation = 0;
        m.page_width_px = 1275;
        m.page_height_px = 1650;
        m.band_top = 0;
        m.band_height = 256;
        m.flags = 5;
        m.slot_index = 1;
        m.generation = 1;
        v.push_back({"req_render_band_typical", 23, 7, 30000, m});
    }
    {
        Hello m;
        m.schema_version = kSchemaVersion;
        m.process_kind = ProcessKind::ShellHandler;
        m.pid = 4242;
        v.push_back({"req_hello_shell_handler", 24, 0, 5000, m});
    }
    {
        ShellOpen m;
        m.section = 0x3A8;
        m.bytes = 33'554'432;
        v.push_back({"req_shell_open", 25, 0, 1700, m});
    }
    {
        ShellRenderPage m;
        m.page = 9999999;
        m.max_width = 1024;
        m.max_height = 1024;
        m.flags = 1;
        m.pixels = 0x3B0;
        m.pixel_bytes = 4'194'304;
        v.push_back({"req_shell_render_page", 26, 3, 1500, m});
    }
    {
        ShellRenderPage m;
        m.page = 0;
        m.max_width = 16;
        m.max_height = 16;
        m.flags = 0;
        m.pixels = 1;
        m.pixel_bytes = 4;
        v.push_back({"req_shell_render_page_smallest", 28, 3, 1, m});
    }
    v.push_back({"req_shell_close", 27, 3, 500, ShellClose{}});
    return v;
}

std::vector<ResponseSample> response_samples() {
    std::vector<ResponseSample> v;
    {
        HelloReply m;
        m.schema_version = kSchemaVersion;
        m.pdfium_version = "PDFium 8125.0.0.0";
        v.push_back({"rsp_hello", 1, Status::Ok, MessageType::Hello, m});
    }
    {
        Pong m;
        m.nonce = 0x1122334455667788ull;
        m.progress = 0xFEDCBA9876543210ull;
        v.push_back({"rsp_pong", 2, Status::Ok, MessageType::Ping, m});
    }
    {
        Pong m;
        m.nonce = 1;
        m.progress = 0;
        v.push_back({"rsp_pong_idle", 28, Status::Ok, MessageType::Ping, m});
    }
    v.push_back({"rsp_shutdown", 3, Status::Ok, MessageType::Shutdown, ShutdownReply{}});
    {
        OpenDocumentReply m;
        m.document_id = 7;
        m.page_count = 342;
        v.push_back({"rsp_open_document", 4, Status::Ok, MessageType::OpenDocument, m});
    }
    v.push_back({"rsp_close_document", 6, Status::Ok, MessageType::CloseDocument, CloseDocumentReply{}});
    {
        GetDocumentInfoReply m;
        m.page_count = 342;
        m.pdf_version = 17;
        m.title = "MikePDF " "\xE2\x80\x94" " user guide";
        m.author = "A. Writer";
        m.subject = "";
        m.keywords = "pdf, reader";
        m.creator = "Word";
        m.producer = "MikePDF 1.0";
        m.permissions = 0xFFFFF0C4u;
        m.is_tagged = true;
        m.has_xfa = false;
        m.has_java_script = true;
        m.signature_count = 2;
        v.push_back({"rsp_get_document_info", 7, Status::Ok, MessageType::GetDocumentInfo, m});
    }
    {
        GetPageSizesReply m;
        m.widths = {612.f, 595.276f, 841.89f};
        m.heights = {792.f, 841.89f, 595.276f};
        m.rotations = {0, 1, 3};
        v.push_back({"rsp_get_page_sizes", 8, Status::Ok, MessageType::GetPageSizes, m});
    }
    {
        RenderTileReply m;
        m.slot_index = 12;
        m.width = 256;
        m.height = 256;
        m.stride = 1024;
        m.generation = 9'000'000'000ull;
        v.push_back({"rsp_render_tile", 9, Status::Ok, MessageType::RenderTile, m});
        m.slot_index = 13;
        m.width = 100;
        m.height = 50;
        m.stride = 400;
        m.generation = 1;
        v.push_back({"rsp_render_tile_partial", 9, Status::PartiallyRendered, MessageType::RenderTile, m});
    }
    v.push_back({"rsp_cancel_before", 10, Status::Ok, MessageType::CancelBefore, CancelBeforeReply{}});
    v.push_back({"rsp_attach_slot_ring", 12, Status::Ok, MessageType::AttachSlotRing, AttachSlotRingReply{}});
    {
        ValidateStructureReply m;
        m.ok = false;
        m.truncated = false;
        m.issue_codes = {3, 65535};
        m.issue_messages = {"xref entry 12 points outside the file", "caf" "\xC3\xA9"};
        v.push_back({"rsp_validate_structure", 11, Status::Ok, MessageType::ValidateStructure, m});
    }
    {
        GetOutlineReply m;
        m.total = 3;
        m.titles = {"Introduction", "Chapter 2 " "\xE2\x80\x94" " Fonts " "\xE2\x9C\x93", ""};
        m.kinds = {3, 1, 0};
        m.pages = {0, 41, 0};
        m.xs = {72.f, 0.f, 0.f};
        m.ys = {700.5f, 0.f, 0.f};
        m.has_children = {1, 0, 0};
        v.push_back({"rsp_get_outline", 13, Status::Ok, MessageType::GetOutline, m});
    }
    {
        GetPageLinksReply m;
        m.total = 2;
        m.lefts = {72.f, -10.5f};
        m.bottoms = {700.f, 20.f};
        m.rights = {144.f, 100.f};
        m.tops = {712.f, 32.f};
        m.kinds = {2, 4};
        m.pages = {7, 0};
        m.xs = {0.f, 0.f};
        m.ys = {640.25f, 0.f};
        v.push_back({"rsp_get_page_links", 15, Status::Ok, MessageType::GetPageLinks, m});
    }
    {
        GetLinkUriReply m;
        m.uri = "https://example.org/a%20b?q=" "\xC3\xBC";
        v.push_back({"rsp_get_link_uri", 16, Status::Ok, MessageType::GetLinkUri, m});
    }
    {
        GetPageBoxesReply m;
        m.media_left = 0.f;
        m.media_bottom = 0.f;
        m.media_right = 612.f;
        m.media_top = 792.f;
        m.has_crop_box = true;
        m.crop_left = 100.f;
        m.crop_bottom = 150.f;
        m.crop_right = 500.f;
        m.crop_top = 650.f;
        m.rotation = 1;
        v.push_back({"rsp_get_page_boxes", 17, Status::Ok, MessageType::GetPageBoxes, m});
    }
    {
        GetPageTextReply m;
        m.total = 5000;
        m.truncated = false;
        m.units = {72, 105, 0xFFFD, 10};
        m.lefts = {72.f, 79.f, 85.f, 90.f};
        m.bottoms = {700.f, 700.f, 700.f, 700.f};
        m.rights = {79.f, 85.f, 90.f, 90.f};
        m.tops = {712.f, 712.f, 712.f, 712.f};
        m.flags = {0, 0, 2, 1};
        v.push_back({"rsp_get_page_text", 18, Status::Ok, MessageType::GetPageText, m});
    }
    {
        SearchPageReply m;
        m.truncated = true;
        m.starts = {0, 199000};
        m.lengths = {5, 1000};
        v.push_back({"rsp_search_page", 19, Status::Ok, MessageType::SearchPage, m});
    }
    {
        GetDocumentDetailsReply m;
        m.creation_date = "D:20240115103000+10'00'";
        m.modification_date = "D:20240201";
        m.security_revision = 4;
        m.user_permissions = 0xFFFFF2C0u;
        m.form_type = 1;
        m.font_names = {"ABCDEF+Arial-BoldMT", "Helvetica", "TimesNewRomanPSMT"};
        m.font_flags = {1, 0, 1};
        m.fonts_complete = false;
        m.font_pages_scanned = 100;
        v.push_back({"rsp_get_document_details", 20, Status::Ok, MessageType::GetDocumentDetails, m});
    }
    {
        GetDocumentDetailsReply m;
        m.creation_date = "";
        m.modification_date = "";
        m.security_revision = 0;
        m.user_permissions = 0xFFFFFFFFu;
        m.form_type = 0;
        m.fonts_complete = true;
        m.font_pages_scanned = 0;
        v.push_back({"rsp_get_document_details_empty", 21, Status::Ok, MessageType::GetDocumentDetails, m});
    }

    // Schema 5.
    {
        GetViewerPreferencesReply m;
        m.direction = 1;
        m.print_scaling = 1;
        m.num_copies = 3;
        m.duplex = 3;
        m.range_firsts = {0, 9999999};
        m.range_lasts = {2, 9999999};
        v.push_back({"rsp_get_viewer_preferences", 21, Status::Ok, MessageType::GetViewerPreferences, m});
    }
    {
        GetViewerPreferencesReply m;
        m.direction = 2;
        v.push_back({"rsp_get_viewer_preferences_absent", 29, Status::Ok, MessageType::GetViewerPreferences, m});
    }
    {
        RenderBandReply m;
        m.slot_index = 12;
        m.width = 65536;
        m.height = 16384;
        m.stride = 262144;
        m.generation = 9'000'000'000ull;
        v.push_back({"rsp_render_band", 22, Status::Ok, MessageType::RenderBand, m});
    }
    {
        RenderBandReply m;
        m.slot_index = 1;
        m.width = 1275;
        m.height = 256;
        m.stride = 5100;
        m.generation = 1;
        v.push_back({"rsp_render_band_partial", 23, Status::PartiallyRendered, MessageType::RenderBand, m});
    }
    {
        ShellOpenReply m;
        m.document_id = 3;
        m.page_count = 342;
        m.first_width = 612.0f;
        m.first_height = 792.0f;
        v.push_back({"rsp_shell_open", 25, Status::Ok, MessageType::ShellOpen, m});
    }
    {
        ShellRenderPageReply m;
        m.width = 1024;
        m.height = 768;
        m.stride = 4096;
        v.push_back({"rsp_shell_render_page", 26, Status::Ok, MessageType::ShellRenderPage, m});
    }
    v.push_back({"rsp_shell_close", 27, Status::Ok, MessageType::ShellClose, ShellCloseReply{}});
    v.push_back({"rsp_error_password_required", 4, Status::PasswordRequired, MessageType::OpenDocument, std::nullopt});
    v.push_back({"rsp_error_timeout", 9, Status::Timeout, MessageType::RenderTile, std::nullopt});
    return v;
}

Bytes encode(const RequestSample& s) {
    auto r = encode_request_frame(s.id, s.document, s.deadline, s.payload);
    EXPECT_TRUE(r.ok()) << s.name;
    return r.value;
}

Bytes encode(const ResponseSample& s) {
    auto r = encode_response_frame(s.id, s.status, s.type, s.payload ? &*s.payload : nullptr);
    EXPECT_TRUE(r.ok()) << s.name;
    return r.value;
}

std::vector<Bytes> all_sample_frames() {
    std::vector<Bytes> frames;
    for (const auto& s : request_samples()) frames.push_back(encode(s));
    for (const auto& s : response_samples()) frames.push_back(encode(s));
    return frames;
}

// ---- Reader ------------------------------------------------------------------------------------

template <class Fn>
Status read_with(const Bytes& bytes, Fn fn) {
    Reader r(bytes);
    return fn(r);
}

TEST(MsgpackReader, IntegersAreReadInAnyWidthWhenTheyFit) {
    const std::vector<Bytes> fives = {{0x05},       {0xCC, 5},       {0xCD, 0, 5},       {0xCE, 0, 0, 0, 5},
                                      {0xCF, 0, 0, 0, 0, 0, 0, 0, 5}, {0xD0, 5}, {0xD1, 0, 5}, {0xD2, 0, 0, 0, 5}, {0xD3, 0, 0, 0, 0, 0, 0, 0, 5}};
    for (const auto& enc : fives) {
        std::uint64_t u = 0;
        EXPECT_EQ(read_with(enc, [&](Reader& r) { return r.read_u64(u, 0, 100); }), Status::Ok);
        EXPECT_EQ(u, 5u);
        std::int32_t i = 0;
        EXPECT_EQ(read_with(enc, [&](Reader& r) { return r.read_i32(i, -10, 10); }), Status::Ok);
        EXPECT_EQ(i, 5);
    }
}

TEST(MsgpackReader, UnsignedBoundaries) {
    std::uint16_t u16 = 0;
    EXPECT_EQ(read_with({0xCD, 0xFF, 0xFF}, [&](Reader& r) { return r.read_u16(u16, 0, 65535); }), Status::Ok);
    EXPECT_EQ(u16, 65535);
    EXPECT_EQ(read_with({0xCE, 0, 1, 0, 0}, [&](Reader& r) { return r.read_u16(u16, 0, 65535); }), Status::ProtocolError);

    std::uint32_t u32 = 0;
    EXPECT_EQ(read_with({0xCE, 0xFF, 0xFF, 0xFF, 0xFF}, [&](Reader& r) { return r.read_u32(u32, 0, 0xFFFFFFFFu); }), Status::Ok);
    EXPECT_EQ(u32, 0xFFFFFFFFu);
    EXPECT_EQ(read_with({0xCF, 0, 0, 0, 1, 0, 0, 0, 0}, [&](Reader& r) { return r.read_u32(u32, 0, 0xFFFFFFFFu); }), Status::ProtocolError);

    std::uint64_t u64 = 0;
    EXPECT_EQ(read_with({0xCF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF}, [&](Reader& r) { return r.read_u64(u64, 0, ~0ull); }), Status::Ok);
    EXPECT_EQ(u64, ~0ull);

    // A negative number is never an unsigned value, in any width.
    for (const Bytes& neg : std::vector<Bytes>{{0xFF}, {0xE0}, {0xD0, 0x80}, {0xD1, 0xFF, 0xFF}, {0xD2, 0x80, 0, 0, 0}, {0xD3, 0x80, 0, 0, 0, 0, 0, 0, 0}, {0xD3, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF}}) {
        EXPECT_EQ(read_with(neg, [&](Reader& r) { return r.read_u64(u64, 0, ~0ull); }), Status::ProtocolError);
    }

    // Inside the type but outside the schema range is InvalidArgument, and the range edges are inclusive.
    EXPECT_EQ(read_with({0x05}, [&](Reader& r) { return r.read_u32(u32, 6, 10); }), Status::InvalidArgument);
    EXPECT_EQ(read_with({0x06}, [&](Reader& r) { return r.read_u32(u32, 6, 10); }), Status::Ok);
    EXPECT_EQ(read_with({0x0A}, [&](Reader& r) { return r.read_u32(u32, 6, 10); }), Status::Ok);
    EXPECT_EQ(read_with({0x0B}, [&](Reader& r) { return r.read_u32(u32, 6, 10); }), Status::InvalidArgument);
}

TEST(MsgpackReader, SignedBoundaries) {
    std::int32_t v = 0;
    const auto read = [&](const Bytes& b) {
        return read_with(b, [&](Reader& r) { return r.read_i32(v, std::numeric_limits<std::int32_t>::min(), std::numeric_limits<std::int32_t>::max()); });
    };
    EXPECT_EQ(read({0xD2, 0x7F, 0xFF, 0xFF, 0xFF}), Status::Ok);
    EXPECT_EQ(v, std::numeric_limits<std::int32_t>::max());
    EXPECT_EQ(read({0xD2, 0x80, 0, 0, 0}), Status::Ok);
    EXPECT_EQ(v, std::numeric_limits<std::int32_t>::min());
    EXPECT_EQ(read({0xD3, 0xFF, 0xFF, 0xFF, 0xFF, 0x80, 0, 0, 0}), Status::Ok);
    EXPECT_EQ(v, std::numeric_limits<std::int32_t>::min());
    EXPECT_EQ(read({0xCE, 0x80, 0, 0, 0}), Status::ProtocolError);                                   // 2^31
    EXPECT_EQ(read({0xD3, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF}), Status::ProtocolError);  // -2^31 - 1
    EXPECT_EQ(read({0xD3, 0x80, 0, 0, 0, 0, 0, 0, 0}), Status::ProtocolError);                       // INT64_MIN
    EXPECT_EQ(read({0xCF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF}), Status::ProtocolError);
    EXPECT_EQ(read({0xE0}), Status::Ok);
    EXPECT_EQ(v, -32);
    EXPECT_EQ(read({0xFF}), Status::Ok);
    EXPECT_EQ(v, -1);
}

TEST(MsgpackReader, WrongTypesAreProtocolErrors) {
    std::uint64_t u = 0;
    const std::vector<Bytes> not_ints = {{0xC0}, {0xC2}, {0xC3}, {0xCA, 0, 0, 0, 0}, {0xCB, 0, 0, 0, 0, 0, 0, 0, 0}, {0xA1, 'x'}, {0x90}, {0x80}, {0xC4, 0}, {0xC1}, {0xD4, 1, 0}};
    for (const auto& b : not_ints) {
        EXPECT_EQ(read_with(b, [&](Reader& r) { return r.read_u64(u, 0, ~0ull); }), Status::ProtocolError);
    }
    bool flag = false;
    EXPECT_EQ(read_with({0xC2}, [&](Reader& r) { return r.read_bool(flag); }), Status::Ok);
    EXPECT_FALSE(flag);
    EXPECT_EQ(read_with({0xC3}, [&](Reader& r) { return r.read_bool(flag); }), Status::Ok);
    EXPECT_TRUE(flag);
    EXPECT_EQ(read_with({0x01}, [&](Reader& r) { return r.read_bool(flag); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0xC0}, [&](Reader& r) { return r.read_bool(flag); }), Status::ProtocolError);
}

TEST(MsgpackReader, FloatsAreFloat32OnlyFiniteAndInRange) {
    float v = 0;
    const auto read = [&](const Bytes& b, float lo, float hi, bool exclusive) {
        return read_with(b, [&](Reader& r) { return r.read_f32(v, lo, hi, exclusive); });
    };
    const auto enc = [](float f) { return Raw().f(f).bytes(); };
    EXPECT_EQ(read(enc(1.5f), 0.f, 2.f, false), Status::Ok);
    EXPECT_EQ(v, 1.5f);
    EXPECT_EQ(read(enc(0.f), 0.f, 2.f, false), Status::Ok);
    EXPECT_EQ(read(enc(0.f), 0.f, 2.f, true), Status::InvalidArgument);
    EXPECT_EQ(read(enc(2.f), 0.f, 2.f, true), Status::Ok);
    EXPECT_EQ(read(enc(std::nextafter(2.f, 3.f)), 0.f, 2.f, false), Status::InvalidArgument);
    EXPECT_EQ(read(enc(std::nextafter(0.f, -1.f)), 0.f, 2.f, false), Status::InvalidArgument);
    EXPECT_EQ(read(enc(kNan), -1e30f, 1e30f, false), Status::InvalidArgument);
    EXPECT_EQ(read(enc(kInf), -1e30f, 1e30f, false), Status::InvalidArgument);
    EXPECT_EQ(read(enc(-kInf), -1e30f, 1e30f, false), Status::InvalidArgument);
    // Negative NaN and a signalling NaN are NaN too.
    EXPECT_EQ(read({0xCA, 0xFF, 0xC0, 0, 0}, -1e30f, 1e30f, false), Status::InvalidArgument);
    EXPECT_EQ(read({0xCA, 0x7F, 0xA0, 0, 0}, -1e30f, 1e30f, false), Status::InvalidArgument);
    // float64 and integers are not float32.
    EXPECT_EQ(read({0xCB, 0x3F, 0xF8, 0, 0, 0, 0, 0, 0}, 0.f, 2.f, false), Status::ProtocolError);
    EXPECT_EQ(read({0x01}, 0.f, 2.f, false), Status::ProtocolError);
    EXPECT_EQ(read({0xCA, 0x3F, 0xC0}, 0.f, 2.f, false), Status::ProtocolError);  // truncated
}

TEST(MsgpackReader, StringHeadersAndLimits) {
    std::string s;
    // fixstr, str8, str16 and str32 headers all read.
    for (const std::size_t n : {0u, 1u, 31u, 32u, 255u, 256u, 65535u}) {
        Raw r;
        r.str(std::string(n, 'a'));
        EXPECT_EQ(read_with(r.bytes(), [&](Reader& rd) { return rd.read_string(s, 100000); }), Status::Ok) << n;
        EXPECT_EQ(s.size(), n);
    }
    Bytes str32 = {0xDB, 0, 0, 0, 3, 'a', 'b', 'c'};
    EXPECT_EQ(read_with(str32, [&](Reader& r) { return r.read_string(s, 10); }), Status::Ok);
    EXPECT_EQ(s, "abc");

    // The limit is in bytes, inclusive.
    EXPECT_EQ(read_with(Raw().str("abc").bytes(), [&](Reader& r) { return r.read_string(s, 3); }), Status::Ok);
    EXPECT_EQ(read_with(Raw().str("abc").bytes(), [&](Reader& r) { return r.read_string(s, 2); }), Status::InvalidArgument);
    EXPECT_EQ(read_with(Raw().str("\xC3\xA9\xC3\xA9").bytes(), [&](Reader& r) { return r.read_string(s, 4); }), Status::Ok);
    EXPECT_EQ(read_with(Raw().str("\xC3\xA9\xC3\xA9").bytes(), [&](Reader& r) { return r.read_string(s, 3); }), Status::InvalidArgument);

    // A length that claims more than the message holds is a protocol error, even for 4 GiB - 1 and with a huge limit.
    EXPECT_EQ(read_with({0xDB, 0xFF, 0xFF, 0xFF, 0xFF, 'a'}, [&](Reader& r) { return r.read_string(s, ~std::size_t{0}); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0xDA, 0x10, 0x00, 'a'}, [&](Reader& r) { return r.read_string(s, 100000); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0xA5, 'a'}, [&](Reader& r) { return r.read_string(s, 100); }), Status::ProtocolError);

    // Only the string family is a string.
    EXPECT_EQ(read_with({0xC4, 1, 'a'}, [&](Reader& r) { return r.read_string(s, 100); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0xC0}, [&](Reader& r) { return r.read_string(s, 100); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0x01}, [&](Reader& r) { return r.read_string(s, 100); }), Status::ProtocolError);
}

TEST(MsgpackReader, StringsMustBeStrictUtf8WithoutNul) {
    std::string s;
    const std::vector<Bytes> bad = {
        {0xFF}, {0xC0, 0x80}, {0xC1, 0xBF}, {0xE0, 0x80, 0x80}, {0xED, 0xA0, 0x80}, {0xED, 0xBF, 0xBF}, {0xF0, 0x80, 0x80, 0x80},
        {0xF4, 0x90, 0x80, 0x80}, {0xF5, 0x80, 0x80, 0x80}, {0xE2, 0x82}, {0xC3}, {0x80}, {0xBF}, {0xE2, 0x28, 0xA1}, {0xF0, 0x9F, 0x98},
    };
    for (const auto& b : bad) {
        EXPECT_EQ(read_with(Raw().str_bytes(b).bytes(), [&](Reader& r) { return r.read_string(s, 100); }), Status::ProtocolError);
        EXPECT_FALSE(is_valid_utf8(b));
    }
    const std::vector<Bytes> good = {{'a'}, {0xC2, 0x80}, {0xDF, 0xBF}, {0xE0, 0xA0, 0x80}, {0xED, 0x9F, 0xBF}, {0xEE, 0x80, 0x80}, {0xEF, 0xBF, 0xBF},
                                     {0xF0, 0x90, 0x80, 0x80}, {0xF4, 0x8F, 0xBF, 0xBF}, {0xF1, 0x80, 0x80, 0x80}};
    for (const auto& b : good) {
        EXPECT_EQ(read_with(Raw().str_bytes(b).bytes(), [&](Reader& r) { return r.read_string(s, 100); }), Status::Ok);
        EXPECT_TRUE(is_valid_utf8(b));
    }
    EXPECT_EQ(read_with(Raw().str(std::string("a\0b", 3)).bytes(), [&](Reader& r) { return r.read_string(s, 100); }), Status::InvalidArgument);
    EXPECT_FALSE(is_valid_text(std::string("a\0b", 3)));
    EXPECT_TRUE(is_valid_text("plain"));
}

TEST(MsgpackReader, OptionalStrings) {
    std::optional<std::string> s = "stale";
    EXPECT_EQ(read_with({0xC0}, [&](Reader& r) { return r.read_optional_string(s, 10); }), Status::Ok);
    EXPECT_FALSE(s.has_value());
    EXPECT_EQ(read_with(Raw().str("hi").bytes(), [&](Reader& r) { return r.read_optional_string(s, 10); }), Status::Ok);
    EXPECT_EQ(s, "hi");
    EXPECT_EQ(read_with(Raw().str("toolong").bytes(), [&](Reader& r) { return r.read_optional_string(s, 3); }), Status::InvalidArgument);
    EXPECT_EQ(read_with({0x05}, [&](Reader& r) { return r.read_optional_string(s, 3); }), Status::ProtocolError);
}

TEST(MsgpackReader, ArrayHeaders) {
    std::size_t n = 0;
    EXPECT_EQ(read_with({0x93, 1, 2, 3}, [&](Reader& r) { return r.read_array_header(n, 3); }), Status::Ok);
    EXPECT_EQ(n, 3u);
    EXPECT_EQ(read_with({0x93, 1, 2, 3}, [&](Reader& r) { return r.read_array_header(n, 2); }), Status::InvalidArgument);
    EXPECT_EQ(read_with({0xDC, 0, 3, 1, 2, 3}, [&](Reader& r) { return r.read_array_header(n, 3); }), Status::Ok);
    EXPECT_EQ(read_with({0xDD, 0, 0, 0, 3, 1, 2, 3}, [&](Reader& r) { return r.read_array_header(n, 3); }), Status::Ok);
    // A count the remaining bytes cannot hold is a protocol error before the schema limit is even consulted.
    EXPECT_EQ(read_with({0xDD, 0xFF, 0xFF, 0xFF, 0xFF, 1}, [&](Reader& r) { return r.read_array_header(n, ~std::size_t{0}); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0xDD, 0xFF, 0xFF, 0xFF, 0xFF, 1}, [&](Reader& r) { return r.read_array_header(n, 5); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0xDC, 0xFF, 0xFF}, [&](Reader& r) { return r.read_array_header(n, 5); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0x92, 1}, [&](Reader& r) { return r.read_array_header(n, 5); }), Status::ProtocolError);
    // Not an array.
    EXPECT_EQ(read_with({0x80}, [&](Reader& r) { return r.read_array_header(n, 5); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0xC0}, [&](Reader& r) { return r.read_array_header(n, 5); }), Status::ProtocolError);
    EXPECT_EQ(read_with({}, [&](Reader& r) { return r.read_array_header(n, 5); }), Status::ProtocolError);

    EXPECT_EQ(read_with({0x92, 1, 2}, [&](Reader& r) { return r.expect_array(2); }), Status::Ok);
    EXPECT_EQ(read_with({0x92, 1, 2}, [&](Reader& r) { return r.expect_array(3); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0x92, 1, 2}, [&](Reader& r) { return r.expect_array(1); }), Status::ProtocolError);
    EXPECT_EQ(read_with({0x91}, [&](Reader& r) { return r.expect_array(1); }), Status::ProtocolError);
}

TEST(MsgpackReader, NilAndEnd) {
    const Bytes data{0xC0, 0x01};
    Reader r(data);
    EXPECT_TRUE(r.next_is_nil());
    EXPECT_EQ(r.read_nil(), Status::Ok);
    EXPECT_FALSE(r.next_is_nil());
    EXPECT_EQ(r.read_nil(), Status::ProtocolError);
    EXPECT_FALSE(r.at_end());
    EXPECT_EQ(r.remaining(), 1u);
    EXPECT_STRNE(r.detail(), "");
    Reader empty{std::span<const std::uint8_t>{}};
    EXPECT_TRUE(empty.at_end());
    EXPECT_FALSE(empty.next_is_nil());
}

TEST(MsgpackReader, EveryTruncationOfEveryValueFails) {
    // Whatever the type, a value cut short at any point is a ProtocolError and never reads past the end.
    struct Case {
        Bytes bytes;
        std::function<Status(Reader&)> read;
    };
    std::uint64_t u64 = 0;
    std::int32_t i32 = 0;
    float f = 0;
    bool b = false;
    std::string s;
    std::size_t n = 0;
    const std::vector<Case> cases = {
        {Raw().u(0xFFFFFFFFFFull).bytes(), [&](Reader& r) { return r.read_u64(u64, 0, ~0ull); }},
        {Raw().i(-100000).bytes(), [&](Reader& r) { return r.read_i32(i32, std::numeric_limits<std::int32_t>::min(), std::numeric_limits<std::int32_t>::max()); }},
        {Raw().f(1.f).bytes(), [&](Reader& r) { return r.read_f32(f, -2.f, 2.f, false); }},
        {Raw().b(true).bytes(), [&](Reader& r) { return r.read_bool(b); }},
        {Raw().str(std::string(300, 'x')).bytes(), [&](Reader& r) { return r.read_string(s, 1000); }},
        {Raw().arr(300).u(1).bytes(), [&](Reader& r) { return r.read_array_header(n, 1000); }},
    };
    for (const auto& c : cases) {
        for (std::size_t keep = 0; keep < c.bytes.size(); ++keep) {
            Bytes cut(c.bytes.begin(), c.bytes.begin() + static_cast<std::ptrdiff_t>(keep));
            Reader r(cut);
            EXPECT_EQ(c.read(r), Status::ProtocolError) << "keep " << keep << " of " << c.bytes.size();
        }
    }
}

// ---- Writer ------------------------------------------------------------------------------------

Bytes written(const std::function<void(Writer&)>& fn) {
    Writer w(1 << 20);
    fn(w);
    return Bytes(w.bytes().begin(), w.bytes().end());
}

TEST(MsgpackWriter, IntegersUseTheShortestForm) {
    EXPECT_EQ(written([](Writer& w) { w.write_uint(0); }), (Bytes{0x00}));
    EXPECT_EQ(written([](Writer& w) { w.write_uint(127); }), (Bytes{0x7F}));
    EXPECT_EQ(written([](Writer& w) { w.write_uint(128); }), (Bytes{0xCC, 0x80}));
    EXPECT_EQ(written([](Writer& w) { w.write_uint(255); }), (Bytes{0xCC, 0xFF}));
    EXPECT_EQ(written([](Writer& w) { w.write_uint(256); }), (Bytes{0xCD, 0x01, 0x00}));
    EXPECT_EQ(written([](Writer& w) { w.write_uint(65535); }), (Bytes{0xCD, 0xFF, 0xFF}));
    EXPECT_EQ(written([](Writer& w) { w.write_uint(65536); }), (Bytes{0xCE, 0, 1, 0, 0}));
    EXPECT_EQ(written([](Writer& w) { w.write_uint(0xFFFFFFFFull); }), (Bytes{0xCE, 0xFF, 0xFF, 0xFF, 0xFF}));
    EXPECT_EQ(written([](Writer& w) { w.write_uint(0x100000000ull); }), (Bytes{0xCF, 0, 0, 0, 1, 0, 0, 0, 0}));
    EXPECT_EQ(written([](Writer& w) { w.write_int(-1); }), (Bytes{0xFF}));
    EXPECT_EQ(written([](Writer& w) { w.write_int(-32); }), (Bytes{0xE0}));
    EXPECT_EQ(written([](Writer& w) { w.write_int(-33); }), (Bytes{0xD0, 0xDF}));
    EXPECT_EQ(written([](Writer& w) { w.write_int(-128); }), (Bytes{0xD0, 0x80}));
    EXPECT_EQ(written([](Writer& w) { w.write_int(-129); }), (Bytes{0xD1, 0xFF, 0x7F}));
    EXPECT_EQ(written([](Writer& w) { w.write_int(-32768); }), (Bytes{0xD1, 0x80, 0x00}));
    EXPECT_EQ(written([](Writer& w) { w.write_int(-32769); }), (Bytes{0xD2, 0xFF, 0xFF, 0x7F, 0xFF}));
    EXPECT_EQ(written([](Writer& w) { w.write_int(std::numeric_limits<std::int32_t>::min()); }), (Bytes{0xD2, 0x80, 0, 0, 0}));
    EXPECT_EQ(written([](Writer& w) { w.write_int(static_cast<std::int64_t>(std::numeric_limits<std::int32_t>::min()) - 1); }),
              (Bytes{0xD3, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF}));
    EXPECT_EQ(written([](Writer& w) { w.write_int(300); }), (Bytes{0xCD, 0x01, 0x2C}));
}

TEST(MsgpackWriter, StringsArraysAndFloats) {
    const auto header_of = [](std::size_t n) {
        return written([n](Writer& w) { w.write_string(std::string(n, 'a')); });
    };
    EXPECT_EQ(header_of(0)[0], 0xA0);
    EXPECT_EQ(header_of(31)[0], 0xBF);
    EXPECT_EQ(header_of(32)[0], 0xD9);
    EXPECT_EQ(header_of(255)[0], 0xD9);
    EXPECT_EQ(header_of(256)[0], 0xDA);
    EXPECT_EQ(header_of(65535)[0], 0xDA);
    EXPECT_EQ(header_of(65536)[0], 0xDB);
    EXPECT_EQ(header_of(65536).size(), 65536u + 5u);

    const auto array_header = [](std::size_t n) {
        return written([n](Writer& w) { w.write_array_header(n); });
    };
    EXPECT_EQ(array_header(15), (Bytes{0x9F}));
    EXPECT_EQ(array_header(16), (Bytes{0xDC, 0, 16}));
    EXPECT_EQ(array_header(65535), (Bytes{0xDC, 0xFF, 0xFF}));
    EXPECT_EQ(array_header(65536), (Bytes{0xDD, 0, 1, 0, 0}));

    EXPECT_EQ(written([](Writer& w) { w.write_f32(1.5f); }), (Bytes{0xCA, 0x3F, 0xC0, 0, 0}));
    EXPECT_EQ(written([](Writer& w) { w.write_nil(); w.write_bool(true); w.write_bool(false); }), (Bytes{0xC0, 0xC3, 0xC2}));
}

TEST(MsgpackWriter, NeverGrowsPastItsCeiling) {
    Writer w(4);
    w.write_uint(1);
    w.write_uint(2);
    w.write_uint(3);
    w.write_uint(4);
    EXPECT_EQ(w.status(), Status::Ok);
    EXPECT_EQ(w.bytes().size(), 4u);
    w.write_uint(5);
    EXPECT_EQ(w.status(), Status::InvalidArgument);
    EXPECT_EQ(w.bytes().size(), 4u);
    w.write_uint(6);  // sticky: nothing more is written
    EXPECT_EQ(w.bytes().size(), 4u);

    Writer big(10);
    big.write_string(std::string(100, 'a'));
    EXPECT_EQ(big.status(), Status::InvalidArgument);
    EXPECT_LE(big.bytes().size(), 10u);

    Writer exact(3);
    exact.write_uint(0x1234);  // three bytes
    EXPECT_EQ(exact.status(), Status::Ok);
    Writer one_short(2);
    one_short.write_uint(0x1234);
    EXPECT_EQ(one_short.status(), Status::InvalidArgument);
    EXPECT_LE(one_short.bytes().size(), 2u);  // after an overflow the content is unspecified, but never past the ceiling

    Writer taken(8);
    taken.write_uint(1);
    const Bytes out = taken.take();
    EXPECT_EQ(out, (Bytes{1}));
}

// ---- messages, framing and limits --------------------------------------------------------------

TEST(ProtocolRoundTrip, EveryRequestAndReplySurvives) {
    for (const auto& s : request_samples()) {
        const Bytes frame = encode(s);
        auto decoded = decode_request_frame(frame);
        ASSERT_TRUE(decoded.ok()) << s.name << ": " << decoded.detail;
        EXPECT_EQ(decoded.value.request_id, s.id);
        EXPECT_EQ(decoded.value.document_id, s.document);
        EXPECT_EQ(decoded.value.deadline_ms, s.deadline);
        EXPECT_EQ(decoded.value.message_type, message_type_of(s.payload));
        EXPECT_TRUE(decoded.value.payload == s.payload) << s.name;
        const auto& e = decoded.value;
        EXPECT_EQ(encode_request_frame(e.request_id, e.document_id, e.deadline_ms, e.payload).value, frame) << s.name;
    }
    for (const auto& s : response_samples()) {
        const Bytes frame = encode(s);
        auto decoded = decode_response_frame(frame);
        ASSERT_TRUE(decoded.ok()) << s.name << ": " << decoded.detail;
        EXPECT_EQ(decoded.value.status, s.status);
        EXPECT_EQ(decoded.value.message_type, s.type);
        EXPECT_TRUE(decoded.value.payload == s.payload) << s.name;
        const auto& e = decoded.value;
        EXPECT_EQ(encode_response_frame(e.request_id, e.status, e.message_type, e.payload ? &*e.payload : nullptr).value, frame) << s.name;
    }
}

TEST(ProtocolRoundTrip, EveryStatusIsCarriedAsItsOwnValue) {
    for (std::uint16_t raw = 0; raw < 32; ++raw) {
        const Status status = static_cast<Status>(raw);
        if (!is_defined_status(raw)) {
            EXPECT_EQ(response_status(response_frame(nullptr, MessageType::Ping, status)), Status::ProtocolError) << raw;
            continue;
        }
        Pong pong;
        const ReplyPayload payload = pong;
        auto frame = encode_response_frame(1, status, MessageType::Ping, carries_payload(status) ? &payload : nullptr);
        ASSERT_TRUE(frame.ok()) << raw;
        auto decoded = decode_response_frame(frame.value);
        ASSERT_TRUE(decoded.ok());
        EXPECT_EQ(decoded.value.status, status);
        EXPECT_EQ(decoded.value.payload.has_value(), carries_payload(status));
    }
}

TEST(ProtocolRoundTrip, TheSchemaConstantsAreWhatTheDocsSay) {
    EXPECT_EQ(kMaxFrameBytes, 65536u);
    EXPECT_EQ(kSchemaVersion, 5u);
    EXPECT_EQ(kMaxDeadlineMs, 86400000u);
}

TEST(ProtocolEncode, RefusesWhatItWouldNotAccept) {
    RenderTile bad;
    bad.scale = kNan;
    bad.tile_width = 1;
    bad.tile_height = 1;
    EXPECT_EQ(encode_request_frame(1, 1, 0, bad).status, Status::InvalidArgument);
    EXPECT_EQ(encode_request_frame(1, 0, 0, CloseDocument{}).status, Status::InvalidArgument);  // needs a document
    EXPECT_EQ(encode_request_frame(1, 9, 0, Ping{}).status, Status::InvalidArgument);            // takes none
    EXPECT_EQ(encode_request_frame(1, 0, kMaxDeadlineMs + 1, Ping{}).status, Status::InvalidArgument);
    EXPECT_EQ(encode_request_frame(1, 0, kMaxDeadlineMs, Ping{}).status, Status::Ok);

    Pong pong;
    const ReplyPayload payload = pong;
    EXPECT_EQ(encode_response_frame(1, Status::Ok, MessageType::Ping, nullptr).status, Status::InvalidArgument);
    EXPECT_EQ(encode_response_frame(1, Status::Timeout, MessageType::Ping, &payload).status, Status::InvalidArgument);
    EXPECT_EQ(encode_response_frame(1, Status::Ok, MessageType::Shutdown, &payload).status, Status::InvalidArgument);
    EXPECT_EQ(encode_response_frame(1, static_cast<Status>(99), MessageType::Ping, nullptr).status, Status::InvalidArgument);
    EXPECT_EQ(encode_response_frame(1, Status::Ok, static_cast<MessageType>(99), &payload).status, Status::InvalidArgument);
}

TEST(ProtocolEncode, TheWorstCaseRepliesFitInOneFrame) {
    GetPageSizesReply sizes;
    sizes.widths.assign(5000, 14400.f);
    sizes.heights.assign(5000, 14400.f);
    sizes.rotations.assign(5000, 3);
    ReplyPayload payload = sizes;
    auto frame = encode_response_frame(~0ull, Status::Ok, MessageType::GetPageSizes, &payload);
    ASSERT_TRUE(frame.ok());
    EXPECT_LE(frame.value.size(), kMaxFrameBytes + kFrameHeaderBytes);
    EXPECT_TRUE(decode_response_frame(frame.value).ok());

    ValidateStructureReply issues;
    issues.truncated = true;
    issues.issue_codes.assign(256, 65535);
    issues.issue_messages.assign(256, std::string(96, 'a') + std::string(96, 'b'));
    payload = issues;
    frame = encode_response_frame(~0ull, Status::Ok, MessageType::ValidateStructure, &payload);
    ASSERT_TRUE(frame.ok());
    EXPECT_TRUE(decode_response_frame(frame.value).ok());

    GetDocumentInfoReply info;
    info.title = std::string(1024, 't');
    info.author = std::string(1024, 'a');
    info.subject = std::string(1024, 's');
    info.keywords = std::string(1024, 'k');
    info.creator = std::string(1024, 'c');
    info.producer = std::string(1024, 'p');
    payload = info;
    EXPECT_TRUE(encode_response_frame(1, Status::Ok, MessageType::GetDocumentInfo, &payload).ok());
}

TEST(ProtocolFraming, LengthPrefixRules) {
    std::size_t n = 0;
    EXPECT_EQ(read_frame_length(Bytes{1, 0, 0, 0}, n), Status::Ok);
    EXPECT_EQ(n, 1u);
    EXPECT_EQ(read_frame_length(Bytes{0, 0, 1, 0}, n), Status::Ok);  // 65536
    EXPECT_EQ(n, 65536u);
    EXPECT_EQ(read_frame_length(Bytes{1, 0, 1, 0}, n), Status::ProtocolError);  // 65537
    EXPECT_EQ(read_frame_length(Bytes{0xFF, 0xFF, 0xFF, 0xFF}, n), Status::ProtocolError);
    EXPECT_EQ(read_frame_length(Bytes{0, 0, 0, 0}, n), Status::ProtocolError);
    EXPECT_EQ(read_frame_length(Bytes{1, 0, 0}, n), Status::ProtocolError);
    EXPECT_EQ(read_frame_length(Bytes{}, n), Status::ProtocolError);
    // Little endian: 0x0100 = 256, not 1.
    EXPECT_EQ(read_frame_length(Bytes{0, 1, 0, 0}, n), Status::Ok);
    EXPECT_EQ(n, 256u);
}

TEST(ProtocolFraming, AGarbageFrameOfMaximumSizeIsRejected) {
    Bytes body(kMaxFrameBytes, 0xDD);
    EXPECT_EQ(request_status(prefixed(body)), Status::ProtocolError);
    EXPECT_EQ(response_status(prefixed(body)), Status::ProtocolError);
    Bytes too_big(kMaxFrameBytes + 1, 0x00);
    EXPECT_EQ(request_status(prefixed(too_big)), Status::ProtocolError);
}

TEST(ProtocolFraming, EveryTruncationOfEveryValidFrameIsRejected) {
    std::size_t index = 0;
    for (const auto& frame : all_sample_frames()) {
        const bool is_request = index++ < request_samples().size();
        for (std::size_t keep = 0; keep < frame.size(); ++keep) {
            const Bytes cut(frame.begin(), frame.begin() + static_cast<std::ptrdiff_t>(keep));
            EXPECT_EQ(is_request ? request_status(cut) : response_status(cut), Status::ProtocolError) << "frame " << index << " keep " << keep;
            if (keep > 4) {
                const Bytes body(frame.begin() + 4, frame.begin() + static_cast<std::ptrdiff_t>(keep));
                const auto lying = prefixed(body, static_cast<std::uint32_t>(frame.size() - 4));
                EXPECT_EQ(is_request ? request_status(lying) : response_status(lying), Status::ProtocolError);
                const auto honest = prefixed(body);
                EXPECT_NE(is_request ? request_status(honest) : response_status(honest), Status::Ok);
            }
        }
    }
}

TEST(ProtocolFraming, ExtraBytesAreRejected) {
    Ping ping;
    ping.nonce = 5;
    Bytes frame = encode_request_frame(1, 0, 0, ping).value;
    Bytes after = frame;
    after.push_back(0);
    EXPECT_EQ(request_status(after), Status::ProtocolError);
    Bytes inside(frame.begin() + 4, frame.end());
    inside.push_back(0xC0);
    EXPECT_EQ(request_status(prefixed(inside)), Status::ProtocolError);
}

TEST(ProtocolFraming, DeepNestingNeitherRecursesNorOverflowsTheStack) {
    for (const int depth : {5, 100, 10'000, 30'000}) {
        Raw payload;
        for (int i = 0; i < depth; ++i) payload.arr(1);
        payload.u(1);
        EXPECT_EQ(request_status(request_frame(payload, MessageType::Ping)), Status::ProtocolError) << depth;
    }
    Raw nested;
    nested.arr(3).arr(1);
    for (int i = 0; i < 20'000; ++i) nested.arr(1);
    EXPECT_EQ(response_status(response_frame(&nested, MessageType::GetPageSizes)), Status::ProtocolError);
}

TEST(ProtocolFraming, MapsExtensionsAndBinaryAreRejected) {
    EXPECT_EQ(request_status(request_frame(Raw().raw({0x80}), MessageType::Ping)), Status::ProtocolError);
    EXPECT_EQ(request_status(request_frame(Raw().arr(1).raw({0xD4, 1, 1}), MessageType::Ping)), Status::ProtocolError);
    EXPECT_EQ(request_status(request_frame(Raw().arr(2).u(1).raw({0xC4, 2, 1, 2}), MessageType::OpenDocument)), Status::ProtocolError);
}

TEST(ProtocolEnvelope, HeaderRules) {
    EXPECT_EQ(request_status(request_frame(Raw().arr(1).u(1), MessageType::Ping)), Status::Ok);
    EXPECT_EQ(request_status(request_frame(Raw().arr(1).u(1), static_cast<MessageType>(0))), Status::ProtocolError);
    for (const int type : {4, 15, 27, 1023, 1025, 65535}) {
        EXPECT_EQ(request_status(request_frame(Raw().arr(0), static_cast<MessageType>(type))), Status::ProtocolError) << type;
    }
    // Four elements instead of five.
    Raw four;
    four.arr(4).u(1).u(0).u(0).u(2);
    EXPECT_EQ(request_status(prefixed(four.bytes())), Status::ProtocolError);
    // Document rules.
    EXPECT_EQ(request_status(request_frame(Raw().arr(0), MessageType::CloseDocument, 1, 0)), Status::InvalidArgument);
    EXPECT_EQ(request_status(request_frame(Raw().arr(0), MessageType::CloseDocument, 1, 1)), Status::Ok);
    EXPECT_EQ(request_status(request_frame(Raw().arr(1).u(1), MessageType::Ping, 1, 5)), Status::InvalidArgument);
    // Deadline edge.
    EXPECT_EQ(request_status(request_frame(Raw().arr(1).u(1), MessageType::Ping, 1, 0, kMaxDeadlineMs)), Status::Ok);
    EXPECT_EQ(request_status(request_frame(Raw().arr(1).u(1), MessageType::Ping, 1, 0, kMaxDeadlineMs + 1)), Status::InvalidArgument);
    // Ids use all 64 bits and the full u32 range of the document.
    EXPECT_EQ(request_status(request_frame(Raw().arr(0), MessageType::CloseDocument, ~0ull, 0xFFFFFFFFu)), Status::Ok);
    // A request is never a response and vice versa.
    for (const auto& s : request_samples()) EXPECT_NE(response_status(encode(s)), Status::Ok) << s.name;
    for (const auto& s : response_samples()) EXPECT_NE(request_status(encode(s)), Status::Ok) << s.name;
}

TEST(ProtocolEnvelope, ResponseRules) {
    const Raw pong = Raw().arr(2).u(3).u(17);
    EXPECT_EQ(response_status(response_frame(&pong, MessageType::Ping)), Status::Ok);
    EXPECT_EQ(response_status(response_frame(nullptr, MessageType::Ping)), Status::ProtocolError);                     // Ok needs a payload
    EXPECT_EQ(response_status(response_frame(&pong, MessageType::Ping, Status::Timeout)), Status::ProtocolError);      // error has none
    EXPECT_EQ(response_status(response_frame(nullptr, MessageType::Ping, Status::Cancelled)), Status::Ok);
    const Raw partial = Raw().arr(5).u(0).u(1).u(1).u(4).u(1);
    EXPECT_EQ(response_status(response_frame(&partial, MessageType::RenderTile, Status::PartiallyRendered)), Status::Ok);
    for (const int status : {13, 255, 65535}) {
        EXPECT_EQ(response_status(response_frame(nullptr, MessageType::Ping, static_cast<Status>(status))), Status::ProtocolError) << status;
    }
    EXPECT_EQ(response_status(response_frame(&pong, static_cast<MessageType>(999))), Status::ProtocolError);
    // A hello with another schema version still decodes: the broker, not the codec, decides what to do about it.
    Hello hello;
    hello.schema_version = kSchemaVersion + 1;
    hello.process_kind = ProcessKind::Engine;
    hello.pid = 1;
    EXPECT_TRUE(decode_request_frame(encode_request_frame(1, 0, 0, hello).value).ok());
}

TEST(ProtocolLimits, RenderTile) {
    const auto status = [](const Raw& r) { return request_status(tile_request(r)); };
    EXPECT_EQ(status(good_tile()), Status::Ok);
    // scale 0.01 ..= 64
    EXPECT_EQ(status(tile(0, 0.01f, 0, 0, 0, 1, 1, 0, 0, 0)), Status::Ok);
    EXPECT_EQ(status(tile(0, 64.f, 0, 0, 0, 1, 1, 0, 0, 0)), Status::Ok);
    EXPECT_EQ(status(tile(0, std::nextafter(0.01f, 0.f), 0, 0, 0, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, std::nextafter(64.f, 100.f), 0, 0, 0, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, kNan, 0, 0, 0, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, kInf, 0, 0, 0, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, -kInf, 0, 0, 0, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 0.f, 0, 0, 0, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, -1.f, 0, 0, 0, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    // page, rotation, tile origin, tile size, flags, slot
    EXPECT_EQ(status(tile(9'999'999, 1.f, 0, 0, 0, 1, 1, 0, 0, 0)), Status::Ok);
    EXPECT_EQ(status(tile(10'000'000, 1.f, 0, 0, 0, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 1.f, 3, 0, 0, 1, 1, 0, 0, 0)), Status::Ok);
    EXPECT_EQ(status(tile(0, 1.f, 4, 0, 0, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 1.f, 0, 16'777'216, -16'777'216, 1, 1, 0, 0, 0)), Status::Ok);
    EXPECT_EQ(status(tile(0, 1.f, 0, 16'777'217, 0, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, -16'777'217, 1, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, 0, 1024, 1024, 0, 0, 0)), Status::Ok);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, 0, 0, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, 0, 1, 0, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, 0, 1025, 1, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, 0, 1, 1025, 0, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, 0, 1, 1, 3, 0, 0)), Status::Ok);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, 0, 1, 1, 4, 0, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, 0, 1, 1, 0, 1023, 0)), Status::Ok);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, 0, 1, 1, 0, 1024, 0)), Status::InvalidArgument);
    EXPECT_EQ(status(tile(0, 1.f, 0, 0, 0, 1, 1, 0, 0, ~0ull)), Status::Ok);
    // wrong wire types
    Raw f64;
    f64.arr(10).u(0).raw({0xCB, 0x3F, 0xF8, 0, 0, 0, 0, 0, 0}).u(0).i(0).i(0).u(1).u(1).u(0).u(0).u(0);
    EXPECT_EQ(status(f64), Status::ProtocolError);
    Raw int_scale;
    int_scale.arr(10).u(0).u(2).u(0).i(0).i(0).u(1).u(1).u(0).u(0).u(0);
    EXPECT_EQ(status(int_scale), Status::ProtocolError);
    Raw wide_rotation;
    wide_rotation.arr(10).u(0).f(1.f).u(70000).i(0).i(0).u(1).u(1).u(0).u(0).u(0);
    EXPECT_EQ(status(wide_rotation), Status::ProtocolError);
    Raw wide_x;
    wide_x.arr(10).u(0).f(1.f).u(0).u(2147483648ull).i(0).u(1).u(1).u(0).u(0).u(0);
    EXPECT_EQ(status(wide_x), Status::ProtocolError);
    Raw negative_page;
    negative_page.arr(10).i(-1).f(1.f).u(0).i(0).i(0).u(1).u(1).u(0).u(0).u(0);
    EXPECT_EQ(status(negative_page), Status::ProtocolError);
    Raw nine_fields;
    nine_fields.arr(9).u(0).f(1.f).u(0).i(0).i(0).u(1).u(1).u(0).u(0);
    EXPECT_EQ(status(nine_fields), Status::ProtocolError);
    Raw eleven_fields;
    eleven_fields.arr(11).u(0).f(1.f).u(0).i(0).i(0).u(1).u(1).u(0).u(0).u(0).u(0);
    EXPECT_EQ(status(eleven_fields), Status::ProtocolError);
}

std::string two_byte_chars(int n) {
    std::string out;
    for (int i = 0; i < n; ++i) {
        out += "\xC3\xA9";
    }
    return out;
}

TEST(ProtocolLimits, OpenDocument) {
    const auto status = [](const Raw& r) { return request_status(request_frame(r, MessageType::OpenDocument)); };
    EXPECT_EQ(status(Raw().arr(2).u(1).nil()), Status::Ok);
    EXPECT_EQ(status(Raw().arr(2).u(0).nil()), Status::InvalidArgument);  // handle >= 1
    EXPECT_EQ(status(Raw().arr(2).u(~0ull).str("")), Status::Ok);
    EXPECT_EQ(status(Raw().arr(2).u(1).str(std::string(1024, 'a'))), Status::Ok);
    EXPECT_EQ(status(Raw().arr(2).u(1).str(std::string(1025, 'a'))), Status::InvalidArgument);
    EXPECT_EQ(status(Raw().arr(2).u(1).str(two_byte_chars(512))), Status::Ok);
    EXPECT_EQ(status(Raw().arr(2).u(1).str(two_byte_chars(513))), Status::InvalidArgument);
    EXPECT_EQ(status(Raw().arr(2).u(1).str(std::string("a\0b", 3))), Status::InvalidArgument);
    EXPECT_EQ(status(Raw().arr(2).u(1).str_bytes({0xFF, 0xFE})), Status::ProtocolError);
    EXPECT_EQ(status(Raw().arr(2).u(1).str_bytes({0xC0, 0x80})), Status::ProtocolError);
    EXPECT_EQ(status(Raw().arr(2).u(1).u(5)), Status::ProtocolError);
    EXPECT_EQ(status(Raw().arr(1).u(1)), Status::ProtocolError);
    EXPECT_EQ(status(Raw().nil()), Status::ProtocolError);
}

TEST(ProtocolLimits, HelloAndEnums) {
    const auto status = [](const Raw& r) { return request_status(request_frame(r, MessageType::Hello)); };
    EXPECT_EQ(status(Raw().arr(3).u(1).u(0).u(0)), Status::Ok);
    EXPECT_EQ(status(Raw().arr(3).u(65535).u(2).u(0xFFFFFFFFu)), Status::Ok);
    EXPECT_EQ(status(Raw().arr(3).u(1).u(3).u(0)), Status::Ok);                   // ProcessKind 3 is the shell handler since schema 5
    EXPECT_EQ(status(Raw().arr(3).u(0).u(0).u(0)), Status::InvalidArgument);      // version >= 1
    EXPECT_EQ(status(Raw().arr(3).u(65536).u(0).u(0)), Status::InvalidArgument);  // version <= 65535
    EXPECT_EQ(status(Raw().arr(3).u(1).u(4).u(0)), Status::InvalidArgument);      // ProcessKind 4 is undefined
    EXPECT_EQ(status(Raw().arr(3).u(1).u(65535).u(0)), Status::InvalidArgument);
    EXPECT_EQ(status(Raw().arr(3).u(1).u(65536).u(0)), Status::ProtocolError);    // does not fit the u16 an enum is
    EXPECT_EQ(status(Raw().arr(3).u(1).i(-1).u(0)), Status::ProtocolError);
    EXPECT_EQ(status(Raw().arr(3).u(1).u(0).u(0x100000000ull)), Status::ProtocolError);  // pid is u32
    const auto reply = [](const Raw& r) { return response_status(response_frame(&r, MessageType::Hello)); };
    EXPECT_EQ(reply(Raw().arr(2).u(1).str(std::string(64, 'v'))), Status::Ok);
    EXPECT_EQ(reply(Raw().arr(2).u(1).str(std::string(65, 'v'))), Status::InvalidArgument);
    EXPECT_EQ(reply(Raw().arr(2).u(1).nil()), Status::ProtocolError);  // not optional
}

TEST(ProtocolLimits, GetPageSizes) {
    const auto request = [](std::uint64_t first, std::uint64_t count) {
        return request_status(request_frame(Raw().arr(2).u(first).u(count), MessageType::GetPageSizes, 1, 1));
    };
    EXPECT_EQ(request(0, 1), Status::Ok);
    EXPECT_EQ(request(9'999'999, 5000), Status::Ok);
    EXPECT_EQ(request(0, 0), Status::InvalidArgument);
    EXPECT_EQ(request(0, 5001), Status::InvalidArgument);
    EXPECT_EQ(request(10'000'000, 1), Status::InvalidArgument);

    const auto reply = [](std::size_t widths, std::size_t heights, std::size_t rotations, float w = 100.f, std::uint16_t rot = 0) {
        Raw r;
        r.arr(3).arr(widths);
        for (std::size_t i = 0; i < widths; ++i) r.f(w);
        r.arr(heights);
        for (std::size_t i = 0; i < heights; ++i) r.f(100.f);
        r.arr(rotations);
        for (std::size_t i = 0; i < rotations; ++i) r.u(rot);
        return response_status(response_frame(&r, MessageType::GetPageSizes));
    };
    EXPECT_EQ(reply(0, 0, 0), Status::Ok);
    EXPECT_EQ(reply(5000, 5000, 5000), Status::Ok);
    EXPECT_EQ(reply(5001, 5001, 5001), Status::InvalidArgument);
    EXPECT_EQ(reply(2, 3, 2), Status::InvalidArgument);  // lengths differ
    EXPECT_EQ(reply(2, 2, 1), Status::InvalidArgument);
    EXPECT_EQ(reply(1, 1, 1, 14400.f), Status::Ok);
    EXPECT_EQ(reply(1, 1, 1, std::nextafter(14400.f, 20000.f)), Status::InvalidArgument);
    EXPECT_EQ(reply(1, 1, 1, std::numeric_limits<float>::min()), Status::Ok);  // smallest positive normal float
    EXPECT_EQ(reply(1, 1, 1, 0.f), Status::InvalidArgument);
    EXPECT_EQ(reply(1, 1, 1, -1.f), Status::InvalidArgument);
    EXPECT_EQ(reply(1, 1, 1, kNan), Status::InvalidArgument);
    EXPECT_EQ(reply(1, 1, 1, kInf), Status::InvalidArgument);
    EXPECT_EQ(reply(1, 1, 1, 100.f, 3), Status::Ok);
    EXPECT_EQ(reply(1, 1, 1, 100.f, 4), Status::InvalidArgument);

    // A count the message cannot hold is a protocol error, not an allocation.
    Raw lie;
    lie.arr(3).raw({0xDD, 0x00, 0x0F, 0x42, 0x40});
    EXPECT_EQ(response_status(response_frame(&lie, MessageType::GetPageSizes)), Status::ProtocolError);
    Raw huge;
    huge.arr(3).raw({0xDD, 0xFF, 0xFF, 0xFF, 0xFF});
    EXPECT_EQ(response_status(response_frame(&huge, MessageType::GetPageSizes)), Status::ProtocolError);
    // float64 elements.
    Raw f64;
    f64.arr(3).arr(1).raw({0xCB, 0x40, 0x59, 0, 0, 0, 0, 0, 0}).arr(1).f(1.f).arr(1).u(0);
    EXPECT_EQ(response_status(response_frame(&f64, MessageType::GetPageSizes)), Status::ProtocolError);
}

TEST(ProtocolLimits, ValidateStructureIssues) {
    const auto reply = [](std::size_t codes, std::size_t messages, std::size_t message_bytes, std::uint64_t code = 1) {
        Raw r;
        r.arr(4).b(false).b(false).arr(codes);
        for (std::size_t i = 0; i < codes; ++i) r.u(code);
        r.arr(messages);
        for (std::size_t i = 0; i < messages; ++i) r.str(std::string(message_bytes, 'm'));
        return response_status(response_frame(&r, MessageType::ValidateStructure));
    };
    EXPECT_EQ(reply(0, 0, 0), Status::Ok);
    EXPECT_EQ(reply(256, 256, 192), Status::Ok);
    EXPECT_EQ(reply(257, 257, 1), Status::InvalidArgument);
    EXPECT_EQ(reply(1, 1, 193), Status::InvalidArgument);
    EXPECT_EQ(reply(1, 1, 192), Status::Ok);
    EXPECT_EQ(reply(2, 1, 1), Status::InvalidArgument);
    EXPECT_EQ(reply(1, 2, 1), Status::InvalidArgument);
    EXPECT_EQ(reply(1, 1, 1, 65535), Status::Ok);
    EXPECT_EQ(reply(1, 1, 1, 65536), Status::InvalidArgument);
    Raw nil_message;
    nil_message.arr(4).b(false).b(false).arr(1).u(1).arr(1).nil();
    EXPECT_EQ(response_status(response_frame(&nil_message, MessageType::ValidateStructure)), Status::ProtocolError);
    Raw bad_utf8;
    bad_utf8.arr(4).b(false).b(false).arr(1).u(1).arr(1).str_bytes({0xFF});
    EXPECT_EQ(response_status(response_frame(&bad_utf8, MessageType::ValidateStructure)), Status::ProtocolError);
}

TEST(ProtocolLimits, OtherReplies) {
    const auto status = [](const Raw& r, MessageType t) { return response_status(response_frame(&r, t)); };
    EXPECT_EQ(status(Raw().arr(2).u(1).u(10'000'000), MessageType::OpenDocument), Status::Ok);
    EXPECT_EQ(status(Raw().arr(2).u(0).u(5), MessageType::OpenDocument), Status::InvalidArgument);
    EXPECT_EQ(status(Raw().arr(2).u(1).u(10'000'001), MessageType::OpenDocument), Status::InvalidArgument);
    EXPECT_EQ(status(Raw().arr(5).u(1023).u(1024).u(1024).u(8192).u(0), MessageType::RenderTile), Status::Ok);
    EXPECT_EQ(status(Raw().arr(5).u(1024).u(1).u(1).u(4).u(0), MessageType::RenderTile), Status::InvalidArgument);
    EXPECT_EQ(status(Raw().arr(5).u(0).u(1025).u(1).u(4).u(0), MessageType::RenderTile), Status::InvalidArgument);
    EXPECT_EQ(status(Raw().arr(5).u(0).u(1).u(0).u(4).u(0), MessageType::RenderTile), Status::InvalidArgument);
    EXPECT_EQ(status(Raw().arr(5).u(0).u(1).u(1).u(3).u(0), MessageType::RenderTile), Status::InvalidArgument);
    EXPECT_EQ(status(Raw().arr(5).u(0).u(1).u(1).u(8193).u(0), MessageType::RenderTile), Status::InvalidArgument);

    const auto info = [&status](std::size_t title_bytes, std::uint64_t version, std::uint64_t signatures) {
        Raw r;
        r.arr(13).u(1).u(version).str(std::string(title_bytes, 't')).str("").str("").str("").str("").str("").u(0xFFFFFFFFu).b(true).b(true).b(true).u(signatures);
        return status(r, MessageType::GetDocumentInfo);
    };
    EXPECT_EQ(info(1024, 99, 65535), Status::Ok);
    EXPECT_EQ(info(1025, 17, 0), Status::InvalidArgument);
    EXPECT_EQ(info(0, 100, 0), Status::InvalidArgument);
    EXPECT_EQ(info(0, 17, 65536), Status::InvalidArgument);
    Raw not_bool;
    not_bool.arr(13).u(1).u(17).str("").str("").str("").str("").str("").str("").u(0).u(1).b(true).b(true).u(0);
    EXPECT_EQ(status(not_bool, MessageType::GetDocumentInfo), Status::ProtocolError);
}

// ---- schema 5: print bands, viewer preferences, the shell handler ---------------------------------

namespace {
Raw band(std::uint64_t page, std::uint64_t dpi, std::uint64_t rotation, std::uint64_t width, std::uint64_t height, std::uint64_t top,
         std::uint64_t band_height, std::uint64_t flags, std::uint64_t slot) {
    Raw r;
    r.arr(10).u(page).u(dpi).u(rotation).u(width).u(height).u(top).u(band_height).u(flags).u(slot).u(1);
    return r;
}
Status band_status(const Raw& r, std::uint32_t document = 7) { return request_status(request_frame(r, MessageType::RenderBand, 22, document, 1000)); }
}  // namespace

TEST(ProtocolLimits, RenderBandRequest) {
    EXPECT_EQ(band_status(band(9999999, 1200, 3, 65536, 65536, 65535, 16384, 7, 1023)), Status::Ok);  // every limit at its edge
    EXPECT_EQ(band_status(band(0, 72, 0, 1, 1, 0, 1, 0, 0)), Status::Ok);
    EXPECT_EQ(band_status(band(10000000, 150, 0, 1275, 1650, 0, 256, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 71, 0, 1275, 1650, 0, 256, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 1201, 0, 1275, 1650, 0, 256, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 150, 4, 1275, 1650, 0, 256, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 150, 0, 0, 1650, 0, 256, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 150, 0, 65537, 1650, 0, 256, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 150, 0, 1275, 0, 0, 256, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 150, 0, 1275, 65537, 0, 256, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 150, 0, 1275, 1650, 65536, 256, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 150, 0, 1275, 1650, 0, 0, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 150, 0, 1275, 1650, 0, 16385, 5, 1)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 150, 0, 1275, 1650, 0, 256, 8, 1)), Status::InvalidArgument);  // the reserved flag bit
    EXPECT_EQ(band_status(band(0, 150, 0, 1275, 1650, 0, 256, 5, 1024)), Status::InvalidArgument);
    EXPECT_EQ(band_status(band(0, 150, 0, 1275, 1650, 0, 256, 5, 1), 0), Status::InvalidArgument);  // a band belongs to a document
    Raw dpi_float;
    dpi_float.arr(10).u(0).f(150.5f).u(0).u(1275).u(1650).u(0).u(256).u(5).u(1).u(1);
    EXPECT_EQ(band_status(dpi_float), Status::ProtocolError);
}

TEST(ProtocolLimits, RenderBandReplyAndViewerPreferencesReply) {
    const auto reply = [](const Raw& r, MessageType t) { return response_status(response_frame(&r, t)); };
    EXPECT_EQ(reply(Raw().arr(5).u(1023).u(65536).u(16384).u(262144).u(0), MessageType::RenderBand), Status::Ok);
    EXPECT_EQ(reply(Raw().arr(5).u(1024).u(1).u(1).u(4).u(0), MessageType::RenderBand), Status::InvalidArgument);
    EXPECT_EQ(reply(Raw().arr(5).u(0).u(0).u(1).u(4).u(0), MessageType::RenderBand), Status::InvalidArgument);
    EXPECT_EQ(reply(Raw().arr(5).u(0).u(65537).u(1).u(4).u(0), MessageType::RenderBand), Status::InvalidArgument);
    EXPECT_EQ(reply(Raw().arr(5).u(0).u(1).u(16385).u(4).u(0), MessageType::RenderBand), Status::InvalidArgument);
    EXPECT_EQ(reply(Raw().arr(5).u(0).u(1).u(1).u(3).u(0), MessageType::RenderBand), Status::InvalidArgument);
    EXPECT_EQ(reply(Raw().arr(5).u(0).u(1).u(1).u(262145).u(0), MessageType::RenderBand), Status::InvalidArgument);

    const auto prefs = [&reply](std::uint64_t direction, std::uint64_t scaling, std::uint64_t copies, std::uint64_t duplex, std::size_t firsts, std::size_t lasts,
                                std::uint64_t last_value = 0) {
        Raw r;
        r.arr(6).u(direction).u(scaling).u(copies).u(duplex).arr(firsts);
        for (std::size_t i = 0; i < firsts; ++i) r.u(0);
        r.arr(lasts);
        for (std::size_t i = 0; i < lasts; ++i) r.u(last_value);
        return reply(r, MessageType::GetViewerPreferences);
    };
    EXPECT_EQ(prefs(2, 2, 99, 3, 64, 64, 9999999), Status::Ok);
    EXPECT_EQ(prefs(3, 0, 0, 0, 0, 0), Status::InvalidArgument);
    EXPECT_EQ(prefs(0, 3, 0, 0, 0, 0), Status::InvalidArgument);
    EXPECT_EQ(prefs(0, 0, 100, 0, 0, 0), Status::InvalidArgument);
    EXPECT_EQ(prefs(0, 0, 0, 4, 0, 0), Status::InvalidArgument);
    EXPECT_EQ(prefs(0, 0, 0, 0, 65, 65), Status::InvalidArgument);
    EXPECT_EQ(prefs(0, 0, 0, 0, 2, 1), Status::InvalidArgument);          // the two arrays differ in length
    EXPECT_EQ(prefs(0, 0, 0, 0, 1, 1, 10000000), Status::InvalidArgument);  // a page past the schema's last
}

TEST(ProtocolClient, AShellHandlerSpeaksTheShellMessagesWithTheClientHalfOfTheCodec) {
    // The handler DLL is a client: it encodes requests and decodes replies, the opposite of the engine. One session as the handler would run it.
    Hello hello;
    hello.schema_version = kSchemaVersion;
    hello.process_kind = ProcessKind::ShellHandler;
    hello.pid = 77;
    auto sent = encode_request_frame(1, 0, 2000, hello);
    ASSERT_TRUE(sent.ok());
    const auto host_sees = decode_request_frame(sent.value);  // what ShellHost reads
    ASSERT_TRUE(host_sees.ok());
    EXPECT_EQ(std::get<Hello>(host_sees.value.payload).process_kind, ProcessKind::ShellHandler);

    // After Hello the session is the connection: ShellOpen has no document, and what it answers with is the documentId of everything after it.
    const auto exchange = [](const RequestPayload& request, std::uint32_t document, MessageType type, const ReplyPayload& reply) {
        auto out = encode_request_frame(7, document, 1500, request);  // what the handler sends
        ASSERT_TRUE(out.ok()) << out.detail;
        const auto host_reads = decode_request_frame(out.value);      // what ShellHost reads
        ASSERT_TRUE(host_reads.ok()) << host_reads.detail;
        EXPECT_EQ(host_reads.value.document_id, document);
        EXPECT_TRUE(host_reads.value.payload == request);
        auto in = encode_response_frame(7, Status::Ok, type, &reply);  // what ShellHost answers
        ASSERT_TRUE(in.ok()) << in.detail;
        const auto decoded = decode_response_frame(in.value);          // what the handler reads
        ASSERT_TRUE(decoded.ok()) << decoded.detail;
        EXPECT_EQ(decoded.value.message_type, type);
        EXPECT_EQ(decoded.value.request_id, 7u);
        EXPECT_TRUE(decoded.value.payload == reply);
    };
    ShellOpen open;
    open.section = 5;  // a handle value valid in the handler: ShellHost duplicates it from this process
    open.bytes = 1234;
    ShellOpenReply opened;
    opened.document_id = 3;
    opened.page_count = 9;
    opened.first_width = 612.0f;
    opened.first_height = 792.0f;
    exchange(open, 0, MessageType::ShellOpen, opened);
    ShellRenderPage render;
    render.max_width = 256;
    render.max_height = 256;
    render.pixels = 9;
    render.pixel_bytes = 262144;
    ShellRenderPageReply rendered;
    rendered.width = 198;
    rendered.height = 256;
    rendered.stride = 792;
    exchange(render, opened.document_id, MessageType::ShellRenderPage, rendered);
    exchange(ShellClose{}, opened.document_id, MessageType::ShellClose, ShellCloseReply{});

    // The client refuses to send what the host would refuse: a picture the tile limit does not allow, a flag that does not exist, a render outside a session,
    // a session id on the open.
    render.max_width = 1025;
    EXPECT_EQ(encode_request_frame(8, 3, 0, render).status, Status::InvalidArgument);
    render.max_width = 256;
    render.flags = 2;
    EXPECT_EQ(encode_request_frame(8, 3, 0, render).status, Status::InvalidArgument);
    render.flags = 0;
    EXPECT_EQ(encode_request_frame(8, 0, 0, render).status, Status::InvalidArgument);
    EXPECT_EQ(encode_request_frame(8, 3, 0, open).status, Status::InvalidArgument);
    EXPECT_EQ(encode_request_frame(8, 0, 0, ShellClose{}).status, Status::InvalidArgument);
    // And it refuses a reply that lies: a stride that is not a row of pixels.
    rendered.stride = 3;
    const ReplyPayload lie = rendered;
    EXPECT_EQ(encode_response_frame(7, Status::Ok, MessageType::ShellRenderPage, &lie).status, Status::InvalidArgument);
}

TEST(ProtocolPong, CarriesTheProgressCounterAndOnlyAsAnUnsignedInteger) {
    Pong pong;
    pong.nonce = 9;
    pong.progress = ~0ull;  // wraps; compared for change only
    const ReplyPayload payload = pong;
    auto frame = encode_response_frame(1, Status::Ok, MessageType::Ping, &payload);
    ASSERT_TRUE(frame.ok());
    const auto decoded = decode_response_frame(frame.value);
    ASSERT_TRUE(decoded.ok());
    EXPECT_EQ(std::get<Pong>(*decoded.value.payload).progress, ~0ull);
    // The schema 4 layout, one field, is no longer a Pong.
    Raw old_layout;
    old_layout.arr(1).u(9);
    EXPECT_EQ(response_status(response_frame(&old_layout, MessageType::Ping)), Status::ProtocolError);
}

// ---- golden vectors shared with the C# tests ---------------------------------------------------

Bytes read_file(const std::string& name) {
    std::ifstream in(std::string(MIKEPDF_GOLDEN_DIR) + "/" + name, std::ios::binary);
    EXPECT_TRUE(in.good()) << name;
    return Bytes(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
}

struct GoldenEntry {
    std::string file;
    bool request;
    Status expect;
};

std::vector<GoldenEntry> manifest() {
    std::ifstream in(std::string(MIKEPDF_GOLDEN_DIR) + "/manifest.txt");
    EXPECT_TRUE(in.good());
    std::vector<GoldenEntry> entries;
    std::string line;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        if (line.empty()) {
            continue;
        }
        std::istringstream fields(line);
        std::string file, kind, expect;
        fields >> file >> kind >> expect;
        Status status = Status::Ok;
        if (expect == "ProtocolError") {
            status = Status::ProtocolError;
        } else if (expect == "InvalidArgument") {
            status = Status::InvalidArgument;
        } else {
            EXPECT_EQ(expect, "Ok") << line;
        }
        entries.push_back({file, kind == "request", status});
    }
    return entries;
}

TEST(ProtocolGolden, ManifestListsEverySampleAndNothingElse) {
    std::map<std::string, bool> expected_valid;
    for (const auto& s : request_samples()) expected_valid[s.name + ".bin"] = true;
    for (const auto& s : response_samples()) expected_valid[s.name + ".bin"] = true;
    std::size_t valid = 0;
    std::size_t invalid = 0;
    for (const auto& e : manifest()) {
        if (e.expect == Status::Ok) {
            ++valid;
            EXPECT_TRUE(expected_valid.count(e.file)) << "no C++ sample for " << e.file;
        } else {
            ++invalid;
        }
    }
    EXPECT_EQ(valid, expected_valid.size());
    EXPECT_GE(invalid, 40u);
}

TEST(ProtocolGolden, ValidFramesDecodeToTheExpectedValuesAndReEncodeToIdenticalBytes) {
    std::map<std::string, RequestSample> requests;
    for (auto& s : request_samples()) requests.emplace(s.name + ".bin", s);
    std::map<std::string, ResponseSample> responses;
    for (auto& s : response_samples()) responses.emplace(s.name + ".bin", s);

    for (const auto& e : manifest()) {
        if (e.expect != Status::Ok) {
            continue;
        }
        const Bytes golden = read_file(e.file);
        if (e.request) {
            const auto& want = requests.at(e.file);
            auto got = decode_request_frame(golden);
            ASSERT_TRUE(got.ok()) << e.file << ": " << got.detail;
            EXPECT_EQ(got.value.request_id, want.id) << e.file;
            EXPECT_EQ(got.value.document_id, want.document) << e.file;
            EXPECT_EQ(got.value.deadline_ms, want.deadline) << e.file;
            EXPECT_TRUE(got.value.payload == want.payload) << e.file;
            EXPECT_EQ(encode_request_frame(got.value.request_id, got.value.document_id, got.value.deadline_ms, got.value.payload).value, golden) << e.file;
        } else {
            const auto& want = responses.at(e.file);
            auto got = decode_response_frame(golden);
            ASSERT_TRUE(got.ok()) << e.file << ": " << got.detail;
            EXPECT_EQ(got.value.request_id, want.id) << e.file;
            EXPECT_EQ(got.value.status, want.status) << e.file;
            EXPECT_EQ(got.value.message_type, want.type) << e.file;
            EXPECT_TRUE(got.value.payload == want.payload) << e.file;
            const auto& p = got.value.payload;
            EXPECT_EQ(encode_response_frame(got.value.request_id, got.value.status, got.value.message_type, p ? &*p : nullptr).value, golden) << e.file;
        }
    }
}

TEST(ProtocolGolden, MalformedFramesAreRejectedWithTheSameStatusAsInCSharp) {
    for (const auto& e : manifest()) {
        if (e.expect == Status::Ok) {
            continue;
        }
        const Bytes golden = read_file(e.file);
        Status got = Status::Ok;
        const char* detail = "";
        if (e.request) {
            const auto r = decode_request_frame(golden);
            got = r.status;
            detail = r.detail;
        } else {
            const auto r = decode_response_frame(golden);
            got = r.status;
            detail = r.detail;
        }
        EXPECT_EQ(got, e.expect) << e.file << ": " << detail;
        EXPECT_STRNE(detail, "") << e.file;
    }
}

// ---- randomised input --------------------------------------------------------------------------

TEST(ProtocolRandom, RandomAndMutatedFramesAreHandledSafelyAndSurvivorsAreStable) {
    std::vector<Bytes> seeds = all_sample_frames();
    for (const auto& e : manifest()) {
        if (e.expect != Status::Ok) {
            seeds.push_back(read_file(e.file));
        }
    }
    constexpr std::array<std::uint8_t, 32> interesting = {0x00, 0x01, 0x7F, 0x80, 0x90, 0x91, 0x9F, 0xA0, 0xBF, 0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC7, 0xCA,
                                                          0xCB, 0xCC, 0xCD, 0xCE, 0xCF, 0xD0, 0xD3, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF, 0xE0, 0xFF};
    std::mt19937_64 rng(0x510E);
    const auto below = [&](std::size_t n) { return static_cast<std::size_t>(rng() % n); };

    std::size_t requests_accepted = 0;
    std::size_t responses_accepted = 0;
    for (int iteration = 0; iteration < 100'000; ++iteration) {
        Bytes frame;
        if (iteration % 4 == 0) {
            frame.resize(below(300));
            for (auto& b : frame) b = below(3) == 0 ? interesting[below(interesting.size())] : static_cast<std::uint8_t>(rng());
        } else {
            frame = seeds[below(seeds.size())];
            const std::size_t edits = 1 + below(4);
            for (std::size_t k = 0; k < edits && !frame.empty(); ++k) {
                const std::size_t at = below(frame.size());
                switch (below(7)) {
                    case 0: frame[at] = static_cast<std::uint8_t>(rng()); break;
                    case 1: frame[at] = interesting[below(interesting.size())]; break;
                    case 2: frame[at] ^= static_cast<std::uint8_t>(1u << below(8)); break;
                    case 3: frame.erase(frame.begin() + static_cast<std::ptrdiff_t>(at)); break;
                    case 4: frame.insert(frame.begin() + static_cast<std::ptrdiff_t>(at), interesting[below(interesting.size())]); break;
                    case 5: frame.erase(frame.begin() + static_cast<std::ptrdiff_t>(at),
                                        frame.begin() + static_cast<std::ptrdiff_t>(std::min(frame.size(), at + 1 + below(15)))); break;
                    default: {
                        const std::size_t from = below(frame.size());
                        const std::size_t len = std::min<std::size_t>(frame.size() - from, 1 + below(11));
                        const Bytes piece(frame.begin() + static_cast<std::ptrdiff_t>(from), frame.begin() + static_cast<std::ptrdiff_t>(from + len));
                        frame.insert(frame.begin() + static_cast<std::ptrdiff_t>(at), piece.begin(), piece.end());
                    }
                }
            }
        }
        if (frame.size() >= 4 && below(2) == 0) {
            const auto n = static_cast<std::uint32_t>(frame.size() - 4);
            frame[0] = static_cast<std::uint8_t>(n);
            frame[1] = static_cast<std::uint8_t>(n >> 8);
            frame[2] = static_cast<std::uint8_t>(n >> 16);
            frame[3] = static_cast<std::uint8_t>(n >> 24);
        }

        const auto request = decode_request_frame(frame);
        if (request.ok()) {
            ++requests_accepted;
            const auto& e = request.value;
            const auto again = encode_request_frame(e.request_id, e.document_id, e.deadline_ms, e.payload);
            ASSERT_TRUE(again.ok()) << again.detail;
            const auto e2 = decode_request_frame(again.value);
            ASSERT_TRUE(e2.ok());
            ASSERT_EQ(encode_request_frame(e2.value.request_id, e2.value.document_id, e2.value.deadline_ms, e2.value.payload).value, again.value);
        } else {
            ASSERT_TRUE(request.status == Status::ProtocolError || request.status == Status::InvalidArgument);
        }

        const auto response = decode_response_frame(frame);
        if (response.ok()) {
            ++responses_accepted;
            const auto& e = response.value;
            const auto again = encode_response_frame(e.request_id, e.status, e.message_type, e.payload ? &*e.payload : nullptr);
            ASSERT_TRUE(again.ok()) << again.detail;
            const auto e2 = decode_response_frame(again.value);
            ASSERT_TRUE(e2.ok());
            ASSERT_EQ(encode_response_frame(e2.value.request_id, e2.value.status, e2.value.message_type, e2.value.payload ? &*e2.value.payload : nullptr).value,
                      again.value);
        } else {
            ASSERT_TRUE(response.status == Status::ProtocolError || response.status == Status::InvalidArgument);
        }
    }
    EXPECT_GT(requests_accepted, 500u);
    EXPECT_GT(responses_accepted, 500u);
}

}  // namespace
