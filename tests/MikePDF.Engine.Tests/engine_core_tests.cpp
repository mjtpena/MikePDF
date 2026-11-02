// Unit tests for the engine's request handling (EngineCore), the command line and the mapped-file wrapper.
// No pipe and no second thread: these drive execute() directly, as the engine thread would.
#include <gtest/gtest.h>

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "command_line.h"
#include "engine_core.h"
#include "mapped_file.h"
#include "mikepdf/pdfium/library.h"
#include "test_support.h"

namespace {

using namespace mikepdf;
using engine::Clock;
using engine::EngineCore;
using engine::EngineKind;
using engine::SlotMemory;
using protocol::MessageType;
using protocol::RequestEnvelope;
using protocol::Status;
using support::build_pdf;
using support::open_handle;

constexpr std::size_t kSlotSize = 64 * 1024;
constexpr std::uint32_t kSlotCount = 4;
constexpr auto kNoDeadline = Clock::time_point::max();

RequestEnvelope make(MessageType type, std::uint32_t document_id, protocol::RequestPayload payload) {
    RequestEnvelope request;
    request.request_id = 1;
    request.document_id = document_id;
    request.message_type = type;
    request.payload = std::move(payload);
    return request;
}

class EngineCoreTest : public ::testing::Test {
protected:
    EngineCoreTest() : slots_memory_(kSlotSize * kSlotCount, 0xCD), core_(EngineKind::Document, SlotMemory{slots_memory_.data(), kSlotCount, kSlotSize}) {}

    ~EngineCoreTest() override { core_.close_all(); }

    protocol::OpenDocumentReply open(const std::string& fixture, std::optional<std::string> password = std::nullopt) {
        protocol::OpenDocument payload;
        payload.handle = open_handle(support::fixture_path(fixture));
        payload.password = std::move(password);
        auto request = make(MessageType::OpenDocument, 0, payload);
        const auto reply = core_.execute(request, kNoDeadline);
        EXPECT_EQ(reply.status, Status::Ok) << fixture;
        return reply.payload ? std::get<protocol::OpenDocumentReply>(*reply.payload) : protocol::OpenDocumentReply{};
    }

    Status status_of(MessageType type, std::uint32_t document_id, protocol::RequestPayload payload, Clock::time_point deadline = kNoDeadline) {
        auto request = make(type, document_id, std::move(payload));
        return core_.execute(request, deadline).status;
    }

    protocol::RenderTile tile(std::uint32_t width, std::uint32_t height) const {
        protocol::RenderTile render;
        render.scale = 1.0f;
        render.tile_width = width;
        render.tile_height = height;
        render.generation = 10;
        return render;
    }

    pdfium::Library library_;
    std::vector<std::uint8_t> slots_memory_;
    EngineCore core_;
};

TEST_F(EngineCoreTest, OpensInfoPageSizesAndCloses) {
    const auto opened = open("hello_world_2_pages.pdf");
    EXPECT_EQ(opened.document_id, 1u);
    EXPECT_EQ(opened.page_count, 2u);
    EXPECT_EQ(core_.open_document_count(), 1u);

    auto info_request = make(MessageType::GetDocumentInfo, opened.document_id, protocol::GetDocumentInfo{});
    const auto info = core_.execute(info_request, kNoDeadline);
    ASSERT_EQ(info.status, Status::Ok);
    EXPECT_EQ(std::get<protocol::GetDocumentInfoReply>(*info.payload).page_count, 2u);

    protocol::GetPageSizes sizes;
    sizes.first = 0;
    sizes.count = 2;
    auto sizes_request = make(MessageType::GetPageSizes, opened.document_id, sizes);
    const auto sized = core_.execute(sizes_request, kNoDeadline);
    ASSERT_EQ(sized.status, Status::Ok);
    EXPECT_EQ(std::get<protocol::GetPageSizesReply>(*sized.payload).widths.size(), 2u);

    EXPECT_EQ(status_of(MessageType::CloseDocument, opened.document_id, protocol::CloseDocument{}), Status::Ok);
    EXPECT_EQ(core_.open_document_count(), 0u);
    EXPECT_EQ(status_of(MessageType::GetDocumentInfo, opened.document_id, protocol::GetDocumentInfo{}), Status::InvalidArgument);
    EXPECT_EQ(status_of(MessageType::CloseDocument, opened.document_id, protocol::CloseDocument{}), Status::InvalidArgument);
}

TEST_F(EngineCoreTest, RendersIntoTheRequestedSlotOnly) {
    const auto opened = open("rectangles.pdf");
    auto render = tile(64, 64);
    render.slot_index = 2;
    auto request = make(MessageType::RenderTile, opened.document_id, render);
    const auto reply = core_.execute(request, kNoDeadline);
    ASSERT_EQ(reply.status, Status::Ok);
    const auto& tile_reply = std::get<protocol::RenderTileReply>(*reply.payload);
    EXPECT_EQ(tile_reply.slot_index, 2u);
    EXPECT_EQ(tile_reply.width, 64u);
    EXPECT_EQ(tile_reply.height, 64u);
    EXPECT_EQ(tile_reply.stride, 256u);
    EXPECT_EQ(tile_reply.generation, 10u);

    // Top-left 64 x 64 of the page: the black corner square, then paper.
    const auto black = slots_memory_.begin() + 2 * kSlotSize;
    EXPECT_EQ(black[5 * 256 + 5 * 4 + 3], 255);
    EXPECT_EQ(black[5 * 256 + 5 * 4 + 0], 0);
    EXPECT_EQ(black[40 * 256 + 40 * 4 + 0], 255);
    // Nothing outside the tile's rows was touched, and no other slot.
    EXPECT_EQ(slots_memory_[2 * kSlotSize + 64 * 256], 0xCD);
    EXPECT_EQ(slots_memory_[0], 0xCD);
    EXPECT_EQ(slots_memory_[3 * kSlotSize], 0xCD);
}

TEST_F(EngineCoreTest, RefusesSlotsAndTilesThatDoNotFit) {
    const auto opened = open("rectangles.pdf");
    auto outside = tile(16, 16);
    outside.slot_index = kSlotCount;
    EXPECT_EQ(status_of(MessageType::RenderTile, opened.document_id, outside), Status::InvalidArgument);

    auto too_big = tile(200, 100);  // 200 * 4 * 100 = 80000 > 65536
    EXPECT_EQ(status_of(MessageType::RenderTile, opened.document_id, too_big), Status::InvalidArgument);

    auto exact = tile(128, 128);  // 128 * 4 * 128 = 65536: exactly one slot
    exact.slot_index = 3;
    EXPECT_EQ(status_of(MessageType::RenderTile, opened.document_id, exact), Status::Ok);

    auto bad_page = tile(16, 16);
    bad_page.page = 7;
    EXPECT_EQ(status_of(MessageType::RenderTile, opened.document_id, bad_page), Status::InvalidArgument);
}

TEST_F(EngineCoreTest, CancelBeforeCancelsOlderGenerationsOfThatDocumentOnly) {
    const auto opened = open("rectangles.pdf");
    EXPECT_EQ(core_.cancel_before(opened.document_id, 11).status, Status::Ok);
    EXPECT_EQ(status_of(MessageType::RenderTile, opened.document_id, tile(16, 16)), Status::Cancelled);  // generation 10 < 11
    auto fresh = tile(16, 16);
    fresh.generation = 11;
    EXPECT_EQ(status_of(MessageType::RenderTile, opened.document_id, fresh), Status::Ok);

    // The floor only goes up.
    EXPECT_EQ(core_.cancel_before(opened.document_id, 5).status, Status::Ok);
    EXPECT_EQ(status_of(MessageType::RenderTile, opened.document_id, tile(16, 16)), Status::Cancelled);
    EXPECT_EQ(core_.cancel_before(opened.document_id + 1, 1).status, Status::InvalidArgument);
}

TEST_F(EngineCoreTest, ADeadlineThatHasPassedIsATimeoutAndStillConsumesTheHandle) {
    const auto opened = open("rectangles.pdf");
    const auto past = Clock::now() - std::chrono::milliseconds(1);
    EXPECT_EQ(status_of(MessageType::RenderTile, opened.document_id, tile(16, 16), past), Status::Timeout);
    EXPECT_EQ(status_of(MessageType::GetDocumentInfo, opened.document_id, protocol::GetDocumentInfo{}, past), Status::Timeout);

    const support::TempCopy copy("hello_world.pdf");
    protocol::OpenDocument second;
    second.handle = open_handle(copy.path);
    ASSERT_FALSE(support::no_handles_left(copy.path));
    EXPECT_EQ(status_of(MessageType::OpenDocument, 0, second, past), Status::Timeout);
    EXPECT_TRUE(support::no_handles_left(copy.path)) << "the engine must close a handle it refuses";
}

TEST_F(EngineCoreTest, ADocumentEngineHoldsOneDocumentAndClosesTheSurplusHandle) {
    (void)open("hello_world.pdf");
    const support::TempCopy copy("hello_world_2_pages.pdf");
    protocol::OpenDocument second;
    second.handle = open_handle(copy.path);
    EXPECT_EQ(status_of(MessageType::OpenDocument, 0, second), Status::Unsupported);
    EXPECT_TRUE(support::no_handles_left(copy.path));
}

TEST_F(EngineCoreTest, APoolEngineHoldsSeveralDocuments) {
    EngineCore pool(EngineKind::Pool, SlotMemory{slots_memory_.data(), kSlotCount, kSlotSize});
    for (int i = 0; i < 3; ++i) {
        protocol::OpenDocument payload;
        payload.handle = open_handle(support::fixture_path("hello_world.pdf"));
        auto request = make(MessageType::OpenDocument, 0, payload);
        const auto reply = pool.execute(request, kNoDeadline);
        ASSERT_EQ(reply.status, Status::Ok);
        EXPECT_EQ(std::get<protocol::OpenDocumentReply>(*reply.payload).document_id, static_cast<std::uint32_t>(i + 1));
    }
    EXPECT_EQ(pool.open_document_count(), 3u);
    pool.close_all();
    EXPECT_EQ(pool.open_document_count(), 0u);
}

// ---- the Shell kind (ADR-0018): one document, an allow-list, everything else Unsupported ----------------

class ShellKindTest : public ::testing::Test {
protected:
    ShellKindTest() : slots_memory_(kSlotSize * kSlotCount, 0xCD), core_(EngineKind::Shell, SlotMemory{slots_memory_.data(), kSlotCount, kSlotSize}) {}

    ~ShellKindTest() override { core_.close_all(); }

    Status status_of(MessageType type, std::uint32_t document_id, protocol::RequestPayload payload) {
        auto request = make(type, document_id, std::move(payload));
        return core_.execute(request, kNoDeadline).status;
    }

    std::uint32_t open(const std::string& fixture) {
        protocol::OpenDocument payload;
        payload.handle = open_handle(support::fixture_path(fixture));
        auto request = make(MessageType::OpenDocument, 0, payload);
        const auto reply = core_.execute(request, kNoDeadline);
        EXPECT_EQ(reply.status, Status::Ok) << fixture;
        return reply.payload ? std::get<protocol::OpenDocumentReply>(*reply.payload).document_id : 0;
    }

    pdfium::Library library_;
    std::vector<std::uint8_t> slots_memory_;
    EngineCore core_;
};

TEST_F(ShellKindTest, HoldsOneDocumentAndClosesTheSurplusHandle) {
    EXPECT_EQ(open("hello_world.pdf"), 1u);
    const support::TempCopy copy("hello_world_2_pages.pdf");
    protocol::OpenDocument second;
    second.handle = open_handle(copy.path);
    ASSERT_FALSE(support::no_handles_left(copy.path));
    EXPECT_EQ(status_of(MessageType::OpenDocument, 0, second), Status::Unsupported);
    EXPECT_TRUE(support::no_handles_left(copy.path)) << "the engine must close a handle it refuses";
    EXPECT_EQ(core_.open_document_count(), 1u);
}

TEST_F(ShellKindTest, ServesOpenInfoPageSizesOneTileAndClose) {
    const std::uint32_t id = open("rectangles.pdf");
    ASSERT_EQ(id, 1u);
    EXPECT_EQ(status_of(MessageType::GetDocumentInfo, id, protocol::GetDocumentInfo{}), Status::Ok);
    protocol::GetPageSizes sizes;
    sizes.first = 0;
    sizes.count = 1;
    EXPECT_EQ(status_of(MessageType::GetPageSizes, id, sizes), Status::Ok);
    protocol::RenderTile render;
    render.scale = 0.1f;
    render.tile_width = 16;
    render.tile_height = 16;
    render.generation = 1;
    EXPECT_EQ(status_of(MessageType::RenderTile, id, render), Status::Ok);
    EXPECT_EQ(core_.cancel_before(id, 5).status, Status::Ok);
    EXPECT_EQ(status_of(MessageType::CloseDocument, id, protocol::CloseDocument{}), Status::Ok);
    EXPECT_EQ(core_.open_document_count(), 0u);
}

TEST_F(ShellKindTest, AnswersUnsupportedToEverythingOffTheAllowList) {
    const std::uint32_t id = open("hello_world.pdf");
    ASSERT_EQ(id, 1u);
    EXPECT_EQ(status_of(MessageType::GetOutline, id, protocol::GetOutline{}), Status::Unsupported);
    EXPECT_EQ(status_of(MessageType::GetPageLinks, id, protocol::GetPageLinks{}), Status::Unsupported);
    EXPECT_EQ(status_of(MessageType::GetLinkUri, id, protocol::GetLinkUri{}), Status::Unsupported);
    EXPECT_EQ(status_of(MessageType::GetPageBoxes, id, protocol::GetPageBoxes{}), Status::Unsupported);
    EXPECT_EQ(status_of(MessageType::GetPageText, id, protocol::GetPageText{}), Status::Unsupported);
    EXPECT_EQ(status_of(MessageType::SearchPage, id, protocol::SearchPage{}), Status::Unsupported);
    EXPECT_EQ(status_of(MessageType::GetDocumentDetails, id, protocol::GetDocumentDetails{}), Status::Unsupported);
    EXPECT_EQ(status_of(MessageType::GetViewerPreferences, id, protocol::GetViewerPreferences{}), Status::Unsupported);
    EXPECT_EQ(status_of(MessageType::RenderBand, id, protocol::RenderBand{}), Status::Unsupported);
    EXPECT_EQ(core_.open_document_count(), 1u) << "refusing a message must not disturb the document";
}

TEST(ShellKindAllowList, IsExactlyTheTenMessagesOfTheSpec) {
    using engine::is_served_by_shell_kind;
    const std::vector<MessageType> allowed = {MessageType::Hello,        MessageType::AttachSlotRing, MessageType::Ping,         MessageType::Shutdown,
                                              MessageType::OpenDocument, MessageType::CloseDocument,  MessageType::GetDocumentInfo, MessageType::GetPageSizes,
                                              MessageType::RenderTile,   MessageType::CancelBefore};
    for (const MessageType type : allowed) {
        EXPECT_TRUE(is_served_by_shell_kind(type)) << static_cast<int>(type);
    }
    int served = 0;
    for (int value = 0; value < 4096; ++value) {
        served += is_served_by_shell_kind(static_cast<MessageType>(value)) ? 1 : 0;
    }
    EXPECT_EQ(served, static_cast<int>(allowed.size())) << "a message type outside the list is served";
    // The handler-pipe messages belong to ShellHost, never to an engine.
    EXPECT_FALSE(is_served_by_shell_kind(MessageType::ShellOpen));
    EXPECT_FALSE(is_served_by_shell_kind(MessageType::ShellRenderPage));
    EXPECT_FALSE(is_served_by_shell_kind(MessageType::ShellClose));
}

TEST_F(EngineCoreTest, PasswordsAndDamageAreTypedAndTheHandleIsAlwaysConsumed) {
    const support::TempCopy copy("encrypted.pdf");
    protocol::OpenDocument locked;
    locked.handle = open_handle(copy.path);
    EXPECT_EQ(status_of(MessageType::OpenDocument, 0, locked), Status::PasswordRequired);
    EXPECT_TRUE(support::no_handles_left(copy.path));

    locked.handle = open_handle(copy.path);
    locked.password = "wrong";
    EXPECT_EQ(status_of(MessageType::OpenDocument, 0, locked), Status::PasswordIncorrect);
    EXPECT_TRUE(support::no_handles_left(copy.path));
    EXPECT_EQ(core_.open_document_count(), 0u);

    const auto broken = std::filesystem::temp_directory_path() / "mikepdf-engine-core-damaged.pdf";
    {
        std::ofstream out(broken, std::ios::binary);
        out << "%PDF-1.7\nthis is not a pdf";
    }
    protocol::OpenDocument damaged;
    damaged.handle = open_handle(broken);
    EXPECT_EQ(status_of(MessageType::OpenDocument, 0, damaged), Status::Damaged);
    EXPECT_TRUE(support::no_handles_left(broken));

    const auto empty = std::filesystem::temp_directory_path() / "mikepdf-engine-core-empty.pdf";
    { std::ofstream out(empty, std::ios::binary); }
    damaged.handle = open_handle(empty);
    EXPECT_EQ(status_of(MessageType::OpenDocument, 0, damaged), Status::Damaged);
    std::filesystem::remove(broken);
    std::filesystem::remove(empty);

    // The failed attempts left the engine usable.
    EXPECT_EQ(open("hello_world.pdf").document_id, 1u);
}

TEST_F(EngineCoreTest, BadHandlesAreInvalidArgumentsNotCrashes) {
    protocol::OpenDocument bogus;
    bogus.handle = 0xDEADBEEF;
    EXPECT_EQ(status_of(MessageType::OpenDocument, 0, bogus), Status::InvalidArgument);
    bogus.handle = static_cast<std::uint64_t>(-1);
    EXPECT_EQ(status_of(MessageType::OpenDocument, 0, bogus), Status::InvalidArgument);
    // A handle to something that is not a disk file (the process's own event) is refused.
    const HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    bogus.handle = static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(event));
    EXPECT_EQ(status_of(MessageType::OpenDocument, 0, bogus), Status::InvalidArgument);
}

TEST_F(EngineCoreTest, MessagesTheIoLaneOwnsAreNotExecutedHere) {
    EXPECT_EQ(status_of(MessageType::Ping, 0, protocol::Ping{}), Status::InvalidArgument);
    EXPECT_EQ(status_of(MessageType::Shutdown, 0, protocol::Shutdown{}), Status::InvalidArgument);
}

TEST_F(EngineCoreTest, AHeavyPageHonoursTheDeadlineInsidePdfium) {
    const auto path = std::filesystem::temp_directory_path() / "mikepdf-engine-core-heavy.pdf";
    {
        const std::string pdf = build_pdf(support::many_rectangles(60000));
        std::ofstream out(path, std::ios::binary);
        out << pdf;
    }
    protocol::OpenDocument payload;
    payload.handle = open_handle(path);
    auto open_request = make(MessageType::OpenDocument, 0, payload);
    const auto opened = core_.execute(open_request, kNoDeadline);
    ASSERT_EQ(opened.status, Status::Ok);
    const auto id = std::get<protocol::OpenDocumentReply>(*opened.payload).document_id;

    // A 1 ms deadline interrupts the same render from the pause callback.
    auto render = tile(128, 128);
    auto request = make(MessageType::RenderTile, id, render);
    EXPECT_EQ(core_.execute(request, Clock::now() + std::chrono::milliseconds(1)).status, Status::Timeout);
    std::filesystem::remove(path);
}

// ---- AttachSlotRing: the ring arrives as a duplicated handle, announced after Hello -------------

// A pagefile-backed section like the one the broker creates, as a handle value for AttachSlotRing (the engine side closes it).
std::uint64_t make_section(std::uint64_t bytes) {
    const HANDLE section = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, static_cast<DWORD>(bytes >> 32), static_cast<DWORD>(bytes), nullptr);
    return static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(section));
}

protocol::AttachSlotRing attach(std::uint64_t handle, std::uint32_t count, std::uint32_t size) {
    protocol::AttachSlotRing request;
    request.handle = handle;
    request.slot_count = count;
    request.slot_size = size;
    return request;
}

TEST_F(EngineCoreTest, RenderTileIsRefusedUntilTheRingIsAttached) {
    EngineCore unattached(EngineKind::Document);
    const auto opened = [&] {
        protocol::OpenDocument payload;
        payload.handle = open_handle(support::fixture_path("rectangles.pdf"));
        auto request = make(MessageType::OpenDocument, 0, payload);
        const auto reply = unattached.execute(request, kNoDeadline);
        EXPECT_EQ(reply.status, Status::Ok);
        return std::get<protocol::OpenDocumentReply>(*reply.payload).document_id;
    }();
    auto render = tile(16, 16);
    auto request = make(MessageType::RenderTile, opened, render);
    EXPECT_EQ(unattached.execute(request, kNoDeadline).status, Status::InvalidArgument);

    EXPECT_EQ(unattached.attach_slot_ring(attach(make_section(4 * 4096), 4, 4096)).status, Status::Ok);
    request = make(MessageType::RenderTile, opened, render);
    EXPECT_EQ(unattached.execute(request, kNoDeadline).status, Status::Ok);
    unattached.close_all();
}

TEST_F(EngineCoreTest, ARingCanBeAttachedOnlyOnce) {
    EngineCore unattached(EngineKind::Document);
    EXPECT_EQ(unattached.attach_slot_ring(attach(make_section(2 * 4096), 2, 4096)).status, Status::Ok);
    const auto second = make_section(2 * 4096);
    EXPECT_EQ(unattached.attach_slot_ring(attach(second, 2, 4096)).status, Status::InvalidArgument);
    // The refused handle was still consumed: closing it again fails.
    EXPECT_EQ(CloseHandle(reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(second))), FALSE);
}

TEST_F(EngineCoreTest, AttachRefusesWhatIsNotAnHonestRing) {
    EngineCore core(EngineKind::Document);
    EXPECT_EQ(core.attach_slot_ring(attach(0, 2, 4096)).status, Status::InvalidArgument);
    EXPECT_EQ(core.attach_slot_ring(attach(static_cast<std::uint64_t>(-1), 2, 4096)).status, Status::InvalidArgument);
    EXPECT_EQ(core.attach_slot_ring(attach(make_section(4096), 0, 4096)).status, Status::InvalidArgument);          // no slots
    EXPECT_EQ(core.attach_slot_ring(attach(make_section(4096), 1025, 4096)).status, Status::InvalidArgument);       // too many
    EXPECT_EQ(core.attach_slot_ring(attach(make_section(4096), 1, 4095)).status, Status::InvalidArgument);          // not page sized
    EXPECT_EQ(core.attach_slot_ring(attach(make_section(4096), 1, (64u << 20) + 4096)).status, Status::InvalidArgument);
    EXPECT_EQ(core.attach_slot_ring(attach(make_section(4096), 2, 4096)).status, Status::InvalidArgument);          // section smaller than promised
    // Not a section at all: a file handle.
    EXPECT_EQ(core.attach_slot_ring(attach(open_handle(support::fixture_path("hello_world.pdf")), 1, 4096)).status, Status::InvalidArgument);
    // Nothing above attached anything, so a good one still can.
    EXPECT_EQ(core.attach_slot_ring(attach(make_section(2 * 4096), 2, 4096)).status, Status::Ok);
}

// ---- command line -------------------------------------------------------------------------------

TEST(CommandLineTest, ParsesTheFullCommandLine) {
    const std::vector<std::wstring_view> args = {L"--pipe", L"0123456789abcdef0123456789abcdef", L"--parent", L"1234", L"--kind", L"pool"};
    engine::EngineArguments parsed;
    EXPECT_EQ(engine::parse_command_line(args, parsed), "");
    EXPECT_EQ(parsed.pipe_name, L"0123456789abcdef0123456789abcdef");
    EXPECT_EQ(parsed.parent_pid, 1234u);
    EXPECT_EQ(parsed.kind, EngineKind::Pool);
}

TEST(CommandLineTest, ParsesTheShellKind) {
    const std::vector<std::wstring_view> args = {L"--pipe", L"0123456789abcdef0123456789abcdef", L"--parent", L"1234", L"--kind", L"shell"};
    engine::EngineArguments parsed;
    EXPECT_EQ(engine::parse_command_line(args, parsed), "");
    EXPECT_EQ(parsed.kind, EngineKind::Shell);
}

TEST(CommandLineTest, RefusesAnythingElse) {
    const std::vector<std::wstring_view> good = {L"--pipe", L"0123456789abcdef0123456789abcdef", L"--parent", L"1234", L"--kind", L"document"};
    engine::EngineArguments parsed;
    ASSERT_EQ(engine::parse_command_line(good, parsed), "");

    const auto with = [&](std::size_t value_index, std::wstring_view replacement) {
        auto args = good;
        args[value_index] = replacement;
        engine::EngineArguments out;
        return engine::parse_command_line(args, out);
    };
    EXPECT_NE(with(1, L"..\\..\\evil"), "");                       // pipe name that is not 32 hex digits
    EXPECT_NE(with(1, L"0123456789ABCDEF0123456789ABCDEF"), "");  // upper case
    EXPECT_NE(with(3, L"0"), "");                                  // parent pid
    EXPECT_NE(with(3, L"12x"), "");
    EXPECT_NE(with(3, L"99999999999"), "");
    EXPECT_NE(with(5, L"both"), "");

    engine::EngineArguments out;
    // Handles and geometry are no longer arguments: nothing is inherited, and the ring is announced over the pipe.
    for (const std::wstring_view old_option : {L"--section", L"--slots", L"--slot-size", L"--file"}) {
        auto args = good;
        args.push_back(old_option);
        args.push_back(L"1");
        EXPECT_NE(engine::parse_command_line(args, out), "") << "accepted an old option";
    }

    auto duplicate = good;
    duplicate[2] = L"--pipe";
    duplicate[3] = L"0123456789abcdef0123456789abcdef";
    EXPECT_NE(engine::parse_command_line(duplicate, out), "");

    EXPECT_NE(engine::parse_command_line(std::span(good).first(good.size() - 2), out), "") << "missing --kind";
    EXPECT_NE(engine::parse_command_line(std::span(good).first(good.size() - 1), out), "") << "option without value";
}

}  // namespace
