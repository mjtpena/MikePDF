# Feature spec: stamps (built-in set and image stamps)

- Status: Draft for Opus review
- Milestone: M4
- Master prompt: §2.1 (stamps: built-in set plus image stamps), §8.2, §13 (untrusted input), §16 (no brand names in the UI), §12.4
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [annotation-core](annotation-core.md), [save-pipeline](save-pipeline.md) (edit blob, writer host), a new writer-host message for image import (section 10)
- Related: [free-text](free-text.md) (same rotated-page rule), [settings-policies](settings-policies.md) (`stamps.library`)
- Not in this spec: Fill and Sign handwritten signature images (P1) and cryptographic signing (P1). Stamps are decoration, not signatures.

Conventions are in [README.md](README.md). Shared rules are in annotation-core.

## 1. What S4 found

S4 A13a: a PDFium stamp with only a name has **no appearance, and draws nothing in any viewer**, because PDFium has no default artwork and no objects to build one from. A13b: with drawn objects it was visible, but `/Name` was saved as a **string**, not a name object (F3). A14: an image stamp via PDFium drew in PDFium and pdf.js. Q05, MikePDF-authored with `/Name /Approved` as a name object and its own appearance, passed every check. Acrobat and Edge were **not measured**. So MikePDF draws every stamp itself.

## 2. User stories

1. As a manager, I stamp a contract "Approved" or "Confidential" and it looks the same in every reader.
2. As someone with a company logo or a scanned initial, I import the image once and place it on any document.
3. As anyone, I move, resize (keeping proportions), rotate in quarter turns and delete stamps, with undo.
4. As a keyboard-only user, I choose and place a stamp without a mouse.
5. As a privacy-minded user, an imported photo doesn't carry its GPS position into the PDF.

## 3. Detailed behaviour

### 3.1 The built-in set

The 14 standard stamp names of ISO 32000 (rubber stamp annotation): Approved, Experimental, NotApproved, AsIs, Expired, NotForPublicRelease, Confidential, Final, Sold, Departmental, ForComment, TopSecret, Draft, ForPublicRelease. `/Name` is written as a **name object** with exactly this spelling.

MikePDF's own artwork for each: a rounded rectangle outline with the stamp text in bold capital letters (Helvetica-Bold, from the shared AFM metrics), in one of three colours: green (Approved, Final, ForPublicRelease), red (NotApproved, Expired, NotForPublicRelease, Confidential, TopSecret, Void-like ones), blue (the rest). Text is English in the file whatever the UI language, matching how other readers show these standard names (OPEN QUESTION OQ-ST-4). The artwork is original; it doesn't copy another product's stamps and uses no brand names (§16). Native design size is computed from the text width plus padding (about 150 x 40 pt); the user can resize.

An optional line with the author and date under the text ("by Alex, 2 Nov 2026") is a candidate property (OQ-ST-1). It is not specified further here; the file would carry it as fixed text.

### 3.2 Image stamps

**Importing.** Stamps panel > Add image stamp > file picker (PNG, JPEG, BMP, GIF; the first frame of a GIF). Limits: file at most 20 MiB; dimensions at most 8,192 x 8,192 and at most 50 megapixels; anything else is refused before decoding with a message. Import happens in a sandboxed process, not in the UI (section 3.3).

**Normalising.** The imported image is converted to a MikePDF stamp asset (`.mks`): metadata (EXIF, GPS, XMP, ICC profiles, comments) is **dropped**; EXIF orientation is applied; 16-bit and CMYK are converted to 8-bit RGB; the longest side is downsampled to at most 2,048 px; images with transparency keep an 8-bit alpha channel. Opaque images are stored as JPEG (quality 90) and images with transparency as lossless RGB plus alpha (both deflate-compressed in the PDF). OPEN QUESTION OQ-ST-3: JPEG re-encoding is lossy; the alternative is lossless for everything (larger files, 4 to 8 MB for a photo). A line of text shows the estimated size added to the file ("This image adds about 300 KB.") before placing.

**Library.** Kept in `LocalState\private\stamps\` (one `.mks` per stamp plus an index), at most 50 stamps and 20 MiB in total. A stamp has a user-chosen name (up to 60 characters) and optional alternative text. Add, rename, delete. A stamp already placed in documents is not affected by deleting it from the library (the pixels are in the document).

### 3.3 Where decoding happens

Decoding an image file is untrusted parsing, and the UI process does not parse untrusted files (§0.2 rule 6, §5.1). Image import is a job for the writer host: the broker opens the chosen file read-only and passes the handle; the host decodes with the system imaging codecs (WIC) inside its sandbox and returns the normalised asset through a broker-provided output handle. The UI only ever sees the normalised bitmap it asked for (its own format), never a codec. Whether WIC runs inside the writer host's AppContainer with the Microsoft-signed-code policy (system codecs are Microsoft-signed) is **UNVERIFIED** and is the first task of this feature; if it fails, the options are a purpose-built decoder (a new parser to fuzz) or a dedicated image-import sandbox process. OPEN QUESTION OQ-ST-2.

### 3.4 Placing

1. Choose a stamp from the gallery flyout (built-ins first, then the library). The pointer carries a translucent preview.
2. **Click** to place at the default size: the stamp's native size scaled down to fit a 200 x 100 pt box, proportions kept. **Drag** a rectangle to place at that size (proportions kept, fitted inside the dragged box).
3. The stamp is then selected. `annot.newStamp` places the chosen stamp at the top-left of the visible page area for keyboard use.
4. The tool returns to `Select` unless `markup.keepToolActive`.

**Edit.** Move, resize with corner and edge handles (proportions locked; **Shift** unlocks), nudge with arrow keys, rotate in 90-degree steps with `stamp.rotateRight` and `stamp.rotateLeft`, opacity 10% to 100%, delete. Free rotation is not in v1 (OPEN QUESTION OQ-ST-5). A resize or move changes only `/Rect` (the appearance form scales to it) so the pixels are not re-encoded.

### 3.5 Rotated pages

As [free-text](free-text.md) section 3.6: the appearance's `/Matrix` counter-rotates the page's `/Rotate` plus any user rotation, and `/Rect` is the enclosing unrotated-space box, so the stamp appears upright in the displayed page. Fixtures for all four rotations.

### 3.6 Appearance and objects

- **Built-in stamp:** a form XObject with the path drawing and text; own `/Resources` with the Helvetica-Bold font dictionary (WinAnsi); `/BBox` the native size; `/Matrix` per 3.5. `/Name` as a name object, `/Rect`, `/CA`, `/F 4`, common keys, `/Subj` the stamp's English name.
- **Image stamp:** one image XObject per distinct asset in the document (`/Subtype /Image`, `/ColorSpace /DeviceRGB`, `/BitsPerComponent 8`, `/Filter /DCTDecode` or `/FlateDecode`, `/SMask` image for alpha), shared by every placement of the same asset in one save; the stamp's form XObject draws it (`q w 0 0 h 0 0 cm /Im0 Do Q`). `/Name` is the name object `/Image` (the standard allows any name when an appearance is present, and no reader has artwork for it, so none will substitute its own; OPEN QUESTION OQ-ST-7 asks whether another reader treats an unknown `/Name` badly); `/Subj` is the user's stamp name; alternative text goes in `/Contents`.
- **Dedupe across a document:** two placements of the same asset in one save produce one image object. Across saves, a later save adds a new image object (the old stays in the file); that is accepted.
- **Size guard:** a save that would add more than 50 MB of stamp images is refused with a message (proposed).

## 4. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Open the stamp gallery | none | `tool.stamp` |
| Place the chosen stamp at the current position | none | `annot.newStamp` |
| Rotate stamp right / left | none | `stamp.rotateRight`, `stamp.rotateLeft` |
| Manage stamp library | none | `stamp.manage` |

Keyboard use: `tool.stamp` opens the gallery (a grid with arrow-key navigation, Enter selects), then `annot.newStamp` places it; arrows nudge, Shift+arrows move 10 pt, handles via the properties pane's numeric size fields. Pen and touch: tap to place, drag to size; handles are 24 DIP minimum.

## 5. Accessibility

- The gallery is a grid with items named "{stamp name} stamp" (for built-ins the localised name, for images the user's name and alternative text). Selected state and keyboard focus are visible and exposed.
- A placed stamp: UIA localized control type "stamp", name "stamp: {name} by {author}", `Value` pattern for the alternative text. Importing asks for optional alternative text ("Describe this image") and says that screen reader users will hear it.
- Built-in stamps contain text in the page content layer of the appearance; a screen reader reads the annotation name, not the artwork.
- Announcements: "Approved stamp added on page 2", "Stamp rotated", "Stamp deleted".
- Contrast of the three stamp colours against white is at least 4.5:1 (checked by test); the UI preview doesn't rely on colour to name the stamp.

## 6. Error states

| Situation | Behaviour |
|---|---|
| File too large, too many pixels or not an image | Refused before decoding: "That image can't be used as a stamp." with the reason. |
| Decoder fails or times out (10 s) | "MikePDF couldn't read that image." Nothing is added. |
| Library full (50 or 20 MiB) | "Your stamp library is full. Remove a stamp to add another." |
| Import host crash | The host is restarted by the broker; the import fails with the message above; the file is not retained. |
| Save would add > 50 MB of images | Refused with a message naming the size. |
| Permission bit 6 clear | Stamp tools disabled with reason. |

## 7. Edge cases

- **Image shapes:** 1 x 1 pixel, 1:100 aspect, 8,192 x 8,192, 1-bit and palette images, animated GIF (first frame), interlaced PNG, PNG with `tRNS` transparency, JPEG with EXIF orientation 1 to 8, CMYK JPEG, 16-bit PNG, progressive JPEG, truncated files, files with trailing garbage: each has a fixture.
- **Decompression bombs:** a small file claiming huge dimensions is refused by the pre-check (dimensions read from the header by the host's decoder wrapper before allocating); memory for decode is capped at 512 MB in the host's Job Object.
- **Transparency:** semi-transparent pixels are kept; the opacity property multiplies.
- **Rotated pages, CropBox offsets, facing views:** as above; clamps to the displayed box.
- **Foreign stamps** (Acrobat's dynamic stamps, custom stamps with arbitrary appearances): drawn by PDFium; move, delete, reply allowed; appearance bytes untouched.
- **Stamp name collisions:** library names need not be unique; the id is a GUID.
- **Loaded owned stamps from an earlier session:** recognised by `/NM` prefix; built-ins by `/Name` and the appearance's recorded marker; image stamps are movable and resizable (a rect change); restyling an image's pixels is not possible.
- **Huge documents and many stamps:** 1,000 placements of one asset share one image object.
- **Encrypted and signed documents:** annotation-core 3.8.
- **Hostile input:** a loaded stamp with a 200 MB image: PDFium renders it within its own limits in the sandboxed engine; MikePDF only lists it; a loaded `/Name` of 1 MB is cut at 64 bytes.

## 8. Undo and redo

Place, move, resize, rotate, opacity, delete: one command each. Importing to the library is **not** an undoable document edit (it changes the library, not the document); deleting a library stamp asks for confirmation. Rules in annotation-core section 8.

## 9. Journal entries

`AddAnnotation` `kind: "Stamp"`, `payload: { "name": "Approved" }` for built-ins, or `{ "image": "<sha256>", "stampName": "...", "altText": "..." }` for images. For an image the journal stores the normalised asset **once**, content-addressed, in the journal's blob folder (`journals\<docKey>.blobs\<sha256>.mks`), so recovery works even if the library entry has been deleted. `UpdateAnnotation` (opacity, rotation), `MoveAnnotation`, `ResizeAnnotation`, `DeleteAnnotation`. Image bytes are user-added content and are in the privacy notes; they are deleted with the journal.

## 10. Save behaviour and writer-host messages

Stamps are written by the writer host in stage 4 as in 3.6. A new writer-host message imports images:

**`ImportStampImage` (1027), `MikePDF.WriterHost` only.**

| Request field | Type | Limits |
|---|---|---|
| `fileHandle` | u64 | min 1; read-only handle duplicated into the host |
| `outputHandle` | u64 | min 1; write handle for the `.mks` asset |
| `maxDimension` | u32 | 64 to 4096 (the broker passes 2048) |

| Reply field | Type | Limits |
|---|---|---|
| `width`, `height` | u32 | 1 to 4096 |
| `hasAlpha` | bool | |
| `encodedBytes` | u64 | max 33554432 (32 MiB) |

Statuses: `InvalidArgument` (too large, bad dimensions), `Unsupported` (format), `Damaged` (undecodable), `Timeout`. The host enforces the file-size, dimension and pixel limits of 3.2 itself. The broker validates the output file's header (`.mks` magic, dimensions equal to the reply, size equal to `encodedBytes`) before using it.

The save-time use of an asset (building the image XObject from the `.mks`) is in the `AppendAnnotations` edit blob ([save-pipeline](save-pipeline.md) section 11).

## 11. Localisation strings

| Key | en-US |
|---|---|
| `Stamp_Tool` | Stamp |
| `Stamp_Gallery_Name` | Stamps |
| `Stamp_Automation` | {0} stamp |
| `Stamp_Std_Approved` | Approved |
| `Stamp_Std_Experimental` | Experimental |
| `Stamp_Std_NotApproved` | Not approved |
| `Stamp_Std_AsIs` | As is |
| `Stamp_Std_Expired` | Expired |
| `Stamp_Std_NotForPublicRelease` | Not for public release |
| `Stamp_Std_Confidential` | Confidential |
| `Stamp_Std_Final` | Final |
| `Stamp_Std_Sold` | Sold |
| `Stamp_Std_Departmental` | Departmental |
| `Stamp_Std_ForComment` | For comment |
| `Stamp_Std_TopSecret` | Top secret |
| `Stamp_Std_Draft` | Draft |
| `Stamp_Std_ForPublicRelease` | For public release |
| `Stamp_AddImage` | Add image stamp |
| `Stamp_Name` | Stamp name |
| `Stamp_AltText` | Describe this image (screen reader users will hear it) |
| `Stamp_SizeEstimate` | This image adds about {0} to the file. |
| `Stamp_Rotate_Right` / `_Left` | Rotate right / Rotate left |
| `Stamp_Err_Unusable` | That image can't be used as a stamp. |
| `Stamp_Err_Read` | MikePDF couldn't read that image. |
| `Stamp_Err_LibraryFull` | Your stamp library is full. Remove a stamp to add another. |
| `Stamp_Err_TooBig` | This would add more than {0} of images to the file. |
| `Stamp_Delete_Confirm` | Remove this stamp from your library? Copies already placed in documents aren't affected. |
| `Stamp_Added_Announce` | {0} stamp added on page {1} |

Telemetry: none.

## 12. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-ST-1 | Dynamic info line ("by Alex, date") on stamps: in v1? | User (product) |
| OQ-ST-2 | Where image decoding runs: WIC in the writer-host sandbox (assumed), a dedicated sandbox, or a purpose-built decoder (first task verifies WIC in the sandbox). | Opus (security) |
| OQ-ST-3 | Lossy JPEG re-encode versus lossless for opaque images. | User (product), Opus |
| OQ-ST-4 | Stamp text in the file: English standard text (assumed) or localised to the UI language. | User (product) |
| OQ-ST-5 | Free rotation of stamps. | User (product) |
| OQ-ST-6 | Artwork and colour set for the 14 stamps. | Design |
| OQ-ST-7 | Readers' handling of a non-standard `/Name` (`/Image`) on a stamp that has an appearance (checked in the Acrobat and Edge pass). | Opus after the pass |

## 13. Acceptance criteria

Shared AC-01 to AC-25 ([annotation-core](annotation-core.md)) apply.

| Id | Criterion | Layer |
|---|---|---|
| ST-01 | Each of the 14 built-in stamps renders a non-blank, correctly bounded appearance: all text and outline inside `/BBox`, text height within the box, colour contrast against white >= 4.5:1. | Unit (`MikePDF.Markup.Tests` + PdfWriter) |
| ST-02 | `/Name` is a **name object** with the standard spelling for each of the 14 (the S4 F3 defect does not recur); a test fails if it is a string. | Unit (PdfWriter) |
| ST-03 | Placement geometry: default fit into 200 x 100 pt with proportions kept for 10 aspect ratios from 1:100 to 100:1; drag-fit inside the dragged box; aspect lock and Shift unlock. | Unit |
| ST-04 | Rotation: quarter-turn commands and page `/Rotate` give the right `/Matrix` and enclosing `/Rect` for all 4 x 4 combinations, upright in the displayed page. | Unit + Integration |
| ST-05 | Asset format: `.mks` header round-trips; truncated, oversized and wrong-magic files are rejected by the broker's validator. | Unit |
| ST-06 | `ImportStampImage` golden vectors and malformed frames (limits exceeded by one) in both codecs; statuses as specified. | Unit (Protocol) |
| ST-07 | Import fixtures (section 7 list, 20 files) produce correct assets in the writer host: orientation applied, metadata dropped (byte search of the asset for the fixture's GPS and camera strings finds nothing), 16-bit and CMYK converted, alpha kept, longest side <= 2,048. | Integration |
| ST-08 | Bombs and junk: a 4 KB file claiming 100,000 x 100,000 pixels, a truncated JPEG, a PNG with a corrupt chunk, a non-image renamed `.png`: refused or failed cleanly, host memory within the Job limit, no crash, the host is reusable afterwards. | Integration + fuzz |
| ST-09 | The UI process never links or calls an image codec: a static check (no WIC calls or image decoding types in `MikePDF.App` and `MikePDF.Core` other than rendering our own bitmap format). | Unit (analyzer/build check) |
| ST-10 | Library limits: 50 stamps / 20 MiB enforced; rename, delete (with confirm); survives restart; stored only under `LocalState\private\stamps`. | Unit + Integration |
| ST-11 | Saved image stamp structure: image XObject with `/SMask` for alpha, shared object for repeated placements (1,000 placements, one image object), form XObject draws it, `/Subj` and `/Contents`, indirect annotation, `qpdf --check` passes. | Unit (PdfWriter) + Fidelity |
| ST-12 | Cold save visibility (no prior render): all 14 built-ins and an image stamp are visible in fresh PDFium and pdf.js (S4 A13a failure does not recur). | Fidelity harness |
| ST-13 | Fidelity, PDFium and pdf.js: region SSIM >= 0.98 against the golden for each built-in and for image stamps on pages rotated 0/90/180/270. | Fidelity harness |
| ST-14 | Fidelity, Acrobat Reader and Edge: all 14 built-ins and an image stamp (opaque and transparent) visible and upright on four rotations; recorded as pass, fail or **not measured**. | Fidelity harness (manual-gated, R-15) |
| ST-15 | Reopen in MikePDF: stamps load as owned; move and resize change only `/Rect` (appearance stream bytes identical). | Fidelity harness |
| ST-16 | Journal: image stamp recovery works after deleting the library entry (blob restored); blob removed with the journal. | Integration |
| ST-17 | Size guard: a save adding > 50 MB of images is refused with the message; the size estimate shown before placing is within 10% of the actual increase for 5 fixtures. | Integration |
| ST-18 | Keyboard-only: gallery navigation, `annot.newStamp`, nudge, rotate, delete, undo. | UI journey |
| ST-19 | UIA: gallery items, placed stamp name and alternative text value, announcements once each. | UI journey (a11y) |
| ST-20 | Foreign stamps keep their appearance bytes after move and reply. | Integration |
| ST-21 | Permission bit 6 clear disables stamp tools. | Unit |
