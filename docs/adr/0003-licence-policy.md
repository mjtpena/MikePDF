# ADR-0003: MikePDF licence and dependency licence policy
- Status: **Accepted: Apache-2.0** (user chose open source, 2026-11-02; Opus chose the permissive licence, option 2)
- Date: 2026-10-27, amended 2026-11-02
- Deciders: User (MikePDF's licence), Opus (dependency policy)

## Context
Master prompt §16 makes MikePDF's own licence a decision that constrains every dependency. Spikes and M1 needed a rule for which packages and tools they could use before that decision was made, so this ADR first set an interim policy. The user has now decided MikePDF is open source.

## Options considered (MikePDF's own licence: user decision)
1. **Proprietary.** Allows a commercial product with no source obligations. It excludes MuPDF, iText, Ghostscript and any GPL/AGPL linked code (§16).
2. **Permissive open source (MIT / Apache-2.0).** Brings community contributions and transparency for a security-sensitive app. The same dependency exclusions as option 1 apply in practice, because we want downstream users to stay free to relicense.
3. **Copyleft open source (GPL-3.0 / AGPL-3.0).** Would allow MuPDF (AGPL) without buying a licence. It locks MikePDF into copyleft and complicates enterprise and Store distribution.

## Decision
**MikePDF is open source under the Apache License 2.0.** Compared with MIT, it adds an explicit patent grant, which matters for a PDF application, and it's compatible with PDFium's BSD-3-Clause and Apache-2.0 terms and with Store distribution. Compared with copyleft, it keeps MikePDF usable in managed enterprise fleets and doesn't force the MuPDF route.

The dependency policy below was written as the interim rule and stays in force unchanged.

| Class | Examples | Shipped code | Build/test tools (not shipped, out of process) |
|---|---|---|---|
| Permissive | MIT, BSD-2/3, Apache-2.0, ISC, zlib, Boost, Unicode, IJG, libpng, FreeType FTL, MS-PL | Allowed. Recorded in third-party notices | Allowed |
| Vendor redistributable | Windows App SDK, Windows SDK, .NET runtime (MIT), Microsoft NuGet packages with redistribution terms | Allowed per the vendor's terms. Record the terms in notices | Allowed |
| Weak copyleft | LGPL-2.1/3.0, MPL-2.0, EPL | **ADR required for each package.** LGPL only when dynamically linked and replaceable | Allowed |
| Strong copyleft | GPL-2.0/3.0, AGPL-3.0, SSPL | **Forbidden** | Allowed only as separate executables that are never distributed, listed in `build/tools.lock` with their licence |
| Fonts | OFL-1.1, Apache-2.0 | Allowed for bundling | n/a |
| Unknown or no licence | n/a | Forbidden | Forbidden |

Rules:
- Every new dependency names its licence in the PR description (A.5). CI gets a licence scan in M1, covering NuGet, npm and pip in test tools, and PDFium's bundled `licenses/`.
- `THIRD-PARTY-NOTICES.md` is generated per build (§15.4). Its PDFium section comes from the `licenses/` folder shipped with each PDFium build. As of chromium/8125 that folder covers abseil, agg23, dragonbox, fast_float, freetype, harfbuzz, highway, icu, lcms, libjpeg_turbo, libopenjpeg, libpng, llvm-libc, pdfium, simdutf and zlib.
- Note for notices: the pdfium-binaries repository is MIT, but its NuGet packages say Apache-2.0 (ADR-0001). Record both while we use the prebuilt.

## Consequences
- MuPDF stays excluded unless the licence changes to copyleft or a commercial licence is bought from Artifex.
- pyHanko (MIT) and qpdf (Apache-2.0) are fine as test tools. Poppler `pdfsig` (GPL) would be allowed only as a non-distributed test executable.

## Verification
- Licence-scan job green in M1 CI.
- The notices file diff is reviewed on every PDFium bump.

## Contributions
Inbound equals outbound under Apache-2.0 section 5. No CLA. See `CONTRIBUTING.md`.
