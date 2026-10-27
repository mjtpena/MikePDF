# ADR-0008: Save pipeline and atomic replace
- Status: Accepted (architecture). Provisional on the manual Acrobat/Edge pass (S4, S8)
- Date: 2026-10-28
- Deciders: Opus (approver), Sonnet (S4 and S8 evidence)

## Context
§7.5 sets out the save pipeline:
1. The engine writes to a temporary handle the broker provides.
2. The broker reopens the output in a fresh engine and validates it.
3. The broker swaps it in with `ReplaceFileW`.
4. The default is an incremental save.
5. The user is warned before anything invalidates a signature.

[S4](../spikes/S4.md) and [S8](../spikes/S8.md) found:
- **PDFium writes no `/AP`** for most annotation types on a cold save. pdf.js then draws nothing for Text, FreeText or Stamp.
- PDFium **can't create** Line, Polygon or PolyLine. It writes `/IRT` and `/Name` as strings, never links `/Popup`, and stores annotations as direct dictionaries.
- PDFium's incremental save is **structurally invalid for xref-stream sources** (7 of 9), and a two-line patch doesn't fix it.
- A no-op incremental save **grows the file** by 24–270%. PDFium also leaves a private `/PDFIUM_HasGeneratedAP` key in saved files.
- For forms, PDFium works: `/V` correct in 12 of 12, appearances regenerated, no `/NeedAppearances`.
- Incremental saves keep the original bytes as an exact prefix, so signatures survive (4 of 4).
- A Quire-written incremental writer (S4's Q01–Q06, S8's signing writer) produces valid output for classic and xref-stream bases, confirmed by qpdf, PDFium, pdf.js and pyHanko.
- **Parser differentials are a security issue.** S8's first validator accepted a duplicate-key attack because it resolved duplicate keys differently from PDFium.

**Trust constraint.** The writer has to parse the untrusted file's xref, trailer and object structure. §0.2 rule 6 and §5.1 forbid PDF parsing in the UI process. So the writer cannot live in the broker.

## Options considered
1. **PDFium-only saves.** Rejected. It fails gaps 1–12 in S4 §5.2.
2. **Quire's writer in the broker (UI process).** Rejected. It parses untrusted bytes in the UI process.
3. **Port the writer to C++ inside the engine.** Possible, but it doubles the work: the S8 and S4 prototypes are C#, and C++ is the less memory-safe place to put a second PDF parser.
4. **Quire's writer as a C# library hosted in a sandboxed .NET "writer host" process**, with the same AppContainer profile as the engine (S3 showed .NET runs there, self-contained). **Chosen.**

## Decision
1. **Ownership.**
   - **`Quire.PdfWriter` owns every annotation dictionary and appearance stream Quire creates or restyles.** It covers S4 §5.2 gaps 1–10 and 12: indirect annotation objects, `/P`, `/NM`, `/M`, `/T`, `/F`, `/Popup`/`/IRT`/`/RT`/`/State`, line endings, variable-width ink AP, stamps, and a structural validator.
   - PDFium remains the engine for reading, rendering, text, **form filling and field appearances** (gap 11), and page operations.
2. **Process placement.**
   - `Quire.PdfWriter` runs only in **`Quire.WriterHost`**: a self-contained .NET process launched by the broker under the ADR-0004 sandbox profile, with no file paths, no network and handles only.
   - The broker never parses PDF bytes.
   - Signing (P1) is split. The writer host computes the `/ByteRange` digest. The broker signs **the digest only**, because it holds the CNG key and the smart-card or Windows Hello prompt. The writer host then patches `/Contents`. CMS **validation** (parsing an untrusted CMS blob) also runs in the writer host.
3. **Save stages.** All of these run in sandboxed processes and write to broker-provided handles.
   1. *No changes?* Do nothing. Never call an incremental save without edits.
   2. *Form or page changes?* The engine runs PDFium `FPDF_SaveAsCopy(FPDF_INCREMENTAL)` into temp file A.
   3. The writer host **normalises PDFium's appended tail**: rewrites the appended xref section in the same form as the previous section (a stream after a stream, a table after a table), and strips `/PDFIUM_HasGeneratedAP`. A repair that only patches the original bytes is forbidden.
   4. *Annotation changes?* The writer host appends its own incremental update: new and replaced objects, the page `/Annots`, the xref, `/Prev`.
   5. A **fresh engine** reopens the result and checks it against the command model: page count, annotation count and properties, and field values (§7.5 step 2). The writer host's structural validator also walks the whole xref chain.
   6. The broker does an atomic `ReplaceFileW`, keeping ACLs and alternate data streams. On any failure the original is untouched, and Save As is offered (§7.5 step 4).
4. **Full rewrite** (`FPDF_NO_INCREMENTAL`) is used only for Save As "optimised", flatten, and redaction (§8.6). **Never** for a signed document without an explicit warning that signatures will be invalidated.
5. **One parser, strict semantics.**
   - `Quire.PdfWriter`'s parser rejects duplicate keys in any object it changes or validates.
   - Any construct where its reading could differ from PDFium's (duplicate keys, hybrid xref, ambiguous object streams) **fails closed**: the save is refused with Save As offered, or the signature verdict becomes Unknown.
   - A lenient parse must never upgrade a validation verdict (S8 §5 item 6).
6. **Forms policy.** Accept PDFium's regenerated field appearances for v1. When only the selected state of a checkbox or radio changes, keep the author's existing state streams (S4 surprise 6). That needs an engine-side check in M4.
7. **Crash safety.** No write feature ships without the §7.4 journal. The journal entry for a save is cleared only after `ReplaceFileW` succeeds.

## Consequences
- A third process type, `Quire.WriterHost`, joins the engine and the UI. The broker's engine pool (ADR-0004) manages it too.
- `Quire.PdfWriter` is a security-critical parser. It gets SharpFuzz targets from M1, ≥ 85% line coverage (§12.1), mutation testing, and the A.9 review.
- The fidelity harness (§12.4) is seeded from `spikes/S4/check`.

## Verification
- M4 fidelity gate: every P0 operation is green in PDFium, pdf.js, Acrobat and Edge.
- Until then, the manual Acrobat/Edge pass on the S4 and S8 checklists confirms or revokes this ADR's provisional status. If Acrobat rejects Quire-written xref streams, that is grounds to revisit stage 3.
- A fuzz-found parser differential between PDFium and `Quire.PdfWriter` counts as a security bug (High).
