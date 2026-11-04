# Feature spec: AcroForm filling, tab order, validation display and flattening

- Status: Draft for Opus review
- Milestone: M4
- Master prompt: §2.1 (AcroForm filling: text, checkbox, radio, combo, list, push buttons; tab order; validation display; flattening on export), §8.4, §5.3 (JavaScript off), §12.4, §12.5 journey 4, §18 scenario 3 (120 fields)
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [annotation-core](annotation-core.md) (focus order, display mirror idea), [save-pipeline](save-pipeline.md) (stage 2 PDFium form save, normaliser, flattened copy), PDFium's form-fill environment in `MikePDF.Engine.Pdfium`
- Related: [settings-policies](settings-policies.md) (`forms.highlightFields`)

Conventions are in [README.md](README.md).

## 1. What S4 found about forms

Forms are the part PDFium does well (ADR-0008 decision 6):

- `/V` was correct in 12 of 12 cases (text, multiline, comb, checkbox, radio, both combos, list box, push button, and three AP-less forms). Text, comb, combo and list fields got a regenerated appearance showing the value. `/NeedAppearances` was never set. Original bytes stayed an exact prefix for incremental saves.
- **PDFium sets values only through form-fill events** (`FORM_SetFocusedAnnot`, `FORM_SelectAllText`, `FORM_ReplaceSelection`, `FORM_SetIndexSelected`, `FORM_OnLButtonDown/Up`, `FORM_ForceToKillFocus`); there is no direct setter.
- **PDFium replaces the checkbox and radio state streams with its own artwork** on any form edit, even when only the selected state changed (surprise 6), and regenerates appearances of every AP-less widget it loads.
- PDFium's incremental save is structurally invalid for xref-stream sources (7 of 9), so the writer host normalises the tail (ADR-0008 stage 3).
- JavaScript is off and V8 is not in the build, so format, validate, keystroke and calculate scripts never run.

## 2. User stories

1. As a citizen with a 120-field government form, I tab through every field in order, fill it, save, and every value is intact when I reopen it and when I open it in Edge (§18 scenario 3).
2. As a screen reader user, I hear each field's name, type, state and whether it is required, and fill it from the keyboard.
3. As someone who has made mistakes, I reset one field or clear the whole form, and I can undo.
4. As a user who wants to see which parts are fillable, I switch the field highlight on and off.
5. As someone sending a finished form, I save a flattened copy that can no longer be edited.
6. As a user opening a form that relies on scripts or on XFA, I'm told plainly what will and won't work.

## 3. Detailed behaviour

### 3.1 Approach

- The **engine** keeps PDFium's form-fill environment for a document: it knows the fields, draws widgets into tiles (render flag bit 1), applies value changes, and regenerates field appearances.
- The **UI** edits values with real XAML controls laid over the widget (a text box, combo box, list box), because the sandboxed engine has no window and so no IME, caret, spell check or accessibility. A commit sends one `SetFormFieldValue` to the engine. Check boxes and radio buttons toggle directly.
- The **edit model** (in the broker) records every field change as a command, so it survives an engine crash, a tab suspension and replay. Before a save the broker checks that the engine's field values equal the model's, replaying commands into a fresh engine if not ([save-pipeline](save-pipeline.md) stage 2).

### 3.2 Field types

| Type | Behaviour |
|---|---|
| Text, single line | In-place text box; Enter or Tab commits. Honours `/MaxLen`, `/Q` alignment, read-only, password (masked, not journaled), comb (fixed cells). |
| Text, multi-line | In-place multi-line box; Enter inserts a newline; Tab commits and moves on; Ctrl+Enter commits and stays. |
| Check box | Click or Space toggles; the on-state name comes from the field. |
| Radio group | Click selects; arrow keys move within the group and select; one tab stop per group; "radios in unison" and "no toggle to off" flags honoured. |
| Combo box | Drop-down; if the editable flag is set, text can be typed. |
| List box | List with single or multiple selection (Ctrl and Shift click; arrow keys). |
| Push button | Activates only a **reset** action (3.8). Other actions are not run. |
| Signature | Drawn, not interactive in v1; activating it says signing isn't available in this version. |

Rich text fields (`/RV`) are edited as plain text; saving replaces the rich value with plain `/V` and the editor says so on first use. File-select fields and fields of unknown type are shown, not editable.

### 3.3 Reading the fields

`GetFormFields` returns widgets **in tab order**, window by window, so a 20,000-widget form never needs a huge message. `GetFormFieldDetail` returns one field's value, options and flags on demand. Both are engine messages (section 10). Field identity within a session is the engine's `fieldId`; the journal also stores the page identity and widget index so replay works after a restart (section 8).

### 3.4 Editing flow

1. Focus reaches a widget (click, Tab, or `forms.next`). The field becomes the focused field in the model and, for editable types, the overlay editor opens with the current value and selects it as the field's flags say.
2. Typing is in the XAML control; the engine is not involved.
3. **Commit** (Enter for single-line, Tab, clicking elsewhere, Ctrl+Enter for multi-line): send `SetFormFieldValue`. The engine applies it through form-fill events, enforces `/MaxLen` and the choice list, regenerates the appearance, and replies with the accepted value and the pages to redraw. One undo command is recorded. If the engine trims or refuses (read-only, too long), the editor shows the accepted value and a message.
4. **Cancel** (Esc) closes the editor and restores the previous value; no command is recorded.
5. Check boxes, radios and push buttons act immediately.
6. Widgets whose displayed orientation is not upright (page `/Rotate` other than 0, or `/MK /R` rotation) use an **edit flyout** anchored to the field instead of the in-place editor, because a rotated text box would break the IME and caret. OPEN QUESTION OQ-FM-7: accept the flyout, or rotate the editor.
7. Fields are also usable while the document is zoomed from 10% to 6400%: the editor's font follows the field's font size times the zoom, with a minimum of 9 px, and an auto-size field (`/DA` size 0) picks a size from the field height.

### 3.5 Tab order and keyboard navigation

Order of fields on a page follows the page's `/Tabs` entry: `R` row order (top to bottom, left to right within a row band), `C` column order, `S` structure order, and absent or other means **document order** (the order of the page's annotation array). Across pages, ascending page order. Hidden (`NoView` or `Hidden`) and read-only widgets are skipped by Tab but remain in the list for screen readers (marked read-only). Widgets entirely outside the visible box are skipped. Structure order needs the widgets' place in the structure tree, which PDFium exposes only through Experimental calls; until that is verified, `S` falls back to document order (OPEN QUESTION OQ-FM-1).

Within a page, form widgets come first in their tab order, then other annotations ([annotation-core](annotation-core.md) 3.7); after the last item in the document Tab leaves the page surface (no keyboard trap).

| Key | Effect |
|---|---|
| Tab / Shift+Tab | Commit and go to the next / previous field |
| Enter | Commit a single-line field; activate a button; toggle a check box |
| Esc | Cancel editing; if not editing, drop item focus to page level |
| Space | Toggle check box or radio; open a combo list |
| Arrows | Move within a radio group (and select); change the combo or list selection |
| Alt+Down | Open the combo list |
| Ctrl+Enter | Commit a multi-line field |

### 3.6 Validation display

JavaScript does not run, so MikePDF shows only what it can know without scripts.

- **Required fields** (`/Ff` bit 2): exposed as required to assistive technology (UIA `IsRequiredForForm`). `forms.checkRequired` ("Check required fields") highlights empty required fields with a red outline plus a small text marker, shows a banner "3 required fields are empty." with "Go to first", and announces the count. It never blocks saving. With "Highlight required fields" on, they are marked all the time.
- **Maximum length, comb, choice lists, read-only:** enforced as the user types (the box stops at the limit and a short message names the limit).
- **Scripted behaviour:** if the document has JavaScript (`hasJavaScript`) or any field has a format, keystroke, validate or calculate action, a dismissible banner says "This form uses scripts to format, check or calculate some fields. MikePDF doesn't run scripts, so values are shown as you type them." A field that has such an action gets a small information marker on focus and in its UIA help text ("Has a format script that isn't run"). The count of affected fields is in the banner.
- **Native formatting (Phase B).** §8.4 asks for number, date and percentage formatting "where it can be done without JavaScript". Acrobat's standard formats are written as script calls (`AFNumber_Format(...)`, `AFDate_FormatEx(...)`, `AFPercent_Format(...)`, `AFTime_Format(...)`, `AFSpecial_Format(...)`). A native formatter would recognise exactly those calls with a strict grammar and format the value for display. **This is specified as a second phase and is not part of the first M4 work package.** The reason: PDFium regenerates field appearances from the raw `/V`, so a formatted display would exist only on screen and the saved appearance would show the raw value in readers without scripts, an inconsistency between what the user saw and what is saved. OPEN QUESTION OQ-FM-2 asks for a ruling (display-only formatting with a note, or writing formatted text into appearances ourselves, or raw values only).
- **Calculated fields** do not update when other fields change (no scripts); the banner covers this.

### 3.7 Highlight, clear and reset

- **Highlight fillable fields** (`forms.highlightFields`, setting `forms.highlightFields`, default on): a light tint behind every fillable, non-read-only widget, drawn by the engine in the form layer through a render flag, in a colour taken from the theme (system colours in high contrast). It is **never** part of a saved file, a print or a flattened copy.
- **Reset field** (`forms.resetField`): sets the field to its default value (`/DV`, or empty if none). One undo step.
- **Clear form** (`forms.clearForm`): resets every field to its default. One undo step. No modal confirmation, because it is undoable; an information bar says "Form cleared. Undo" for 10 seconds. If the form has no filled fields, the command is disabled.

### 3.8 Buttons and actions

A push button with a **ResetForm** action runs `forms.clearForm` semantics (limited to its field list if it names one; if the flags exclude fields, those are respected). Every other action is **not executed**: SubmitForm (it would send data to a server; the message says "This button sends the form to a server, which MikePDF doesn't do."), ImportData, JavaScript, Launch, URI, Named. A GoTo action on a button is not run in v1 (OPEN QUESTION OQ-FM-8: allow in-document GoTo). External addresses are never opened silently ([settings-policies](settings-policies.md) `ExternalLinks`).

### 3.9 XFA

If the document has XFA (`hasXfa`), MikePDF shows a banner: "This form uses a format MikePDF can't fill (XFA). The basic version is shown if the file has one." Any AcroForm fallback is drawn. **Filling is disabled** for XFA documents: saving AcroForm values into a file whose XFA data still holds the old values would produce a file that different readers show differently. OPEN QUESTION OQ-FM-3: allow filling the fallback with a warning.

### 3.10 Permissions

Filling is allowed when permission bit 9 (fill in form fields, value 256) **or** bit 6 (add or modify annotations and fill forms, value 32) is set. Both clear: all fields are read-only, with the owner reason. Clear form and reset need the same. Document contents modification bits are not needed. For encrypted, signed and over-size documents see annotation-core 3.8; note that PDFium's own incremental save is used for forms, so the encrypted-document question (OQ-AC-6) applies here too.

### 3.11 Flattening on export

"Save a flattened copy" is defined in [save-pipeline](save-pipeline.md) section 3.3. For forms: the copy shows every field's current value as page content; there are no widgets, no `/AcroForm`, nothing to click or edit; text can still be selected and searched. The dialog offers two check boxes, "Form fields" and "Comments and markup", both on by default (OPEN QUESTION OQ-FM-6: is "form fields only" needed). The open document and its file are unchanged. Flattening removes digital signatures' validity (full rewrite) and says so.

## 4. Keyboard, pen and touch

Section 3.5 is the keyboard model. Commands:

| Command | Default | Id |
|---|---|---|
| Highlight fillable fields | none | `forms.highlightFields` |
| Clear form | none | `forms.clearForm` |
| Reset field | none | `forms.resetField` |
| Check required fields | none | `forms.checkRequired` |
| Next / previous field | Tab / Shift+Tab (in form navigation) | `annot.next`, `annot.previous` |

Touch: tap a field to edit (the on-screen keyboard opens); check boxes and radios have at least a 24 DIP hit area even if the widget is smaller (the hit area is enlarged around small widgets without changing the file). Pen: tap or write into a field; handwriting-to-text uses the system's text input panel. In pen mode ([ink-pen](ink-pen.md)) the pen inks everywhere, so fields are then filled with the keyboard or touch, or by turning pen mode off. OPEN QUESTION OQ-FM-9: should a pen tap (not a stroke) on a field in pen mode open the field for editing.

## 5. Accessibility

Each widget is a UIA element (child of the page element), in tab order.

| Field | Control type | Name | Patterns and states |
|---|---|---|---|
| Text | Edit | alternate name (`/TU`) if present, else partial name | `Value` (read-only when the field is), `IsPassword`, `IsRequiredForForm`, multi-line known through the `Text` pattern |
| Check box | CheckBox | as above | `Toggle` |
| Radio | RadioButton | group name; "option {n} of {m}" | `SelectionItem`, container = group |
| Combo | ComboBox | as above | `ExpandCollapse`, `Value`, `Selection` |
| List | List | as above | `Selection` (multi), items with `SelectionItem` |
| Button | Button | caption | `Invoke` |
| Signature | Custom ("signature field") | name | read-only |

- Announcements: on entering a field, Narrator reads name, type, value and states (required, read-only, has a script). After commit: "{name} set to {value}" (not for password fields: "{name} set"). After clear form: "Form cleared, {n} fields reset".
- The banner for scripts, XFA and required fields are `InfoBar`s with names and polite announcements once each.
- The highlight tint has 3:1 against the page; required markers use text as well as colour.
- IME, spell check and text scaling work in the overlay editors because they are real controls.
- High contrast: highlights and required outlines use system colours.

## 6. Error states

| Situation | Behaviour |
|---|---|
| Field is read-only | Editing refused; reason shown ("This field can't be changed."). |
| Value longer than `/MaxLen` | Input stops at the limit; message with the limit. |
| `SetFormFieldValue` fails or times out | The editor stays open with the typed value and "MikePDF couldn't apply that. Try again." Nothing is recorded. |
| Engine restarts | Overlay editor keeps the typed text; the model replays committed values into the new engine. |
| Permissions deny filling | All fields read-only with the owner reason. |
| XFA document | Banner; filling disabled. |
| Push button action not supported | Message naming the action type in plain words. |
| Save fails | Values stay in the model and journal. |

## 7. Edge cases

- **Rotated pages / CropBox offsets:** widget rects are user space; overlay editors are placed through `PageProjection`; fixtures for four rotations and CropBox origin (50, 30); the flyout editor for non-upright widgets; widgets outside the CropBox are not tabbable.
- **Same field name on several widgets** (a field with many widgets, radio groups): the widgets share a value; setting it redraws all (the reply names affected pages).
- **Fields with the same name but different types, empty names, 1 MB names, deep parent chains or loops:** names are truncated for display (128 bytes) and the full name is used only inside the engine; field-tree loops are cut by the engine; such fields are listed read-only if unsafe.
- **Comb fields with MaxLen 0, text fields with huge MaxLen, multiline + password + comb combinations:** defined by the flag precedence of PDFium; the editor never exceeds `maxLen` or 8,192 bytes.
- **Option lists with thousands of entries:** windowed (64 per request, up to 10,000); the combo list is virtualised.
- **Encrypted documents:** filling allowed per permission bits; saving per OQ-AC-6.
- **Signed documents:** a notice before the first change (annotation-core 3.8).
- **Huge forms:** 20,000 widgets: `GetFormFields` is windowed; only widgets near the viewport create overlay objects.
- **Author-made checkbox and radio artwork:** preserved on save (10.2).
- **Hostile input:** JavaScript strings of 10 MB, object streams with huge field trees, appearance streams that are decompression bombs, options with 1 MB labels, 500,000 widgets: bounded by the schema limits, the engine's caps and the Job Object; the form-fill environment is torn down if a limit is hit and the document opens read-only for forms.

## 8. Undo and redo

Commands: `SetFieldValue` (field, previous value, new value), `ResetField`, `ClearForm` (composite of `ResetField`s). A radio change is one command recording the previously selected button. Undo and redo apply the value to the engine through `SetFormFieldValue` and update the overlay and the highlight state. The editor's own character-level undo is separate while open. Rules in annotation-core section 8.

## 9. Journal entries

| `cmd` | Payload |
|---|---|
| `SetFieldValue` | `field`: `{ "page": <page identity>, "widget": <index in the page's annotation order>, "name": "<fully qualified name, at most 256 bytes>" }`; `value` (text, or checked state, or selected option indices); `previous`; `redacted` true for password fields (the value is not stored) |
| `ResetField` | `field`, `previous` |
| `ClearForm` | `fields`: list of `{field, previous}` |

Replay finds the widget by page identity and widget index, then verifies the name; on mismatch it reports the entry as not replayed and continues. Typed values are personal data (names, addresses, tax numbers): they are written to the journal in clear text in the broker-only private directory, which the privacy notes must say; OPEN QUESTION OQ-FM-5 asks whether journals should be protected with DPAPI.

## 10. Save behaviour and engine messages

### 10.1 Save

[save-pipeline](save-pipeline.md) stage 2 saves form changes with PDFium's incremental save into a temporary file; stage 3 (writer host) normalises PDFium's appended tail and strips the private `/PDFIUM_HasGeneratedAP` key.

### 10.2 Preserving author-made appearances

PDFium rewrites check box and radio state streams with its own artwork even when only the selection changed. ADR-0008 decision 6 says to keep the author's streams in that case and calls for an engine-side check in M4. The proposal here is to do it in the writer host's normaliser: for every check box or radio widget whose set of appearance state names (`/AP /N` keys) is unchanged, restore the **original** state stream objects from the base file and keep only `/AS` and `/V` from PDFium's output. Widgets that had no appearances, or whose state names changed, keep PDFium's output. OPEN QUESTION OQ-FM-4 asks Opus to approve this rule. Criterion FM-19 tests it.

### 10.3 Engine messages (schema v5)

**`GetFormFields` (45).** Request: `first` (u32, max 99999), `count` (u32, 1 to 64). Reply:

| Key | Name | Type | Limits |
|---|---|---|---|
| 0 | `total` | u32 | max 100000 |
| 1 | `fieldIds` | array u32 | maxCount 64 |
| 2 | `pages` | array u32 | maxCount 64, max 9999999, `sameLengthAs` |
| 3 | `lefts` | array f32 | maxCount 64, -1000000 to 1000000, `sameLengthAs` |
| 4 | `bottoms` | array f32 | as above |
| 5 | `rights` | array f32 | as above |
| 6 | `tops` | array f32 | as above |
| 7 | `types` | array u16 | maxCount 64, max 7 (PDFium `FPDF_FORMFIELD_*`: 0 unknown, 1 push button, 2 check box, 3 radio, 4 combo, 5 list, 6 text, 7 signature) |
| 8 | `flags` | array u32 | maxCount 64 (`/Ff`) |
| 9 | `states` | array u16 | maxCount 64: bit 0 checked or has selection, bit 1 has an action script, bit 2 hidden, bit 3 skipped by Tab |
| 10 | `maxLens` | array u16 | maxCount 64 (0 = none) |
| 11 | `names` | array string | maxCount 64, maxBytes 128 |
| 12 | `alternateNames` | array string | maxCount 64, maxBytes 128 |
| 13 | `values` | array string | maxCount 64, maxBytes 128 (start of the value; empty for passwords) |

Widgets are in tab order. Worst-case frame about 35 KiB. Indexes are one window at a time, as `GetPageLinks`.

**`GetFormFieldDetail` (46).** Request: `fieldId` (u32), `firstOption` (u32, max 9999). Reply: `type`, `flags`, `name` (maxBytes 512), `alternateName` (maxBytes 256), `value` (maxBytes 8192), `valueTruncated`, `defaultValue` (maxBytes 8192), `maxLen`, `quadding` (u16, max 2), `fontSize` (f32, 0 to 1000), `exportValue` (maxBytes 64), `checked` (bool), `widgetCount` (u32, max 100000), `optionsTotal` (u32, max 10000), `optionLabels` and `optionValues` (arrays of string, maxCount 64, maxBytes 128, `sameLengthAs`), `selectedIndices` (array u32, maxCount 64, max 9999), `hasFormatScript`, `hasValidateScript`, `hasCalculateScript` (bool).

**`SetFormFieldValue` (47).** Request: `fieldId`, `kind` (u16, max 2: 0 text, 1 toggle state, 2 choice), `text` (string, maxBytes 8192), `checked` (bool), `selected` (array u32, maxCount 64, max 9999). Reply: `accepted` (bool), `value` (string, maxBytes 8192), `affectedPages` (array u32, maxCount 256, max 9999999). Statuses: `PermissionDenied` (read-only or permissions), `InvalidArgument` (bad index, too long), `Unsupported`.

**`FormAction` (48).** Request: `fieldId`, `action` (u16, max 2: 0 reset field, 1 reset all, 2 activate button). Reply: `affectedPages` (array u32, maxCount 256), `performed` (u16, max 3: 0 nothing, 1 reset, 2 refused as unsupported action, 3 refused because it would send data). 

**`RenderTile` change.** `flags` maximum moves from 3 to 7: bit 2 draws the fillable-field highlight (never set for print or snapshots). This is a layout change inside the schema and is part of the same schema bump as the annotation messages.

`ReplyChecks`: arrays equal the window length; `fieldIds` are unique within a window; `affectedPages` are below the page count; `value` is valid UTF-8.

## 11. Localisation strings

| Key | en-US |
|---|---|
| `Form_Banner_Scripts` | This form uses scripts to format, check or calculate some fields. MikePDF doesn't run scripts, so values are shown as you type them. |
| `Form_Banner_Xfa` | This form uses a format MikePDF can't fill (XFA). The basic version is shown if the file has one. |
| `Form_Banner_Required_One` / `_Other` | 1 required field is empty. / {0} required fields are empty. |
| `Form_GoToFirst` | Go to first |
| `Form_Highlight` | Highlight fillable fields |
| `Form_Clear` | Clear form |
| `Form_Cleared` | Form cleared. |
| `Form_Reset` | Reset field |
| `Form_CheckRequired` | Check required fields |
| `Form_Type_Text` / `_Multiline` / `_Checkbox` / `_Radio` / `_Combo` / `_List` / `_Button` / `_Signature` | text field / multi-line text field / check box / radio button / drop-down list / list box / button / signature field |
| `Form_Required` | required |
| `Form_ReadOnly` | read-only |
| `Form_HasScript` | Has a script that isn't run |
| `Form_Radio_Position` | {0}, option {1} of {2} |
| `Form_Automation` | {0}, {1} |
| `Form_Set_Announce` | {0} set to {1} |
| `Form_SetPassword_Announce` | {0} set |
| `Form_Cleared_Announce_One` / `_Other` | Form cleared, 1 field reset / Form cleared, {0} fields reset |
| `Form_MaxLen` | This field holds up to {0} characters. |
| `Form_ReadOnly_Message` | This field can't be changed. |
| `Form_Apply_Failed` | MikePDF couldn't apply that. Try again. |
| `Form_Signature_Unavailable` | Signing isn't available in this version of MikePDF. |
| `Form_Submit_Refused` | This button sends the form to a server, which MikePDF doesn't do. |
| `Form_Action_Refused` | MikePDF doesn't run this kind of button action. |
| `Form_RichText_Note` | This field supports formatted text. MikePDF saves it as plain text. |
| `Form_Disabled_Owner` | The document's owner doesn't allow filling in this form. |
| `Form_Flatten_Fields` / `_Markup` | Form fields / Comments and markup |

Telemetry: none.

## 12. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-FM-1 | Structure-order (`/Tabs /S`) tab order needs widget-to-structure mapping: verify the Experimental PDFium calls or fall back to document order. | Sonnet spike, Opus |
| OQ-FM-2 | Native number/date/percent formatting (Phase B): ruling on the saved-appearance mismatch. | Opus, User |
| OQ-FM-3 | XFA documents: read-only (assumed) or fill the AcroForm fallback with a warning. | User (product) |
| OQ-FM-4 | Preserve author check box and radio artwork in the writer-host normaliser (10.2). | Opus |
| OQ-FM-5 | Protect journals (typed form values) with DPAPI. | Opus (security), User |
| OQ-FM-6 | "Form fields only" flatten option. | User (product) |
| OQ-FM-7 | Rotated fields: edit flyout (assumed) or a rotated editor. | Design |
| OQ-FM-8 | Allow in-document GoTo actions on push buttons. | User (product) |
| OQ-FM-9 | Pen tap on a field while in pen mode. | Design |

## 13. Acceptance criteria

Shared AC-01 to AC-25 ([annotation-core](annotation-core.md)) apply where relevant.

| Id | Criterion | Layer |
|---|---|---|
| FM-01 | Protocol golden vectors and malformed frames (every limit exceeded by one, unequal arrays, bad type, text over 8,192 bytes) for messages 45 to 48 and the `RenderTile` flag change, in both codecs; `ReplyChecks` rejects invalid replies. | Unit (Protocol + Engine C++) |
| FM-02 | Tab order: for fixtures with `/Tabs` R, C, S, none, and mixed pages, `GetFormFields` order equals the committed expected order; hidden and read-only widgets are flagged skipped; S falls back to document order and the fallback is asserted. | Integration (engine) |
| FM-03 | Row and column ordering on rotated pages and with a CropBox origin (50, 30) follows displayed orientation. | Integration |
| FM-04 | The S4 B01 to B12 matrix reproduced by the production code: for each of the 12 cases `/V` is correct in the saved file, the appearance shows the value, `/NeedAppearances` is absent, and the original bytes are an exact prefix. | Fidelity harness |
| FM-05 | Xref-stream sources: after the stage-3 normaliser the saved forms files pass `qpdf --check` and the writer host's structural validator for 9 of 9 xref-stream fixtures (S4 had 7 of 9 invalid). | Fidelity harness |
| FM-06 | Values entered through the overlay path (text, multiline, comb, check box, radio, both combos, list with multi-select) arrive in the engine and the saved file exactly, including Unicode within the field's font coverage and `/MaxLen` truncation. | Integration |
| FM-07 | Journey 4 (§12.5): fill a multi-page form, save, open in the fidelity harness: values intact in PDFium and pdf.js. | UI journey + Fidelity |
| FM-08 | Scenario 3 (§18): a 120-field form fixture filled by Tab through every field in order (assert the sequence of focused field ids equals the expected 120), saved, reopened with every value intact, and rendered in PDFium and pdf.js; the Edge column is **not measured** until the manual pass. | UI journey + Fidelity (Edge manual-gated) |
| FM-09 | Editing flow: commit on Enter, Tab and click-away; Esc cancels and restores; multi-line Enter inserts a newline and Ctrl+Enter commits. | UI journey |
| FM-10 | Read-only, max length, choice-list and permission enforcement in the engine: refused with the right status and a message. | Integration |
| FM-11 | Permission matrix: bit 9 or bit 6 set allows filling; both clear makes all fields read-only with the owner reason. | Unit + Integration |
| FM-12 | Radio groups: one tab stop per group, arrow keys select within the group, "no toggle to off" and "in unison" flags respected, `affectedPages` includes pages with sibling buttons. | Integration + UI journey |
| FM-13 | Required fields: `forms.checkRequired` finds exactly the empty required fields, banner counts and the Go to first target are right, UIA `IsRequiredForForm` is true. | Integration + UI journey (a11y) |
| FM-14 | Script banner appears exactly when the document has JavaScript or a field action script; affected field count matches the fixture; no script is ever executed (instrumented V8-less build asserts no script engine call). | Integration |
| FM-15 | XFA fixture: banner shown, AcroForm fallback drawn, filling disabled. | Integration |
| FM-16 | Highlight toggle: tint appears in tiles when on and not when off; the saved file, a print and a flattened copy never contain it (pixel and structure tests). | Integration + Fidelity |
| FM-17 | Clear form and Reset field: values return to `/DV` or empty; one undo step each; undo restores; the information bar and the disabled state when nothing is filled. | Unit + UI journey |
| FM-18 | Push button actions: ResetForm runs; SubmitForm, ImportData, JavaScript, Launch, URI and Named are refused with the right messages and make no network call. | Integration |
| FM-19 | Author artwork preserved: for check box and radio fixtures with author-made state streams, a state change saves with the **original** state stream bytes (S4 surprise 6 does not recur); widgets without appearances get PDFium's. | Fidelity harness |
| FM-20 | Flattened copy: no widget annotations, no `/AcroForm` entries that matter (`GetFormFields` total is 0 in the output), every field's value drawn, text still selectable, SSIM >= 0.98 against the pre-flatten render without highlight; the source file is byte-identical and the open document still editable. | Integration + Fidelity |
| FM-21 | Journal replay after a crash restores all field values by page identity and widget index; a renamed field is reported not replayed; password fields are not in the journal file (byte search). | Integration |
| FM-22 | Rotated fixtures: fields on `/Rotate` 90/180/270 pages and with `/MK /R` use the flyout editor; values commit correctly. | UI journey |
| FM-23 | UIA tree for the 12 field types: control types, names (from `/TU`), states, announcements per section 5, once each. Raw `IUIAutomation`. | UI journey (a11y) |
| FM-24 | Keyboard-only form filling: open, Tab through all fields, fill each type, check required, save, no mouse. | UI journey |
| FM-25 | Hostile forms (500,000 widgets, loops in the field tree, 10 MB scripts, option lists of 1 M entries, appearance bombs) open within caps without crash; form features degrade to read-only with a message. | Integration + fuzz |
| FM-26 | Performance: a 20,000-widget form opens with the first page interactive within 1.0 s and creates overlay objects only for visible widgets (recorded). | Integration (perf) |
| FM-27 | Facing mode and 400% zoom: the editor sits on the field and tracks scrolling and zoom without lag. | Integration |
| FM-28 | Signature fields are drawn and not interactive; activation shows the message. | UI journey |
| FM-29 | Phase B (only if OQ-FM-2 approves it): the recognised `AF*_Format` grammar formats values; unrecognised scripts are left raw and flagged; the saved appearance matches the ruling. Not part of the first work package. | Unit + Fidelity |

## Implementation notes: the engine side (W3-engine, schema 7, ADR-0005 A5)

`GetFormFields` (45), `GetFormFieldDetail` (46), `SetFormFieldValue` (47), `FormAction` (48) and the field highlight (`RenderTile.flags` bit 3, not bit 2: bit 2 was already the dark-pages bit) are built. Where the pinned PDFium (chromium/8125) is narrower than this spec:
- **Tab order (3.5, OQ-FM-1).** No call reads a page's `/Tabs`, so the engine lists every page in document order (`/Annots` order within a page). `states` bit 3 says a widget is skipped by Tab. The row, column and structure orders are not implemented.
- **Defaults and options.** `defaultValue` is the widget's own `/DV`, not an inherited one. An option's export value is not readable, so `optionValues` repeats the labels.
- **Push buttons.** The action type of a button cannot be read. `FormAction` action 2 presses the button in an environment without scripting or network and reports a reset (1) when a field changed and "unsupported" (2) otherwise; "refused because it would send data" (3) is never reported. The has-script flags of the detail reply say that a format, validate or calculate action exists; no script is run.
- **How a value changes.** Only through PDFium's form events (focus, select all, replace, kill focus; a click; set index selected), so appearance regeneration and `/MaxLen` are PDFium's. `SaveDocument` ends an edit in progress first. `NormalizeTail` (writer host) repairs the saved tail; the check box and radio artwork rule (10.2) is its flag bit 0.
