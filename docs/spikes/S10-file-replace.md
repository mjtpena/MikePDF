# S10: file replace semantics for Save

- Status: **Complete for local NTFS, a loopback SMB share and a OneDrive Files On-Demand folder on this machine (x64).** ARM64, ReFS/FAT/exFAT, SharePoint-synced folders, WebDAV, mapped network drives and power loss are **not measured**
- Date: 2026-11-02
- Author: Sonnet (spike). Reviewer: Opus
- Target documents: ADR-0005 decision 6, ADR-0008 decision 3 step 6, §7.5 steps 1 and 3, [save-pipeline](../features/save-pipeline.md) 3.6 and 7 (OQ-SP-4), SP-16, SP-17, SP-20, SP-36
- Repo state: branch `m4/spikes-save`, base `50dfa75`
- Code: `spikes/S10/` (throwaway; nothing under `src/` is changed). Raw output is written to `spikes/S10/out/raw/` (gitignored, re-created by the commands in section 13); the files that back this report are copied to `spikes/S10/results/` (the `.jsonl` files hold every row; `tables-*.md` are the tables below)

---

## 1. Questions and short answers

| # | Question | Short answer | Where |
|---|---|---|---|
| 1 | Can a save replace the document while the broker holds it open as ADR-0005 does and the engine has a mapped view? | **No.** The broker opens the document with `FileShare.Read` only (`EngineSupervisor.cs:231`). With that, `ReplaceFileW`, `MoveFileExW`, a POSIX-semantics rename and a rename-away-and-in swap **all fail** (sharing violation 32 or access denied 5), with the engine's mapped view and also with the broker's handle alone. Confirmed with the real broker and the real sandboxed engine. The mapped view by itself does **not** block anything; the sharing mode on the broker's handle does. | 3 |
| 2 | Which sharing flags make it work, and what does that cost in safety? | **`FILE_SHARE_READ \| FILE_SHARE_DELETE`** on the broker's handle (.NET `FileShare.Read \| FileShare.Delete`, confirmed by observation). Then `ReplaceFileW` and a POSIX-semantics rename (`FileRenameInfoEx`) succeed **while the engines still map the old file**; the engines keep reading the old bytes. The cost: another process can then **rename, delete or replace the path** while the document is open (it still cannot write to the file or truncate it, and attribute-only opens such as touching the timestamp always work). So: contents are still immutable while we hold them, the path is not. | 4 |
| 3 | What survives the replace? | `ReplaceFileW` keeps the DACL, `Zone.Identifier` and other streams, attributes (including compressed) and the creation time, and loses the file ID and hard links. A plain rename (`MoveFileEx`, `FileRenameInfoEx`) keeps **none** of them: the new file's ACL, no `Zone.Identifier`, no `NotContentIndexed`, a new creation time, and **it carries the temp file's `Hidden` attribute onto the user's file**. A rename after copying the metadata to the temp file keeps everything `ReplaceFileW` keeps. Hard links break with every mechanism; a symlink at the path is replaced by a regular file (the rename mechanisms) or refused with error 1464 (`ReplaceFileW`), unless the link is resolved first and its target replaced. | 5 |
| 4 | OneDrive Files On-Demand folder? | A POSIX rename under an R\|D handle with a mapped view works in a OneDrive folder, replacing an online-only placeholder with `ReplaceFileW` works, no conflict copy and no stray file appeared in any sample, and 1 of 40 rapid saves failed with a transient access denied (5). Versions and conflicts on the server side are **UNVERIFIED** (no API without signing in), and the placeholder state could not be read from the test process. | 6 |
| 5 | SMB share? | Tested through the existing administrative share `\\localhost\C$` (loopback, I created nothing). `ReplaceFileW` works under R\|D with a mapped view; **POSIX rename fails with 87 (invalid parameter) over SMB**; `MoveFileEx` fails whenever another handle is open. So `ReplaceFileW` is the fallback, not `MoveFileEx`. A loopback share is not a remote server: remote behaviour is **UNVERIFIED**. | 7 |
| 6 | Crash safety? | A kill at each of 9 steps of a simulated save, with 4 mechanisms and 2 journal designs (72 runs): after recovery the target is always the old or the new file, never missing or torn. **The journal as specified leaks the temporary file for a kill between its creation and the `SaveBegin` record** (12 of 36 runs), because the spec deletes only files a record names. A kill loop (600 kills per mechanism): `ReplaceFileW` left the target **missing in 17**, a POSIX rename and `MoveFileEx` **in 0**, the rename-away swap in 211. | 8 |

**Recommendation (section 9):** open documents with `FileShare.Read | FileShare.Delete`; replace with `SetFileInformationByHandle(FileRenameInfoEx, REPLACE_IF_EXISTS | POSIX_SEMANTICS)` on a temp file whose metadata was first copied from the original; fall back to `ReplaceFileW` (with a journalled backup name) where the POSIX rename is not supported; add a `SaveIntent` journal record before the temp file is created. Do not use `MoveFileEx` or the swap. The engines do not need to be closed first, so the rebase can be gapless.

---

## 2. Method

### 2.1 Environment

| Item | Value |
|---|---|
| Machine | Intel Core Ultra 9 275HX, Windows 11 Home build 26300, x64, not admin, Developer Mode on. Volume: local NTFS (`C:`) |
| .NET | SDK 10.0.300, `net10.0-windows` (probe) and `net10.0-windows10.0.26100.0` (real-engine run) |
| Win32 | `CreateFileW`, `ReplaceFileW`, `MoveFileExW`, `SetFileInformationByHandle` (`FileRenameInfo`, `FileRenameInfoEx` with `FILE_RENAME_FLAG_REPLACE_IF_EXISTS` and `_POSIX_SEMANTICS`, `FileDispositionInfoEx`), `CreateFileMappingW`/`MapViewOfFile`, `FindFirstStreamW`, `DeviceIoControl(FSCTL_SET_COMPRESSION)`. Declarations checked against the Windows SDK headers |
| New dependencies | none. The real-engine run compiles the repo's `MikePDF.Broker` sources into the spike and therefore needs `Microsoft.Windows.CsWin32` 0.3.367, which is **already a repo dependency** (MIT) |
| Real engine | `build/out/cmake-x64/bin/Release/MikePDF.Engine.exe` and `pdfium.dll` built by `./build.ps1` on this branch, launched by the real `EngineSupervisor` into a throw-away AppContainer profile (`MikePDF.S10Spike`, deleted at the end) |

### 2.2 What ADR-0005 actually does (read from the code, not the ADR)

- The broker opens a document with `File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read)` (`src/MikePDF.Broker/EngineSupervisor.cs:231`), keeps the handle for the document's lifetime, and gives the engine a duplicate (`DuplicateIntoEngine`). `WriterHostClient.cs:71` and `WarmUp.cs:34` open with the same flags.
- The engine (`src/MikePDF.Engine/mapped_file.cpp`) calls `CreateFileMappingW(PAGE_READONLY)` and `MapViewOfFile(FILE_MAP_READ)` on the duplicate, then **closes the file handle and the mapping handle** and keeps the view. PDFium reads the view (`FPDF_LoadMemDocument64`).
- Consequence: the file object lives on in two places: the broker's handle (sharing mode `Read`) and the engine's section (no handle). Section 3 tests each separately.
- The `.NET` flag mapping was measured, not assumed (`netshare`): `FileShare.Read` lets another process open for reading only and **denies rename**; `FileShare.Read | FileShare.Delete` lets it rename and still denies write; `ReadWrite | Delete` allows everything; `None` denies even reading.

### 2.3 The harness (`spikes/S10/Probe`)

- **Holder x mechanism matrix.** 10 holder configurations (nothing; the broker's handle alone with R and R\|D; broker plus an in-process view; broker plus a view in a **separate process** that received the handle by `DuplicateHandle` and maps it the way the engine does; view only with the broker's handle closed; R\|W\|D) against 8 mechanisms (`ReplaceFileW`, with a backup name, `MoveFileEx` with replace-existing, `FileRenameInfoEx` with and without POSIX semantics, legacy `FileRenameInfo`, rename-away-then-in, POSIX-delete-then-rename). Each in a fresh directory with a 256 KiB original and a hidden temp file. After each run the holders re-read the old file (hash).
- **What can another process do** (`attack`): a child process tries write-open, append, timestamp change, rename, delete, POSIX delete, `ReplaceFileW` and read-open against each broker sharing mode, with and without a view.
- **Metadata** (`meta`): the original gets a protected DACL (user, SYSTEM, Authenticated Users read), `Zone.Identifier` (ZoneId 3 and host URL) and a custom stream, attributes Hidden, Archive, NotContentIndexed, creation time 2001, last write 2020; variants read-only, compressed, symlink, hard link. Compared before and after, and with the temp file's own values.
- **Crash** (`crash`, `stress`): `savesim` runs the save steps (SaveIntent record, create temp, write half, write rest and flush, validate and `SaveBegin` record, release engines or rename away, replace, cleanup and `SaveCommitted`) and kills itself with `TerminateProcess` at step k; the parent then runs the recovery rule of save-pipeline 3.6 and 3.5 and inspects the directory. `stress` replaces in a loop while the parent kills the process at a random moment (5 to 120 ms), 600 times per mechanism.
- **Real engine** (`RealEngine`): the same mechanisms against the real engine, with the broker's open flags as built and as proposed (one patched line in a copy of `EngineSupervisor.cs`, generated into `out/gen` by `run-real-engine.ps1`).
- Not done: power loss, disk-full, locked-by-antivirus, large files (all `UNVERIFIED`), ARM64, anything needing admin.

---

## 3. Question 1: replace while the broker and the engine hold the file

Local NTFS, per holder configuration x mechanism (`OK` = the replace succeeded and the old holder still read the old bytes; `fail n` = Win32 error n; 32 = sharing violation, 5 = access denied):

| holder | ReplaceFileW | ReplaceFileW+backup | MoveFileEx(REPLACE_EXISTING) | FileRenameInfoEx(REPLACE\|POSIX) | FileRenameInfoEx(REPLACE) | FileRenameInfo(ReplaceIfExists) | swap: rename away, rename in | POSIX delete, rename in |
|---|---|---|---|---|---|---|---|---|
| none | OK | OK | OK | OK | OK | OK | OK | OK |
| broker handle R, no view | fail 32 | fail 32 | fail 5 | fail 32 | fail 5 | fail 5 | fail 32 | fail 32 |
| broker handle R\|D, no view | OK | OK | **fail 5** | OK | **fail 5** | **fail 5** | OK | OK |
| **broker R + engine view (ADR-0005 as built)** | **fail 32** | fail 32 | fail 5 | fail 32 | fail 5 | fail 5 | fail 32 | fail 32 |
| broker R\|D + engine view | OK | OK | fail 5 | OK | fail 5 | fail 5 | OK | OK |
| broker R\|W\|D + engine view | OK | OK | fail 5 | OK | fail 5 | fail 5 | OK | OK |
| engine view only (broker closed, opened R) | OK | OK | OK | OK | OK | OK | OK | OK |
| engine view only (broker closed, opened R\|D) | OK | OK | OK | OK | OK | OK | OK | OK |
| broker R + view in a separate ENGINE PROCESS | fail 32 | fail 32 | fail 5 | fail 32 | fail 5 | fail 5 | fail 32 | fail 32 |
| broker R\|D + view in a separate ENGINE PROCESS | OK | OK | fail 5 | OK | fail 5 | fail 5 | OK | OK |

After every successful replace the broker's handle and the (in-process and separate-process) view still read the **old** bytes (0 anomalies in the matrix pass), and a fresh open of the path reads the new ones. The old file is freed when the last holder closes: no leftover file in any case except the deliberate `.bak`.

Findings:

1. **The mapped view never blocks a rename-based replace.** "Engine view only" succeeded with every mechanism. Mapping a data file does not prevent replacing or deleting it on this Windows build; the broker's sharing mode is the only blocker.
2. **`FILE_SHARE_DELETE` on the broker's handle is necessary and, for `ReplaceFileW` and the POSIX rename, sufficient.**
3. **`MoveFileEx(REPLACE_EXISTING)` and the non-POSIX rename fail with access denied whenever any other handle is open on the target**, even one with delete sharing. They only work when the broker's handle is closed. They are not usable for this design.
4. **The POSIX-semantics rename is the one that replaces a file that others have open**, in one system call.
5. Repeats: 30 runs of each proposed combination (R\|D with the in-process view and with the separate-process view; `ReplaceFileW`, with backup, POSIX rename, swap, POSIX-delete-then-rename): 30 of 30 OK. As built (R): 30 of 30 `fail 32`. One `ReplaceFileW` over the separate-process view (R\|D) failed once with **1175** (`ERROR_UNABLE_TO_REMOVE_REPLACED`) in the first full pass and in none of the 50 later repeats of that combination: treat 1175 as retryable. The cause was not investigated.

### 3.1 The same with the real broker and the real sandboxed engine (`results/real-engine.txt`)

The real `EngineSupervisor` opens a 2-page PDF; the real engine maps it inside its AppContainer. Then each mechanism is tried from the test process. In every row the engine **still serves the old document** (`GetInfo` OK with 2 pages, a tile renders OK).

| Broker open flags | ReplaceFileW | +backup | MoveFileEx | POSIX rename | swap | close the document first, then ReplaceFileW |
|---|---|---|---|---|---|---|
| `FileShare.Read` (as built) | fail 32 | fail 32 | fail 5 | fail 32 | fail 32 | REPLACED |
| `FileShare.Read \| FileShare.Delete` (proposed) | **REPLACED** | **REPLACED** | fail 5 | **REPLACED** | **REPLACED** | REPLACED |

A new engine opening the path afterwards reads the new file (1 page) in every successful case. (A document-kind engine holds one document, so the check uses a second engine, which is also what the save pipeline's validation does.) This is the evidence for ADR-0005 A2 below.

---

## 4. Question 2: sharing flags and what they cost in safety

What another process can do to the file while the broker holds a handle (`attack`; "view" = engine mapped view; every row looked identical with and without the view):

| Broker share | write-open | append-open | touch timestamp (attribute-only open) | rename | delete | POSIX delete | `ReplaceFileW` over it | read-open |
|---|---|---|---|---|---|---|---|---|
| R (ADR-0005 as built) | denied (32) | denied (32) | **allowed** | denied (32) | denied (32) | denied (32) | denied (32) | allowed |
| **R\|D (proposed)** | denied (32) | denied (32) | **allowed** | **allowed** | **allowed** | **allowed** | **allowed** | allowed |
| R\|W\|D | allowed | allowed | allowed | allowed | allowed | allowed | allowed | allowed |
| none | denied | denied | allowed | denied | denied | denied | denied | **denied** |

In every row where another process succeeded, the broker's handle and the view kept reading the **original bytes** (the old file object lives on).

What this means for safety:

1. **Contents stay immutable under R\|D.** No other process can write, append or truncate while we hold the handle (denied 32). The property ADR-0005 wanted from "deny-write" is kept. `R|W|D` would lose it and is rejected.
2. **The path is no longer protected.** Another program (Explorer rename, OneDrive, an editor's atomic save, an attacker with the user's rights) can rename, delete or replace the file at the path while it is open. With the as-built `R`, MikePDF blocks Explorer renames, OneDrive's updates and other programs' atomic saves for as long as a document is open; with R\|D it does not. Measured on the as-built flags: 4 of 4 of those operations are denied (32), which is a user-visible cost of the current design, separate from Save.
3. **A fingerprint by timestamp is not safe**, because attribute-only opens can change timestamps under any sharing mode. Because the contents cannot change while the handle is held, the safe test before replacing is **identity, not contents**: open the path with a verification handle (`FILE_READ_ATTRIBUTES`, share all), compare `FileIdInfo` (volume serial plus 128-bit ID) with the broker's own handle. Equal means the path still names the file whose bytes we have held since open. Different means somebody replaced the path: prompt (save-pipeline 3.8). A TOCTOU window remains between this check and the rename, bounded by microseconds; a replacement landing exactly there is overwritten without the prompt. It cannot corrupt a file, only discard a concurrent change.
4. **A deleted or renamed original does not break the open document** (the engines keep serving it). The save then has no target: report it ("{name} was moved or deleted"), offer Save As.
5. `FileShare.None` on the broker's handle would also stop MikePDF's own verification and the writer host's handles; not an option.

---

## 5. Question 3: what survives the replace

Original: protected DACL (explicit ACEs), `Zone.Identifier`, a custom stream, attributes Hidden+Archive+NotContentIndexed, creation time 2001. The temp file has the directory's inherited DACL, no streams, Hidden, a current creation time. Local NTFS:

| Mechanism | DACL | `Zone.Identifier` and other streams | Attributes | Creation time | File ID | Hard links |
|---|---|---|---|---|---|---|
| `ReplaceFileW` (also with `IGNORE_MERGE_ERRORS \| IGNORE_ACL_ERRORS`) | **kept** | **kept** | **kept** (incl. Compressed) | **kept** | new | broken (link count 2 to 1, the other name keeps the old content) |
| `MoveFileEx(REPLACE_EXISTING)` | temp's (original lost) | **lost** | temp's: **NotContentIndexed lost; Hidden is whatever the temp had** (the original here was Hidden too; the carry-over is shown by the real-engine and OneDrive runs) | temp's | new | broken |
| `FileRenameInfoEx(REPLACE\|POSIX)` | temp's | **lost** | temp's | temp's | new | broken |
| rename away, rename in | temp's | **lost** | temp's | "kept", but almost certainly NTFS file-name tunneling (the same name reappeared within 15 seconds), which restores the creation time; not verified and not something to rely on | new | broken |
| `ReplaceFileW` after copying the metadata | kept | kept | kept | kept | new | broken |
| **`FileRenameInfoEx(REPLACE\|POSIX)` after copying the metadata to the temp file** | **kept** | **kept** | **kept** except Compressed (not copied; `FSCTL_SET_COMPRESSION` could) | **kept** | new | broken |

Variants:
- **Read-only original:** `ReplaceFileW`, `MoveFileEx` and the POSIX rename all fail with access denied (5). The swap **succeeds** (it renames the read-only file away and a new file in): it defeats the read-only attribute and must not be used. Matches the spec: read-only becomes Save As.
- **Symbolic link at the path** (Developer Mode lets the test create one): `ReplaceFileW` fails with **1464**; `MoveFileEx`, the POSIX rename and the swap **replace the symlink itself with a regular file** and leave the real file untouched. Resolving the final path first and replacing **that** keeps the link and updates the real file; with the metadata copy it keeps DACL, streams, attributes and creation time of the real file.
- **Hard link:** every mechanism breaks it (the other name keeps the old bytes, link count falls to 1). Detectable beforehand (`NumberOfLinks > 1`).
- **Compressed original:** `ReplaceFileW` keeps compression; the others do not.
- **File ID:** changes in every case. Anything that tracks files by ID (backup "changed file" detection, search indexers) sees a new file; paths, recents and the position store are unaffected.
- **The `Hidden` finding:** the spec makes the temp file hidden (3.6 item 6). A rename-based replace moves the temp file's attributes onto the user's file: in the real-engine run `doc.pdf` was **hidden after the POSIX rename**, and in the OneDrive run too. **Clear Hidden on the temp file before the rename** (or copy the original's attributes, which the metadata copy does).
- **A .NET trap, recorded because it cost one run:** `new FileInfo(to).SetAccessControl(new FileInfo(from).GetAccessControl(Access))` copies **nothing**, because .NET writes only sections marked modified. Load the descriptor into a fresh `FileSecurity` with `SetSecurityDescriptorBinaryForm(...)` (marks it modified), or call `SetSecurityInfo` directly.
- SMB (loopback): the same table for the mechanisms that work (`ReplaceFileW` keeps everything; `MoveFileEx` and the swap lose DACL, streams and attributes); POSIX rows fail with 87.

---

## 6. Question 4: OneDrive-synced folder

OneDrive for Windows is installed and signed in (personal and two business accounts). **Only the personal account's folder was used** (`C:\Users\mjtpe\OneDrive`), with a test folder `MikePDF-S10-test-<random>` containing 256 KiB synthetic files; the folders were deleted afterwards (ordinary delete, nothing emptied from any recycle bin). Nothing was installed or signed in.

Observed (full logs in `results/onedrive-*.txt`):

| Test | Result |
|---|---|
| POSIX rename under an R\|D handle with a mapped view of the old file, in the synced folder | **OK**; the mapped view still read the old bytes; the path had the new bytes |
| `ReplaceFileW` under the same holders | OK |
| As built (R) + mapped view | **fail 32**, exactly as on the local disk |
| `MoveFileEx` with no holder | OK |
| Replace over an **online-only** (dehydrated, `attrib +U -P`) placeholder with `ReplaceFileW` | **OK** (the recall bits `RECALL_ON_DATA_ACCESS` and `UNPINNED` were visible to the test process). `ReplaceFileW` merges attributes, so the new file kept `UNPINNED`: OneDrive is free to dehydrate it again |
| 40 saves 150 ms apart (create temp, POSIX rename onto the target), OneDrive watching | 39 of 40 OK; **1 failed with access denied (5)**, transient. The retry rule must include 5 |
| Folder contents after each replace (sampled for 90 to 300 s) | only `doc.pdf`; **no conflict copy, no leftover temp file** at any sample |
| One save with 300 s waits before and after (`cloud-focused`): create, wait, POSIX rename under an R\|D handle with a view, wait | rename OK; the old view read the old bytes; the folder listing 300 s later was `[doc.pdf]` only |
| After the replace, as seen from PowerShell | `doc.pdf` had `Archive, ReparsePoint` (a cloud placeholder) within about 3 minutes of the replace: OneDrive accepted the new file at the old name |
| The hidden attribute | `doc.pdf` was `Hidden` after the rename (the temp's attribute), as on the local disk |

Limits, stated plainly:
- **Version history and conflict handling on the server are UNVERIFIED.** They need an authenticated API; I did not sign in to anything. "No conflict copy appeared locally within 90 to 300 s" is all this shows.
- **The probe could not read the placeholder's reparse bit.** A non-cloud-aware .NET process got attributes `0x20` (no reparse point) for a file that PowerShell reported as `Archive, ReparsePoint` at the same moment (`GetFileAttributesW`, `File.GetAttributes` and directory enumeration all gave the same masked answer in the probe). The recall bits (`RECALL_ON_DATA_ACCESS`, `UNPINNED`) **were** visible. So my attempts to time "upload finished" from inside the probe returned "not synced" (60, 120, 300 s) while PowerShell showed the file converted; sync latency is therefore **not measured**. Consequence for the product: §7.5 step 6 ("detect cloud-only placeholders") must test the recall attributes, not the reparse bit, unless MikePDF's process turns out to be cloud-aware (not tested).
- No SharePoint-synced folder was available.

---

## 7. Question 5: SMB

No share was created. The machine already has the default administrative shares (`C$`, `D$`, `ADMIN$`); this user could open `\\localhost\C$` without elevation, so the matrix (subset: no holder, R + view, R\|D + view, R\|D + view in a separate process; mechanisms `ReplaceFileW`, `MoveFileEx`, POSIX rename, swap) and the metadata test were run against a temp directory under `\\localhost\C$\Users\<me>\AppData\Local\Temp`, then deleted. (`results/meta-smb.*` was produced before the DACL-copy fix described in section 5; the rows it affects are the `ReplaceFileW + copy` ones, which merge metadata anyway, and the POSIX rows, which fail.)

| holder | ReplaceFileW | MoveFileEx | FileRenameInfoEx(POSIX) | swap |
|---|---|---|---|---|
| none | OK | OK | **fail 87** | OK |
| broker R + engine view (as built) | fail 32 | fail 5 | fail 87 | fail 32 |
| broker R\|D + engine view | **OK** | fail 5 | **fail 87** | OK |
| broker R\|D + view in separate process | **OK** | fail 5 | **fail 87** | OK |

- **A POSIX-semantics rename is not supported over SMB** (87, `ERROR_INVALID_PARAMETER`). The fallback is `ReplaceFileW`, which works under R\|D with a view and keeps DACL, streams, attributes and creation time (same metadata table as local). The spec's "fall back to `MoveFileExW` only for ERROR_NOT_SUPPORTED" is wrong on both counts: `MoveFileEx` fails with 5 whenever another handle is open, and the error to key on is 87.
- **Loopback is not a network.** The client and server are the same machine, no latency, no oplock breaks across hosts, no disconnects. A real remote share, a mapped drive and DFS are **UNVERIFIED**. `ReplaceFileW` over a remote share is documented not to be atomic across failures; this was not tested.

---

## 8. Question 6: crash safety

### 8.1 Kill at each step (72 runs)

`savesim` hard-kills itself at step k, then the recovery rule runs. Steps: 0 nothing; 1 after `SaveIntent`; 2 temp created; 3 temp half written; 4 temp complete and flushed; 5 validated and `SaveBegin` flushed; 6 engines released (or the target renamed away, for the swap); 7 replace done; 8 `SaveCommitted` and cleanup done. Two journal designs: **literal** (spec 3.3/3.6: one `SaveBegin` record with the output hash, written after validation) and **intent** (a `SaveIntent` record naming the temp file *before* it is created, then `SaveBegin`). Mechanisms: release-then-`ReplaceFileW`, release-then-`MoveFileEx`, release-then-POSIX rename, and the engine-holds-the-view swap.

| Design | Runs | Target after recovery | Leftover files after recovery |
|---|---|---|---|
| literal | 36 | old or new in 36 of 36; never missing, never torn | **12 of 36 leak the temp file** (kill at steps 2, 3 or 4, every mechanism) |
| intent | 36 | old or new in 36 of 36 | **0 of 36** |

- Between a kill and recovery the target was **missing** in 2 runs, both the swap killed between its two renames (step 6); recovery rolled forward (temp complete) in both. All other kills left the old or the new file in place.
- **The leak is a spec bug, found by measurement.** Spec 3.6 item 6 says a stale temp is removed "only if a journal `SaveBegin` record names it", but `SaveBegin` (which carries `outputSha256`) cannot exist until the output is complete. A crash while the output is being written leaves a hidden `.doc.pdf.mikepdf-xxxx.tmp` that nothing will ever clean up or even list. Fix: write a **`SaveIntent{temp, backup}`** record, flushed, before creating the temp file.
- A kill after the replace and before `SaveCommitted` (step 7) is recognised by the hash and cleared silently, as the spec says (SP-07).
- Rolling back at step 5 or 6 deletes the complete, validated temp file. The user's edits are still in the edit journal, so nothing is lost; rolling forward (renaming the validated temp into place) is possible too if the policy prefers.

### 8.2 Kill during the replace itself (600 kills per mechanism)

A child replaces the target in a loop (write a new temp, replace); the parent kills it at a random moment between 5 and 120 ms.

| Mechanism | Kills | Target = old | Target = new | **Target missing** | Torn |
|---|---|---|---|---|---|
| `ReplaceFileW` | 600 | 272 | 311 | **17 (2.8%)** | 0 |
| `MoveFileEx` | 600 | 307 | 293 | 0 | 0 |
| `FileRenameInfoEx(POSIX)` | 600 | 298 | 302 | **0** | 0 |
| rename away, rename in | 600 | 200 | 189 | **211 (35%)** | 0 |

(The "stray temp" counts in the raw output belong to the loop's own temp writes, not to the replace.)

- **`ReplaceFileW` is not atomic against process death.** A re-run of 400 kills (with a directory dump) found 20 missing, and in the three cases dumped the directory held the **old file renamed to `doc.pdf~RFxxxxxxxx.TMP`** and the new temp file, both complete: no data is lost, but the user's file *name* does not exist until recovery finds the `~RF*.TMP` file. The spec's claim "an interrupted stage 6 leaves either the old file or the new file (NTFS atomic replace)" holds for the POSIX rename and `MoveFileEx`, not for `ReplaceFileW`, and not for the swap.
- 0 of 1,200 kills left the POSIX rename or `MoveFileEx` with a missing or torn target. That is evidence for process death. **Power loss and OS crash were not tested** (UNVERIFIED): NTFS journals metadata, so a rename is expected to be all or nothing, but the new file's *data* must have been flushed first (`FlushFileBuffers` on the temp, as the sim does). `MOVEFILE_WRITE_THROUGH` exists for `MoveFileEx`; `REPLACEFILE_WRITE_THROUGH` is documented as unsupported.
- If `ReplaceFileW` is the fallback (SMB, or a file system without POSIX rename), pass a **backup name** recorded in `SaveIntent`, so recovery knows what to look for, and make recovery look for `~RF*.TMP` as well.

---

## 9. Recommendation

### 9.1 The replace mechanism

1. **Open** documents (broker handle, the writer host's handles, the verification handle) with `FileShare.Read | FileShare.Delete`, read-only. (Section 4.)
2. **Stage 6 on a local NTFS volume:**
   1. Verify identity: the path's `FileIdInfo` equals the broker's handle's. If not, the save asks (Overwrite / Save As / Cancel). If `NumberOfLinks > 1`, tell the user the other names will keep the old content.
   2. Resolve the path with `GetFinalPathNameByHandle` on the broker's handle, so a symlink target is replaced and the link survives.
   3. Prepare the temp file: **clear `Hidden`**; copy the original's attributes (Archive, NotContentIndexed, Temporary; Compressed through `FSCTL_SET_COMPRESSION`), its security descriptor (as a binary descriptor, see the .NET trap), **every named stream** (`Zone.Identifier` first) and the creation time; flush.
   4. **Rename the temp file over the target** with `SetFileInformationByHandle(FileRenameInfoEx, FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS)` while the engines still map the old file. One system call; the old file is freed when its last holder closes.
   5. Open the new path in a **fresh engine**, swap the display, close the old engine and the old handle (this is the rebase, now gapless), write `SaveCommitted`, delete the journal.
3. **Retry** on 32, 5 and 1175 (and 33, not observed), 5 times at 200 ms (OneDrive produced a transient 5; a 1175 was seen once). Then fail with Save As.
4. **Fallback** when the rename returns 87, 50 or 1 (SMB, FAT, WebDAV): `ReplaceFileW(target, temp, backup)` with the backup name recorded in `SaveIntent`; it keeps DACL, streams, attributes and creation time itself and works under R\|D. Not atomic against process death: recovery must also handle "target missing and a backup or `~RF*.TMP` present".
5. **Do not use** `MoveFileEx(REPLACE_EXISTING)` (fails when any handle is open), the non-POSIX `FileRenameInfo(Ex)` (same), or rename-away-and-in (35% missing under kill; defeats read-only; loses metadata).
6. **Close-the-engines-first is no longer needed**, and was the only mechanism that worked with the flags as built.
7. **Refusals that stay Save As:** read-only attribute; a directory that cannot hold the temp file; a path on a file system where neither mechanism works.

### 9.2 Proposed amendments (proposals only; no ADR or spec was edited)

**ADR-0005, new Amendment A2 (replace decision 6's first bullet):**

> **A2 (S10): the broker opens documents with `FILE_SHARE_READ | FILE_SHARE_DELETE`.** Decision 6 said "read-only file handle, opened deny-write". It stays deny-write (measured: no other process can write, append or truncate while the handle is open) but now also grants `FILE_SHARE_DELETE`, because `ReplaceFileW` and the POSIX-semantics rename cannot replace a file whose open handle lacks it, and the as-built `FileShare.Read` made every replace mechanism fail with the engine's mapped view present (real broker and engine, S10 section 3.1). The engine's mapped view does not block replacement. Consequences: other processes may rename, delete or replace the path while a document is open (they could not before); the broker detects this by comparing the path's file ID with its own handle's before it replaces; the engines keep reading the original bytes until they are reopened. The same flags apply to every handle the broker opens on a document, including the writer host's.

**ADR-0008, decision 3 step 6 (replace the text):**

> 6. The broker replaces the original by renaming the validated temporary file over it with `FileRenameInfoEx` (`REPLACE_IF_EXISTS | POSIX_SEMANTICS`) while the engines still hold the old file; before that it verifies the path still names the file it holds (file ID), resolves symbolic links, clears the temp file's Hidden attribute and copies the original's security descriptor, alternate data streams (notably `Zone.Identifier`), attributes and creation time onto it. Where the rename is not supported (SMB, FAT; error 87) it uses `ReplaceFileW` with a journalled backup name. `MoveFileEx` with replace-existing is not used. A `SaveIntent` journal record naming the temporary and backup files is flushed before the temporary file is created; the temporary file is removed on failure and on the next start from that record.

**Master prompt §7.5 step 3 (replace):**

> 3. The broker replaces the original atomically (a POSIX-semantics rename of the validated temporary file over it, with the original's ACL, alternate data streams and attributes copied onto the temporary file first; `ReplaceFileW` with a backup where that rename is unsupported), while the engines still map the old file.

**Save-pipeline spec 3.6 (rewrite items 1, 2, 4, 5, 6):** items 1 and 2 as 9.1 items 1 and 2.4; item 4 as 9.1 items 3 and 4 (retry codes 32, 33, 5, 1175; fallback on 87, 50, 1 to `ReplaceFileW`, **not** `MoveFileExW`); item 5 as 9.1 item 2.3 (explicit metadata copy; `ReplaceFileW` does it itself); item 6: "Write `SaveIntent` before creating the temp file; the temp is created hidden and exclusive, **un-hidden before the rename**; a stale temp or backup is removed at startup only if a `SaveIntent` or `SaveBegin` record names it."

**Save-pipeline spec 3.3 stage 0 and 3.4 control records:** add `SaveIntent{temp, backup}` (written at stage 0, flushed, before any file is created); `SaveBegin` keeps `outputSha256`; recovery adds "target missing and a recorded backup or `~RF*.TMP` exists: roll back or forward as in S10 8.1".

**Save-pipeline spec 7 (Edge cases), Power loss:** replace "an interrupted stage 6 leaves either the old file or the new file (NTFS atomic replace)" with "a POSIX-semantics rename leaves the old or the new file (0 of 600 kills lost the target; power loss not tested); the `ReplaceFileW` fallback can leave the target missing with the old file under `~RF*.TMP` or the recorded backup name, which recovery restores".

**Acceptance criteria:** SP-16 adds the kill sweep of S10 8.1 (both the literal and the intent journal; the literal one must be shown to leak); SP-17 gains "after a save through the POSIX rename and through the `ReplaceFileW` fallback, DACL, streams, attributes and creation time equal the original's, Hidden is clear"; **new SP-38** "open flags: another process can rename but cannot write while a document is open; the broker's handle and the engine's view read the original bytes after an external replace"; **new SP-39** "path replaced by another file between open and save is detected by file ID and prompts"; **new SP-40** "symlink at the path: the link survives and the real file is updated; hard-linked file: warning"; **new SP-41** "rename returns 87 on SMB: the fallback runs, no leftover file".

---

## 10. Surprises

1. **The mapped view is not the obstacle; the broker's own sharing mode is.** The hard part of ADR-0005 decision 6 looked like "the engine maps the file" and turns out to be one missing flag.
2. **`MoveFileEx(REPLACE_EXISTING)` fails if any other handle is open, even with delete sharing**, while the POSIX rename does not. Spec 3.6 item 4 names `MoveFileExW` as the fallback.
3. **`ReplaceFileW` is not atomic against a kill** (2.8% to 5% target missing), though it is the only mechanism that keeps the metadata by itself.
4. **The spec's journal cannot clean up after a crash while the output is being written.**
5. **A hidden temp file makes the user's file hidden.**
6. **A symlinked PDF is silently turned into a regular copy** by every mechanism except one that resolves the link first; the original real file is left stale.
7. **The swap defeats the read-only attribute.**
8. **POSIX rename does not exist over SMB** (error 87, not "not supported").
9. **OneDrive hides the reparse bit from a non-cloud-aware process** but shows the recall bits.
10. **`SetAccessControl(GetAccessControl())` copies nothing in .NET.**

## 11. UNVERIFIED and not measured

- ARM64; any file system other than NTFS and a loopback SMB share; ReFS, exFAT, FAT, WebDAV, DFS, a real remote SMB server, a mapped drive; SharePoint-synced folders.
- OneDrive server-side versions and conflict handling; OneDrive upload latency (the probe could not observe it); behaviour while OneDrive is paused or offline.
- Power loss and OS crash; behaviour with antivirus or backup software holding the temp file open (the retry rule is untested against a real scanner beyond the single transient 5).
- Large files (the semantics should not depend on size, but flush and scan time do); files above 4 GiB through the mapped view.
- Whether MikePDF's packaged process is cloud-aware (it decides how placeholders are seen); the MSIX-packaged broker (all runs unpackaged).
- Timing of the rename itself and of the metadata copy (not timed).
- Behaviour of other programs when a document is replaced under them (Explorer, thumbnails, indexers).

## 12. Open questions for Opus

1. **Accept R\|D?** It reopens the path to other writers (rename/delete/replace), which the as-built flags prevented. The alternative is to keep `Read` and close the engine's document for every save, which works (section 3.1, last column) but costs a reload and a blank frame, and blocks Explorer and OneDrive meanwhile.
2. **`ReplaceFileW` fallback vs refuse:** on SMB and other volumes without POSIX rename, accept the 2.8% window with a journalled backup, or make Save write a copy and ask the user to confirm the swap?
3. **Hard-linked files and symlinks:** warn, refuse, or silently break the link (what every mechanism does)?
4. **Compressed and encrypted (EFS) originals:** copy Compressed, and EFS (`Encrypted` attribute) was not tested; refuse EFS files for Save or test them?
5. **OQ-SP-4/OQ-SP-7** can be closed by the amendments above, except the journal location question (unrelated).

## 13. How to reproduce

```powershell
./build.ps1                                   # builds the native engine for the real-engine run
spikes/S10/run.ps1                            # holder matrix, attack, metadata, repeats, .NET share mapping, crash sim, kill stress -> out/raw
spikes/S10/run.ps1 -ExtraRoot '\\localhost\C$\Users\<you>\AppData\Local\Temp\mikepdf-s10-smb' -Label smb   # set S10_SUBSET=1 first for the reduced SMB matrix
spikes/S10/run-real-engine.ps1                # real broker + sandboxed engine, as built and with FileShare.Read|Delete
S10.Probe.exe cloud-focused <OneDrive folder> <log> 300                       # one save in a OneDrive folder (the test folder is deleted afterwards)
python spikes/S10/summarize.py matrix|attack|meta|crash|stress <file.jsonl>   # the tables above
```
