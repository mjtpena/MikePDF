// The thumbnail provider end to end against a scripted ShellHost: sizes, alpha, the policy gate, the stream cap, and the 2 s limit. (SHELL-03 and SHELL-15 for the
// handler; the same cases through the real ShellHost and engine are in tests/MikePDF.Integration.Tests/ShellHandlerTests.cs.)
#include <gtest/gtest.h>

#include <shlwapi.h>

#include <chrono>
#include <cmath>

#include "fake_shellhost.h"
#include "thumbnail_provider.h"

namespace {

using namespace mikepdf;
using mikepdf::protocol::MessageType;
using testing_support::FakeShellHost;

IStream* pdf_like_stream(std::size_t bytes = 4096) {
    const std::vector<std::uint8_t> data(bytes, 0x25);
    return SHCreateMemStream(data.data(), static_cast<UINT>(data.size()));
}

shellext::ThumbnailEnvironment environment_for(FakeShellHost& server, shellext::PolicyReader policy = nullptr) {
    shellext::ThumbnailEnvironment environment;
    environment.config = [&server] { return server.config(); };
    environment.policy_reader = policy ? policy : [](HKEY, const wchar_t*) { return shellext::DwordRead{}; };
    return environment;
}

/// A provider initialised with `stream`, released on scope exit.
struct Provider {
    shellext::ThumbnailProvider* object;
    IThumbnailProvider* thumbs;
    explicit Provider(shellext::ThumbnailEnvironment environment, IStream* stream) : object(new shellext::ThumbnailProvider(std::move(environment))), thumbs(nullptr) {
        EXPECT_EQ(object->QueryInterface(IID_PPV_ARGS(&thumbs)), S_OK);
        IInitializeWithStream* init = nullptr;
        EXPECT_EQ(object->QueryInterface(IID_PPV_ARGS(&init)), S_OK);
        if (stream != nullptr) {
            EXPECT_EQ(init->Initialize(stream, STGM_READ), S_OK);
        }
        init->Release();
    }
    ~Provider() {
        thumbs->Release();
        object->Release();
    }
    Provider(const Provider&) = delete;
    Provider& operator=(const Provider&) = delete;
};

TEST(Thumbnail, ReturnsAPremultipliedArgbBitmapThatFitsTheRequestedSize) {
    FakeShellHost server;
    IStream* stream = pdf_like_stream();
    Provider provider(environment_for(server), stream);
    HBITMAP bitmap = nullptr;
    WTS_ALPHATYPE alpha = WTSAT_UNKNOWN;
    ASSERT_EQ(provider.thumbs->GetThumbnail(256, &bitmap, &alpha), S_OK);
    ASSERT_NE(bitmap, nullptr);
    EXPECT_EQ(alpha, WTSAT_ARGB);
    BITMAP info{};
    ASSERT_GT(GetObjectW(bitmap, sizeof(info), &info), 0);
    EXPECT_EQ(info.bmWidth, 198);
    EXPECT_EQ(info.bmHeight, 256);
    EXPECT_EQ(info.bmBitsPixel, 32);
    std::uint32_t first = 0;
    ASSERT_EQ(GetBitmapBits(bitmap, 4, &first), 4);
    EXPECT_EQ(first, 0xFF000000u | FakeShellHost::kPagePixel) << "alpha is forced opaque";
    DeleteObject(bitmap);
    stream->Release();
}

TEST(Thumbnail, TheLongerSideEqualsCxAtEverySize) {
    // SHELL-03 for the handler: 32, 96, 256 and 1024. The page is 612 x 792, so the height is the long side and the width follows the aspect ratio within a pixel.
    for (const UINT cx : {32u, 96u, 256u, 1024u}) {
        FakeShellHost server;
        IStream* stream = pdf_like_stream();
        Provider provider(environment_for(server), stream);
        HBITMAP bitmap = nullptr;
        WTS_ALPHATYPE alpha{};
        ASSERT_EQ(provider.thumbs->GetThumbnail(cx, &bitmap, &alpha), S_OK) << cx;
        BITMAP info{};
        GetObjectW(bitmap, sizeof(info), &info);
        EXPECT_EQ(static_cast<UINT>(std::max(info.bmWidth, info.bmHeight)), cx);
        EXPECT_NEAR(static_cast<double>(info.bmWidth) / info.bmHeight, 612.0 / 792.0, 1.0 / info.bmHeight + 1e-9) << cx;
        EXPECT_EQ(server.last_max_width, cx);
        DeleteObject(bitmap);
        stream->Release();
    }
}

TEST(Thumbnail, CxIsClampedToTheSchemaRange) {
    FakeShellHost server;
    IStream* stream = pdf_like_stream();
    Provider provider(environment_for(server), stream);
    HBITMAP bitmap = nullptr;
    WTS_ALPHATYPE alpha{};
    ASSERT_EQ(provider.thumbs->GetThumbnail(4, &bitmap, &alpha), S_OK);
    EXPECT_EQ(server.last_max_width, 16u);
    DeleteObject(bitmap);
    ASSERT_EQ(provider.thumbs->GetThumbnail(100000, &bitmap, &alpha), S_OK);
    EXPECT_EQ(server.last_max_width, 1024u);
    DeleteObject(bitmap);
    stream->Release();
}

TEST(Thumbnail, PolicyOffNeverTouchesTheStreamOrTheServer) {
    FakeShellHost server;
    IStream* stream = pdf_like_stream();
    const shellext::PolicyReader off = [](HKEY hive, const wchar_t* name) {
        return hive == HKEY_LOCAL_MACHINE && std::wstring(name) == L"ShellThumbnailsEnabled" ? shellext::DwordRead{shellext::DwordRead::Kind::Valid, 0} : shellext::DwordRead{};
    };
    Provider provider(environment_for(server, off), stream);
    HBITMAP bitmap = nullptr;
    WTS_ALPHATYPE alpha{};
    EXPECT_EQ(provider.thumbs->GetThumbnail(256, &bitmap, &alpha), HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY));
    EXPECT_EQ(bitmap, nullptr);
    Sleep(100);
    EXPECT_EQ(server.connections, 0) << "SHELL-15: a policy-off machine never starts or reaches ShellHost";
    stream->Release();
}

TEST(Thumbnail, TheOtherFeaturesPolicyDoesNotSwitchThumbnailsOff) {
    FakeShellHost server;
    IStream* stream = pdf_like_stream();
    const shellext::PolicyReader preview_off = [](HKEY, const wchar_t* name) {
        return std::wstring(name) == L"ShellPreviewEnabled" ? shellext::DwordRead{shellext::DwordRead::Kind::Valid, 0} : shellext::DwordRead{};
    };
    Provider provider(environment_for(server, preview_off), stream);
    HBITMAP bitmap = nullptr;
    WTS_ALPHATYPE alpha{};
    EXPECT_EQ(provider.thumbs->GetThumbnail(64, &bitmap, &alpha), S_OK);
    DeleteObject(bitmap);
    stream->Release();
}

TEST(Thumbnail, AStreamOverTheCapFailsWithoutAConnection) {
    // An IStream that claims 33 MiB and has nothing to read: the cap is checked from Stat, before any read.
    struct BigStream final : IStream {
        std::atomic<long> refs{1};
        STDMETHODIMP QueryInterface(REFIID iid, void** out) override {
            if (iid == IID_IUnknown || iid == IID_IStream || iid == IID_ISequentialStream) {
                *out = static_cast<IStream*>(this);
                AddRef();
                return S_OK;
            }
            *out = nullptr;
            return E_NOINTERFACE;
        }
        STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
        STDMETHODIMP_(ULONG) Release() override { return --refs; }  // lives on the test's stack
        STDMETHODIMP Read(void*, ULONG, ULONG*) override { ADD_FAILURE() << "the cap must be checked before reading"; return E_FAIL; }
        STDMETHODIMP Write(const void*, ULONG, ULONG*) override { return E_NOTIMPL; }
        STDMETHODIMP Seek(LARGE_INTEGER, DWORD, ULARGE_INTEGER*) override { return S_OK; }
        STDMETHODIMP SetSize(ULARGE_INTEGER) override { return E_NOTIMPL; }
        STDMETHODIMP CopyTo(IStream*, ULARGE_INTEGER, ULARGE_INTEGER*, ULARGE_INTEGER*) override { return E_NOTIMPL; }
        STDMETHODIMP Commit(DWORD) override { return E_NOTIMPL; }
        STDMETHODIMP Revert() override { return E_NOTIMPL; }
        STDMETHODIMP LockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override { return E_NOTIMPL; }
        STDMETHODIMP UnlockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override { return E_NOTIMPL; }
        STDMETHODIMP Stat(STATSTG* stat, DWORD) override {
            *stat = {};
            stat->cbSize.QuadPart = (32ull << 20) + 1;
            return S_OK;
        }
        STDMETHODIMP Clone(IStream**) override { return E_NOTIMPL; }
    } big;

    FakeShellHost server;
    Provider provider(environment_for(server), &big);
    HBITMAP bitmap = nullptr;
    WTS_ALPHATYPE alpha{};
    EXPECT_EQ(provider.thumbs->GetThumbnail(256, &bitmap, &alpha), HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE));
    EXPECT_EQ(server.connections, 0);
}

TEST(Thumbnail, ARefusalFromShellHostIsAFailureNotABitmap) {
    FakeShellHost server([](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
        if (request.message_type == MessageType::ShellOpen) {
            return FakeShellHost::frame(request.request_id, protocol::Status::PasswordRequired, request.message_type, nullptr);
        }
        return host.default_reply(request);
    });
    IStream* stream = pdf_like_stream();
    Provider provider(environment_for(server), stream);
    HBITMAP bitmap = nullptr;
    WTS_ALPHATYPE alpha{};
    EXPECT_EQ(provider.thumbs->GetThumbnail(256, &bitmap, &alpha), HRESULT_FROM_WIN32(ERROR_INVALID_PASSWORD));
    EXPECT_EQ(bitmap, nullptr);
    stream->Release();
}

TEST(Thumbnail, AServerThatNeverAnswersFailsAtTwoSecondsNotLater) {
    // SHELL-07, the handler half: the call returns at its own 2 s limit however long the server would take. (ShellHost kills the engine; see the integration test.)
    FakeShellHost server([](FakeShellHost& host, const protocol::RequestEnvelope& request) -> FakeShellHost::Reply {
        if (request.message_type == MessageType::ShellRenderPage) {
            return std::nullopt;
        }
        return host.default_reply(request);
    });
    IStream* stream = pdf_like_stream();
    Provider provider(environment_for(server), stream);
    HBITMAP bitmap = nullptr;
    WTS_ALPHATYPE alpha{};
    const auto started = std::chrono::steady_clock::now();
    const HRESULT hr = provider.thumbs->GetThumbnail(256, &bitmap, &alpha);
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started);
    EXPECT_EQ(hr, HRESULT_FROM_WIN32(ERROR_TIMEOUT));
    EXPECT_EQ(bitmap, nullptr);
    EXPECT_GE(elapsed.count(), 1900);
    EXPECT_LE(elapsed.count(), 2500) << "it must not return late";
    stream->Release();
}

TEST(Thumbnail, TheComContractIsHonoured) {
    FakeShellHost server;
    shellext::ThumbnailProvider* object = new shellext::ThumbnailProvider(environment_for(server));
    IThumbnailProvider* thumbs = nullptr;
    ASSERT_EQ(object->QueryInterface(IID_PPV_ARGS(&thumbs)), S_OK);
    IUnknown* unrelated = nullptr;
    EXPECT_EQ(object->QueryInterface(IID_IPreviewHandler, reinterpret_cast<void**>(&unrelated)), E_NOINTERFACE);
    EXPECT_EQ(unrelated, nullptr);

    HBITMAP bitmap = nullptr;
    WTS_ALPHATYPE alpha{};
    EXPECT_EQ(thumbs->GetThumbnail(256, &bitmap, &alpha), E_UNEXPECTED) << "before Initialize";
    EXPECT_EQ(thumbs->GetThumbnail(256, nullptr, &alpha), E_POINTER);

    IInitializeWithStream* init = nullptr;
    ASSERT_EQ(object->QueryInterface(IID_PPV_ARGS(&init)), S_OK);
    EXPECT_EQ(init->Initialize(nullptr, STGM_READ), E_POINTER);
    IStream* stream = pdf_like_stream();
    EXPECT_EQ(init->Initialize(stream, STGM_READ), S_OK);
    EXPECT_EQ(init->Initialize(stream, STGM_READ), HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED));
    init->Release();
    stream->Release();
    thumbs->Release();
    object->Release();
}

}  // namespace
