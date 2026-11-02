// The two COM classes this DLL serves. The same GUIDs are in the package manifest (src/MikePDF.App/Package.appxmanifest) and in the integration
// workflow; a build check (tests/MikePDF.Core.Tests/ShellManifestTests.cs, SHELL-01) fails if they ever differ.
#pragma once

#include <guiddef.h>

namespace mikepdf::shellext {

// {31D801F3-AE53-4B73-B786-601B36D28C99}
inline constexpr GUID kClsidThumbnailProvider = {0x31D801F3, 0xAE53, 0x4B73, {0xB7, 0x86, 0x60, 0x1B, 0x36, 0xD2, 0x8C, 0x99}};
// {8A471493-BD13-4956-A0B9-199A67BD325E}
inline constexpr GUID kClsidPreviewHandler = {0x8A471493, 0xBD13, 0x4956, {0xA0, 0xB9, 0x19, 0x9A, 0x67, 0xBD, 0x32, 0x5E}};

}  // namespace mikepdf::shellext
