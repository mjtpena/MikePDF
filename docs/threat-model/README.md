# Quire threat model (skeleton)

- Status: Skeleton, M0. Owner: Opus. Full STRIDE review due at M5 (§13.1, §17)
- Method: STRIDE per trust boundary. Each mitigation links to the ADR or spike that provides it
- Last updated: 2026-10-27

## Assets
1. The user's documents, and the integrity of their saves (no destructive writes, §0.2 rule 5)
2. The user's account context: files, network identity (NTLM), credentials, clipboard
3. Signing keys in the Windows certificate store, smart cards, and Windows Hello (P1)
4. Local data: edit journals (§7.4), session state, the AI index (P1)
5. The integrity of Quire's update channel and binaries

## Trust boundaries
| # | Boundary | Untrusted side | Trusted side | Primary control |
|---|---|---|---|---|
| B1 | PDF bytes → engine | Document content | Engine process | PDFium in a sandboxed engine (ADR-0004, S3) |
| B2 | Engine → UI/broker (IPC) | Engine (assume compromised) | UI/broker | Validate every response. Pipe DACL. Tile slots are the only shared memory (ADR-0005) |
| B3 | Shell extension → engine | Explorer-hosted handler (any file) | Sandboxed engine | Out-of-proc engine with timeouts (§8.11) |
| B4 | Document → OS actions | Links, launch actions, attachments | OS shell, network | Confirmation, scheme allowlist, MOTW (§13.1) |
| B5 | Document text → AI model | Prompt-injection content | AI layer | Delimited data, no acting tools (§9.5) |
| B6 | Update feed → installed app | Network | Package | Signed MSIX over HTTPS (ADR-0015) |
| B7 | Policy registry → app behaviour | Local admin / GPO (trusted) | App | HKLM-only policy reads (§7.7) |

## STRIDE starter table (expand at M1/M5)
| Boundary | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| B1 | n/a | Malformed PDF corrupts engine memory | n/a | Engine reads files beyond the passed handle (blocked by AppContainer, S3 negative suite) | Hang or memory bomb (watchdog, Job Object limit) | Parser RCE → sandbox escape (mitigation policies, fuzzing) |
| B2 | Fake engine on the pipe (DACL, random name, PID check) | Forged responses: bad sizes, counts, coordinates (validation, IPC fuzzing) | n/a | Engine exfiltrates via shared memory (one-way slots) | Flooding the UI with responses (bounded queues) | Overflow in the UI decoder (SharpFuzz on the decoder) |
| B3 | n/a | n/a | n/a | Thumbnail of a sensitive file shown | Explorer hang (2 s timeout) | Handler in Explorer's process (never host PDFium in-proc) |
| B4 | Phishing link text (show the full URL) | n/a | n/a | NTLM leak via UNC or `file://` (blocked by default) | n/a | Launch action runs an executable (never executed) |
| B5 | n/a | Injection changes an answer (eval red-team set) | n/a | Cloud provider receives document text (opt-in plus disclosure) | n/a | AI triggers an action (no tools) |
| B6 | Fake update server (HTTPS + signature) | Tampered package (MSIX signature) | n/a | n/a | n/a | Downgrade (App Installer version rules) |

## Feature-specific threats (owned by feature specs)
- Redaction leakage: §8.6 verification gate. The adversarial fixtures are an M6 deliverable.
- Signature spoofing (shadow and incremental-save attacks): S8 fixtures, then ADR-0012 and the M6 validator.
- Journal and AI-index exposure: per-user app data, DPAPI option, clear-data command.

## Open items
- [ ] Fill each STRIDE cell with concrete test references once S3 and S8 report
- [ ] Data-flow diagram per boundary (M1)
- [ ] Security review gate checklist (A.9) wired into the PR template (M1)
