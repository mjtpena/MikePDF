# Contributing to MikePDF

MikePDF is an early-stage project; see [docs/STATUS.md](docs/STATUS.md) for where it stands. Issues and small pull requests are welcome.

## Licence of contributions
MikePDF is Apache-2.0. Under section 5 of the licence, anything you intentionally submit is contributed under those same terms. No CLA is needed.

## Build
You need Windows 11, the .NET 10 SDK, and Visual Studio or Build Tools with the C++ workload and CMake.

```powershell
./build.ps1 -Test
```

## Ground rules
- **Keep PRs small:** under about 600 changed lines, excluding generated code and fixtures.
- **Tests:** every behaviour change comes with tests that would fail without it.
- **Security boundaries:**
  - No PDF parsing in the UI process.
  - PDFium is called only from `MikePDF.Engine`, and `MikePDF.PdfWriter` runs only inside `MikePDF.WriterHost`.
  - Everything coming back from a sandboxed process is untrusted.
- **Protocol changes:** edit `src/MikePDF.Protocol/schema/mikepdf.protocol.json`, then run `dotnet run --project tools/MikePDF.ProtocolGen -- generate`. Never edit generated files by hand.
- **Dependencies:** no GPL or AGPL code in anything we ship ([ADR-0003](docs/adr/0003-licence-policy.md)). New dependencies need their licence stated in the PR.
- **Significant decisions** get an ADR in [docs/adr](docs/adr/).
- **Test files:** never commit PDFs containing real personal data, or PDFs you don't have the right to redistribute.

Security problems go through [SECURITY.md](SECURITY.md), not public issues. Participation is covered by the [Code of Conduct](CODE_OF_CONDUCT.md).
