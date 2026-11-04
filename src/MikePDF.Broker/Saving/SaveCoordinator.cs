using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using MikePDF.Core.Editing;
using MikePDF.Core.Editing.Journal;
using MikePDF.Core.Editing.Recovery;
using MikePDF.Protocol.Edits;

namespace MikePDF.Broker.Saving;

public enum SaveOutcome
{
    Saved,

    /// <summary>No commands since the clean point: nothing was written and the file's timestamp is untouched.</summary>
    NothingToSave,

    /// <summary>Not saved, and the reason is the file's or the document's, not a fault: Save As is offered.</summary>
    Refused,

    /// <summary>The path now names another file: Overwrite, Save As or Cancel.</summary>
    NeedsConfirmation,

    /// <summary>Something failed. The original is untouched and the temporary files are gone.</summary>
    Failed,
}

public enum SaveReason
{
    None,
    NothingToSave,
    ReadOnly,
    HardLinks,
    EncryptedFile,
    PasswordProtected,
    TooLarge,
    NoSpace,
    DirectoryNotWritable,
    ChangedOnDisk,
    TargetGone,
    InUse,

    /// <summary>A pending command no stage can write in this build (forms and pages need the engine's save).</summary>
    UnsupportedEdits,

    /// <summary>The strict parser refuses the file's structure (R-39): editing is not safe, reading still is.</summary>
    Structure,

    /// <summary>Stage 5 found the output wrong.</summary>
    ValidationFailed,

    HostFailed,
    Cancelled,
    Io,

    /// <summary>
    /// The replace failed part way (ReplaceFileW moved the original aside) and the original could not be put back. Nothing is at the document's path;
    /// <see cref="SaveResult.OriginalAt"/> says where the original is. Nothing was deleted and the journal keeps its records, so the next start restores it.
    /// </summary>
    RestorePending,
}

/// <summary>Where a save is, for the progress strip (save-pipeline 3.2: "Saving...", "Checking the saved file..."). After <see cref="Replacing"/> there is no cancel.</summary>
public enum SaveProgressStage
{
    /// <summary>Preflight and the writer host's work.</summary>
    Saving,

    /// <summary>Stage 5: the structure walk, the prefix check, the fresh engine.</summary>
    Checking,

    /// <summary>Stage 6 has begun: the cancel point has passed.</summary>
    Replacing,
}

/// <summary>The result of a save. <c>Detail</c> is a short, non-sensitive reason code for the Details button, never a path or document text.</summary>
public sealed record SaveResult(SaveOutcome Outcome, SaveReason Reason, string? Detail = null, string? TargetPath = null, ReplaceOutcome? Replace = null, string? OutputSha256 = null, long OutputLength = 0)
{
    public bool IsSaved => Outcome == SaveOutcome.Saved;

    /// <summary>For <see cref="SaveReason.RestorePending"/>: the path of the user's original file, so the message can tell them where it is.</summary>
    public string? OriginalAt { get; init; }
}

public sealed record SaveRequest
{
    /// <summary>The file the edits were made against.</summary>
    public required string DocumentPath { get; init; }

    /// <summary>Save: the same path. Save As and a flattened copy: the chosen one.</summary>
    public required string TargetPath { get; init; }

    public required SaveMode Mode { get; init; }

    public required EditSnapshot Snapshot { get; init; }

    /// <summary>The page count of <see cref="DocumentPath"/>, from the engine.</summary>
    public required int PageCount { get; init; }

    /// <summary>The broker's handle on the document, opened with <see cref="EngineSupervisor.DocumentShare"/>. Its file identity is what a Save checks the path against. Null: the file is opened here, and no identity check is made.</summary>
    public SafeFileHandle? HeldOriginal { get; init; }

    /// <summary>Save As option "Rewrite the whole file": a smaller output with no edit history, which invalidates signatures.</summary>
    public bool RewriteWholeFile { get; init; }

    /// <summary>Flattened copy: draw form fields into the pages.</summary>
    public bool FlattenForms { get; init; } = true;

    /// <summary>Flattened copy: draw comments and markup into the pages.</summary>
    public bool FlattenComments { get; init; } = true;

    /// <summary>
    /// Save only: the person chose Overwrite after being told the path now names another file (save-pipeline 3.6 item 3). The edits are written onto the bytes the broker holds, over whatever is at the
    /// path now; the file-identity check is skipped. Nothing else about the save changes.
    /// </summary>
    public bool OverwriteChangedFile { get; init; }

    /// <summary>Told where the save is. The shell shows a strip when a save takes long (and disables Cancel from <see cref="SaveProgressStage.Replacing"/>).</summary>
    public IProgress<SaveProgressStage>? Progress { get; init; }
}

/// <summary>
/// The save pipeline (save-pipeline 3.3, ADR-0008 and its amendment A1): preflight, a <c>SaveIntent</c> record, the writer host's work into a temporary
/// file, a <c>SaveBegin</c> record with the output's hash, stage 5's validation, the replace, <c>SaveCommitted</c>, and the cleanup. Every step that
/// leaves something behind is recorded in the journal first, so a kill at any point leaves the old file or the new one and a start-up recovery that
/// removes what is left. The original is not touched until the replace, which is one rename.
/// <para>
/// This build writes annotation edits (stage 4) and the copies (Save As, full rewrite, flattened copy). Form and page changes need the engine's
/// PDFium save and the tail normaliser (stages 2 and 3), which belong to the forms and page-organiser work packages: a snapshot that holds them
/// is refused with <see cref="SaveReason.UnsupportedEdits"/>.
/// </para>
/// </summary>
public sealed class SaveCoordinator(ISaveHost host, ISaveEngineCheck engineCheck, FileReplacer? replacer = null, ISaveFaults? faults = null)
{
    private const long MaxDocumentBytes = 1L << 30;
    private static readonly byte[] PdfiumPrivateKey = "/PDFIUM_"u8.ToArray();
    private readonly FileReplacer _replacer = replacer ?? new FileReplacer();

    /// <summary>Saves the pending edits of a session: edits are blocked while it runs, and the clean point moves only when the file was replaced.</summary>
    // switchesToTarget (Save As only): the tab switches to the new file (save-pipeline 3.2), so the edits are in the file it shows. On success the model's clean point moves and the session's journal is keyed by
    // the new path. False leaves the pending edits where they are (a second copy made from the same session, a test).
    public async Task<SaveResult> SaveSessionAsync(EditSession session, string documentPath, string targetPath, SaveMode mode, int pageCount, SafeFileHandle? held, bool rewriteWholeFile = false, bool overwriteChangedFile = false, IProgress<SaveProgressStage>? progress = null, bool switchesToTarget = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = session.Model.BeginSave();
        SaveResult result;
        try
        {
            result = await SaveAsync(
                new SaveRequest { DocumentPath = documentPath, TargetPath = targetPath, Mode = mode, Snapshot = snapshot, PageCount = pageCount, HeldOriginal = held, RewriteWholeFile = rewriteWholeFile, OverwriteChangedFile = overwriteChangedFile, Progress = progress },
                new JournalSaveSink(session),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            session.Model.AbortSave();
            throw;
        }

        if (result.IsSaved && (mode == SaveMode.Save || (mode == SaveMode.SaveAs && switchesToTarget)))
        {
            session.Model.MarkSaved(snapshot);
            if (mode == SaveMode.SaveAs)
            {
                session.Rekey(targetPath);
            }
        }
        else
        {
            session.Model.AbortSave();
        }

        return result;
    }

    public async Task<SaveResult> SaveAsync(SaveRequest request, ISaveJournal? journal = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        journal ??= new NullSaveJournal();
        var plan = StagePlanner.Plan(request.Snapshot);
        var hasEdits = !plan.IsNothing;
        if (!hasEdits && request.Mode == SaveMode.Save)
        {
            return new SaveResult(SaveOutcome.NothingToSave, SaveReason.NothingToSave);
        }

        if (plan.Runs(SaveStage.EngineSave))
        {
            return Refuse(SaveReason.UnsupportedEdits, "forms-pages");
        }

        // ---- stage 0: preflight ----
        TargetInfo target;
        long baseLength;
        SafeFileHandle? opened = null;
        try
        {
            var original = request.HeldOriginal ?? (opened = File.OpenHandle(request.DocumentPath, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare));
            baseLength = RandomAccess.GetLength(original);
            if (baseLength > MaxDocumentBytes)
            {
                return Refuse(SaveReason.TooLarge, "over-1gib");
            }

            target = FileReplacer.Inspect(request.TargetPath);
            if (target.Problem != TargetProblem.None)
            {
                return Refuse(target.Problem switch { TargetProblem.ReadOnly => SaveReason.ReadOnly, TargetProblem.HardLinks => SaveReason.HardLinks, _ => SaveReason.EncryptedFile }, target.Problem.ToString());
            }

            FileIdentity? expected = request.Mode == SaveMode.Save && !request.OverwriteChangedFile && request.HeldOriginal is { } held ? FileReplacer.IdentityOf(held) : null;
            if (request.Mode == SaveMode.Save)
            {
                if (!target.Exists)
                {
                    return new SaveResult(SaveOutcome.Refused, SaveReason.TargetGone, "moved-or-deleted");
                }

                if (expected is { } wanted && target.Identity != wanted)
                {
                    return new SaveResult(SaveOutcome.NeedsConfirmation, SaveReason.ChangedOnDisk, "file-id");
                }
            }

            if (!HasRoomFor(target.FinalPath, baseLength, rewrite: request.RewriteWholeFile || request.Mode == SaveMode.FlattenedCopy))
            {
                return Refuse(SaveReason.NoSpace, "no-space");
            }

            return await RunAsync(request, journal, target, original, baseLength, expected, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return new SaveResult(SaveOutcome.Refused, SaveReason.TargetGone, "moved-or-deleted");
        }
        catch (DirectoryNotFoundException)
        {
            return new SaveResult(SaveOutcome.Refused, SaveReason.DirectoryNotWritable, "no-directory");
        }
        catch (UnauthorizedAccessException)
        {
            return Refuse(SaveReason.DirectoryNotWritable, "access-denied");
        }
        finally
        {
            opened?.Dispose();
        }
    }

    private async Task<SaveResult> RunAsync(SaveRequest request, ISaveJournal journal, TargetInfo target, SafeFileHandle original, long baseLength, FileIdentity? expected, CancellationToken ct)
    {
        faults?.Reach(SaveStep.Start);
        var targetName = Path.GetFileName(target.FinalPath);
        var directory = Path.GetDirectoryName(target.FinalPath)!;
        var suffix = SaveRecovery.NewSuffix();
        var tempPath = Path.Combine(directory, SaveRecovery.TempNameFor(targetName, suffix));
        var backupPath = Path.Combine(directory, SaveRecovery.BackupNameFor(targetName, suffix));

        // The record that names every file the save may create comes before any of them exists (S10: otherwise a crash while the output is written leaks one).
        journal.SaveIntent(tempPath, backupPath, target.FinalPath);
        faults?.Reach(SaveStep.IntentWritten);

        SafeFileHandle temp;
        try
        {
            temp = FileReplacer.CreateTemporary(target.FinalPath, Path.GetFileName(tempPath), out tempPath);
        }
        catch (Win32Exception e)
        {
            return e.NativeErrorCode switch
            {
                SaveNative.ErrorAccessDenied or 3 or 19 => Refuse(SaveReason.DirectoryNotWritable, "no-temp"),
                112 or 39 => Refuse(SaveReason.NoSpace, "no-space"),
                _ => new SaveResult(SaveOutcome.Failed, SaveReason.Io, "win32-" + e.NativeErrorCode),
            };
        }

        var tempOwned = true;
        SaveResult Abort(SaveResult result)
        {
            // A failure the save understood: the temporary file goes. (A kill, or a test that stands in for one, goes through none of this.)
            temp.Dispose();
            tempOwned = false;
            DeleteQuietly(tempPath);
            return result;
        }

        try
        {
            faults?.Reach(SaveStep.TempCreated);

            // ---- stage 4 and the copies ----
            var blob = request.Snapshot.IsEmpty ? null : AnnotationEditMapper.ToBlob(request.Snapshot.Pending, request.PageCount);
            var deadline = SaveDeadlines.ForSize(baseLength);
            request.Progress?.Report(SaveProgressStage.Saving);
            var produced = await ProduceAsync(request, blob, original, baseLength, temp, deadline, ct).ConfigureAwait(false);
            if (produced is not null)
            {
                return Abort(produced);
            }

            faults?.Reach(SaveStep.OutputWritten);
            var length = RandomAccess.GetLength(temp);
            var sha = FileBytes.Sha256(temp, length);

            // The write handle ends here. What follows (the checks, a fresh engine opening the file, the rename) needs the file readable by others and
            // renamable, not writable, and a reader cannot open a file that a writer holds without sharing writes.
            temp = FileReplacer.ReopenForRename(temp, tempPath);
            journal.SaveBegin(sha, length);
            faults?.Reach(SaveStep.BeginWritten);

            // ---- stage 5: validation ----
            request.Progress?.Report(SaveProgressStage.Checking);
            var incremental = request.Mode != SaveMode.FlattenedCopy && !request.RewriteWholeFile && blob is not null;
            var invalid = await ValidateAsync(request, blob, original, baseLength, tempPath, length, incremental, sha, deadline, ct).ConfigureAwait(false);
            if (invalid is not null)
            {
                return Abort(invalid);
            }

            faults?.Reach(SaveStep.Validated);
            ct.ThrowIfCancellationRequested(); // after this there is no cancel
            request.Progress?.Report(SaveProgressStage.Replacing);

            // ---- stage 6: replace ----
            tempOwned = false;
            var replaced = _replacer.Replace(target.Exists ? target.FinalPath : request.TargetPath, temp, tempPath, expected, backupPath);
            faults?.Reach(SaveStep.Replaced);

            // From here the file is replaced, so nothing below may turn this save into a failure: what a journal that cannot be written leaves behind,
            // the next start's recovery finishes (the output's hash is in SaveBegin, and the target now has it).
            AfterReplace(journal.SaveCommitted);
            faults?.Reach(SaveStep.CommittedWritten);

            if (replaced.BackupPath is { } backup)
            {
                DeleteQuietly(backup);
            }

            // The edits are in the file now. A flattened copy leaves the document, its tab and its journal alone.
            if (request.Mode != SaveMode.FlattenedCopy)
            {
                AfterReplace(journal.Clear);
            }

            faults?.Reach(SaveStep.Done);
            return new SaveResult(SaveOutcome.Saved, SaveReason.None, null, replaced.FinalPath, replaced, sha, length);
        }
        catch (ReplaceException e) when (e.Failure == ReplaceFailure.RestorePending)
        {
            // Keep both files: the original is under its backup name, the validated output under the temporary one, and the journal still has the
            // SaveIntent and SaveBegin records that recovery needs. Deleting anything here could delete the only copy of either.
            return new SaveResult(SaveOutcome.Failed, SaveReason.RestorePending, "win32-" + e.Win32Error) { OriginalAt = e.OriginalAt };
        }
        catch (ReplaceException e)
        {
            DeleteQuietly(tempPath);
            return e.Failure switch
            {
                ReplaceFailure.ChangedOnDisk => new SaveResult(SaveOutcome.NeedsConfirmation, SaveReason.ChangedOnDisk, "file-id"),
                ReplaceFailure.TargetGone => new SaveResult(SaveOutcome.Refused, SaveReason.TargetGone, "moved-or-deleted"),
                ReplaceFailure.InUse => new SaveResult(SaveOutcome.Failed, SaveReason.InUse, "win32-" + e.Win32Error),
                _ => new SaveResult(SaveOutcome.Failed, SaveReason.Io, "win32-" + e.Win32Error),
            };
        }
        catch (EditBlobException)
        {
            // The model produced values the blob format does not allow (a reversed rectangle, a stroke of one point): not something to write.
            temp.Dispose();
            tempOwned = false;
            DeleteQuietly(tempPath);
            return new SaveResult(SaveOutcome.Refused, SaveReason.UnsupportedEdits, "edit-blob");
        }
        catch (UnsupportedEditException e)
        {
            temp.Dispose();
            tempOwned = false;
            DeleteQuietly(tempPath);
            return new SaveResult(SaveOutcome.Refused, SaveReason.UnsupportedEdits, e.Command);
        }
        catch (OperationCanceledException)
        {
            temp.Dispose();
            tempOwned = false;
            DeleteQuietly(tempPath);
            return new SaveResult(SaveOutcome.Failed, SaveReason.Cancelled);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or EndOfStreamException)
        {
            temp.Dispose();
            tempOwned = false;
            DeleteQuietly(tempPath);
            // ERROR_DISK_FULL (112) and ERROR_HANDLE_DISK_FULL (39), however they reached us.
            var code = e is Win32Exception w ? w.NativeErrorCode : e.HResult & 0xFFFF;
            return new SaveResult(SaveOutcome.Failed, code is 112 or 39 ? SaveReason.NoSpace : SaveReason.Io, $"{e.GetType().Name}-win32-{code}");
        }
        finally
        {
            if (tempOwned)
            {
                temp.Dispose();
            }
        }
    }

    /// <summary>Stage 4 or the copy. Null when the temporary file now holds the output; a result when the save stops here (the temporary file is removed).</summary>
    private async Task<SaveResult?> ProduceAsync(SaveRequest request, EditBlob? blob, SafeFileHandle original, long baseLength, SafeFileHandle temp, TimeSpan deadline, CancellationToken ct)
    {
        var rewrite = request.Mode == SaveMode.FlattenedCopy || request.RewriteWholeFile;
        var flags = request.Mode == SaveMode.FlattenedCopy ? (request.FlattenForms ? 1u : 0u) | (request.FlattenComments ? 2u : 0u) : 0u;
        SafeFileHandle? intermediate = null;
        try
        {
            var source = original;
            if (blob is not null)
            {
                var bytes = EditBlobCodec.Encode(blob);
                SafeFileHandle destination = temp;
                if (rewrite)
                {
                    intermediate = File.OpenHandle(Path.Combine(Path.GetTempPath(), "mikepdf-stage-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose);
                    destination = intermediate;
                }

                var appended = await host.AppendAsync(original, bytes, destination, request.PageCount, deadline, ct).ConfigureAwait(false);
                if (!appended.Ok)
                {
                    return Fail(appended);
                }

                source = destination;
            }

            if (rewrite)
            {
                var rewritten = await host.RewriteAsync(source, temp, flags, deadline, ct).ConfigureAwait(false);
                if (!rewritten.Ok)
                {
                    return Fail(rewritten);
                }
            }
            else if (blob is null)
            {
                // Save As with nothing to add: a copy. The broker reads and writes bytes and parses none; stage 5 checks the result like any other.
                FileBytes.Copy(original, baseLength, temp);
            }

            return null;
        }
        finally
        {
            intermediate?.Dispose();
        }
    }

    private async Task<SaveResult?> ValidateAsync(SaveRequest request, EditBlob? blob, SafeFileHandle original, long baseLength, string tempPath, long length, bool incremental, string outputSha, TimeSpan deadline, CancellationToken ct)
    {
        using var output = File.OpenHandle(tempPath, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);

        var structure = await host.ValidateAsync(output, deadline, ct).ConfigureAwait(false);
        if (!structure.Ok)
        {
            // Real files already have faults (a stream without a usable /Length is common) and the strict parser still reads them. The save must not
            // add any: the output may have no error the original did not have (§10: a baseline comparison, not a clean bill).
            if (structure.ErrorCodes is null)
            {
                return new SaveResult(SaveOutcome.Failed, SaveReason.HostFailed, structure.Code ?? "structure");
            }

            var baseline = await host.ValidateAsync(original, deadline, ct).ConfigureAwait(false);
            if (baseline.ErrorCodes is null || HasNewErrors(structure.ErrorCodes, baseline.ErrorCodes))
            {
                return new SaveResult(SaveOutcome.Failed, SaveReason.ValidationFailed, "structure");
            }
        }

        if (incremental)
        {
            // The original bytes are an exact prefix (compared as a hash over the range), and the update holds no key PDFium leaves behind.
            if (length < baseLength || FileBytes.Sha256(output, baseLength) != FileBytes.Sha256(original, baseLength))
            {
                return new SaveResult(SaveOutcome.Failed, SaveReason.ValidationFailed, "prefix");
            }

            if (FileBytes.Contains(output, baseLength, length, PdfiumPrivateKey))
            {
                return new SaveResult(SaveOutcome.Failed, SaveReason.ValidationFailed, "pdfium-key");
            }

            var (pages, added, removed) = PageChanges(blob!);
            for (var from = 0; from < pages.Length; from += 2048)
            {
                var count = Math.Min(2048, pages.Length - from);
                var verified = await host.VerifyAsync(original, output, request.PageCount, pages[from..(from + count)], added[from..(from + count)], removed[from..(from + count)], deadline, ct).ConfigureAwait(false);
                if (!verified.Ok || verified.Mismatches is { Count: > 0 })
                {
                    return new SaveResult(SaveOutcome.Failed, SaveReason.ValidationFailed, "annotation-count");
                }
            }
        }

        // A copy (Save As with nothing to add) is the original, byte for byte.
        var isCopy = blob is null && !(request.RewriteWholeFile || request.Mode == SaveMode.FlattenedCopy);
        if (isCopy && (length != baseLength || outputSha != FileBytes.Sha256(original, baseLength)))
        {
            return new SaveResult(SaveOutcome.Failed, SaveReason.ValidationFailed, "copy");
        }

        // The fresh engine: PDFium opens the result and sees the pages the model says it has.
        var problem = await engineCheck.CheckAsync(tempPath, request.PageCount, ct).ConfigureAwait(false);
        return problem is null ? null : new SaveResult(SaveOutcome.Failed, SaveReason.ValidationFailed, problem);
    }

    private static bool HasNewErrors(IReadOnlyList<uint> output, IReadOnlyList<uint> baseline)
    {
        var allowed = baseline.GroupBy(static c => c).ToDictionary(static g => g.Key, static g => g.Count());
        return output.GroupBy(static c => c).Any(g => g.Count() > allowed.GetValueOrDefault(g.Key));
    }

    private static (uint[] Pages, uint[] Added, uint[] Removed) PageChanges(EditBlob blob)
    {
        var added = new SortedDictionary<uint, uint>();
        var removed = new SortedDictionary<uint, uint>();
        foreach (var edit in blob.Edits)
        {
            if (edit.Operation == EditOperation.Add)
            {
                added[edit.Page] = added.GetValueOrDefault(edit.Page) + 1;
            }
            else if (edit.Operation == EditOperation.Delete)
            {
                removed[edit.Page] = removed.GetValueOrDefault(edit.Page) + 1;
            }
        }

        var pages = added.Keys.Union(removed.Keys).Order().ToArray();
        return (pages, [.. pages.Select(p => added.GetValueOrDefault(p))], [.. pages.Select(p => removed.GetValueOrDefault(p))]);
    }

    private static SaveResult Fail(HostResult result) => result.Code switch
    {
        "UnsupportedEncryption" => new SaveResult(SaveOutcome.Refused, SaveReason.PasswordProtected, result.Code),
        "LimitExceeded" => new SaveResult(SaveOutcome.Refused, SaveReason.TooLarge, result.Code),
        "InvalidEdit" or "ExpectationMismatch" or "PageCountMismatch" => new SaveResult(SaveOutcome.Failed, SaveReason.ValidationFailed, result.Code),
        "Timeout" => new SaveResult(SaveOutcome.Failed, SaveReason.HostFailed, "timeout"),
        "HostCrashed" => new SaveResult(SaveOutcome.Failed, SaveReason.HostFailed, "host-crashed"),
        _ => new SaveResult(SaveOutcome.Refused, SaveReason.Structure, result.Code),
    };

    private static void AfterReplace(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for recovery.
        }
    }

    private static SaveResult Refuse(SaveReason reason, string detail) => new(SaveOutcome.Refused, reason, detail);

    private static bool HasRoomFor(string path, long size, bool rewrite)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true; // a share: the volume cannot be asked reliably, and the write will say
            }

            var needed = rewrite ? size * 2 : size + (size / 5);
            return new DriveInfo(root).AvailableFreeSpace >= needed;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next start: the journal's SaveIntent record names it.
        }
    }
}
