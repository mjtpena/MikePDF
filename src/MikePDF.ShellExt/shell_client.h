// The handler's client of ShellHost (ADR-0018, spec 3.2 and 11): finds or starts ShellHost, checks that it really is ShellHost, then speaks Hello, ShellOpen and
// ShellRenderPage with the generated client codec, and checks every reply before it hands a number to anything that allocates.
#pragma once

#include <Windows.h>

#include <chrono>
#include <string>

#include "mikepdf/protocol/codec.h"
#include "pipe_channel.h"
#include "policy_gate.h"
#include "section.h"
#include "validation.h"

namespace mikepdf::shellext {

struct ClientConfig {
    /// \\.\pipe\MikePDF.Shell.<user SID>
    std::wstring pipe_path;
    /// MikePDF.ShellHost.exe, started when the pipe is absent.
    std::wstring shellhost_path;
    /// Extra command line for ShellHost (test runs only).
    std::wstring shellhost_arguments;
    /// The named mutex that makes launching single-flight, so a folder of cold hosts starts one ShellHost.
    std::wstring launch_mutex_name;
    /// True inside the package: the server must have this process's package family. False (unpackaged, tests): the server's image must be shellhost_path.
    bool require_package_family{true};
};

/// Where this DLL's ShellHost is and what the pipe is called, for the process that loaded it (`module` is the DLL). Unpackaged only, the test environment variables
/// MIKEPDF_SHELL_TEST_PIPE and MIKEPDF_SHELL_TEST_HOST override the pipe name and the ShellHost path; a packaged host never looks at them. The two classes use
/// two pipes of the same ShellHost (the preview's name ends in ".Preview"), which is how ShellHost knows which switch and which rules apply to a request.
[[nodiscard]] ClientConfig production_config(HMODULE module, ShellFeature feature);

/// The pipe's name for a user: MikePDF.Shell. followed by the user's SID. ShellHost computes the same name (MikePDF.ShellHost/ShellPipe.cs).
[[nodiscard]] std::wstring pipe_name_for_sid(const std::wstring& sid);

struct OpenedDocument {
    std::uint32_t session{0};
    std::uint32_t page_count{0};
    float first_width{0};
    float first_height{0};
};

struct RenderedPage {
    std::uint32_t width{0};
    std::uint32_t height{0};
    /// stride x height, checked.
    std::uint64_t bytes{0};
};

class ShellClient {
public:
    explicit ShellClient(ClientConfig config) : config_(std::move(config)) {}

    /// Connects (starting ShellHost once if the pipe is absent), verifies the server, and sends Hello. `cancel` is an optional manual-reset event that
    /// aborts every wait (the preview raises it when it is unloaded).
    [[nodiscard]] HRESULT connect(Deadline deadline, HANDLE cancel = nullptr);

    /// ShellOpen. `host_ms` is the budget the handler gives ShellHost (the envelope's deadlineMs).
    [[nodiscard]] HRESULT open(const Section& source, Deadline deadline, std::uint32_t host_ms, OpenedDocument& out, HANDLE cancel = nullptr);

    /// ShellRenderPage into `pixels`, then the reply checks of spec section 7. On success the first `out.bytes` bytes of `pixels` are the page.
    [[nodiscard]] HRESULT render(std::uint32_t page, std::uint32_t max_width, std::uint32_t max_height, const Section& pixels, Deadline deadline,
                                 std::uint32_t host_ms, RenderedPage& out, HANDLE cancel = nullptr);

    void close() noexcept { channel_.close(); }
    /// False once the pipe was closed, by us after a protocol failure or because the peer went away.
    [[nodiscard]] bool is_connected() const noexcept { return channel_.is_open(); }

    /// The status ShellHost answered with in the last failing exchange, or ProtocolError / Timeout for one that never got an answer.
    [[nodiscard]] protocol::Status last_status() const noexcept { return last_status_; }
    /// True when ShellHost had to be started for this connection.
    [[nodiscard]] bool launched() const noexcept { return launched_; }

private:
    [[nodiscard]] HRESULT exchange(const protocol::RequestPayload& request, std::uint32_t document_id, Deadline deadline, std::uint32_t host_ms,
                                   protocol::ReplyPayload& reply, HANDLE cancel);
    [[nodiscard]] HRESULT start_shellhost(Deadline deadline, HANDLE cancel);
    [[nodiscard]] bool verify_server() const;

    ClientConfig config_;
    PipeChannel channel_;
    std::uint64_t next_request_id_{1};
    std::uint32_t session_{0};
    protocol::Status last_status_{protocol::Status::Ok};
    bool launched_{false};
};

/// What a failing ShellHost status means as an HRESULT. Never Ok.
[[nodiscard]] HRESULT hresult_for_status(protocol::Status status) noexcept;

}  // namespace mikepdf::shellext
