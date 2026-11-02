#include "class_factory.h"

#include <new>

#include "preview_handler.h"
#include "shellext_ids.h"
#include "thumbnail_provider.h"

namespace mikepdf::shellext {

STDMETHODIMP ClassFactory::QueryInterface(REFIID iid, void** out) {
    return query_table(iid, out, {{&IID_IClassFactory, static_cast<IClassFactory*>(this)}});
}

STDMETHODIMP_(ULONG) ClassFactory::Release() {
    const long left = --references_;
    if (left == 0) {
        delete this;
    }
    return static_cast<ULONG>(left);
}

STDMETHODIMP ClassFactory::CreateInstance(IUnknown* outer, REFIID iid, void** out) {
    if (out == nullptr) {
        return E_POINTER;
    }
    *out = nullptr;
    if (outer != nullptr) {
        return CLASS_E_NOAGGREGATION;
    }
    IUnknown* object = nullptr;
    try {
        object = make_();
    } catch (...) {
        object = nullptr;
    }
    if (object == nullptr) {
        return E_OUTOFMEMORY;
    }
    const HRESULT hr = object->QueryInterface(iid, out);
    object->Release();
    return hr;
}

STDMETHODIMP ClassFactory::LockServer(BOOL lock) {
    if (lock) {
        ++g_module_objects;
    } else {
        --g_module_objects;
    }
    return S_OK;
}

HRESULT get_class_object(REFCLSID clsid, REFIID iid, void** out) noexcept {
    if (out == nullptr) {
        return E_POINTER;
    }
    *out = nullptr;
    std::function<IUnknown*()> make;
    if (IsEqualCLSID(clsid, kClsidThumbnailProvider)) {
        make = []() -> IUnknown* { return static_cast<IInitializeWithStream*>(new (std::nothrow) ThumbnailProvider(ThumbnailEnvironment{})); };
    } else if (IsEqualCLSID(clsid, kClsidPreviewHandler)) {
        make = []() -> IUnknown* { return static_cast<IInitializeWithStream*>(new (std::nothrow) PreviewHandler(PreviewEnvironment{})); };
    } else {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    auto* factory = new (std::nothrow) ClassFactory(std::move(make));
    if (factory == nullptr) {
        return E_OUTOFMEMORY;
    }
    const HRESULT hr = factory->QueryInterface(iid, out);
    factory->Release();
    return hr;
}

}  // namespace mikepdf::shellext
