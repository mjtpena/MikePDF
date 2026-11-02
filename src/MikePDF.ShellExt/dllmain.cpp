// The DLL's entry points. There is no DllRegisterServer and no DllInstall on purpose: registration is the package manifest's (desktop2:ThumbnailHandler,
// desktop2:DesktopPreviewHandler and com:SurrogateServer), so nothing in here ever writes to the registry.
#include <Windows.h>

#include "class_factory.h"
#include "com_support.h"

using namespace mikepdf::shellext;

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** out) { return get_class_object(clsid, iid, out); }

STDAPI DllCanUnloadNow() { return g_module_objects.load() == 0 ? S_OK : S_FALSE; }
