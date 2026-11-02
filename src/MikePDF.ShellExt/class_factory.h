#pragma once

#include <Windows.h>
#include <unknwn.h>

#include <atomic>
#include <functional>

#include "com_support.h"

namespace mikepdf::shellext {

/// A class factory for one of the two classes. `make` returns a new object with one reference, or nullptr when out of memory. The module is locked while a
/// factory (or LockServer) holds it, so DllCanUnloadNow stays honest.
class ClassFactory final : public IClassFactory {
public:
    explicit ClassFactory(std::function<IUnknown*()> make) : make_(std::move(make)) { ++g_module_objects; }
    ClassFactory(const ClassFactory&) = delete;
    ClassFactory& operator=(const ClassFactory&) = delete;

    STDMETHODIMP QueryInterface(REFIID iid, void** out) override;
    STDMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(++references_); }
    STDMETHODIMP_(ULONG) Release() override;
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** out) override;
    STDMETHODIMP LockServer(BOOL lock) override;

private:
    ~ClassFactory() { --g_module_objects; }

    std::function<IUnknown*()> make_;
    std::atomic<long> references_{1};
};

/// The factory for `clsid` (the thumbnail provider or the preview handler, with the production environment), or CLASS_E_CLASSNOTAVAILABLE.
[[nodiscard]] HRESULT get_class_object(REFCLSID clsid, REFIID iid, void** out) noexcept;

}  // namespace mikepdf::shellext
