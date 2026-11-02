// The preview window's UI Automation provider (spec 5, SHELL-22): the window is named "PDF preview" and its value is "Page n of N". It gives a screen reader the
// position, not the text (OQ-SHELL-6, a known limitation).
#pragma once

#include <Windows.h>
#include <ole2.h>
#include <UIAutomation.h>

#include <atomic>
#include <mutex>
#include <string>

namespace mikepdf::shellext {

class PreviewProvider final : public IRawElementProviderSimple, public IValueProvider {
public:
    explicit PreviewProvider(HWND window);
    PreviewProvider(const PreviewProvider&) = delete;
    PreviewProvider& operator=(const PreviewProvider&) = delete;

    /// Sets the name and the value, and tells listening clients when the value changed.
    void update(const std::wstring& name, const std::wstring& value);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID iid, void** out) override;
    STDMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(++references_); }
    STDMETHODIMP_(ULONG) Release() override;

    // IRawElementProviderSimple
    STDMETHODIMP get_ProviderOptions(ProviderOptions* options) override;
    STDMETHODIMP GetPatternProvider(PATTERNID pattern, IUnknown** provider) override;
    STDMETHODIMP GetPropertyValue(PROPERTYID property, VARIANT* value) override;
    STDMETHODIMP get_HostRawElementProvider(IRawElementProviderSimple** host) override;

    // IValueProvider
    STDMETHODIMP SetValue(LPCWSTR) override { return UIA_E_NOTSUPPORTED; }
    STDMETHODIMP get_Value(BSTR* value) override;
    STDMETHODIMP get_IsReadOnly(BOOL* readOnly) override;

private:
    ~PreviewProvider() = default;

    HWND window_;
    std::atomic<long> references_{1};
    std::mutex mutex_;
    std::wstring name_;
    std::wstring value_;
};

}  // namespace mikepdf::shellext
