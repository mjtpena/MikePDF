# M1 plan: foundations (under the M0 waiver)

- Date: 2026-10-28
- Owner: Opus (plan and review). Sonnet (implementation)
- Scope: limited to [the waiver](M0-waiver.md). The full M1 gate (§17) also needs the corpus, visual diff, perf harness and 24 h of fuzzing; those come later.

## Build layout
| Area | Tooling | Notes |
|---|---|---|
| C# | .NET 10, `MikePDF.slnx`, `Directory.Build.props` (nullable, warnings as errors, analysers latest-recommended), `Directory.Packages.props` (central package management with lock files) | `global.json` pins the SDK feature band. DEV runs 10.0.300; CI installs 10.0.4xx (ADR-0001 A1) |
| C++ | CMake ≥ 3.28, C++20, MSVC (v143 on DEV with 14.44, v145 on CI with VS 2026), hardening flags `/guard:cf /CETCOMPAT /sdl /Qspectre /W4 /WX` | `CMakeLists.txt` at the root. GoogleTest v1.18.0 via `FetchContent`, pinned by SHA-256 |
| PDFium | `build/pdfium.lock` (JSON: release, asset SHA-256s) + `build/fetch-pdfium.ps1` | Prebuilt chromium/8125 until ADR-0002's own build exists |
| Entry point | `build.ps1 [-Configuration Release] [-Arch x64\|arm64] [-Test]` | The same script runs locally and in CI |

## Work packages (each lands as reviewable commits, ≤ ~600 changed lines where possible)
| WP | Content | Owner | Depends on |
|---|---|---|---|
| WP1 | Skeleton: projects per §5.5 (only those in scope), props, `global.json`, `.editorconfig`, `build.ps1`, CMake root, `build/pdfium.lock`, CI workflow | Opus | n/a |
| WP2 | `MikePDF.Protocol`: schema (`schema/mikepdf.protocol.json`), generator (`tools/MikePDF.ProtocolGen`) emitting C# (MessagePack-CSharp, integer keys, plus generated `Validate()` with limits) and C++ (a MikePDF-owned bounds-checked MessagePack core plus structs, encode and decode); round-trip tests both ways; a cross-language golden-bytes test | Sonnet | WP1 |
| WP3 | `MikePDF.PdfWriter` core (lexer, parser for classic, stream and hybrid xref plus object streams, object model, serializer, incremental writer, strict mode, structural validator) from the S8/S4 prototypes, plus `MikePDF.WriterHost` (sandboxed process, protocol over pipe); tests ≥ 85% line coverage on PdfWriter | Sonnet | WP1 (WP2 for the WriterHost messages) |
| WP4 | `MikePDF.Engine` (C++20): pipe client, I/O lane plus engine thread, slot section, runtime signature policy, `MikePDF.Engine.Pdfium` wrapper (load via a mapped view and `FPDF_LoadMemDocument64`, page sizes, RenderTile with progressive rendering and cancel), typed errors; `MikePDF.Broker` (C#): AppContainer launcher per ADR-0004, pipe server with DACL and PID check, job and IOCP, watchdog, respawn; integration tests that run the real sandboxed engine | Sonnet | WP1, WP2 |
| WP5 | Opus review (A.6 plus A.9 for the IPC, sandbox and writer code), then STATUS update | Opus | WP2–WP4 |

## Message set for M1 (subset of §5.4)
`Hello`, `Ping`, `Shutdown`, `OpenDocument`, `CloseDocument`, `GetDocumentInfo`, `GetPageSizes`, `RenderTile`, `CancelBefore`, plus the response envelope with the typed error codes (§6.4). WriterHost adds `ValidateStructure`. Everything else in §5.4 arrives with the milestone that uses it.

## Definition of done (per WP, §19.1)
Tests added and passing locally via `build.ps1 -Test`; a CI workflow that would run the same steps; a completion note with what was verified, on what hardware, and known limitations. **No performance claims.**
