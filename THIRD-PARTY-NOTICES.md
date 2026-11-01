# Third-party notices

Quire itself is licensed under the [Apache License 2.0](LICENSE). Binary distributions also contain the components below, under their own licences. Dependency rules are in [ADR-0003](docs/adr/0003-licence-policy.md).

Release packages embed the full licence texts: `build/generate-notices.ps1` assembles them, including one file per PDFium bundled library from the `licenses/` folder of the pinned PDFium build.

## Shipped in the package
| Component | Licence | Source |
|---|---|---|
| PDFium | BSD-3-Clause, plus Apache-2.0 for some files | https://pdfium.googlesource.com/pdfium/ |
| Libraries bundled inside PDFium: abseil, agg23, dragonbox, fast_float, freetype, harfbuzz, highway, icu, lcms, libjpeg_turbo, libopenjpeg, libpng, llvm-libc, simdutf, zlib | Each under its own licence, see the pinned build's `licenses/` folder | via PDFium |
| pdfium-binaries build scripts (prebuilt only; the repository says MIT, its NuGet packages say Apache-2.0) | MIT / Apache-2.0 | https://github.com/bblanchon/pdfium-binaries |
| .NET runtime and libraries | MIT | https://github.com/dotnet/runtime |
| Windows App SDK | Microsoft Software License Terms (redistributable) | https://github.com/microsoft/WindowsAppSDK |
| Win2D | MIT | https://github.com/microsoft/Win2D |
| MessagePack for C# | MIT | https://github.com/MessagePack-CSharp/MessagePack-CSharp |

## Used only to build or test (not shipped)
| Component | Licence |
|---|---|
| GoogleTest | BSD-3-Clause |
| xunit.v3 | Apache-2.0 |
| Microsoft.Testing.Extensions.CodeCoverage | Microsoft licence |
| Microsoft.Windows.CsWin32 | MIT |
| SharpFuzz | MIT |
| qpdf, pdf.js, pyHanko (spikes only) | Apache-2.0, Apache-2.0, MIT |

PDFium test PDFs under `tests/*/Fixtures/` are BSD-3-Clause. Each folder has a `MANIFEST.md` with source paths and hashes.
