# Design decisions on the open questions

- Date: 2026-11-02
- Decided by: Opus, as defaults so wave 2 isn't blocked. **The product owner can override any of these.** A change here updates the screen file it names.

| # | Decision | Why |
|---|---|---|
| Q1 | MikePDF print sheet, with a link to the printer's own preferences. ADR-0011 to be written in wave 2 | The system dialog can't preview tiles from our engine or show annotation and form options together |
| Q2 | No: the command bar stays on its own row even with one tab | No layout jump when a second tab opens |
| Q3 | `HKLM\Software\Policies\MikePDF` (and `HKCU` for user policies) | ADR-0016 |
| Q4 | Selection toolbar on by default, with a setting to turn it off | One-click highlight is a headline advantage |
| Q5 | Pen inks by default when a digitizer is present, with a one-time notice | Pen-first is design principle 5 |
| Q6 | F11 hides the title bar too | True full screen, as in Edge and the Photos app |
| Q7 | Page Down scrolls one screen; in single-page or fit-page layouts a screen is a page | Doesn't skip content in continuous scroll. Interprets §10.2 rather than changing it |
| Q8 | Document properties are read-only in v1.0 | Editing metadata is a save-path feature; it can come with M4 |
| Q9 | No first-run diagnostics prompt; the choice lives in Settings, off by default | Private by default (§1.2) |
| Q10 | Default-app prompt once, on the empty state only | As the spec says |
| Q11 | Ask once, inline, for the comment author name the first time a comment is added. Prefill the Windows display name, but don't use it silently | The name leaves the device with the file |
| Q12 | Confirm before opening more than 10 files at once | As the spec says |
| Q13 | Explorer launches add a tab to the most recent window; the setting can choose a new window | §7.6 default |
| Q14 | Share is deferred to v1.x. It isn't in v1.0 | It sends the file elsewhere; it needs its own privacy review |
| Q15 | Open: a wave 2 research item. Headings come from the structure tree (`FPDF_StructTree_*`) when the PDF is tagged, exposed through the Text pattern's heading attributes. Scenario 6 can't be claimed until a manual NVDA pass shows it works | Needs a spike before a design |
| Q16 | The Ask tab isn't shown, even disabled, before M7 | No placeholder UI for unbuilt features |

## After the M4 spikes (2026-11-02)
| Item | Decision |
|---|---|
| Annotation display | S9's hybrid (annotation-core 3.3 option 4): a retained overlay with cached tessellation, a raster-cache layer over 100 strokes, highlights drawn with Multiply through Win2D `BlendEffect` using PDFium's pixel-grid rectangle fill, and the hidden-flag mirror for moved or deleted originals. The flag is always restored before a save. FreeText gets its own visual threshold |
| Printing | ADR-0011 accepted: a MikePDF print sheet, with GDI printing from the broker. The engine stays bitmap-only and keeps its sandbox |
| Save: file replace (S10) | Open documents with `FileShare.Read` and `FileShare.Delete`. Replace with a POSIX rename (`FileRenameInfoEx`, replace-if-exists) of the validated temp file, after copying the DACL, `Zone.Identifier` and other streams, attributes and creation time onto it, and clearing its Hidden attribute. Where the POSIX rename is unsupported (SMB), fall back to `ReplaceFileW` with a journalled backup name; at startup, recovery restores a missing target from that backup. Write a `SaveIntent` journal record before the temp file exists. Other processes may now rename or delete the open file's path but still can't write to it; that's an accepted trade-off. Files with more than one hard link, and EFS-encrypted files: Save is refused with an explanation and Save As is offered (v1.0). Symlinks: replace the final target |
| Page operations (S11) | Hybrid. PDFium in place for rotate, move, delete and insert blank, with the writer host fixing page labels and normalising xref-stream tails (`NormalizeTail`). The `PdfWriter` page assembler (`AssemblePages`) for extract, split, merge, insert from file and duplicate. PDFium import is never used for anything saved. The display shows the working state from PDFium; the file is produced by the assembler at save |
| Dead references after a delete | Removed: links and outline entries pointing to deleted pages are dropped, not left inert |
| Files the strict parser refuses (R-39) | In-place page operations still work through PDFium. Merge, split, extract and insert from such a file are unavailable, with an explanation |
| Tags through the assembler | Dropped in v1.0, with a warning before the save. Rebuilding them is v1.x |
| Insert from file | Not incremental: the result is a full rewrite by the assembler |
| `/DR` resource-name clashes in merges | Rename the clashing resources and rewrite their references |

The ADR-0005, ADR-0008 and spec amendments proposed in the S10 and S11 reports are applied by the save-pipeline and page-organiser work packages, as part of their first change. The S10 ones (ADR-0005 A3, ADR-0008 A1, save-pipeline 3.3 to 3.8, SP-16, SP-17, SP-38 to SP-41) were applied by W3-save-core on 2026-11-03; the S11 ones are the page-organiser work package's.

## After the S6b spike (2026-11-03)
| Item | Decision |
|---|---|
| Shell handler transport | ADR-0018 accepted: a native handler DLL, a per-user pipe to a Native AOT ShellHost started on demand, one sandboxed engine per file |
| Section source for `OpenDocument` (OQ-SHELL-13) | Not in v1.0. ShellHost stages to a temp file |
| Thumbnails when MikePDF isn't the default (OQ-SHELL-14) | Product owner to decide. Default until then: on, with a setting and a policy to turn them off |

The shell-extensions work package applies the ten spec corrections in the S6b report as part of its first change.

## After the print and shell reviews (2026-11-03)
| Item | Decision |
|---|---|
| PRINT-06, bands against a whole-page render | Amended: at most 0.3% of pixels may differ, none within 1 pixel of a band boundary, solid content identical. The cause is PDFium's float band offset (measured about 0.1%, on anti-aliased edges) |
| SSIM floor for printed pages | ADR-0011's 0.99 stays the target. 0.98 is accepted for shrunk text (custom scale below 100%) printed to Microsoft Print to PDF until the cause (suspected: the queue's image re-encoding) is isolated |
| Multiple pages per sheet | Offered in the print sheet: 2, 4, 6, 9 and 16 per sheet, as the pipeline already supports |
| Thumbnails when the handler is off (OQ-SHELL-14), measured in PR #14 | The shell doesn't fall through to another app's handler when ours declines: Explorer shows the generic icon. The settings text says so. Whether MikePDF's thumbnails should replace another default app's is still the product owner's call; default on |
| Native AOT toolchain for ShellHost | Accepted without a new ADR: the ILCompiler packages are part of the .NET SDK (MIT) and follow its patch version, so ShellHost has no lock file for them |
| Printing form field contents (PRINT-15) | A v1.0 blocker. The engine must draw widgets with `FPDF_FFLDraw` when `RenderBand` flag bit 1 is set. Goes to the next engine work package, before the print sheet ships |

## After the tabs review (2026-11-03)
| Item | Decision |
|---|---|
| Tab shortcuts (OQ-TAB-5) | Alt+1 to Alt+8 go to a tab and Alt+9 to the last, because Ctrl+1 and Ctrl+2 are fit page and fit width (master prompt 10.2) |
| Session restore default (OQ-TAB-1) | `Ask` after a crash; restore without asking after a normal close with more than one tab |
| Opening many files (Q12) | Confirm above 10 files, read at most 100. The spec's 25 is replaced |
| UI journeys and the shell handlers | `build/run-ui-tests.ps1` always registers a copy of the package without the Explorer handlers and COM servers, and refuses to run if any remain. Shell handlers are tested only on hosted runners |
| Test seams in shipped builds | Every `MIKEPDF_TEST_*` variable goes through one gate that ignores them in Store-signed packages |
