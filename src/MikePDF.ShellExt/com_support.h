// A few lines of COM plumbing shared by the two classes: the module's object count (DllCanUnloadNow) and an interface table for QueryInterface.
#pragma once

#include <Windows.h>
#include <unknwn.h>

#include <atomic>
#include <initializer_list>
#include <utility>

namespace mikepdf::shellext {

/// Live COM objects and locks of this module. DllCanUnloadNow answers S_OK only at zero.
inline std::atomic<long> g_module_objects{0};

/// The handle of this DLL (set in DllMain): where to find ShellHost and the string table.
inline HMODULE g_module = nullptr;

struct InterfaceEntry {
    const IID* iid;
    void* pointer;
};

/// QueryInterface over a table. The first entry is the object's IUnknown.
[[nodiscard]] inline HRESULT query_table(REFIID iid, void** out, std::initializer_list<InterfaceEntry> table) noexcept {
    if (out == nullptr) {
        return E_POINTER;
    }
    *out = nullptr;
    for (const InterfaceEntry& entry : table) {
        if (IsEqualIID(iid, entry.iid[0]) || (IsEqualIID(iid, IID_IUnknown) && &entry == table.begin())) {
            *out = entry.pointer;
            static_cast<IUnknown*>(entry.pointer)->AddRef();
            return S_OK;
        }
    }
    return E_NOINTERFACE;
}

}  // namespace mikepdf::shellext
