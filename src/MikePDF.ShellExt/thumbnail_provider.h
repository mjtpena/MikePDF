// IThumbnailProvider over IInitializeWithStream (spec 3.3). It parses nothing: the stream goes to a section, ShellHost and the sandboxed engine do the rest.
#pragma once

#include <Windows.h>
#include <shobjidl.h>
#include <thumbcache.h>

#include <atomic>
#include <functional>

#include "com_support.h"
#include "policy_gate.h"
#include "shell_client.h"

namespace mikepdf::shellext {

/// The pieces a test replaces: where the policy comes from and how the client is configured.
struct ThumbnailEnvironment {
    PolicyReader policy_reader = read_policy_from_registry;
    std::function<ClientConfig()> config = [] { return production_config(g_module, ShellFeature::Thumbnails); };
};

class ThumbnailProvider final : public IInitializeWithStream, public IThumbnailProvider {
public:
    explicit ThumbnailProvider(ThumbnailEnvironment environment);
    ThumbnailProvider(const ThumbnailProvider&) = delete;
    ThumbnailProvider& operator=(const ThumbnailProvider&) = delete;

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID iid, void** out) override;
    STDMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(++references_); }
    STDMETHODIMP_(ULONG) Release() override;

    // IInitializeWithStream: no work here, the shell may initialise handlers it never asks anything of.
    STDMETHODIMP Initialize(IStream* stream, DWORD mode) override;

    // IThumbnailProvider
    STDMETHODIMP GetThumbnail(UINT cx, HBITMAP* bitmap, WTS_ALPHATYPE* alpha) override;

    /// The time limit of a thumbnail from entry to return, and the part of it ShellHost gets (ADR-0018 decision 7).
    static constexpr std::chrono::milliseconds kTotalBudget{2000};
    static constexpr std::uint32_t kHostBudgetMs = 1700;

private:
    ~ThumbnailProvider();

    ThumbnailEnvironment environment_;
    std::atomic<long> references_{1};
    IStream* stream_{nullptr};
};

}  // namespace mikepdf::shellext
