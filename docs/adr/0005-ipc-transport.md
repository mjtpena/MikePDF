# ADR-0005: IPC transport and serialisation
- Status: Accepted (design). Performance gates provisional until R1/R2 runs
- Date: 2026-10-28
- Deciders: Opus (approver), Sonnet (S3 evidence)

## Context
§5.4 specifies:
- named pipes with a random per-session name and an ACL for the user plus the AppContainer SID
- MessagePack with a versioned schema
- `requestId`, `documentId`, `deadlineMs` and a cancellation token on every message
- a shared-memory ring of fixed slots for large payloads
- UI-side validation of every response, treated as untrusted
- fuzzing on both sides

[S3](../spikes/S3.md) built this between a C# launcher and a .NET engine stub inside a zero-capability AppContainer, on DEV under 18–34% background load:
- Ping RTT p50 19–37 µs, p99 86–122 µs (n = 10,000, four runs).
- RenderTile transport overhead, excluding the render, p50 about 0.08 ms and p99 about 0.25 ms. Zero-copy rendering into the section works.
- A synchronous pipe handle shared by two threads **deadlocks**.
- MessagePack-CSharp 3.1.12 (source-generated) decodes a 400 KB message in about 0.4 ms p50. A flat custom reader is 2.5–4× faster.
- `FPDF_LoadCustomDocument` caps documents at **4 GiB**, because `FPDF_FILEACCESS::m_FileLen` is `unsigned long`, which is 32-bit on Windows.

## Options considered
1. **MessagePack for control messages, fixed slots for bulk data** (§5.4 as written).
2. A fixed-layout custom binary protocol. Faster to decode and trivially bounds-checked, but schema evolution is hand-rolled and it deviates from §5.4.
3. Protobuf/FlatBuffers. Adds a toolchain and dependency for no measured benefit.

## Decision
**Option 1**, with these rules:
1. **Transport.**
   - Native `CreateNamedPipe` with a 128-bit random name, `FILE_FLAG_FIRST_PIPE_INSTANCE`, and `PIPE_REJECT_REMOTE_CLIENTS`.
   - A DACL granting the current user plus the engine's AppContainer SID only.
   - After connecting, the broker checks the client's PID (`GetNamedPipeClientProcessId`) against the process it spawned.
   - **Overlapped I/O on both ends.** Never share a synchronous handle between threads.
2. **Two lanes in the engine:**
   - The I/O thread answers `Hello`, `Ping` and `CancelRender` directly, so the heartbeat (§5.2 rule 4) keeps working during a long render.
   - Everything that touches PDFium goes to the single engine thread.
3. **Serialisation.**
   - MessagePack, using one schema file in `src/MikePDF.Protocol` that generates both the C# (MessagePack-CSharp source generator, MIT) and the C++ code.
   - The C++ side uses a **MikePDF-generated, bounds-checked reader and writer** over a small MessagePack core, not a general-purpose library. That keeps the C++ attack surface to code we fuzz. If a library is preferred later, it needs an ADR naming it and its licence.
   - Every message carries `schemaVersion`. `Hello` negotiates it. On a mismatch the engine is killed, not accommodated.
4. **Bulk data goes through the slot ring.** Tiles, thumbnails, OCR page images, **and any response that can exceed 64 KiB**, such as `GetCharBoxes` and `GetPageText`. Control messages carry slot indices only. Slots are written by the engine and mapped read-only in the UI.
5. **Validation in the UI.** Generated decoders enforce maximum lengths, counts and nesting per field, and reject unknown enum values. Semantic validators then check coordinates, page indices and slot bounds before anything reaches view models.
6. **Document loading.**
   - The broker passes a read-only file handle, opened deny-write.
   - The engine maps it with `CreateFileMapping`/`MapViewOfFile` and loads it with **`FPDF_LoadMemDocument64`**, which removes the 4 GiB limit.
   - `FPDF_LoadCustomDocument` is kept only as a fallback for streamed sources.
   - M1 must verify that `FPDF_LoadMemDocument64` doesn't copy the buffer and handles a mapping larger than 4 GiB (§18 scenario 10 uses 1.2 GB; master §0 implies bigger files will appear).
7. **Fuzzing.** SharpFuzz on the C# decoder and libFuzzer on the C++ decoder from M1 (§12.6). The schema generator emits a fuzz harness for each message.

## Consequences
- §5.4 holds as written. There's no scope change.
- The C++ MessagePack core and its generator are MikePDF-owned code. That's a small but security-critical component, so the A.9 review applies.
- Responses that might exceed 64 KiB need slot allocation logic and back-pressure.

## Verification
- On R1 and R2: Ping p99 ≤ 1 ms, and RenderTile transport p99 ≤ 1 ms.
- M1 IPC fuzzers run 24 h with no crashes (the M1 gate).
- A test loading a document larger than 4 GiB through a mapped view.

## Amendment A1 (2026-11-02): reading data goes in bounded windows, not through the slot ring (WP-S2, schema version 3)
Decision 4 says any response that can exceed 64 KiB, "such as `GetCharBoxes` and `GetPageText`", goes through the slot ring. The reading messages added in schema version 3 (`GetOutline`, `GetPageLinks`, `GetLinkUri`, `GetPageBoxes`, `GetPageText`, `SearchPage`) do **not**. Each reply is bounded by its own schema limits so that it always fits one control frame, and a long list is read in **windows** that the broker asks for one at a time.
- **Windows.** Bookmarks: 64 siblings per request, a path of at most 16 sibling indices, addressed by position, never by a handle, so a restarted engine can be asked again. Links: 512 per request. Page text: 2048 UTF-16 units per request, 200000 units per page at most (`truncated` says when the page had more). Search: at most 2048 matches per page. A link address: 2048 bytes. Every reply names the `total`, and the broker checks the window against what it asked for (`ReplyChecks`): a list that does not end where the total says, parallel arrays of unequal length, a reply longer than asked and an index outside the document are all protocol violations: the session ends and the supervisor restarts the engine, as for any other.
- **Why not slots.** A slot is a mapped, read-only view of memory the engine owns while the broker reads it: a lifetime to manage, and an engine that can change the bytes after they were validated. A windowed reply is copied out of the frame by the generated, bounds-checked decoder and then belongs to the broker alone. At the largest window a frame is about 50 KiB (2048 units, each a 16-bit value, four 32-bit floats and a flag byte, MessagePack encoded), under the 64 KiB at which this ADR wanted slots, and a page of ordinary text is one window.
- **Cost.** A page above 2048 units takes more than one round trip; `PageDataCache` single-flights them and reads the pages near the one in view ahead of time, on the shared pool engine, so no document waits for another and the document's own engine is never asked for text. Measured text for a typical page arrives well within a frame's budget; no timing gate is claimed here (the performance gates in "Verification" stay provisional).
- **What stays as written.** Tiles and thumbnails still use the slot ring (`RenderTile`, a thumbnail is a tile on the pool engine). Decision 5 holds and is stricter than before for the new messages: every array carries `maxCount`, every value a range, every string `maxBytes`, parallel arrays `sameLengthAs`; the generated decoders reject anything else and the golden and malformed-frame tests cover each new message in both languages.
- **If this changes.** Pages with more than 200000 units, or a measured need for fewer round trips, would reopen it: the window sizes are schema constants, and a slot-backed variant would be a new message type under a new schema version.

## Amendment A2 (2026-11-02): schema version 5 (W2-engine)
- `Pong` gains `progress`, a counter the engine's work thread moves and the I/O thread reports (ADR-0004 amendment A3 explains how the broker uses it). Hosts that do their work on the I/O thread report 0.
- New engine messages: `GetViewerPreferences` (28; the print spec called it 26, which `GetDocumentDetails` took in schema 4) and `RenderBand` (34, for printing, ADR-0011: its own limits, up to 65536 pixels wide and 16384 rows, `flags` up to 7 so that the reserved bit is refused by the decoder). Both are checked again in the broker (`ReplyChecks`, `EngineClient.ValidateBand`).
- `ProcessKind.ShellHandler = 3` and `ShellOpen` (2048), `ShellRenderPage` (2049), `ShellClose` (2050): the handler's pipe protocol as [ADR-0018](0018-shell-handler-transport.md) and the S6b spike ("Protocol: messages and schema v5") define it. The connection is the session; `ShellOpen` has no document and its reply's `documentId` is carried in the envelope of every later message, as for the engine. The handle values in `ShellOpen.section` and `ShellRenderPage.pixels` are valid in the **handler** (the sender): ShellHost duplicates them from the process at the other end of the pipe. Limits: the file at most 32 MiB, tiles 16 to 1024 pixels, a pixel section at most 4 MiB; the reply's `width <= maxWidth`, `height <= maxHeight` and `stride == 4 x width` are the handler's checks, since the schema cannot tie a reply to its request. An engine refuses a `Hello` whose `processKind` is `ShellHandler` (`AShellHandlerCannotSpeakToAnEngine`), and `OpenDocument` has no section source (ADR-0018). `EngineKind::Shell` and ShellHost itself belong to the shell-extensions work package.
- The C++ side already had both halves of the codec for every message (`encode_request_frame` and `decode_response_frame` are what a client uses), so the shell handler needs no new generator output; a test runs a handler's whole session through them.

## Amendment A3 (2026-11-03): documents are opened with `FILE_SHARE_READ | FILE_SHARE_DELETE` (S10, work package W3-save-core)
Decision 6 said "a read-only file handle, opened deny-write". It stays deny-write, which [S10](../spikes/S10-file-replace.md) measured: no other process can write, append or truncate a file while the broker's handle is open. It now **also grants `FILE_SHARE_DELETE`**, because a rename-based replace cannot replace a file whose open handle lacks it. With the flags as built (`FileShare.Read`), every mechanism (`ReplaceFileW`, `MoveFileEx`, a POSIX-semantics rename, rename-away-and-in) failed with a sharing violation or access denied, with the real broker and the real sandboxed engine (S10 section 3.1). The engine's mapped view does not block replacement; the sharing mode of the broker's handle is the only blocker.
- **Consequences.** Other processes may now rename, delete or replace the path while a document is open (they could not before). They still cannot change the bytes the broker and the engines hold: the old file object lives on until its last holder closes, and the engines keep reading the original bytes until they are reopened. Before replacing, the broker compares the path's file ID with its own handle's and asks when they differ ([save-pipeline](../features/save-pipeline.md) 3.6 item 3). As a side effect MikePDF stops blocking Explorer renames, OneDrive updates and other programs' atomic saves while a document is open.
- **Scope.** The same flags apply to every handle the broker opens on a document: the engine's (`EngineSupervisor.OpenDocumentAsync`), the writer host's (`WriterHostClient`) and the verification handles the save pipeline opens. `FileShare.ReadWrite | FileShare.Delete` was rejected: it would lose the immutability of the contents while a document is open. `FileShare.None` was rejected: it would stop MikePDF's own verification and the writer host's handles.
- **The engines need not be closed before a replace**, so the rebase after a save can be gapless.

## Amendment A4 (2026-11-03): schema version 6, the writer host's save messages (W3-save-core)
`schemaVersion` is 6. New messages, all for the writer host (`ProcessKind.WriterHost`); an engine answers none of them:
- `AppendAnnotations` (1025): appends one incremental update for the annotation edits in the edit blob to the base and writes base plus update to the output. `baseHandle`, `editsHandle` and `outputHandle` are file handles duplicated into the host (the first two read-only, the last writable and empty); `editsLength` is the blob's length (at most 256 MiB); `pageCount` is the base's page count the edits were made against. The reply carries counts, `bytesWritten` (at most 2 GiB: the writer refuses larger files) and, when `ok` is false, the reason as `issueCodes` and `issueMessages` in the shape of `ValidateStructureReply`.
- `RewriteDocument` (1029): a full rewrite of the base into the output, for Save As "Rewrite the whole file" and the flattened copy. `flags`: bit 0 flatten form fields, bit 1 flatten comments and markup (at most 3).
- `VerifyAnnotations` (1030): a second, independent strict parse of the base and the output; for up to 2048 named pages it compares the output's annotation count (links, popups and widgets excluded) with the base's plus the model's additions minus its removals, and names the pages that differ.
- **A refusal with a reason is an `Ok` reply with `ok` false**, because a status other than `Ok` carries no payload. `InvalidArgument` is for a handle that is not a file, an edits length that is not the file's, and a blob that does not decode; `Timeout` and `PermissionDenied` as for `ValidateStructure`.
- **The edit blob is not a frame.** It is a second MessagePack format read from a file handle, so the 64 KiB frame limit and "messages have no nesting" do not apply to it. It is written and read by hand on the strict primitives of the frame codec, not generated (one consumer; a second generator would cost more than the codec), with every limit a constant beside the codec and pinned by tests; it has its own fuzz target. This settles OQ-SP-3 in favour of a second format.
- **Not in schema 6:** `NormalizeTail` (1026), `ImportStampImage` (1027), `AssemblePages` (1028) and the engine's `SaveDocument` (62). Forms and page changes need the engine's PDFium save and the tail normaliser; they belong to the forms and page-organiser work packages, which bump the schema when they add them. ids 1026 to 1028 stay reserved.