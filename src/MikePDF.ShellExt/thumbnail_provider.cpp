#include "thumbnail_provider.h"

#include "bitmap.h"
#include "section.h"
#include "validation.h"

namespace mikepdf::shellext {

ThumbnailProvider::ThumbnailProvider(ThumbnailEnvironment environment) : environment_(std::move(environment)) { ++g_module_objects; }

ThumbnailProvider::~ThumbnailProvider() {
    if (stream_ != nullptr) {
        stream_->Release();
    }
    --g_module_objects;
}

STDMETHODIMP ThumbnailProvider::QueryInterface(REFIID iid, void** out) {
    return query_table(iid, out, {{&IID_IInitializeWithStream, static_cast<IInitializeWithStream*>(this)}, {&IID_IThumbnailProvider, static_cast<IThumbnailProvider*>(this)}});
}

STDMETHODIMP_(ULONG) ThumbnailProvider::Release() {
    const long left = --references_;
    if (left == 0) {
        delete this;
    }
    return static_cast<ULONG>(left);
}

STDMETHODIMP ThumbnailProvider::Initialize(IStream* stream, DWORD) {
    if (stream == nullptr) {
        return E_POINTER;
    }
    if (stream_ != nullptr) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
    }
    stream_ = stream;
    stream_->AddRef();
    return S_OK;
}

STDMETHODIMP ThumbnailProvider::GetThumbnail(UINT cx, HBITMAP* bitmap, WTS_ALPHATYPE* alpha) {
    if (bitmap == nullptr || alpha == nullptr) {
        return E_POINTER;
    }
    *bitmap = nullptr;
    *alpha = WTSAT_UNKNOWN;
    if (stream_ == nullptr) {
        return E_UNEXPECTED;
    }
    const Deadline deadline = after(kTotalBudget);

    // The policy first: a machine where an organisation switched thumbnails off never copies a byte and never starts ShellHost (SHELL-15).
    if (evaluate_policy(ShellFeature::Thumbnails, environment_.policy_reader) == PolicyState::OffByPolicy) {
        return HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY);
    }

    const std::uint32_t edge = clamp_edge(cx);
    Section source;
    HRESULT hr = copy_stream_to_section(*stream_, kMaxStreamBytes, source);
    if (FAILED(hr)) {
        return hr;
    }
    Section pixels;
    hr = create_section(pixel_section_bytes(edge, edge), pixels);
    if (FAILED(hr)) {
        return hr;
    }

    ShellClient client(environment_.config());
    hr = client.connect(deadline);
    if (FAILED(hr)) {
        return hr;
    }
    OpenedDocument document;
    hr = client.open(source, deadline, std::min(kHostBudgetMs, remaining_ms(deadline)), document);
    if (FAILED(hr)) {
        return hr;
    }
    RenderedPage page;
    hr = client.render(0, edge, edge, pixels, deadline, std::min(kHostBudgetMs, remaining_ms(deadline)), page);
    if (FAILED(hr)) {
        return hr;
    }
    // The bitmap is the answer: leaving now closes the pipe, and ShellHost kills the one-shot engine and frees everything by itself.
    MappedView view;
    hr = view.map(pixels.handle.get(), page.bytes);
    if (FAILED(hr)) {
        return hr;
    }
    HBITMAP made = nullptr;
    hr = make_bitmap(view.data(), page.width, page.height, made);
    if (FAILED(hr)) {
        return hr;
    }
    *bitmap = made;
    *alpha = WTSAT_ARGB;
    return S_OK;
}

}  // namespace mikepdf::shellext
