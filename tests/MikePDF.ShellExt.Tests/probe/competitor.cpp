// A classic in-process thumbnail provider that draws a checkerboard and never touches ShellHost. CI ONLY (the shell integration workflow): the probe registers it by
// hand on a disposable GitHub-hosted runner as another app's handler for .pdf, to learn what the shell shows when MikePDF's handler declines. It is never built into a
// package and never registered anywhere else: the probe refuses to write the registry unless GITHUB_ACTIONS is true and RUNNER_ENVIRONMENT is github-hosted.
#include <Windows.h>
#include <shobjidl.h>
#include <thumbcache.h>

#include <atomic>
#include <new>

namespace {

// {C0FFEE00-5A1E-4F3B-9C11-0A5B6D6B0004}
const CLSID kCompetitor = {0xC0FFEE00, 0x5A1E, 0x4F3B, {0x9C, 0x11, 0x0A, 0x5B, 0x6D, 0x6B, 0x00, 0x04}};

class Competitor final : public IInitializeWithStream, public IThumbnailProvider {
public:
    STDMETHODIMP QueryInterface(REFIID iid, void** out) override {
        if (iid == IID_IUnknown || iid == IID_IInitializeWithStream) {
            *out = static_cast<IInitializeWithStream*>(this);
        } else if (iid == IID_IThumbnailProvider) {
            *out = static_cast<IThumbnailProvider*>(this);
        } else {
            *out = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    STDMETHODIMP_(ULONG) Release() override {
        const ULONG left = --refs_;
        if (left == 0) {
            delete this;
        }
        return left;
    }
    STDMETHODIMP Initialize(IStream*, DWORD) override { return S_OK; }
    STDMETHODIMP GetThumbnail(UINT cx, HBITMAP* bitmap, WTS_ALPHATYPE* alpha) override {
        const int n = static_cast<int>(cx < 16 ? 16 : (cx > 1024 ? 1024 : cx));
        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = n;
        info.bmiHeader.biHeight = -n;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;
        void* bits = nullptr;
        HBITMAP made = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (made == nullptr) {
            return E_OUTOFMEMORY;
        }
        auto* pixels = static_cast<std::uint32_t*>(bits);
        for (int y = 0; y < n; ++y) {
            for (int x = 0; x < n; ++x) {
                pixels[y * n + x] = 0xFF000000u | ((((x / 8) + (y / 8)) & 1) ? 0x2060C0u : 0xE0E0E0u);
            }
        }
        *bitmap = made;
        *alpha = WTSAT_ARGB;
        return S_OK;
    }

private:
    std::atomic<ULONG> refs_{1};
};

class Factory final : public IClassFactory {
public:
    STDMETHODIMP QueryInterface(REFIID iid, void** out) override {
        if (iid == IID_IUnknown || iid == IID_IClassFactory) {
            *out = static_cast<IClassFactory*>(this);
            return S_OK;
        }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** out) override {
        if (outer != nullptr) {
            return CLASS_E_NOAGGREGATION;
        }
        auto* object = new (std::nothrow) Competitor();
        if (object == nullptr) {
            return E_OUTOFMEMORY;
        }
        const HRESULT hr = object->QueryInterface(iid, out);
        object->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL) override { return S_OK; }
} g_factory;

}  // namespace

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** out) {
    if (!IsEqualCLSID(clsid, kCompetitor)) {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    return g_factory.QueryInterface(iid, out);
}

STDAPI DllCanUnloadNow() { return S_FALSE; }
