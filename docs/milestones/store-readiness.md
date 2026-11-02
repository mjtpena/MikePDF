# Store readiness: scope

- Date: 2026-11-02
- Instruction from the user: "make it open source. To fix the CI. Get everything ready for Windows Store publishing"
- Recorded by: Opus

## What this changes
- **Licence:** Apache-2.0, repository public (ADR-0003).
- **Distribution:** Store first (ADR-0015).
- **Milestone order:** a Store package needs an app, and none existed, so reader work from M2 starts now. M0 is still **not passed**, and this document doesn't change the [M0 report](M0-report.md). The performance gates stay open.

## In scope: the Store preview
A reader with no write features:
- Open (picker, drag and drop, command line, file association), password documents, recent files
- Continuous scroll with the ADR-0006 tile pipeline, zoom, view rotation, page navigation
- Thumbnails, outline, links, text selection and copy, find
- Screen-reader access to page text (S7)
- Light, dark and high-contrast themes; engine crash recovery
- Packaging, Store assets, listing, privacy policy, certification notes, release workflow

Leaving out save means the unresolved **fidelity** gate (Acrobat and Edge manual passes, S4 and S8) doesn't apply to this package. `MikePDF.WriterHost` isn't shipped.

## Out of scope for the preview
Annotations, forms, ink, page operations, save, printing, tabs, shell handlers, AI, OCR, signatures. These stay in their milestones.

## What "ready" doesn't mean
- **It isn't v1.0.** §0.3 production-quality criteria aren't met: no corpus run, no performance budgets on R1/R2/R3, no 72-hour fuzz, and no manual screen-reader pass.
- **Nothing has been submitted.** Account creation, name reservation, identity values, the first submission and going public are the user's steps ([runbook](../store/README.md)). Agents don't create accounts or submit.
