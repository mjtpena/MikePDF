// The handler's client of ShellHost against a scripted server: the protocol, the server check, the deadlines, and what a hostile or broken peer can and cannot do.
// (SHELL-11 and SHELL-17 on the handler side, and the handler half of SHELL-07.)
#include <gtest/gtest.h>

#include <chrono>

#include "fake_shellhost.h"
#include "mikepdf/protocol/msgpack.h"

namespace {

using namespace mikepdf;
using mikepdf::protocol::MessageType;
using mikepdf::protocol::Status;
using mikepdf::shellext::Section;
using mikepdf::shellext::ShellClient;
using testing_support::FakeShellHost;

constexpr std::uint32_t kEdge = 256;

Section filled_section(std::uint64_t bytes, std::uint8_t value = 0x25) {
    Section section;
    EXPECT_EQ(shellext::create_section(bytes, section), S_OK);
    void* view = MapViewOfFile(section.handle.get(), FILE_MAP_WRITE, 0, 0, static_cast<SIZE_T>(bytes));
    std::memset(view, value, static_cast<std::size_t>(bytes));
    UnmapViewOfFile(view);
    return section;
}

shellext::Deadline soon(int ms = 3000) { return shellext::after(std::chrono::milliseconds(ms)); }

struct Session {
    FakeShellHost server;
    ShellClient client;
    Section document = filled_section(5000, 0x25);
    Section pixels;
    explicit Session(FakeShellHost::Script script = {}) : server(std::move(script)), client(server.config()) {
        EXPECT_EQ(shellext::create_section(shellext::pixel_section_bytes(kEdge, kEdge), pixels), S_OK);
    }
};

TEST(ClientProtocol, HelloOpenAndRenderAgainstACorrectServer) {
    Session s;
    ASSERT_EQ(s.client.connect(soon()), S_OK);
    EXPECT_EQ(s.server.hellos, 1);
    EXPECT_EQ(s.server.hello_kind, protocol::ProcessKind::ShellHandler);
    EXPECT_EQ(s.server.hello_schema, protocol::kSchemaVersion);
    EXPECT_EQ(s.server.hello_pid, GetCurrentProcessId());

    shellext::OpenedDocument document;
    ASSERT_EQ(s.client.open(s.document, soon(), 1700, document), S_OK);
    EXPECT_EQ(s.server.opened_bytes, 5000u);
    EXPECT_EQ(s.server.opened_first_byte, 0x25u) << "the server reads the client's section";
    EXPECT_EQ(document.session, 1u);
    EXPECT_EQ(document.page_count, FakeShellHost::kPageCount);

    shellext::RenderedPage page;
    ASSERT_EQ(s.client.render(0, kEdge, kEdge, s.pixels, soon(), 1700, page), S_OK);
    EXPECT_EQ(s.server.last_max_width, kEdge);
    EXPECT_EQ(s.server.last_max_height, kEdge);
    EXPECT_EQ(s.server.last_flags, 1u) << "annotations are drawn";
    EXPECT_EQ(page.height, kEdge) << "612 x 792 into 256 x 256: the height is the long side";
    EXPECT_EQ(page.width, 198u);
    EXPECT_EQ(page.bytes, std::uint64_t{198} * 4 * 256);
    shellext::MappedView view;
    ASSERT_EQ(view.map(s.pixels.handle.get(), page.bytes), S_OK);
    EXPECT_EQ(*reinterpret_cast<const std::uint32_t*>(view.data()), FakeShellHost::kPagePixel) << "the pixels come back in the handler's own section";
}

TEST(ClientProtocol, TheEnvelopeDeadlineIsClampedAndCarried) {
    Session s;
    ASSERT_EQ(s.client.connect(soon()), S_OK);
    shellext::OpenedDocument document;
    ASSERT_EQ(s.client.open(s.document, soon(), 1700, document), S_OK);
    EXPECT_EQ(s.server.last_deadline_ms, 1700u);
    ASSERT_EQ(s.client.open(s.document, soon(), 0, document), S_OK);
    EXPECT_EQ(s.server.last_deadline_ms, 1u) << "never 0: that means no deadline";
    ASSERT_EQ(s.client.open(s.document, soon(), 999999, document), S_OK);
    EXPECT_EQ(s.server.last_deadline_ms, 5000u);
}

TEST(ClientProtocol, ARequestTheSchemaRefusesIsNeverSent) {
    Session s;
    ASSERT_EQ(s.client.connect(soon()), S_OK);
    shellext::OpenedDocument document;
    Section too_big;
    too_big.bytes = shellext::kMaxStreamBytes + 1;
    EXPECT_EQ(s.client.open(too_big, soon(), 1700, document), E_INVALIDARG);
    Section nothing;
    EXPECT_EQ(s.client.open(nothing, soon(), 1700, document), E_INVALIDARG);
    shellext::RenderedPage page;
    EXPECT_EQ(s.client.render(0, kEdge, kEdge, s.pixels, soon(), 1700, page), E_INVALIDARG) << "render before open: there is no session";
    EXPECT_EQ(s.server.opens, 0);
    EXPECT_EQ(s.server.renders, 0);
}

TEST(ClientProtocol, ARenderSectionThatIsTooSmallIsRefusedBeforeSending) {
    Session s;
    ASSERT_EQ(s.client.connect(soon()), S_OK);
    shellext::OpenedDocument document;
    ASSERT_EQ(s.client.open(s.document, soon(), 1700, document), S_OK);
    Section tiny;
    ASSERT_EQ(shellext::create_section(100, tiny), S_OK);
    shellext::RenderedPage page;
    EXPECT_EQ(s.client.render(0, kEdge, kEdge, tiny, soon(), 1700, page), E_INVALIDARG);
    EXPECT_EQ(s.server.renders, 0);
}

// ---- hostile replies --------------------------------------------------------------------------------------------------------------------------------

/// A server that answers every ShellRenderPage with `payload` and everything else correctly.
FakeShellHost::Script render_answers(protocol::ShellRenderPageReply payload) {
    return [payload](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
        if (request.message_type != MessageType::ShellRenderPage) {
            return host.default_reply(request);
        }
        protocol::ReplyPayload reply = payload;
        return FakeShellHost::frame(request.request_id, Status::Ok, request.message_type, &reply);
    };
}

HRESULT render_with(const protocol::ShellRenderPageReply& payload, bool* still_connected = nullptr) {
    Session s(render_answers(payload));
    EXPECT_EQ(s.client.connect(soon()), S_OK);
    shellext::OpenedDocument document;
    EXPECT_EQ(s.client.open(s.document, soon(), 1700, document), S_OK);
    shellext::RenderedPage page;
    const HRESULT hr = s.client.render(0, kEdge, kEdge, s.pixels, soon(), 1700, page);
    if (still_connected != nullptr) {
        *still_connected = s.client.is_connected();
    }
    return hr;
}

TEST(ClientHostileReplies, AStrideThatIsNotFourTimesTheWidthEndsTheSession) {
    bool connected = true;
    EXPECT_EQ(render_with({100, 100, 404}, &connected), HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    EXPECT_FALSE(connected) << "a peer that sent a wrong reply ends the session";
}

TEST(ClientHostileReplies, ABitmapLargerThanRequestedIsRefused) {
    EXPECT_EQ(render_with({257, 100, 257 * 4}), HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    EXPECT_EQ(render_with({100, 257, 400}), HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    EXPECT_EQ(render_with({1024, 1024, 4096}), HRESULT_FROM_WIN32(ERROR_INVALID_DATA)) << "the schema allows it; the request did not";
}

TEST(ClientHostileReplies, ASizeTheSchemaItselfForbidsNeverReachesTheChecks) {
    // The server cannot even encode width 5000 (the generated encoder refuses), so this is a hand-made frame: the decoder must reject it too.
    protocol::Writer writer;
    writer.write_array_header(3);
    writer.write_uint(5000);
    writer.write_uint(10);
    writer.write_uint(20000);
    ASSERT_EQ(writer.status(), Status::Ok);
    const auto body = writer.take();
    protocol::Reader reader(body);
    EXPECT_NE(protocol::ShellRenderPageReply::decode(reader).status, Status::Ok);
}

TEST(ClientHostileReplies, AnAnswerToTheWrongRequestEndsTheSession) {
    Session s([](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
        auto reply = host.default_reply(request);
        if (request.message_type == MessageType::ShellOpen) {
            // The right body with another request's id.
            protocol::ReplyPayload payload = protocol::ShellOpenReply{1, 3, 612, 792};
            return FakeShellHost::frame(request.request_id + 7, Status::Ok, request.message_type, &payload);
        }
        return reply;
    });
    ASSERT_EQ(s.client.connect(soon()), S_OK);
    shellext::OpenedDocument document;
    EXPECT_EQ(s.client.open(s.document, soon(), 1700, document), HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    EXPECT_FALSE(s.client.is_connected());
}

TEST(ClientHostileReplies, AnAnswerOfAnotherMessageTypeEndsTheSession) {
    Session s([](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
        if (request.message_type == MessageType::ShellOpen) {
            protocol::ReplyPayload payload = protocol::ShellCloseReply{};
            return FakeShellHost::frame(request.request_id, Status::Ok, MessageType::ShellClose, &payload);
        }
        return host.default_reply(request);
    });
    ASSERT_EQ(s.client.connect(soon()), S_OK);
    shellext::OpenedDocument document;
    EXPECT_EQ(s.client.open(s.document, soon(), 1700, document), HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
}

TEST(ClientHostileReplies, AnOpenReplyWithNoSessionOrNoPagesIsRefused) {
    for (const auto& bad : {protocol::ShellOpenReply{1, 0, 612, 792}}) {
        Session s([bad](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
            if (request.message_type == MessageType::ShellOpen) {
                protocol::ReplyPayload payload = bad;
                return FakeShellHost::frame(request.request_id, Status::Ok, request.message_type, &payload);
            }
            return host.default_reply(request);
        });
        ASSERT_EQ(s.client.connect(soon()), S_OK);
        shellext::OpenedDocument document;
        EXPECT_EQ(s.client.open(s.document, soon(), 1700, document), HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    }
}

TEST(ClientHostileReplies, GarbageAndOversizeFramesAreRefused) {
    {
        // A frame whose length is within the limit but whose body is not MessagePack.
        Session s([](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
            if (request.message_type == MessageType::Hello) {
                return host.default_reply(request);
            }
            return std::vector<std::uint8_t>{4, 0, 0, 0, 0xC1, 0xC1, 0xC1, 0xC1};
        });
        ASSERT_EQ(s.client.connect(soon()), S_OK);
        shellext::OpenedDocument document;
        EXPECT_EQ(s.client.open(s.document, soon(), 1700, document), HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    }
    {
        // A length prefix of 1 MiB: refused before a body is read, so the client never waits for the bytes.
        Session s([](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
            if (request.message_type == MessageType::Hello) {
                return host.default_reply(request);
            }
            return std::vector<std::uint8_t>{0, 0, 0x10, 0};
        });
        ASSERT_EQ(s.client.connect(soon()), S_OK);
        shellext::OpenedDocument document;
        const auto started = std::chrono::steady_clock::now();
        EXPECT_EQ(s.client.open(s.document, soon(), 1700, document), HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
        EXPECT_LT(std::chrono::steady_clock::now() - started, std::chrono::milliseconds(1500));
    }
}

TEST(ClientHostileReplies, AHelloReplyWithAnotherSchemaVersionIsRefused) {
    Session s([](FakeShellHost&, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
        protocol::ReplyPayload payload = protocol::HelloReply{protocol::kSchemaVersion + 1, ""};
        return FakeShellHost::frame(request.request_id, Status::Ok, request.message_type, &payload);
    });
    EXPECT_EQ(s.client.connect(soon()), HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
}

// ---- statuses -----------------------------------------------------------------------------------------------------------------------------------------

TEST(ClientStatuses, AreTypedAndLeaveTheSessionUsable) {
    struct Case {
        Status status;
        HRESULT expected;
    };
    const Case cases[] = {
        {Status::PasswordRequired, HRESULT_FROM_WIN32(ERROR_INVALID_PASSWORD)}, {Status::Damaged, HRESULT_FROM_WIN32(ERROR_BAD_FORMAT)},
        {Status::Unsupported, HRESULT_FROM_WIN32(ERROR_BAD_FORMAT)},            {Status::Timeout, HRESULT_FROM_WIN32(ERROR_TIMEOUT)},
        {Status::PermissionDenied, E_ACCESSDENIED},                              {Status::EngineCrashed, HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED)},
    };
    for (const Case& c : cases) {
        Session s([c](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
            if (request.message_type == MessageType::ShellOpen) {
                return FakeShellHost::frame(request.request_id, c.status, request.message_type, nullptr);
            }
            return host.default_reply(request);
        });
        ASSERT_EQ(s.client.connect(soon()), S_OK);
        shellext::OpenedDocument document;
        EXPECT_EQ(s.client.open(s.document, soon(), 1700, document), c.expected) << static_cast<int>(c.status);
        EXPECT_EQ(s.client.last_status(), c.status);
        EXPECT_TRUE(s.client.is_connected()) << "a refusal is an answer, not a protocol failure";
    }
}

TEST(ClientStatuses, APartiallyRenderedPageIsNotShown) {
    Session s([](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
        if (request.message_type == MessageType::ShellRenderPage) {
            protocol::ReplyPayload payload = protocol::ShellRenderPageReply{16, 16, 64};
            return FakeShellHost::frame(request.request_id, Status::PartiallyRendered, request.message_type, &payload);
        }
        return host.default_reply(request);
    });
    ASSERT_EQ(s.client.connect(soon()), S_OK);
    shellext::OpenedDocument document;
    ASSERT_EQ(s.client.open(s.document, soon(), 1700, document), S_OK);
    shellext::RenderedPage page;
    EXPECT_TRUE(FAILED(s.client.render(0, kEdge, kEdge, s.pixels, soon(), 1700, page)));
}

// ---- who is on the other end --------------------------------------------------------------------------------------------------------------------------

TEST(ClientServerCheck, RefusesAServerThatIsNotShellHostAndSendsItNothing) {
    FakeShellHost server;
    auto config = server.config();
    config.shellhost_path = L"C:\\Windows\\System32\\cmd.exe";  // the server in this test is the test program, not this
    ShellClient client(config);
    EXPECT_EQ(client.connect(soon()), HRESULT_FROM_WIN32(ERROR_NOT_AUTHENTICATED));
    EXPECT_FALSE(client.is_connected());
    Sleep(100);
    EXPECT_EQ(server.hellos, 0) << "not even a Hello goes to a process that failed the check";
}

TEST(ClientServerCheck, InsidePackagesTheServerMustHaveThePackageFamilyAndFailsClosedWithout) {
    // This test process has no package identity, so a handler that demands a family (as in the package) cannot match anything and must refuse.
    FakeShellHost server;
    auto config = server.config();
    config.require_package_family = true;
    ShellClient client(config);
    EXPECT_EQ(client.connect(soon()), HRESULT_FROM_WIN32(ERROR_NOT_AUTHENTICATED));
    Sleep(100);
    EXPECT_EQ(server.hellos, 0);
}

// ---- time -----------------------------------------------------------------------------------------------------------------------------------------------

TEST(ClientTime, AServerThatNeverAnswersEndsAtTheDeadline) {
    Session s([](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
        if (request.message_type == MessageType::ShellOpen) {
            return std::nullopt;
        }
        return host.default_reply(request);
    });
    ASSERT_EQ(s.client.connect(soon()), S_OK);
    shellext::OpenedDocument document;
    const auto started = std::chrono::steady_clock::now();
    const HRESULT hr = s.client.open(s.document, shellext::after(std::chrono::milliseconds(300)), 300, document);
    const auto elapsed = std::chrono::steady_clock::now() - started;
    EXPECT_EQ(hr, HRESULT_FROM_WIN32(ERROR_TIMEOUT));
    EXPECT_EQ(s.client.last_status(), Status::Timeout);
    EXPECT_GE(elapsed, std::chrono::milliseconds(250));
    EXPECT_LT(elapsed, std::chrono::milliseconds(1500));
    EXPECT_FALSE(s.client.is_connected()) << "after a timeout the stream position is unknown: the connection ends";
}

TEST(ClientTime, AnEventCancelsEveryWait) {
    Session s([](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
        if (request.message_type == MessageType::ShellOpen) {
            return std::nullopt;
        }
        return host.default_reply(request);
    });
    ASSERT_EQ(s.client.connect(soon()), S_OK);
    shellext::UniqueHandle cancel(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    std::thread raiser([&] {
        Sleep(150);
        SetEvent(cancel.get());
    });
    shellext::OpenedDocument document;
    const auto started = std::chrono::steady_clock::now();
    EXPECT_EQ(s.client.open(s.document, soon(10000), 10000, document, cancel.get()), HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));
    EXPECT_LT(std::chrono::steady_clock::now() - started, std::chrono::milliseconds(1500));
    raiser.join();
}

TEST(ClientLaunch, AMissingShellHostFailsFastAndNeverHangs) {
    shellext::ClientConfig config;
    config.pipe_path = L"\\\\.\\pipe\\mikepdf-shellext-test-nobody-home";
    config.shellhost_path = L"C:\\this\\does\\not\\exist\\MikePDF.ShellHost.exe";
    config.launch_mutex_name = L"Local\\mikepdf-shellext-test-launch-missing";
    config.require_package_family = false;
    ShellClient client(config);
    const auto started = std::chrono::steady_clock::now();
    EXPECT_TRUE(FAILED(client.connect(soon(1500))));
    EXPECT_LT(std::chrono::steady_clock::now() - started, std::chrono::milliseconds(2500));
    EXPECT_FALSE(client.launched());
}

TEST(ClientLaunch, ATestOverrideIsHonouredOnlyWhenUnpackaged) {
    // This process is unpackaged, so the override applies: the pipe name comes from the variable and nothing else.
    SetEnvironmentVariableW(L"MIKEPDF_SHELL_TEST_PIPE", L"mikepdf-shellext-test-override");
    const auto with = shellext::production_config(nullptr, shellext::ShellFeature::Thumbnails);
    EXPECT_EQ(with.pipe_path, L"\\\\.\\pipe\\mikepdf-shellext-test-override");
    EXPECT_EQ(shellext::production_config(nullptr, shellext::ShellFeature::Preview).pipe_path, L"\\\\.\\pipe\\mikepdf-shellext-test-override.Preview") << "the preview has its own pipe on the same ShellHost";
    EXPECT_EQ(with.shellhost_arguments, L"--pipe mikepdf-shellext-test-override");
    EXPECT_FALSE(with.require_package_family);
    // A name that could escape the pipe namespace is ignored.
    SetEnvironmentVariableW(L"MIKEPDF_SHELL_TEST_PIPE", L"..\\..\\evil");
    const auto without = shellext::production_config(nullptr, shellext::ShellFeature::Thumbnails);
    EXPECT_NE(without.pipe_path.find(L"\\\\.\\pipe\\MikePDF.Shell.S-1-"), std::wstring::npos) << without.pipe_path;
    SetEnvironmentVariableW(L"MIKEPDF_SHELL_TEST_PIPE", nullptr);
}

TEST(ClientLaunch, ThePipeIsPerUser) {
    EXPECT_EQ(shellext::pipe_name_for_sid(L"S-1-5-21-1-2-3-1001"), L"MikePDF.Shell.S-1-5-21-1-2-3-1001");
}

}  // namespace
