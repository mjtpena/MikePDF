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
   - MessagePack, using one schema file in `src/Quire.Protocol` that generates both the C# (MessagePack-CSharp source generator, MIT) and the C++ code.
   - The C++ side uses a **Quire-generated, bounds-checked reader and writer** over a small MessagePack core, not a general-purpose library. That keeps the C++ attack surface to code we fuzz. If a library is preferred later, it needs an ADR naming it and its licence.
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
- The C++ MessagePack core and its generator are Quire-owned code. That's a small but security-critical component, so the A.9 review applies.
- Responses that might exceed 64 KiB need slot allocation logic and back-pressure.

## Verification
- On R1 and R2: Ping p99 ≤ 1 ms, and RenderTile transport p99 ≤ 1 ms.
- M1 IPC fuzzers run 24 h with no crashes (the M1 gate).
- A test loading a document larger than 4 GiB through a mapped view.
