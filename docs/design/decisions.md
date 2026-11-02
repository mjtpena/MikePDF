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
