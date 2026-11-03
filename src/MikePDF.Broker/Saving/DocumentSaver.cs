using Microsoft.Win32.SafeHandles;
using MikePDF.Core.Editing.Journal;

namespace MikePDF.Broker.Saving;

/// <summary>One request to save an open document: what the shell knows when the person presses Save, Save as or Save a flattened copy.</summary>
public sealed record SaveJob
{
    /// <summary>The document's edits and journal.</summary>
    public required EditSession Session { get; init; }

    /// <summary>The file the document was opened from.</summary>
    public required string DocumentPath { get; init; }

    public required int PageCount { get; init; }

    /// <summary>The broker's handle on the document (opened with <see cref="EngineSupervisor.DocumentShare"/>), whose file identity a Save checks the path against.</summary>
    public SafeFileHandle? Held { get; init; }

    public required SaveMode Mode { get; init; }

    /// <summary>The chosen file for Save as and a flattened copy; null for Save.</summary>
    public string? TargetPath { get; init; }

    /// <summary>The person chose Overwrite after the path turned out to name another file.</summary>
    public bool OverwriteChangedFile { get; init; }

    /// <summary>The document was opened with a password: MikePDF cannot write into it, so a save with edits is refused before anything is created.</summary>
    public bool PasswordProtected { get; init; }

    public IProgress<SaveProgressStage>? Progress { get; init; }
}

/// <summary>What a save did, and what to tell the person.</summary>
/// <param name="Result">The coordinator's result.</param>
/// <param name="Notice">Null when the file was saved (or there was nothing to save).</param>
/// <param name="SwitchedToTarget">Save as succeeded and the tab now shows the new file: the edits are in it, the journal is keyed by its path.</param>
/// <param name="FileChanged">The document's file on disk now has other bytes than the engines show: reopen the document on it (the rebase of save-pipeline 3.3 stage 6).</param>
public sealed record SaveFlowResult(SaveResult Result, SaveNotice? Notice, bool SwitchedToTarget, bool FileChanged)
{
    public bool IsSaved => Result.IsSaved;

    public bool NeedsConfirmation => Result.Outcome == SaveOutcome.NeedsConfirmation;
}

/// <summary>
/// The shell's one door to the save pipeline: decides which mode a request really is (Save as to the file's own path is a Save), refuses what is known to be impossible before anything is created,
/// runs <see cref="SaveCoordinator"/>, and turns the result into what the person is told. No UI types, so every rule is a test.
/// </summary>
public sealed class DocumentSaver(SaveCoordinator coordinator)
{
    public async Task<SaveFlowResult> RunAsync(SaveJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var mode = job.Mode;
        var target = job.TargetPath ?? job.DocumentPath;
        if (mode == SaveMode.SaveAs && SamePath(target, job.DocumentPath))
        {
            mode = SaveMode.Save; // Save as to the file's own path is a Save (SP-25)
        }

        var hasEdits = job.Session.Model.IsDirty;
        if (job.PasswordProtected && hasEdits)
        {
            // The writer host cannot write encrypted objects; it would say so after a journal record and a temporary file. Nothing is created.
            return Flow(new SaveResult(SaveOutcome.Refused, SaveReason.PasswordProtected, "known-encrypted"), mode);
        }

        var result = await coordinator.SaveSessionAsync(
            job.Session,
            job.DocumentPath,
            target,
            mode,
            job.PageCount,
            job.Held,
            rewriteWholeFile: false,
            overwriteChangedFile: job.OverwriteChangedFile,
            progress: job.Progress,
            switchesToTarget: mode == SaveMode.SaveAs,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return Flow(result, mode);
    }

    private static SaveFlowResult Flow(SaveResult result, SaveMode mode) =>
        new(result, SavePresentation.Describe(result), SwitchedToTarget: result.IsSaved && mode == SaveMode.SaveAs, FileChanged: result.IsSaved && mode == SaveMode.Save);

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
