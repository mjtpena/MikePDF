# Quire

A Windows-native PDF reader built on WinUI 3 and PDFium, designed so that a malicious PDF can't reach your files. Licensed under [Apache-2.0](LICENSE).

> **Status: early development.** The foundations exist and are tested, but this is not yet a finished product. See [docs/STATUS.md](docs/STATUS.md) for what works, what's been measured, and what hasn't.

## Why another PDF reader
- **Sandboxed by design.** PDF parsing never happens in the app's UI process. PDFium runs in a separate process inside a zero-capability AppContainer: no files beyond the document it was handed, no network, no child processes. A crash there is contained and recovered from.
- **Native.** WinUI 3, Fluent design, pen and touch, and accessibility through UI Automation.
- **Honest saves.** Quire writes its own annotation objects and appearance streams, so other viewers see what you saw, and saves are atomic.
- **Private by default.** No telemetry without opt-in, and no account.

## Architecture
```text
Quire.App (UI + broker, medium integrity)  never parses PDF bytes
   |  named pipe (MessagePack, every response validated)
   |  shared-memory slots (tiles, read-only in the UI)
   +-- Quire.Engine      C++20 + PDFium, AppContainer, one per document
   +-- Quire.WriterHost  .NET + Quire.PdfWriter, AppContainer, for saves and validation
```

| Path | What it is |
|---|---|
| `src/Quire.App` | WinUI 3 application |
| `src/Quire.Broker` | Sandbox launcher, pipe server, response validation, watchdog |
| `src/Quire.Engine`, `src/Quire.Engine.Pdfium` | Sandboxed C++ engine host and the PDFium wrapper |
| `src/Quire.Protocol` | IPC schema and generated C#/C++ codecs |
| `src/Quire.PdfWriter`, `src/Quire.WriterHost` | Strict PDF parser, incremental writer, and its sandboxed host |
| `docs/adr` | Architecture decisions |
| `docs/spikes` | Feasibility spike reports, with raw numbers |
| `docs/threat-model` | Threat model |
| `spikes` | Frozen spike code, kept as evidence. Never used by production code |

## Building
You need Windows 11, the .NET 10 SDK, and Visual Studio or Build Tools with the C++ workload and CMake.

```powershell
./build.ps1 -Test
```

PDFium is downloaded on first build and verified against the SHA-256 in [build/pdfium.lock](build/pdfium.lock).

## Contributing and security
See [CONTRIBUTING.md](CONTRIBUTING.md). Please report vulnerabilities privately, as described in [SECURITY.md](SECURITY.md).

## Licence
Apache License 2.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
