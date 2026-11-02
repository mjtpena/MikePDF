#include "preview_uia.h"

#include <OleAuto.h>

namespace mikepdf::shellext {

PreviewProvider::PreviewProvider(HWND window) : window_(window) {}

void PreviewProvider::update(const std::wstring& name, const std::wstring& value) {
    std::wstring previous;
    {
        std::lock_guard lock(mutex_);
        previous = value_;
        name_ = name;
        value_ = value;
    }
    if (previous != value && UiaClientsAreListening()) {
        VARIANT before{};
        VARIANT after{};
        before.vt = VT_BSTR;
        before.bstrVal = SysAllocString(previous.c_str());
        after.vt = VT_BSTR;
        after.bstrVal = SysAllocString(value.c_str());
        UiaRaiseAutomationPropertyChangedEvent(this, UIA_ValueValuePropertyId, before, after);
        VariantClear(&before);
        VariantClear(&after);
    }
}

STDMETHODIMP PreviewProvider::QueryInterface(REFIID iid, void** out) {
    if (out == nullptr) {
        return E_POINTER;
    }
    *out = nullptr;
    if (IsEqualIID(iid, IID_IUnknown) || IsEqualIID(iid, __uuidof(IRawElementProviderSimple))) {
        *out = static_cast<IRawElementProviderSimple*>(this);
    } else if (IsEqualIID(iid, __uuidof(IValueProvider))) {
        *out = static_cast<IValueProvider*>(this);
    } else {
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) PreviewProvider::Release() {
    const long left = --references_;
    if (left == 0) {
        delete this;
    }
    return static_cast<ULONG>(left);
}

STDMETHODIMP PreviewProvider::get_ProviderOptions(ProviderOptions* options) {
    if (options == nullptr) {
        return E_POINTER;
    }
    *options = ProviderOptions_ServerSideProvider | ProviderOptions_UseComThreading;
    return S_OK;
}

STDMETHODIMP PreviewProvider::GetPatternProvider(PATTERNID pattern, IUnknown** provider) {
    if (provider == nullptr) {
        return E_POINTER;
    }
    *provider = nullptr;
    if (pattern == UIA_ValuePatternId) {
        *provider = static_cast<IValueProvider*>(this);
        AddRef();
    }
    return S_OK;
}

STDMETHODIMP PreviewProvider::GetPropertyValue(PROPERTYID property, VARIANT* value) {
    if (value == nullptr) {
        return E_POINTER;
    }
    VariantInit(value);
    std::lock_guard lock(mutex_);
    switch (property) {
        case UIA_NamePropertyId:
            value->vt = VT_BSTR;
            value->bstrVal = SysAllocString(name_.c_str());
            break;
        case UIA_ValueValuePropertyId:
            value->vt = VT_BSTR;
            value->bstrVal = SysAllocString(value_.c_str());
            break;
        case UIA_ValueIsReadOnlyPropertyId:
        case UIA_IsContentElementPropertyId:
        case UIA_IsControlElementPropertyId:
        case UIA_IsKeyboardFocusablePropertyId:
            value->vt = VT_BOOL;
            value->boolVal = VARIANT_TRUE;
            break;
        case UIA_ControlTypePropertyId:
            value->vt = VT_I4;
            value->lVal = UIA_DocumentControlTypeId;
            break;
        case UIA_ClassNamePropertyId:
            value->vt = VT_BSTR;
            value->bstrVal = SysAllocString(L"MikePDF.ShellPreview");
            break;
        default:
            break;  // VT_EMPTY: not supported, UI Automation falls back to the window's own defaults
    }
    return S_OK;
}

STDMETHODIMP PreviewProvider::get_HostRawElementProvider(IRawElementProviderSimple** host) {
    if (host == nullptr) {
        return E_POINTER;
    }
    return UiaHostProviderFromHwnd(window_, host);
}

STDMETHODIMP PreviewProvider::get_Value(BSTR* value) {
    if (value == nullptr) {
        return E_POINTER;
    }
    std::lock_guard lock(mutex_);
    *value = SysAllocString(value_.c_str());
    return *value != nullptr ? S_OK : E_OUTOFMEMORY;
}

STDMETHODIMP PreviewProvider::get_IsReadOnly(BOOL* readOnly) {
    if (readOnly == nullptr) {
        return E_POINTER;
    }
    *readOnly = TRUE;
    return S_OK;
}

}  // namespace mikepdf::shellext
