# Store listing (en-US)

Paste into Partner Center. Use the same text for en-AU and en-GB; "colour" isn't used here, so nothing changes. Replace "MikePDF" throughout if a different name is reserved. Keep this honest: update it only when a feature has shipped.

## Description
MikePDF is a private PDF reader for Windows, built to keep untrusted documents away from your files.

Every PDF is opened inside a locked-down sandbox with no access to your files, your network or other apps. If a damaged or malicious document crashes the renderer, MikePDF recovers and carries on.

MikePDF has no account, no ads, no upsell and no telemetry. It never connects to the internet. Your documents stay on your PC. It's also open source, so you can read exactly how it works.

**This is an early preview.** It's for reading: annotations, form filling, printing and saving aren't in yet, and they're being worked on in the open.

## What's in this preview ("Product features", one per line)
- Each PDF opens in its own sandbox, with no access to your files or network
- Continuous scrolling, with pages rendered in the background
- Zoom from 10% to 6400%, fit to width or page, actual size (as accurate as your display reports its size), and rotate the view
- Back and forward through where you've been (Alt+Left and Alt+Right, the mouse's side buttons), and links that use named destinations
- Dark pages and sepia for night reading, a full screen mode (F11), and a document properties dialog (Ctrl+D)
- Thumbnails, bookmarks and internal links for getting around
- Select and copy text (a document's owner restrictions on copying are respected), and find words in the document, whole words only or with a regular expression
- Open password-protected PDFs
- Keyboard navigation, with page text exposed to assistive technology
- Picks up where you left off in each document
- No account, no ads, no telemetry, and no internet connection needed
- Native on Windows 11, on both x64 and ARM64
- Open source under the Apache 2.0 licence

## Short description
A private, open-source PDF reader that opens every document in a locked-down sandbox.

## What's new in this version
First preview release.

## Keywords
PDF, PDF reader, PDF viewer, document viewer, open source, private, secure

## Other fields
| Field | Value |
|---|---|
| Category | Productivity |
| Website | https://github.com/mjtpena/MikePDF |
| Support contact | https://github.com/mjtpena/MikePDF/issues |
| Privacy policy | https://github.com/mjtpena/MikePDF/blob/main/docs/user/privacy.md |
| Copyright | Copyright 2026 Michael John Peña and the MikePDF contributors |
| Additional licence terms | Apache License 2.0: https://github.com/mjtpena/MikePDF/blob/main/LICENSE |
| Accessibility | Tick "accessible" only after the manual Narrator and NVDA pass (spike S7) has been run |
| System requirements | Windows 11 version 24H2 or later (the package's minimum is 10.0.26100.0), x64 or ARM64 |

Left out until tested: speed and smoothness claims (no measurements on reference hardware yet), "works with screen readers" (no manual pass yet), and light and high-contrast themes (implemented, but only dark has been seen running).

## Images
- 1:1 tile: `build/store/assets/store-listing/tile-1080x1080.png` and `tile-300x300.png`
- Screenshots: `docs/store/screenshots/` (PNG, at least 1366×768). One is required, four or more recommended.
