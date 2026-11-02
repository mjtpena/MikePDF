# S12: printing candidates, with ADR-0011

- Status: **Done on DEV only (x64, Windows 11 build 26300). Printed only to "Microsoft Print to PDF"; no physical, PCL, PostScript or duplex-capable printer was available, and no XPS writer queue exists on this machine. Verdicts are provisional.**
- Date: 2026-11-02
- Author: Sonnet. Reviewer: Opus.
- Target: [print](../features/print.md) 3.1 (criteria), OQ-PRINT-1, -2, -6, -7; decision Q1 in [design decisions](../design/decisions.md). Output: [ADR-0011](../adr/0011-printing.md) (Proposed).
- Code: `spikes/S12/` (throwaway; `run.ps1` runs everything). Raw results: `spikes/S12/out/` (gitignored; text and JSON results are force-added). Every number below comes from those files.
- Branch `m4/spikes-display-print`. `src/` is untouched. Every PDF the printer wrote was deleted after its run (the generated input fixtures stay in the gitignored `out/fixtures`).

## Questions

1. Which candidate best meets the criteria of print.md 3.1: preview; how ranges, copies, collate and duplex reach the driver; whether the printer's DPI can be requested; peak memory with many pages; ARM64; Native AOT; Narrator; effort?
   - (a) `PrintManager` / `PrintDocument` from a desktop app, and its status in Windows App SDK 2.5;
   - (b) Win32 `PrintDlgEx` or a custom print sheet, engine bands, sent by GDI or by `IPrintDocumentPackageTarget` (XPS);
   - (c) PDFium's `FPDF_RenderPage` into a printer DC inside the sandbox;
   - (d) generate a print-ready PDF and hand it to the system.
2. Prototype the likeliest candidate far enough to print three pages with a range, scaling and duplex settings, and measure its fidelity (SSIM against engine renders) and the time and memory of a 100-page job.

## Method

- **Environment.** Windows 11 build 26300 (`10.0.26300.0`), not admin, Intel Core Ultra 9 275HX (24 cores), 64 GB, .NET SDK 10.0.300, Windows App SDK 2.5.1, PDFium `chromium/8125` through P/Invoke. Printers present (`Get-Printer`): "Microsoft Print to PDF" (driver "Microsoft Print To PDF", port `PORTPROMPT:`), and two OneNote queues that were not used. **No XPS Document Writer queue exists.**
- **Safety.** Output went only to files named by `DOCINFO.lpszOutput` under `spikes/S12/out`; the default printer was not read for use or changed; no printer or driver was added; no window was shown (the print path is GDI; `info` creates and destroys one never-shown window to call `PrintManagerInterop.GetForWindow`).
- **Prototype (candidate b, GDI flavour).** `PrintJob.cs`: parse the range grammar of print.md 3.3 (and odd/even), get the driver's default `DEVMODE` through `DocumentProperties`, apply copies, collate, duplex, paper and orientation, let the driver merge and validate it (`DM_IN_BUFFER | DM_OUT_BUFFER`), create a printer DC (`CreateDC("WINSPOOL", ...)`), then per page: `PrintLayout.Place` (fit, shrink-only, actual, custom; auto-rotate and centre; displayed box), plan bands of `bandBytes / (4 x width)` rows (default 16 MiB), render each band with `FPDF_RenderPageBitmap` (flags `FPDF_ANNOT | FPDF_PRINTING`) on a worker while the previous band goes out with `StretchDIBits` (two buffers, as print.md 3.5). PDFium runs **in process** here, not in the sandboxed engine over the pipe, so the pipe and slot-copy cost is not in these numbers.
- **Fixtures** (generated, `Fixtures.cs`): `mix6.pdf` with six pages (Letter, A4, Letter landscape, A4 with `/Rotate 90`, Letter with a CropBox at (50, 30), a 30 x 40 inch poster) and `text100.pdf` with 100 Letter pages (48 lines of text, a 40-step gradient bar, a 120 x 120 raw image). Pages carry a frame at the crop edge, diagonals and a ruler so that cropping and scale errors show.
- **Fidelity.** Each output PDF is opened with PDFium and compared to the engine's own render of the source page **with the same placement**. Two methods: the one print.md PRINT-13 names (output at 150 dpi against source at 150 dpi), and one at the printer's own resolution (600 dpi, 1 device pixel = 1 image pixel) with both images box-filtered by 4 before SSIM, plus controls. SSIM is the same implementation as S9 (Wang et al. 2004, 11 x 11 Gaussian, luma; not cross-checked against a second implementation).
- **Cost.** One job per fresh process for the memory figures, so the baseline is clean. A sampler thread records the process's private bytes every 25 ms, and every 100 ms the private bytes of `spoolsv`, `PrintFilterPipelineSvc`, `PrintIsolationHost` and `splwow64` (the spooler side). Wall time and its parts (render, `StretchDIBits`, `StartDoc`, `EndDoc`) are measured with `Stopwatch`.
- **Sandbox probe (candidate c).** A small C# probe and a native C++ probe (same compiler flags as the S3b engine stub) were started in the **existing** per-user AppContainer profile `mikepdf.render` (zero capabilities; the profile was found, not created), with and without the engine's `SandboxPolicy` mitigations, and they tried the printing calls (see Results 3). One ACL grant was needed on `spikes/S12/out/probe` for that profile's SID so the container could run the probe and write its report; the folder is deleted afterwards. No other setting was changed.

## Results

### 1. Candidate (a): `PrintManager` and `PrintDocument` from a desktop app

What was established (details in `spikes/S12/out/s12-a-docs-evidence.txt` and `s12-a-winmd-evidence.txt`):

| Fact | Evidence |
|---|---|
| Microsoft documents it for WinUI 3 / Windows App SDK desktop apps: `PrintManagerInterop.GetForWindow(hWnd)`, `Microsoft.UI.Xaml.Printing.PrintDocument` (Paginate, GetPreviewPage, AddPages), and `await PrintManagerInterop.ShowPrintUIForWindowAsync(hWnd)`. Only the displayed screen can be registered; pages are XAML `UIElement`s; check `PrintManager.IsSupported()` first | [Print from your app](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/print-from-your-app) (canonical: [apps/develop/devices-sensors/print-from-your-app](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/print-from-your-app)), `ms.date` 2024-09-11, updated 2026-07-14 |
| The page does **not** mention a Windows App SDK 2.5 limitation; the Windows App SDK release-channels page (2026-10-27) lists no printing row and no statement about it in any channel. Stable is **2.6.4** (2026-10-27); ADR-0001 pins 2.5.1 | [Release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/stable-channel) |
| `Microsoft.UI.Xaml.Printing.PrintDocument` (and `PaginateEventArgs`, `GetPreviewPageEventArgs`, `AddPagesEventArgs`) **ship in the metadata of the WinUI package that 2.5.1 depends on** (WinUI 2.3.9) | string scan of `Microsoft.UI.Xaml.winmd`: this proves the types exist, not that they work |
| An open issue shows it was unreliable: microsoft-ui-xaml #4419 "Printing in WinUI 3 Desktop", opened 2021-03-04, still open at 2025-11-20. Microsoft said in 2021 that `PrintDocument` "hasn't been updated for WinUI3 yet"; 2025 comments say it works in **full-trust** apps and **not in partial-trust** ones, and one user reports on Windows 10 build 19044 that the dialog appears and nothing prints | [issue](https://github.com/microsoft/microsoft-ui-xaml/issues/4419) (summaries of comments, not verified by me) |
| In an unpackaged console process, **`PrintManager.IsSupported()` returned True**, and `PrintManagerInterop.GetForWindow(hwnd)` returned a `PrintManager` for a created-but-never-shown window | `s12-info.txt`. `ShowPrintUIForWindowAsync` was **not** called (it shows UI) |

**The runtime behaviour of candidate (a) is UNVERIFIED**: the flyout, preview, pagination, memory per XAML page and the Paginate/AddPages events all need a visible window. The facts support: it is documented and available in 2.5.1 for full-trust (MikePDF is packaged full trust), not that it works well for 1,000 image pages. Known design fits: it gives a system preview and the system's options but, for MikePDF, annotation, form-field and print-as-image options would live in a custom area of the flyout, a bounded UI the design (Q1) rejects.

### 2. Candidate (b): custom print sheet and GDI, prototyped

**What the driver accepted** (`DocumentProperties` merge; `s12-info.txt`, `s12-jobs.txt`):

| Setting | Request | After the driver's merge | Reached the output? |
|---|---|---|---|
| Paper | A4 (9) or Letter (1) | kept | Yes: output pages are 595.3 x 841.9 pt (A4) and 612 x 792 pt (Letter) |
| Copies 2, collate | `dmCopies` 2, `dmCollate` 1 | kept (the capability call says max 1) | **Yes: the output has 6 pages for a 3-page range** (order 1,3,5,1,3,5 checked by SSIM against the right source page) |
| Duplex long or short edge | `dmDuplex` 2 or 3 | **cleared to 1 (simplex)** | Not applicable: `DC_DUPLEX` = 0, the PrintTicket capabilities list only `PageMediaSize`, `PageOrientation`, `PageResolution`, `PageOutputColor`. **The duplex path to a duplex printer is UNVERIFIED**; what is shown is that the request goes to the driver and the driver drops what it cannot do |
| Resolution | not settable | 600 x 600 only (`DC_ENUMRESOLUTIONS`) | The DC is 600 dpi |
| Range, odd/even, scaling, auto-rotate | MikePDF logic | n/a | Yes: only the chosen pages are sent; see the table below |

**Layout and geometry** (placements recorded per page): Fit and Shrink centre the page; Actual size at 600 dpi gives 8.3333 device px per point; custom 50% gives 4.1667; a landscape page on portrait paper is rotated 90 degrees clockwise and centred when auto-rotate is on, and not rotated (smaller) when it is off. Examples: Letter on A4 with Fit is placed at (0, 298) with size 4961 x 6420; Letter on A4 with Actual would be clipped (the layout reports it). Ranges: `1`, `1-3`, `5-`, `-5`, `1-3,5,8-` and spaces parse as specified; `a`, `3-1`, `-`, `1,,2`, `0`, `11` and `1-11` on a 10-page document are rejected with the right reason; odd/even filtering is applied after the range (`s12-layout-tests.txt`).

**Three-page jobs** (all with the spike's own code; `s12-jobs.txt`):

| Job | Options | Pages out | Wall | Own render + send | Output |
|---|---|---|---|---|---|
| A | range 2-4 of 6, Fit, A4, duplex long edge | 3 | 6.2 s | 383 + 372 ms | 9,002 KiB |
| B | `1,3,5-` odd only, Actual size, Letter, duplex short edge, 2 copies collated | 6 | 6.5 s | 293 + 280 ms | 8,733 KiB |
| C | 1-3, custom 50%, no auto-rotate, A4 | 3 | 2.2 s | 77 + 77 ms | 3,375 KiB |
| D | 2-4, Fit, A4, print as image (one bitmap per page, at most 300 dpi) | 3 | 2.4 s | 73 + 67 ms | 3,375 KiB |
| E | 1-3, Shrink, Letter, auto-rotate on (page 3 is landscape) | 3 | 5.2 s | 309 + 279 ms | 8,759 KiB |

Most of the wall time is the printer: `EndDoc` alone took 1.0 to 2.9 s for three pages.

**Fidelity** (SSIM of the printed PDF against the engine's render of the same page with the same placement):

| Job, page | print.md PRINT-13 method: both at 150 dpi | at 600 dpi, both 4x box-filtered | **control: no printing**, the engine at 150 dpi against the engine at 600 dpi box-filtered |
|---|---|---|---|
| A p1, p2, p3 | 0.960, 0.951, 0.956 | **0.996, 0.995, 0.993** | 0.972, 0.950, 0.967 |
| B p1, p2, p3 | 0.958, 0.966, 0.960 | 0.996, 0.995, 0.997 | 0.977, 0.985, 0.978 |
| C p1, p2, p3 (50%) | 0.932, 0.950, 0.969 | 0.999, 0.997, 0.999 | 0.952, 0.971, 0.978 |
| D p1, p2 (as image, 300 dpi) | 0.980, 0.965 | not measured | not measured |
| E p1, p2, p3 | 0.958, 0.952, 0.966 | 0.996, 0.997, 0.995 | 0.977, 0.966, 0.985 |

- **The PRINT-13 method cannot reach 0.98 on text pages even with no printing at all** (control: 0.95 to 0.985): a 600 dpi render reduced to 150 dpi is not the same image as a render made at 150 dpi. The threshold of 0.98 is "untested" in print.md; it should be defined on the printer's own resolution and a box filter (above: 0.993 to 0.999).
- **The output is not pixel-identical**: at 600 dpi about 20% of the pixels differ (mean absolute difference 1.7 to 2.5 of 255, maximum 254, 6 to 7% for the half-size pages), with no whole-pixel shift giving a better match. The printer's output file shows why: `Microsoft: Print To PDF` writes the bands as **JPEG (`/DCTDecode`) images**, 8 or 9 per page (one per band). That is the driver's choice for this queue, not MikePDF's; for a real printer it will differ.
- The printed pages are images: **0 text characters** in every raster output.
- Placement is correct for Letter, A4, landscape (rotated), `/Rotate 90` and a non-origin CropBox (page 5 in job B, frame intact at the crop edge); one output page was viewed.

**Cost of a 100-page job** (`text100.pdf`, A4, Fit, 600 dpi device; one fresh process per row; `s12-perfone-*.txt`):

| Variant | Pages | Wall | Own render | Own `StretchDIBits` | `EndDoc` | Own process private bytes: base to peak | `PrintFilterPipelineSvc` peak | Output |
|---|---|---|---|---|---|---|---|---|
| 16 MiB bands | 1 | 2.0 s | 87 ms | 121 ms | 0.6 s | 13 to **137 MiB** | 108 MiB | 3.0 MiB |
| 16 MiB bands | 10 | 16.0 s | 871 ms | 896 ms | 5.9 s | 13 to **148 MiB** | 134 MiB | 29.0 MiB |
| 16 MiB bands | **100** | **377.6 s** | 9.7 s | 9.9 s | 258.1 s | 13 to **152 MiB** | **410 MiB** | 290.2 MiB |
| whole-page bitmap (no banding) | 10 | 15.2 s | 887 ms | 779 ms | 5.2 s | 14 to **155 MiB** | 294 MiB | 29.8 MiB |
| 4 MiB bands | 10 | 24.2 s | 857 ms | 958 ms | 6.6 s | 14 to **273 MiB** | 125 MiB | 29.2 MiB |
| print as image, 300 dpi | 100 | 127.6 s | 4.9 s | 4.3 s | 62.6 s | 13 to **65 MiB** | 243 MiB | 112.4 MiB |
| vector, `FPDF_RenderPage` into the DC (not sandboxable, see 3) | 10 | 13.0 s | 1.4 s | n/a | 6.4 s | 14 to **37 MiB** | 158 MiB | 2.5 MiB |
| vector | 100 | 169.2 s | 14.0 s | n/a | 102.4 s | 13 to **50 MiB** | **1,543 MiB** | 24.5 MiB |

- **MikePDF's own part is small**: render plus send was about 20 s of the 378 s for 100 pages (0.2 s per page). The rest is the Print To PDF queue converting 12 GiB of 600 dpi raster, and it is superlinear: 1.6 s per page at 10 pages, 3.8 s per page at 100. **A physical printer will behave differently; no physical printer was measured.**
- **Own memory does not grow with the page count** (137, 148, 152 MiB for 1, 10, 100 pages: +3% from 10 to 100, inside PRINT-10's 5%). **But banding does not cap it**: whole-page and 16 MiB bands peak at the same level, and 4 MiB bands peak higher (273 MiB with 310 band calls). The peak is about one page of device pixels (A4 at 600 dpi is 34.8 Mpixels, 139 MiB) and at 300 dpi it is a quarter of that (65 MiB). The cause is not established; it is consistent with GDI buffering each page's records until `EndPage`. **The cap that works is the page's pixel count, that is the dpi.**
- **The spooler side is not flat**: `PrintFilterPipelineSvc` peaked at 108, 134 and 410 MiB for 1, 10 and 100 pages in the raster path and 1,543 MiB for 100 vector pages. **A 1,000-page job was not run** (it would be about an hour and 3 GB of output here); flatness at 1,000 pages is UNVERIFIED, and the spooler side suggests it may not hold.
- Wall times vary from run to run (Job A took 5.5 s, 6.2 s and, in the first Native AOT run, 11.2 s, mostly in `EndDoc`); page counts and SSIM values were identical between the JIT and AOT runs.

**Cancel** (`s12-cancel.txt`): `AbortDoc` after three of 100 pages returned in **7.5 ms**; the print queue was empty afterwards. **A 0-byte output file was left behind** and must be deleted by MikePDF.

**Native AOT**: the spike (GDI, winspool, prntvpt, the `DOCINFO` string marshalling, `UnmanagedCallersOnly`) publishes with `PublishAot=true` to a 5 MB exe, and `jobs` on that exe gave the same page counts and SSIM values. Only the spike's reflection JSON writer failed under AOT. This needs `vswhere.exe` on `PATH` for the link step (see S6).

**XPS flavour of (b), `IPrintDocumentPackageTarget`** (`s12-xps-probe.txt`): `CoCreateInstance` of the factory, `CreateDocumentPackageTargetForPrintJob("Microsoft Print to PDF", ..., a file stream)` and `GetPackageTargetTypes` all returned `S_OK` from an unpackaged process, and it reports two package target types (GUIDs `9cae40a8-...` and `0056bb72-...`, which I believe are Microsoft XPS and OpenXPS per `printdocumentpackage.h`; not re-checked). The job was cancelled before any page; **no XPS page was authored or sent, so its fidelity, memory and speed are UNVERIFIED.** It carries a Print Ticket instead of a `DEVMODE`.

**PrintDlgEx** and `DocumentProperties(DM_PROMPT)` (the driver's own preferences dialog) are visible dialogs and were not run. With the MikePDF print sheet (Q1) only the second would be used, from a "Printer preferences..." link.

### 3. Candidate (c): `FPDF_RenderPage` into a printer DC, in the sandbox

`FPDF_RenderPage(HDC, ...)` exists, is Windows-only, and "Render[s] contents of a page to a device (screen, bitmap, or printer)" (`fpdfview.h`, pinned header); `FPDF_SetPrintMode` selects EMF, text-only or PostScript output. **Unsandboxed, it works and is much better than raster** (comparison above): for the same three pages, 678 KiB against 9,002 KiB, 3.5 s against 5.3 s, 30 MiB against 162 MiB of own memory, SSIM at 600 dpi boxed 0.998 against 0.994 to 0.996 (the page is vector paths, 2,000 to 2,600 path objects and one image per page, with text turned into paths: 0 text characters).

**Why the print.md assumption "the engine has no printer access" is only partly right** (`s12-appcontainer-probe.txt`, `s12-appcontainer-bisect.txt`):

| Where the probe ran | `OpenPrinter` / `DocumentProperties` / `CreateDC` / `StartDoc` | Complete job to a folder the container may write | Complete job to a folder it may **not** write |
|---|---|---|---|
| Unsandboxed (C# and C++) | allowed | file written | file written |
| **Zero-capability AppContainer** (profile `mikepdf.render`), C# and C++ | **allowed** | **file written** | **`StartDoc` denied (win32 5), no file** |
| Same plus every `SandboxPolicy` creation mitigation **except** prohibit-dynamic-code, plus restricted child processes (C++) | allowed | file written | denied |
| Same plus the runtime Microsoft-signed-only policy applied before the printing calls (C++), inbox driver | allowed | file written | denied |
| **The engine's real profile: all of the above including prohibit-dynamic-code** (C++) | `OpenPrinter` allowed, then the process **dies with `0xC000041D`** | no | no |

- So an AppContainer **can** reach the spooler, and the spooler honours the caller's file rights (the path the container could not write was refused: no sandbox-escape through `lpszOutput` in the one case tested). What stops (c) in the engine as designed is **prohibit-dynamic-code at creation (ADR-0004 decision 2)**: the printing path crashes the process right after `OpenPrinter`. Bisecting each creation mitigation alone showed that **only prohibit-dynamic-code** has this effect (and only the restricted-child-process policy with a `CREATE_NO_WINDOW` start, which is a launcher artefact: it starts fine with `DETACHED_PROCESS`, which the engine uses).
- Other reasons not tested: printing loads the printer driver's DLLs into the calling process (a vendor driver that is not Microsoft-signed would be refused by the signed-only policy; only the inbox driver was tried), and a process that parses untrusted PDFs would hold a channel to the spooler.
- **Conclusion for the ADR**: vector printing is not possible from the **engine process** as specified. A **separate print host** (a second sandbox profile without prohibit-dynamic-code) would work for the inbox driver on this evidence, but it needs its own negative-test suite (the S3 method) and vendor drivers have not been tried. That is a v1.x decision, not a v1.0 one.

### 4. Candidate (d): a print-ready PDF handed to the system

- **No print verb on this machine.** The default `.pdf` handler is Microsoft Edge (`MSEdgePDF`, only an `open` verb: `msedge.exe --single-argument %1`); `print` and `printto` are **not registered** (`s12-d-pdf-handler-verbs.txt`; Acrobat and SumatraPDF are not installed). A `ShellExecute` "print" would therefore fail with no association here, and where a handler has the verb it prints with **that application's** renderer and options (not called: it would start another application).
- **Privacy**: the document (and any unsaved edits, flattened) leaves MikePDF as a file in a place another process can read, and is opened by a handler with its own telemetry and cloud features. Design principle 4 in §1.2 is "Private by default": documents do not leave the device's control unless the user chooses it, and a handler application is outside what MikePDF can promise (the handler's own behaviour was not examined).
- **Fidelity**: a different engine renders it (Edge, Acrobat), so it need not match the screen; MikePDF cannot show an honest preview; form-field and annotation options need a flattened copy produced by the save pipeline first (stages 1 to 4, [print](../features/print.md) 3.9).
- **Settings**: range, duplex, scaling and copies reach the handler only through its own command line or dialog, if at all; the verbs `print` and `printto` carry no standard options. This fails the criterion on ranges and duplex.
- UNVERIFIED: the Windows 11 native PDF print path to PDF-capable printers (IPP/Mopria) was not examined.

## Surprises

1. **The printing APIs work inside a zero-capability AppContainer**; what blocks vector printing in the engine is prohibit-dynamic-code, not the lack of a spooler channel. The spooler enforced the container's file rights on `lpszOutput`.
2. **Banding does not cap memory in the GDI path.** The peak is about one device-resolution page and a smaller band raised it. The effective knob is dpi.
3. **Print To PDF re-encodes bands as JPEG**, so a byte-exact test of the raster path is impossible there; and it is slow and superlinear (3.8 s per page at 100 pages; 258 s inside `EndDoc`).
4. **The 0.98 SSIM threshold in PRINT-13 is unreachable by its own method** on text pages (the control with no printing scores 0.95 to 0.985).
5. **The 16 MiB band gives 8 to 9 bands per A4 page at 600 dpi**, not "about 6" as print.md 3.5 says (26 bands for 3 pages, 800 for 100).
6. The driver honoured `dmCopies` (6 pages from a 3-page range, collated) although `DC_COPIES` reports 1, and cleared duplex.
7. Cancel leaves a 0-byte file.
8. Windows App SDK stable is already 2.6.4 while ADR-0001 pins 2.5.1.

## Recommendation

**Candidate (b): a MikePDF print sheet (decision Q1) and GDI printing from the broker**, with these changes to the specs. Details are in [ADR-0011](../adr/0011-printing.md).

1. The print sheet is ours (preview from engine renders at a low dpi, every option in one place). The printer, its capabilities and its settings come from the spooler: `EnumPrinters`, `DeviceCapabilities` and `prntvpt` capabilities decide which options to show (duplex is hidden or disabled when `DC_DUPLEX` is 0; the PrintTicket feature list is the same truth in another form). "Printer preferences..." calls `DocumentProperties` with `DM_PROMPT`.
2. Output: engine bands to the broker, `StretchDIBits` per band, on a worker thread; one printer DC and one `StartDoc` per job; `lpszOutput` is never set in production (it was here to avoid the file prompt of the PDF queue).
3. **Cap pixels per page, not band bytes**: choose the device dpi as `min(printer dpi, 600)` and reduce it for large pages so that a page is at most about 40 Mpixels (A4 at 600 dpi is 35); "print as image" at 300 dpi; low-quality permission at 150 dpi. Rewrite print.md 3.5 and PRINT-10 accordingly (the page-count independence holds; the band cap does not bound memory).
4. Redefine PRINT-13: SSIM at the printer's resolution, box-filtered, **>= 0.99** for the Print To PDF cell (measured 0.993 to 0.999), with the 150 dpi comparison only as a control. It is not a proof for text-heavy or scanned pages beyond the fixtures.
5. Expect Print To PDF to be slow: the 1,000-page matrix cell cannot be a routine test; run it at 150 dpi image mode and mark the 600 dpi 1,000-page run manual.
6. Delete the 0-byte file that `AbortDoc` leaves; cancel stops the queue in milliseconds.
7. Keep `IPrintDocumentPackageTarget` (XPS) as the fallback when GDI misbehaves with a driver (it is available and carries a Print Ticket), and a separate vector print host as a v1.x option (the 12 times smaller output and 4 times lower own memory are large wins, but the engine cannot do it).

### Criteria of print.md 3.1 against the candidates

| Criterion | (a) PrintManager | (b) GDI print sheet | (b) XPS target | (c) vector in sandbox | (d) hand off PDF |
|---|---|---|---|---|---|
| Preview | System flyout (documented; **not run**) | Ours, from engine renders (not built here) | Ours | Ours | None under our control |
| Ranges | System UI, app honours them in `AddPages` (not run) | Ours; **verified** (3 pages from ranges and odd/even) | Ours | Ours | Handler only |
| Copies, collate | System | **Verified**: `dmCopies` 2 gave 6 pages collated | Print Ticket (not run) | n/a | Handler |
| Duplex | System | `DEVMODE` reaches the driver; **driver cleared it** (Print To PDF has no duplex); unverified on a duplex printer | Print Ticket (not run) | n/a | Handler |
| Printer dpi | `PrintPageDescription` (documented, not run) | **Read from the DC (600)**, cannot be requested on this queue | Print Ticket resolution | DC | Handler |
| Memory, many pages | XAML pages, **not measured** | Own 137 to 152 MiB, flat from 1 to 100 pages; spooler 108 to 410 MiB and growing | **not measured** | Own 37 to 50 MiB; spooler 1,543 MiB at 100 pages | Not ours |
| ARM64 | UNVERIFIED | UNVERIFIED (class drivers only) | UNVERIFIED | UNVERIFIED | UNVERIFIED |
| Native AOT | UNVERIFIED | **Works** (spike published and run) | UNVERIFIED | n/a | n/a |
| Narrator | System UI, **not run** | Our sheet (needs a pass); driver dialog is the driver's | Same | n/a | Handler |
| Effort | Low to medium; XAML per page | Medium (built here to a spike level: about 600 lines) | High (XPS OM authoring) | High (new host, new sandbox) | Low |

## Go / No-go

- **Go** for (b) GDI with the MikePDF print sheet as the M3 print path. **No-go** on (c) in the engine; **no-go** on (d) as the normal path (fails privacy, preview, and settings). **(a)** not rejected on facts, but not chosen: it cannot host the Q1 sheet and its runtime behaviour is unverified.
- **Not a gate**: the PCL, PostScript, duplex and ARM64 cells of the test matrix, which need hardware and the user (OQ-PRINT-8, -9).

## Unverified

- Everything on a **physical printer**: PCL, PostScript, a duplex printer, a printer with a vendor (v3) driver, a network printer; duplex and collate to a real driver; paper trays; the driver's own preferences dialog.
- The **XPS Document Writer** (not installed here: OQ-PRINT-7 is answered for this machine; Windows 11 24H2-and-later machines may lack it), and **authoring and sending XPS pages**.
- **Candidate (a) at runtime**: the system flyout, preview, pagination, accessibility, memory per XAML page. Only API availability was checked.
- **Narrator** and any accessibility of the system or driver dialogs. **ARM64** (no ARM64 hardware here).
- A **1,000-page** job; page sizes of 14,400 pt; very large pages at 600 dpi (a 30 x 40 inch poster was in the fixture but not printed); 1,200 dpi printers; scans (JBIG2, JPX); encrypted documents and the 150 dpi permission cap.
- **The sandboxed engine over the pipe**: render and slot-copy costs per band (the spike calls PDFium in process). S2 measured about 0.1 ms transport per small tile; a 16 MiB slot copy is not measured.
- **Per-page paper size change** with `ResetDC` (print.md says unsupported in v1; not tried).
- The probe's findings for **vendor drivers** and for a signed-only policy with them; and a **negative test suite** for a hypothetical print host.
- **Cause of the memory behaviour** (EMF buffering is a hypothesis) and of the superlinear time (spooler side).
- The same SSIM-implementation caveat as S9.

## Open questions for Opus

1. OQ-PRINT-2: accept (b) GDI + MikePDF sheet (ADR-0011), and the changes to print.md 3.5, PRINT-10 and PRINT-13 above?
2. Is a **300 dpi default for large pages** acceptable, or should the 600 dpi cap be a setting? The 35 Mpixel page is the largest that kept our process near 150 MiB.
3. Should an **XPS spike** (author a page with `IXpsOMObjectFactory`, send it, measure) be scheduled before M3 print work starts, or only when a driver problem shows up?
4. Is a separate **vector print host** worth an ADR now, given its 12 times smaller output and the 1.5 GB spooler peak that came with it at 100 pages?
5. The spike found `StartDoc` honours the caller's file rights from an AppContainer in one case. Does the threat model need an explicit note that printing from a sandbox is not a file-write primitive (one test, one path, one printer)?
