// A stand-in for ShellHost on a real named pipe, in a thread of the test process, so a test can answer the handler's client with exactly the frames it likes,
// valid or forged. By default it answers like a correct ShellHost: it maps the sections the client names (in the same process a handle value is its own copy),
// reads the "document", and draws a solid page into the pixel section.
#pragma once

#include <Windows.h>

#include <atomic>
#include <functional>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include "mikepdf/protocol/codec.h"
#include "section.h"
#include "shell_client.h"

namespace testing_support {

using namespace mikepdf;

class FakeShellHost {
public:
    using Reply = std::optional<std::vector<std::uint8_t>>;
    /// Gets each request after the default bookkeeping and returns the frame to send, or nullopt to stay silent. Returning a frame for the wrong request is the point.
    using Script = std::function<Reply(FakeShellHost&, const protocol::RequestEnvelope&)>;

    static constexpr float kPageWidth = 612;
    static constexpr float kPageHeight = 792;
    static constexpr std::uint32_t kPageCount = 3;
    /// The colour of the page the default render draws (B, G, R, alpha 0: the handler must make it opaque).
    static constexpr std::uint32_t kPagePixel = 0x00804020;

    explicit FakeShellHost(Script script = {});
    FakeShellHost(const FakeShellHost&) = delete;
    FakeShellHost& operator=(const FakeShellHost&) = delete;
    ~FakeShellHost();

    /// The client configuration that reaches this server: the "ShellHost" executable is this test program, which is what the server's process is.
    [[nodiscard]] shellext::ClientConfig config() const;
    [[nodiscard]] const std::wstring& pipe_path() const { return pipe_path_; }

    // What the server saw.
    std::atomic<int> connections{0};
    std::atomic<int> hellos{0};
    std::atomic<int> opens{0};
    std::atomic<int> renders{0};
    std::atomic<std::uint32_t> hello_pid{0};
    std::atomic<std::uint32_t> hello_schema{0};
    std::atomic<protocol::ProcessKind> hello_kind{protocol::ProcessKind::Broker};
    std::atomic<std::uint32_t> opened_bytes{0};
    std::atomic<std::uint32_t> last_max_width{0};
    std::atomic<std::uint32_t> last_max_height{0};
    std::atomic<std::uint32_t> last_flags{0};
    std::atomic<std::uint32_t> last_deadline_ms{0};
    std::atomic<std::uint32_t> opened_first_byte{0};

    /// A correct reply to `request`: the default behaviour, for a script that wants to reuse it and then alter it.
    [[nodiscard]] Reply default_reply(const protocol::RequestEnvelope& request);
    [[nodiscard]] static std::vector<std::uint8_t> frame(std::uint64_t id, protocol::Status status, protocol::MessageType type, const protocol::ReplyPayload* payload);

private:
    void serve();
    bool read_exact(std::uint8_t* data, std::size_t size);
    bool write_all(const std::vector<std::uint8_t>& bytes);

    Script script_;
    std::wstring pipe_path_;
    std::wstring mutex_name_;
    shellext::UniqueHandle pipe_;
    shellext::UniqueHandle stop_;
    std::thread thread_;
};

}  // namespace testing_support
