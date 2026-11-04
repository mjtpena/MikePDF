# Third-party notices

MikePDF itself is licensed under the [Apache License 2.0](LICENSE). Binary distributions also contain the components below, under their own licences. Dependency rules are in [ADR-0003](docs/adr/0003-licence-policy.md).

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
| Advance widths and vertical metrics of the standard 14 fonts' twelve non-symbolic faces (numbers only, in `MikePDF.Markup`; the AFM files are not shipped). Copyright Adobe Systems Incorporated, with the notices and the permission text reproduced in full under "Adobe font metrics (AFM)" below | Adobe's permission for the 14 PDF core font metrics: may be used, copied and distributed for any purpose and without charge provided the copyright notices are kept | `tools/afm/generate_standard_font_data.py` says where the files came from |

## Adobe font metrics (AFM)
`MikePDF.Markup` contains the advance widths and vertical metrics of the twelve non-symbolic standard-14 faces (Helvetica, Times and Courier, each in regular, bold, oblique/italic and bold oblique/italic). They come from Adobe's AFM files for the PDF core fonts; the AFM files themselves are not shipped. Adobe's terms require the copyright notices to be kept, so they are reproduced here exactly as they stand in the files.

The copyright lines of the twelve AFM files used (`phvr8a`, `phvb8a`, `phvro8a`, `phvbo8a`, `ptmr8a`, `ptmb8a`, `ptmri8a`, `ptmbi8a`, `pcrr8a`, `pcrb8a`, `pcrro8a`, `pcrbo8a`), verbatim:

```
Copyright (c) 1985, 1987, 1989, 1990 Adobe Systems Incorporated.  All rights reserved.
Copyright (c) 1985, 1987, 1989, 1990 Adobe Systems Incorporated.  All Rights Reserved.
Copyright (c) 1989, 1990, 1991 Adobe Systems Incorporated. All rights reserved.
Copyright (c) 1989, 1990, 1991, Adobe Systems Incorporated. All rights reserved.
```

The Helvetica files add "Helvetica is a trademark of Linotype AG and/or its subsidiaries." and the Times files add "Times is a trademark of Linotype AG and/or its subsidiaries."

Adobe's permission text for these files, verbatim from the readme that accompanies the copy of the files the table was made from (the `pdfcorefonts` folder of matplotlib 3.11.1; Adobe's download page was http://partners.adobe.com/public/developer/font/index.html):

```
This file and the 14 PostScript(R) AFM files it accompanies may be used, copied,
and distributed for any purpose and without charge, with or without modification,
provided that all copyright notices are retained; that the AFM files are not
distributed without this file; that all modifications to this file or any of
the AFM files are prominently noted in the modified file(s); and that this
paragraph is not modified. Adobe Systems has no responsibility or obligation
to support the use of the AFM files.
```

MikePDF distributes only numbers derived from the files, not the files, and keeps this notice with every distribution.

## Used only to build or test (not shipped)
| Component | Licence |
|---|---|
| GoogleTest | BSD-3-Clause |
| xunit.v3 | Apache-2.0 |
| Microsoft.Testing.Extensions.CodeCoverage | Microsoft licence |
| Microsoft.Windows.CsWin32 | MIT |
| SharpFuzz, SharpFuzz.Common, SharpFuzz.CommandLine (fuzz instrumenter, a local tool), dnlib (inside it) | MIT |
| libfuzzer-dotnet-windows.cc (libFuzzer driver for .NET, vendored in `tests/fuzz/libfuzzer-dotnet/`, built from source; ADR-0017) | MIT |
| qpdf, pdf.js, pyHanko (spikes only) | Apache-2.0, Apache-2.0, MIT |

PDFium test PDFs under `tests/*/Fixtures/` are BSD-3-Clause. Each folder has a `MANIFEST.md` with source paths and hashes.
