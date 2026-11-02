# 09 Form filling: fields, tab order, validation, clear and flatten

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 3 (a 120-field government form: tab through every field in order, save, reopen, values intact, opens correctly in Edge), 6 (screen-reader user fills fields)
- Spec: master prompt §2.1 (AcroForm filling, tab order, validation display, flatten on export), §8.4. Milestone M4. Nothing here exists in the app yet.
- There's **no separate Form mode**. A fillable form is just a document whose fields are live. A **Form bar** offers form commands. (README and [01](01-main-window.md) refer to a "Form mode"; that means the Form bar and the form commands in the View menu and palette.)

## 1. Purpose

Fill a form quickly, with the keyboard, in the right order, and see clearly what is empty, wrong or required, without ever being blocked from saving.

## 2. Layout

### 2.1 The Form bar and a form (1280 x 800 epx)

```text
+--------------------------------------------------------------------------------------------+
| [command bar]                                                                              |
+--------------------------------------------------------------------------------------------+
|    +------------------------------------------------------------------------------------+  |
|    | (i) This form has 120 fillable fields.  [Highlight fields: on]  [Required: 14 >]   |  |  InfoBar, overlay
|    |                                                    [Clear form]              [x]   |  |  top centre, max 720
|    +------------------------------------------------------------------------------------+  |
|                       +----------------------------------------+                           |
|                       |  Application for a permit              |                           |
|                       |  Surname*  [ Pena________________ ]    |  <- focused field: 2-ring focus
|                       |  Given     [ ______________________ ]  |     highlighted fields: pale blue
|                       |  Date      [ __/__/____ ]  (!)         |  <- (!) invalid badge, 2 epx critical outline
|                       |  [x] I agree      ( ) Yes  (o) No      |                           |
|                       |  Country [ Australia           v ]     |                           |
|                       +----------------------------------------+                           |
+--------------------------------------------------------------------------------------------+
   * = required: a red asterisk at the field's corner, drawn by the UI layer (not in the saved file)
```

### 2.2 The required-fields list (opens from the Form bar)

```text
        +------------------------------------------+
        | Required fields (14)               [x]    |
        | [!] Date of birth       Page 1   Invalid  |  "That doesn't look like a date. Use DD/MM/YYYY."
        | [ ] Surname             Page 1   Empty    |
        | [ ] Signature           Page 3   Empty    |
        | ...                                       |
        | [Go to first empty field]                 |
        +------------------------------------------+
```

### 2.3 Validation message on a field

```text
                       |  Date  [ 31/02/2026 ]  (!)
                       |        +-----------------------------------------+
                       |        | (!)  That date doesn't exist.            |   Flyout, anchored to the field,
                       |        |      Check the day and the month.        |   shown when the field commits an invalid
                       |        +-----------------------------------------+   value and when it gets focus again
```

### 2.4 Flatten dialog (Save a flattened copy...)

```text
        +--------------------------------------------------+
        | Save a flattened copy                            |
        | Fields and comments become part of the page, so  |
        | they can't be edited. Your original isn't changed.|
        | [x] Flatten form fields                          |
        | [x] Flatten comments and drawings                |
        | File name  [ Permit application (flattened).pdf ]|
        |                          [ Save ]   [ Cancel ]    |
        +--------------------------------------------------+
```

### 2.5 XFA or scripts notice

```text
    | (i) This form uses technology MikePDF can't show (XFA). The simple version in the file is shown.      [Details] |
    | (i) This form has scripts for formatting and checking. MikePDF doesn't run scripts, so values are shown as typed. [Details] |
```

## 3. Behaviour

### 3.1 Fields

| Field type | How it works | Keyboard |
|---|---|---|
| Text, single line | Click focuses it. A native text editor appears over the field (section 3.2). Commits on `Tab`, `Enter`, or click away | `Enter` commits. `Esc` reverts to the last committed value |
| Text, multi-line | As above | `Enter` inserts a new line. `Tab` commits and moves on. `Ctrl+Enter` commits and stays |
| Comb (fixed number of boxes) | The editor draws the boxes and limits characters to `MaxLen` | As text |
| Password | Shows dots; value is not shown to the UIA tree | As text |
| Checkbox | Click toggles | `Space` |
| Radio group | Click selects; arrows move within the group | `Up/Down/Left/Right`, `Space` |
| Combo box | Drop-down list; if editable, typing is allowed | `Alt+Down` opens, `Up/Down` choose, `Enter` commits |
| List box | List, multi-select if the field says so | `Up/Down`, `Space`, `Ctrl+click` |
| Push button | Click runs a **supported** action only: Reset form. Submit, JavaScript, Import and Launch buttons do nothing and show a short message "This button runs a script or sends data. MikePDF doesn't run it." (Never sends form data anywhere) | `Space`, `Enter` |
| Signature field | Shows "Signature field". Signing isn't in v1.0 (P1). If the field is signed, its validity shows in the Signatures pane (P1) | `Enter` shows a message |
| Read-only field | Drawn, focusable by the screen reader, not editable. UIA `IsReadOnly` | n/a |

### 3.2 Editing surface (design decision)

PDFium's form-fill environment draws fields and can take key events (`FormKeyEvent`, `FormMouseEvent`, §5.4) but it has **no input-method (IME) support, no spell-checking, no accessibility tree, and no touch keyboard integration**. This spec therefore uses a **hybrid**:

1. Fields are **drawn** by the engine in the annotation layer (their appearance streams; `Form.FieldHint` highlight is drawn by the UI).
2. When a text or combo field takes focus, the UI shows a native XAML `TextBox` (or an editable `ComboBox`) **over** the field, with the same font, size (but never smaller than **14 epx** on screen so tiny fields remain editable), alignment, colour and `MaxLen`. IME, Narrator, the touch keyboard, clipboard, undo within the field and drag selection come for free.
3. On commit, the UI sends `SetFieldValue`. The engine updates the value and regenerates the appearance. The overlay disappears and the engine's drawing shows through.
4. Checkboxes, radios and push buttons use `FormMouseEvent` and commands, with no overlay.
5. Spell-check is **off** in field editors by default (names and IDs), with a setting.

**Risk:** the overlay's text layout may differ from the saved appearance (kerning, wrapping, comb spacing). The fidelity test (S4 matrix, `Quire.Fidelity.Tests`) must compare the overlay's value to what other viewers show. A mismatch isn't a layout bug, it's a data bug if the saved *value* differs. The value, not the layout, is the contract. **OPEN QUESTION:** confirm this hybrid in the forms feature spec and a spike on IME input.

### 3.3 Tab order (§8.4, scenario 3)

- `Tab` and `Shift+Tab` move through fields in the **document's tab order**: each page's `/Tabs` entry (`R` row, `C` column, `S` structure) or, if absent, the annotation array order **[design]**. Across pages: page order. At the last field, `Tab` shows a one-line hint "End of the form. Press Tab to start again" (polite) and wraps on the next press (no keyboard trap).
- Focus brings the field **into view** with margins (README 4.8). Zoom does not change.
- **Annotations and links** are interleaved in the same walk **only on documents with no form fields**. On forms, `Tab` visits fields only, and annotations are reached with `Alt+Down` and `Alt+Up` (next and previous comment).
- Read-only fields are skipped by `Tab`, but remain in the UIA tree so a screen reader still reads them.
- **Next empty required field** has a palette entry and the **Required** list, and no default shortcut (OPEN QUESTION 3).
- Whether PDFium's environment moves focus on `Tab` **is not verified**. If it doesn't, the broker walks the list from `GetFormFields` (which must include tab-order index) and calls `FormFocus` ([README E12](../README.md#10-protocol-and-engine-implications)).

### 3.4 Validation

Principle: **show, never block.** A user must always be able to type a value, leave a field, and save.

| Rule | Source | Behaviour |
|---|---|---|
| Max length | `/MaxLen` | The editor stops accepting characters. A short polite message "Limit: 12 characters" |
| Comb | `/MaxLen`, comb flag | Boxes limit input |
| Required | `/Ff` required bit | **Empty required fields are marked** (asterisk, count in the Form bar, entry in the Required list). Leaving one empty doesn't block anything |
| Number, date, percentage, time formats | The field's format script, **parsed, not executed**, when it is one of the standard format calls with parameters (for example number with decimals and separators, a date mask) **[design]** | Values are checked and **displayed** with the mask. A value that doesn't fit is **accepted as typed** and marked invalid: critical outline, `(!)` badge, a flyout with a plain message ("That date doesn't exist") and an entry in the Required list as Invalid |
| Any other script (calculation, custom validation, show/hide) | Document JavaScript | **Not run.** Banner (2.5) says so once per document. Calculated fields keep their stored values |
| Read-only | `/Ff` | Not editable |

The parser that reads format actions is **untrusted-input parsing**: it runs in the engine or the writer host (sandboxed), never in the UI process. It handles only a fixed set of function names and arguments, and ignores everything else.

Error messages are plain sentences: they say what is wrong and how to fix it. The `(!)` badge is a shape (not colour only). The flyout is non-modal, dismissed with `Esc` or by leaving the field, and its text is also in the field's UIA `HelpText` and in the Required list.

### 3.5 Required list and the Form bar

- The **Form bar** appears when the document has fields. It is dismissible, and the dismissal is remembered **per document**. Its commands are also in the View menu and palette: **Highlight fields** (toggle; on by default), **Required fields...** (opens the list), **Clear form**.
- The **Required list** shows empty and invalid required fields and any field with an error, with page and state. A click goes to the field and focuses it. `Go to first empty field` focuses the first empty required field.
- The list has a count: "Required (14)" counts empty or invalid required fields, and it updates as the user fills.

### 3.6 Clear, reset, undo

- **Clear form** (`form.clear`) resets **all** fields to their default values in one step. It's undoable, so there's no confirmation dialog: the **undo bar** says "Form cleared. Undo" for 8 s.
- **Reset field** (`form.resetField`, context menu): one field to its default value.
- `Esc` in an editor reverts the uncommitted text.
- `Ctrl+Z` inside an editor undoes text first. Once the editor has nothing to undo (or when it's closed), it undoes **document-level** edits: the previous committed field value, in order. Each commit is one edit command in the journal.

### 3.7 Save and flatten

- **Save** (`Ctrl+S`) writes an incremental update with `/V` set and appearances regenerated by PDFium (S4: 12 of 12 field types correct **[repo: ADR-0008]**). **Needs no validation to pass.** Fields with an invalid value still save as typed.
- **Reopen** shows every value, including after other viewers have been used on the file (fidelity journey, §12.4).
- **Save a flattened copy...** (`file.saveFlattenedCopy`) produces a new file with fields (and optionally comments) painted into the page. It never modifies the open document. The dialog (2.4) names the file `{name} (flattened).pdf` by default and asks before overwriting.
- A flattened copy has no fields, so Edge and Acrobat show the same thing.
- Signed documents: filling a form on a signed document is covered by the pre-save warning (§7.5 step 5).

### 3.8 XFA and scripts

- **XFA**: detected (a `/XFA` entry). If the file has an AcroForm fallback, show it with the notice in 2.5. If it has none (the page typically says "Please wait... If this message is not eventually replaced..."), show the notice "This is a dynamic form (XFA) that MikePDF can't show or fill. Open it in the program that made it." **[design]**, with no "open in browser" action (§6.4 offers it for unsupported encryption, but opening unknown content in a browser is a user choice, not an app action; see [14](14-errors-and-recovery.md)).
- **JavaScript** is off by default (§5.3). A document-level banner appears once per document if it contains scripts: "This document has scripts. They didn't run." If JS is enabled by policy (separate engine flavour), the banner reads "This document contains scripts" (§5.3).

### 3.9 Performance

A 120-field form must be interactive on open: field list loaded with the first page (`GetFormFields` per page, lazily), tab order across pages built in the background. Typing in an editor has zero engine round trips until commit.

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | Fields appear as the first page's tiles arrive. The Form bar appears after the field count is known (`GetFormFields` for the whole document, which can take a moment on a large form), with "Reading form fields..." Fields on pages not yet loaded are reachable by `Tab` as they load: `Tab` waits at most 300 ms, then moves on and re-queues. Form bar, Required list: determinate "Reading fields, 62%" for large forms |
| **Empty** | No fields: no Form bar, commands disabled with "This document has no form fields." A form with zero required fields: the Required list says "No required fields." |
| **Error** | A field value that the engine refuses (too long, wrong type): the field keeps the previous value, an `InfoBar` says "The value wasn't accepted" with the field name and the reason. A form that can't be read at all: "The form fields couldn't be read. The page is shown without them." and the engine is restarted if needed (journal replays). A scripts-only button: the message in 3.1 |
| **Keyboard focus** | Visible two-ring focus around the focused field (and the overlay editor has the standard text-box focus visual). The Form bar, then fields in tab order. `F6` leaves the page well (G-KB3). After `Esc` in the Required list, focus returns to the field that was focused |
| **High contrast** | Field hint: 1 epx dotted `WindowText` outline instead of tint. Invalid: 2 epx solid `WindowText` outline + `(!)` badge as text and shape. Required asterisk stays `WindowText`. The overlay editor uses system colours (`Window`, `WindowText`) and keeps the field's text size. The Form bar and the Required list follow G-HC |
| **225% text scale** | The Form bar wraps onto two or three lines; its buttons stack. The overlay editor text follows page zoom (document content) with a 14 epx floor, **not** the Windows text-size setting; the floor grows to 18 epx at 225% text scale **[design]**. The Required list wraps rows, and its group header sticks. Validation flyout text wraps and the flyout widens to 360 epx |
| **Narrow window** | Form bar wraps; buttons become a **Form** menu. The Required list is an overlay sheet. At XS the validation flyout is full width. Field editors may need the page to scroll: the page well scrolls so the editor is above the keyboard |
| **Touch** | Tapping a field focuses it and raises the on-screen keyboard (the page well scrolls to keep the field above it). Checkboxes and radios have a 44 x 44 hit area around the visible control (minimum 24 x 24 visible). A **Previous / Next** field pair appears in a small bar above the keyboard (44 x 44 buttons), replacing `Tab` for touch users |

## 5. Accessibility

| Element | Type | Accessible name | AutomationId |
|---|---|---|---|
| Form bar | InfoBar (Group) | "Form, 120 fillable fields" | `FormBar` |
| Highlight fields | Button (toggle) | "Highlight fields" | `FormHighlightToggle` |
| Required list button | Button | "Required fields, 14" | `FormRequiredButton` |
| Clear form | Button | "Clear form" | `FormClearButton` |
| Required list | List | "Required fields" | `RequiredList` |
| Text field | Edit | The field's label (`/TU`, else `/T`, else "Field {n}") | `Field_{id}` |
| Checkbox | CheckBox | Label | `Field_{id}` |
| Radio | RadioButton in a Group named by the group label | Option label | `Field_{id}` |
| Combo, list | ComboBox, List | Label | `Field_{id}` |
| Push button | Button | Its caption | `Field_{id}` |
| Validation | Text in the field's `HelpText` and a flyout (live, assertive when it appears on commit) | The message | `FieldError_{id}` |

Required fields set `IsRequiredForForm` (UIA). Invalid fields set `IsDataValidForForm=false` (UIA) **[verify the property is exposed by the custom peer]**. Field help text: "Field 37 of 120, page 3." The overlay editor is a normal `TextBox` with the same name as the field.

Narrator, intended:

| Action | Announcement |
|---|---|
| Open a form | "Permit application, 3 pages. This form has 120 fillable fields." (polite, once, through the Form bar) |
| `Tab` into a field | "Surname, required, edit, blank. Field 1 of 120." |
| Tab into a filled field | "Given name, edit, Mike." |
| Checkbox | "I agree, check box, not checked" |
| Radio | "Yes, radio button, 1 of 2, not selected" |
| Invalid value | Assertive: "Date. That date doesn't exist. Check the day and the month." |
| Last field | "End of the form. Press Tab to start again." |
| Clear form | "Form cleared. Undo available" |

Reading order of the UIA tree is the **tab order** for fields, and the **reading order** (structure tree) for other content. A document with a tagged form uses the tags for labels where `/TU` is missing **[design; depends on E16]**.

## 6. Keyboard path (scenario 3: 120 fields, no mouse)

| Task | Keys |
|---|---|
| Move to the first field | `Tab` (from the page), or `Ctrl+Shift+P` then "first form field" |
| Next / previous field | `Tab` / `Shift+Tab` |
| Commit a text field | `Enter` (single line), `Tab` |
| Revert the field | `Esc` |
| Toggle a checkbox | `Space` |
| Choose a radio | Arrows, `Space` |
| Open a drop-down | `Alt+Down` |
| Required list | `Alt+Shift+R` **[design: assignable; none by default]** or the Form bar button |
| Clear form | Palette: "Clear form" (undo with `Ctrl+Z`) |
| Save | `Ctrl+S` |
| Flattened copy | Palette: "Save a flattened copy" |
| Leave the page | `F6` |

## 7. Engine and protocol implications

| # | Need |
|---|---|
| E12 | `GetFormFields`: per field: id, page, rect, type, flags (required, read-only, multiline, password, comb, `MaxLen`), label (`/TU`, `/T`), **tab-order index** and the page's `/Tabs`, format kind and parameters (parsed), default value, current value, font, size, alignment, options for combo and list |
| E7 | `SetFieldValue(id, value)`, `ClearForm`, `ResetField(id)`. Each is a journalled command. The engine returns the regenerated appearance region so the UI can refresh the annotation layer without re-rendering page content (§7.1 rule 7) |
| | `FormFocus` and `FormMouseEvent` for non-text controls, and for Tab if PDFium doesn't walk the order itself |
| | `SaveFlattened` (in §5.4's list) with flags: flatten fields, flatten annotations |
| | XFA and JavaScript detection in `GetDocumentInfo` (E3) |
| | **Parser for format actions** in the engine (fixed grammar, fuzzed) |
| | PDFium's form-fill environment may call back for **JavaScript, alert and submit actions**: all must be refused by the engine host (no network, no new processes) and reported as "script not run" |

## 8. OPEN QUESTIONS

1. The **hybrid editing surface** (3.2): confirm with a spike on IME, touch keyboard and Narrator.
2. Where a **required** field's asterisk is drawn: UI-only (not in the file) or also in the file's appearance? This spec says UI-only: a saved file mustn't change because of a UI hint.
3. A default shortcut for **Next empty required field**. Candidates: `Alt+Shift+Down`. Unassigned until a conflict check.
4. Parsing format scripts (3.4): is it safe and valuable enough for v1.0, or should v1.0 show raw values and the scripts banner only (§8.4 allows that)? This spec includes it as a **should**, dropping to "raw values" if the parser can't be fuzzed in time.
5. Should merging or flattening a form with signatures warn? Covered by the §7.5 warning for saves. A flattened **copy** of a signed document breaks the signature in the copy; say so in the dialog when `GetSignatures` is non-empty.
6. A **Previous/Next field** bar for touch (4) could instead be a keyboard-accessory view built into the field overlay.
