using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MikePDF.App.Services;
using MikePDF.Broker;
using MikePDF.Broker.Saving;
using MikePDF.Core;
using MikePDF.Core.Editing;
using MikePDF.Core.Editing.Journal;
using MikePDF.Core.Windowing;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MikePDF.App;

/// <summary>
/// Saving from the window (docs/features/save-pipeline.md, design 14 sections 2.8 and 2.9, 01 "Edited"): the Save, Save as and Save a flattened copy commands, the modified marker, the strip that shows a save that takes
/// long or did not happen, the "changed on disk" banner, and the chip that says changes cannot be backed up. The pipeline itself (the journal, the writer host, the checks, the replace) is in
/// <c>MikePDF.Broker.Saving</c> and knows nothing of windows; this file is what binds it to controls.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan SaveStripDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SavedLinger = TimeSpan.FromSeconds(4);

    private Button? _saveCancelButton;
    private DispatcherQueueTimer? _saveLingerTimer;
    private TabEntry? _saveStripOwner;

    private void InitializeSave()
    {
        _saveCancelButton = new Button { Content = Strings.Get("Save_Cancel") };
        AutomationProperties.SetAutomationId(_saveCancelButton, "SaveCancel");
        _saveCancelButton.Click += (_, _) => CancelSave();
        SaveStrip.ActionButton = _saveCancelButton;
        AutomationProperties.SetName(BackupChip, Strings.Get("Save_BackupFailed"));
        BackupChip.Message = Strings.Get("Save_BackupFailed");
    }

    private void RegisterSaveActions(Action<string, Action> sync, Action<string, Func<Task>> async)
    {
        _ = sync;
        async("file.save", SaveAsync);
        async("file.saveAs", () => SaveCopyAsync(SaveMode.SaveAs));
        async("file.saveFlattenedCopy", () => SaveCopyAsync(SaveMode.FlattenedCopy));
    }

    /// <summary>The shown document has edits that are not in its file.</summary>
    private bool ActiveHasEdits => _active?.Edits is { Model.IsDirty: true };

    private bool ActiveIsSaving => _active?.SaveRun is { Running: true };

    // ---- the tab's edits ------------------------------------------------------------------------------------------------------------------------

    private EditSession NewEditSession(string path)
    {
        if (_services.Journals is { } store)
        {
            return new EditSession(store, path, AppPaths.Version);
        }

        var unbacked = new EditSession(null);
        unbacked.MarkBackupUnavailable(); // the journals folder could not be made: editing goes on, and the chip says so
        return unbacked;
    }

    /// <summary>
    /// The document of the tab has opened: it gets its edits (the journal is made when the first command arrives), restored from a journal the person chose to restore, or asked about when a journal is found
    /// for it. The tab keeps them across reloads and moves.
    /// </summary>
    private async Task AttachEditsAsync(TabEntry entry, string path)
    {
        if (entry.Edits is null)
        {
            Core.Editing.Recovery.RecoveryCandidate? restore = _app.TakeRestore(path);
            if (restore is null && !_app.IsDeferred(path) && _services.Journals is { } journals && journals.Exists(path))
            {
                // Only a document that has a journal waits for an answer; the others go straight on, in the same turn of the UI thread.
                restore = await OfferRecoveryAsync(path);
                if (entry.Owner is null || _closed)
                {
                    return; // the tab was closed while the question was open
                }
            }

            EditSession? edits = null;
            if (restore is not null && _services.Recovery is { } recovery)
            {
                try
                {
                    var (session, report) = recovery.Restore(restore);
                    edits = session;
                    if (report.Skipped > 0 || report.UnknownCommands > 0)
                    {
                        ShowNotice(Strings.Format("Recover_Partial", report.Applied, report.Commands));
                    }
                    else if (restore.PartiallyReadable)
                    {
                        ShowNotice(Strings.Get("Recover_Damaged"));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JournalUnavailableException)
                {
                    ShowNotice(Strings.Format("Recover_Unreadable", System.IO.Path.GetFileName(path)));
                }
            }

            entry.SetEdits(edits ?? NewEditSession(path), () => entry.Owner?.OnEntryEditsChanged(entry));
            if (edits is null && FakeDirty && !entry.FakeEditApplied)
            {
                entry.FakeEditApplied = true;
                entry.Edits!.Model.Apply(FakeEdit());
            }
        }

        entry.Stamp = FileStamp.Take(path);
        entry.Changed = ExternalChange.None;
        entry.KeptStamp = null;
        OnEntryEditsChanged(entry);
    }

    // The test seam MIKEPDF_TEST_FAKE_DIRTY: one real highlight (page 1), so a journey can close, save and recover a document that has edits before any tool creates them.
    private static readonly float[] FakeRect = [10f, 20f, 110f, 40f];
    private static readonly float[] FakeColour = [1f, 0.9f, 0f];
    private static readonly float[] FakeQuads = [10f, 40f, 110f, 40f, 10f, 20f, 110f, 20f];

    private static EditCommand FakeEdit() => EditCommand.Create("AddAnnotation", new
    {
        page = 0,
        kind = "Highlight",
        name = "mikepdf-test-1",
        rect = FakeRect,
        color = FakeColour,
        opacity = 0.5f,
        contents = "test edit",
        author = "MikePDF test",
        quads = FakeQuads,
    });

    /// <summary>The edits of a tab changed (a command, an undo, a save began or ended): the marker, the title, the commands, the chip.</summary>
    internal void OnEntryEditsChanged(TabEntry entry)
    {
        if (_closed)
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnEntryEditsChanged(entry));
            return;
        }

        entry.Model.IsDirty = entry.Edits?.Model.IsDirty == true;
        RefreshTabs();
        if (ReferenceEquals(entry, _active))
        {
            UpdateChrome();
        }
    }

    /// <summary>"Changes can't be backed up right now" for as long as it is true of the shown document, and the Save button while it has edits.</summary>
    private void UpdateSaveChrome()
    {
        var edits = _active?.Edits;
        BackupChip.IsOpen = edits is { BackedUp: false, Model.IsDirty: true };
        SaveButton.Visibility = edits is { Model.IsDirty: true } || ActiveIsSaving ? Visibility.Visible : Visibility.Collapsed;
        ShowChangedBar(_active);
    }

    // ---- the commands ---------------------------------------------------------------------------------------------------------------------------

    // Save: the same file; with the policy SaveMode=AlwaysSaveAsCopy it is Save as, and the original is never written (save-pipeline 3.2).
    private async Task SaveAsync()
    {
        if (_active is not { } entry)
        {
            return;
        }

        if (_services.Settings.Value<string>("save.mode") == "AlwaysSaveAsCopy")
        {
            await SaveCopyCoreAsync(entry, SaveMode.SaveAs, edited: true, rebase: true);
            return;
        }

        await SaveEntryAsync(entry, SaveMode.Save, null);
    }

    private async Task SaveCopyAsync(SaveMode mode)
    {
        if (_active is { } entry)
        {
            await SaveCopyCoreAsync(entry, mode, edited: false, rebase: true);
        }
    }

    /// <summary>The Save button of the close prompt: saves one tab that is about to close. True when the edits are in a file (or there were none). The tab is shown while it is saved, and nothing is reloaded.</summary>
    internal async Task<bool> SaveForCloseAsync(TabEntry entry)
    {
        if (!ReferenceEquals(entry, _active))
        {
            ActivateEntry(entry, moveFocus: false);
        }

        if (entry.Edits is not { Model.IsDirty: true })
        {
            return true;
        }

        return _services.Settings.Value<string>("save.mode") == "AlwaysSaveAsCopy"
            ? await SaveCopyCoreAsync(entry, SaveMode.SaveAs, edited: true, rebase: false)
            : await SaveEntryAsync(entry, SaveMode.Save, null, rebase: false);
    }

    private async Task<bool> SaveCopyCoreAsync(TabEntry entry, SaveMode mode, bool edited, bool rebase)
    {
        if (entry.Session is not { IsOpen: true } || entry.Model.Path is not { } current || entry.SaveRun is { Running: true })
        {
            return false;
        }

        var baseName = System.IO.Path.GetFileNameWithoutExtension(current);
        var suggested = mode == SaveMode.FlattenedCopy ? SaveNames.WithSuffix(baseName, Strings.Get("Save_SuggestedFlatName")) : edited ? SaveNames.WithSuffix(baseName, Strings.Get("Save_SuggestedName")) : baseName; // no second "(edited)" on a copy of a copy
        var picked = await PickSavePathAsync(suggested, mode == SaveMode.FlattenedCopy ? Strings.Get("Save_FlattenedTitle") : Strings.Get("Save_AsDialog_Title"));
        if (picked is null)
        {
            return false;
        }

        var target = picked.Path;
        var sameFile = DocumentSaver.SamePath(target, current);
        if (mode == SaveMode.FlattenedCopy && sameFile)
        {
            ShowNotice(Strings.Get("Save_FlatSameFile")); // a flattened copy must not replace the working file
            return false;
        }

        if (!sameFile && _app.Model.Find(DocumentIdentity.Of(target)) is not null)
        {
            DiscardPickerPlaceholder(picked);
            ShowNotice(Strings.Format("Save_AlreadyOpen", System.IO.Path.GetFileName(target)));
            return false;
        }

        var saved = await SaveEntryAsync(entry, mode, target, rebase: rebase);
        if (!saved)
        {
            DiscardPickerPlaceholder(picked);
        }

        return saved;
    }

    // ---- the picker -------------------------------------------------------------------------------------------------------------------------------

    private sealed record PickedPath(string Path, bool Placeholder);

    // The file picker makes an empty file at the name it returns. If the save then does not happen, that empty file is removed again (a file that was already there and empty is not worth keeping either).
    private async Task<PickedPath?> PickSavePathAsync(string suggestedName, string title)
    {
        if (TestSeams.Get(TestSeamGate.SaveAsPath) is { Length: > 0 } fixedPath)
        {
            return new PickedPath(System.IO.Path.GetFullPath(fixedPath), false);
        }

        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName, CommitButtonText = title };
        picker.FileTypeChoices.Add(Strings.Get("Save_FileType_Pdf"), [".pdf"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        var path = System.IO.Path.GetFullPath(file.Path);
        return new PickedPath(path, new FileInfo(path) is { Exists: true, Length: 0 });
    }

    private static void DiscardPickerPlaceholder(PickedPath picked)
    {
        try
        {
            if (picked.Placeholder && File.Exists(picked.Path) && new FileInfo(picked.Path).Length == 0)
            {
                File.Delete(picked.Path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An empty file is left; nothing else is.
        }
    }

    // ---- the save ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Saves a tab's edits (<see cref="SaveMode.Save"/>), or a copy of its document with them (<see cref="SaveMode.SaveAs"/>, <see cref="SaveMode.FlattenedCopy"/>) to <paramref name="target"/>. Tells the person what
    /// happened. True when the edits are in the file (or there was nothing to save).
    /// </summary>
    internal async Task<bool> SaveEntryAsync(TabEntry entry, SaveMode mode, string? target, bool overwrite = false, bool rebase = true)
    {
        if (entry.Session is not { IsOpen: true } session || entry.Edits is not { } edits || entry.Model.Path is not { } path || entry.SaveRun is { Running: true })
        {
            return false;
        }

        if (mode == SaveMode.Save && !edits.Model.IsDirty)
        {
            return true;
        }

        if (mode == SaveMode.FlattenedCopy && session.SignatureCount > 0 && !await ConfirmBreakSignaturesAsync())
        {
            return false;
        }

        var name = TitleOf(entry.Model);
        entry.SaveRun?.Dispose();
        var run = new SaveRun(name, mode, target, new CancellationTokenSource());
        entry.SaveRun = run;
        HideSaveStrip();
        BeginSaveStrip(entry, run);
        RefreshCommands();

        SaveFlowResult flow;
        try
        {
            var saver = await _app.Saves.GetSaverAsync(CancellationToken.None);
            flow = saver is null
                ? Unavailable("writer-host-missing")
                : await saver.RunAsync(
                    new SaveJob
                    {
                        Session = edits,
                        DocumentPath = path,
                        PageCount = session.Controller.PageCount,
                        Held = session.HeldFile,
                        Mode = mode,
                        TargetPath = target,
                        OverwriteChangedFile = overwrite,
                        PasswordProtected = session.OpenedWithPassword,
                        Progress = new Progress<SaveProgressStage>(stage => OnSaveStage(entry, run, stage)),
                    },
                    run.Cancel.Token);
        }
        catch (Exception e) when (e is EngineStartException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("Save failed: " + e.GetType().Name); // only the kind: nothing from the document
            flow = Unavailable(e is EngineStartException ? "writer-host-start" : e.GetType().Name);
        }
        finally
        {
            run.Running = false;
            run.Delay?.Stop();
        }

        if (flow.IsSaved)
        {
            await OnSavedAsync(entry, run, flow, mode, target, rebase);
            return true;
        }

        if (flow.Result.Outcome == SaveOutcome.NothingToSave)
        {
            HideSaveStrip();
            entry.SaveRun = null;
            RefreshCommands();
            return true;
        }

        if (flow.NeedsConfirmation)
        {
            HideSaveStrip();
            entry.SaveRun = null;
            RefreshCommands();
            switch (await AskChangedOnDiskAsync(name))
            {
                case ChangedAnswer.Overwrite:
                    return await SaveEntryAsync(entry, mode, target, overwrite: true, rebase: rebase);
                case ChangedAnswer.SaveAs:
                    return await SaveCopyCoreAsync(entry, SaveMode.SaveAs, edited: false, rebase: rebase);
                default:
                    return false;
            }
        }

        run.Failure = flow;
        ShowSaveFailure(entry, run, flow);
        RefreshCommands();
        return false;
    }

    private static SaveFlowResult Unavailable(string detail)
    {
        var result = new SaveResult(SaveOutcome.Failed, SaveReason.HostFailed, detail);
        return new SaveFlowResult(result, SavePresentation.Describe(result), false, false);
    }

    private async Task OnSavedAsync(TabEntry entry, SaveRun run, SaveFlowResult flow, SaveMode mode, string? target, bool rebase)
    {
        entry.SaveRun = null;
        run.Delay?.Stop();
        var shown = run.StripShown;
        run.Dispose();
        if (mode == SaveMode.FlattenedCopy)
        {
            // The working document, its tab and its journal are as they were; only the copy is new.
            var copyName = System.IO.Path.GetFileName(target ?? string.Empty);
            AnnounceSave(Strings.Format("Save_Copy_Done", copyName));
            FinishSaveStrip(shown, Strings.Format("Save_FlatCopy_Done", copyName), entry); // always shown: it says what the copy is, and that the open document is as it was
            RefreshCommands();
            return;
        }

        var current = entry.Model.Path!;
        if (flow.SwitchedToTarget && target is not null)
        {
            // Save as: the tab now shows the new file (save-pipeline 3.2); the original is as it was.
            var path = System.IO.Path.GetFullPath(target);
            entry.Model.Path = path;
            entry.Model.Identity = DocumentIdentity.Of(path);
            entry.Model.DocumentTitle = null;
            current = path;
            _app.RequestSessionWrite();
        }

        entry.Stamp = FileStamp.Take(current);
        entry.Changed = ExternalChange.None;
        entry.KeptStamp = null;
        var name = System.IO.Path.GetFileNameWithoutExtension(current);
        AnnounceSave(Strings.Format("Save_Done_Announce", name));
        FinishSaveStrip(shown, Strings.Format("Save_Done_Announce", name), null);
        OnEntryEditsChanged(entry);
        if (rebase)
        {
            await RebaseAsync(entry);
        }
    }

    /// <summary>
    /// The file has the saved edits now: the engines still map the old one, so the document is opened again on the file, at the same page and zoom (save-pipeline 3.3 stage 6). The tab keeps its edits and its place.
    /// The page shows the busy ring for a moment (the frame-level swap without a gap, annotation-core AC-15, is not built).
    /// </summary>
    private async Task RebaseAsync(TabEntry entry)
    {
        if (entry.Session is not { } session)
        {
            return;
        }

        var wasActive = ReferenceEquals(entry, _active);
        var view = session.CaptureView();
        entry.SetSession(null);
        entry.ShownLease?.Dispose();
        entry.ShownLease = null;
        if (wasActive)
        {
            DeactivateEntryView();
        }

        await session.DisposeAsync();
        await LoadEntryAsync(entry, view, removeOnFailure: false);
    }

    // ---- the strip --------------------------------------------------------------------------------------------------------------------------------

    private void BeginSaveStrip(TabEntry entry, SaveRun run)
    {
        run.Delay?.Stop();
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = SaveStripDelay;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (run.Running && ReferenceEquals(entry, _active))
            {
                ShowSaveProgress(entry, run);
            }
        };
        run.Delay = timer;
        timer.Start();
    }

    private void ShowSaveProgress(TabEntry entry, SaveRun run)
    {
        _saveLingerTimer?.Stop();
        _saveStripOwner = entry;
        run.StripShown = true;
        SaveStrip.Severity = InfoBarSeverity.Informational;
        SaveStrip.Title = Strings.Format("Save_Progress_Name", run.DocumentName);
        SaveStrip.IsClosable = false;
        SaveBar.Visibility = Visibility.Visible;
        SaveBar.IsIndeterminate = true;
        AutomationProperties.SetName(SaveBar, Strings.Format("Save_Progress_Name", run.DocumentName));
        SaveDetail.Visibility = Visibility.Collapsed;
        SaveActions.Children.Clear();
        SaveActions.Visibility = Visibility.Collapsed;
        _saveCancelButton!.Visibility = Visibility.Visible;
        _saveCancelButton.IsEnabled = run.Stage != SaveProgressStage.Replacing;
        SaveStatus.Text = StageText(run.Stage);
        SaveStrip.IsOpen = true;
        AnnounceSave(SaveStatus.Text);
    }

    private static string StageText(SaveProgressStage stage) => stage switch
    {
        SaveProgressStage.Checking => Strings.Get("Save_Stage_Checking"),
        SaveProgressStage.Replacing => Strings.Get("Save_Stage_Finishing"),
        _ => Strings.Get("Save_Stage_Saving"),
    };

    private void OnSaveStage(TabEntry entry, SaveRun run, SaveProgressStage stage)
    {
        if (!run.Running || _closed)
        {
            return;
        }

        run.Stage = stage;
        if (run.StripShown && ReferenceEquals(entry, _active) && ReferenceEquals(_saveStripOwner, entry))
        {
            SaveStatus.Text = StageText(stage);
            _saveCancelButton!.IsEnabled = stage != SaveProgressStage.Replacing; // after the replace begins there is no cancel
            AnnounceSave(SaveStatus.Text);
        }
    }

    private void CancelSave()
    {
        if (_active?.SaveRun is { Running: true, Stage: not SaveProgressStage.Replacing } run)
        {
            run.Cancel.Cancel();
        }
    }

    private void OnSaveStripKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && ActiveIsSaving)
        {
            e.Handled = true;
            CancelSave();
        }
    }

    private void HideSaveStrip()
    {
        _saveLingerTimer?.Stop();
        SaveStrip.IsOpen = false;
        _saveStripOwner = null;
    }

    // A save that was slow enough to show the strip says it is done, for a moment; one that was not says nothing but the announcement ("No toast", design 9.3).
    private void FinishSaveStrip(bool wasShown, string message, TabEntry? forceFor)
    {
        if (!wasShown && (forceFor is null || !ReferenceEquals(forceFor, _active)))
        {
            HideSaveStrip();
            return;
        }

        _saveStripOwner = forceFor ?? _saveStripOwner;
        SaveStrip.Severity = InfoBarSeverity.Success;
        SaveStrip.Title = message;
        SaveStrip.IsOpen = true;
        SaveStrip.IsClosable = true;
        SaveBar.Visibility = Visibility.Collapsed;
        _saveCancelButton!.Visibility = Visibility.Collapsed;
        SaveActions.Children.Clear();
        SaveActions.Visibility = Visibility.Collapsed;
        SaveDetail.Visibility = Visibility.Collapsed;
        SaveStatus.Text = string.Empty;
        _saveLingerTimer ??= DispatcherQueue.CreateTimer();
        _saveLingerTimer.IsRepeating = false;
        _saveLingerTimer.Interval = SavedLinger;
        _saveLingerTimer.Stop();
        _saveLingerTimer.Tick -= OnSaveLingerTick;
        _saveLingerTimer.Tick += OnSaveLingerTick;
        _saveLingerTimer.Start();
    }

    private void OnSaveLingerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!ActiveIsSaving)
        {
            HideSaveStrip();
        }
    }

    /// <summary>A save that did not happen: what happened, that the changes are still here, and what can be done (design 14 section 2.8). It stays until it is dealt with or closed.</summary>
    private void ShowSaveFailure(TabEntry entry, SaveRun run, SaveFlowResult flow)
    {
        if (flow.Notice is not { } notice)
        {
            return;
        }

        if (!ReferenceEquals(entry, _active))
        {
            return; // shown when the tab is shown again (ActivateEntry)
        }

        _saveLingerTimer?.Stop();
        _saveStripOwner = entry;
        run.StripShown = true;
        string Words(string key, SaveNoticeArgument argument) => argument switch
        {
            SaveNoticeArgument.DocumentName => Strings.Format(key, run.DocumentName),
            SaveNoticeArgument.OriginalPath => Strings.Format(key, flow.Result.OriginalAt ?? string.Empty),
            _ => Strings.Get(key),
        };
        SaveStrip.Severity = notice.Severity switch
        {
            SaveNoticeSeverity.Error => InfoBarSeverity.Error,
            SaveNoticeSeverity.Warning => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        SaveStrip.Title = Words(notice.TitleKey, notice.TitleArgument);
        SaveStrip.IsClosable = true;
        SaveBar.Visibility = Visibility.Collapsed;
        _saveCancelButton!.Visibility = Visibility.Collapsed;
        SaveStatus.Text = Words(notice.BodyKey, notice.BodyArgument);
        SaveDetail.Visibility = Visibility.Collapsed;
        SaveActions.Children.Clear();
        foreach (var action in notice.Actions)
        {
            AddSaveAction(entry, run, flow, notice, action);
        }

        SaveActions.Visibility = SaveActions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveStrip.IsOpen = true;
        AnnounceSave(SaveStrip.Title + ". " + SaveStatus.Text);
    }

    private void AddSaveAction(TabEntry entry, SaveRun run, SaveFlowResult flow, SaveNotice notice, SaveAction action)
    {
        var (textKey, id) = action switch
        {
            SaveAction.SaveAs => ("Save_Action_SaveAs", "SaveSaveAs"),
            SaveAction.TryAgain => ("Save_Try_Again", "SaveTryAgain"),
            SaveAction.Overwrite => ("Save_Action_Overwrite", "SaveOverwrite"),
            SaveAction.ShowOriginal => ("Save_Action_ShowOriginal", "SaveShowOriginal"),
            _ => ("Save_Failed_Details", "SaveDetails"),
        };
        var button = new Button { Content = Strings.Get(textKey) };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) =>
        {
            switch (action)
            {
                case SaveAction.Details:
                    SaveDetail.Text = Strings.Format("Save_Details_Code", notice.DetailCode ?? string.Empty);
                    SaveDetail.Visibility = SaveDetail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case SaveAction.ShowOriginal:
                    await ShowOriginalAsync(flow.Result.OriginalAt);
                    break;
                case SaveAction.SaveAs:
                    HideSaveStrip();
                    await RunCommandAsync("file.saveAs");
                    break;
                case SaveAction.TryAgain:
                    HideSaveStrip();
                    await SaveEntryAsync(entry, run.Mode, run.Target);
                    break;
                default:
                    HideSaveStrip();
                    await SaveEntryAsync(entry, run.Mode, run.Target, overwrite: true);
                    break;
            }
        };
        SaveActions.Children.Add(button);
    }

    private async Task ShowOriginalAsync(string? originalAt)
    {
        try
        {
            if (originalAt is { Length: > 0 } && System.IO.Path.GetDirectoryName(originalAt) is { Length: > 0 } folder)
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            ShowNotice(Strings.Get("ShowInFolder_Failed"));
        }
    }

    // A UIA notification from an element that is always there, once for each stage and once at the end (save-pipeline 5).
    private void AnnounceSave(string message)
    {
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(SaveAnnouncement)
            ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(SaveAnnouncement);
        peer?.RaiseNotificationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.Other, Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.MostRecent, message, "SaveAnnouncement");
    }

    /// <summary>A tab is shown again: the strip that belongs to it comes back, any other goes.</summary>
    private void ShowSaveStripFor(TabEntry? entry)
    {
        if (_saveStripOwner is not null && !ReferenceEquals(_saveStripOwner, entry))
        {
            HideSaveStrip();
        }

        if (entry?.SaveRun is not { } run)
        {
            return;
        }

        if (run.Running && run.StripShown)
        {
            ShowSaveProgress(entry, run);
        }
        else if (!run.Running && run.Failure is { } failure)
        {
            ShowSaveFailure(entry, run, failure);
        }
    }

    // ---- the questions ----------------------------------------------------------------------------------------------------------------------------

    private enum ChangedAnswer
    {
        Overwrite,
        SaveAs,
        Cancel,
    }

    // The path names another file now: Overwrite, Save a copy, Cancel. Save a copy is the default; overwriting is a choice (save-pipeline 3.6 item 3, 4).
    private async Task<ChangedAnswer> AskChangedOnDiskAsync(string name)
    {
        var dialog = NewDialog();
        dialog.Title = Strings.Get("Save_Changed_Title");
        dialog.Content = new TextBlock { Text = Strings.Format("Save_ChangedOnDisk", name), TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = Strings.Get("Save_SaveAs");
        dialog.SecondaryButtonText = Strings.Get("Save_Overwrite");
        dialog.CloseButtonText = Strings.Get("Save_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(dialog, "SaveChangedDialog");
        return await ShowDialogAsync(dialog) switch
        {
            ContentDialogResult.Primary => ChangedAnswer.SaveAs,
            ContentDialogResult.Secondary => ChangedAnswer.Overwrite,
            _ => ChangedAnswer.Cancel,
        };
    }

    // A flattened copy rewrites the whole file, so a signature in the document is no longer valid in the copy: asked first (save-pipeline 3.3).
    private async Task<bool> ConfirmBreakSignaturesAsync()
    {
        var dialog = NewDialog();
        dialog.Title = Strings.Get("Save_Signed_Break_Title");
        dialog.Content = new TextBlock { Text = Strings.Get("Save_Signed_Break_Body"), TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = Strings.Get("Save_Signed_Continue");
        dialog.CloseButtonText = Strings.Get("Dialog_Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "SignedBreakDialog");
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>Reload with unsaved changes: they are dropped, so ask (commands.md: "Confirms if Edits").</summary>
    private async Task<bool> ConfirmReloadAsync(TabEntry entry)
    {
        var dialog = NewDialog();
        dialog.Title = Strings.Get("Save_Revert_Title");
        dialog.Content = new TextBlock { Text = Strings.Format("Save_Revert_Confirm", TitleOf(entry.Model)), TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = Strings.Get("Save_Revert_Button");
        dialog.CloseButtonText = Strings.Get("Dialog_Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "ReloadDirtyDialog");
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    // ---- the file changed on disk (design 14 section 2.9) -------------------------------------------------------------------------------------

    /// <summary>Looks at the file of a tab when its window is activated or the tab is shown, never on a timer. Changed or gone: the banner goes up.</summary>
    private void CheckExternalChange(TabEntry? entry)
    {
        if (entry is not { Session.IsOpen: true } || entry.Stamp is not { } stamp || entry.Model.Path is not { } path || entry.SaveRun is { Running: true })
        {
            return;
        }

        _ = CheckExternalChangeAsync(entry, stamp, path);
    }

    private async Task CheckExternalChangeAsync(TabEntry entry, FileStamp stamp, string path)
    {
        var change = await Task.Run(() => ExternalChanges.Check(stamp, path));
        if (_closed || entry.Stamp != stamp)
        {
            return; // saved or reloaded meanwhile
        }

        if (change == ExternalChange.Changed && entry.KeptStamp is { } kept && FileStamp.Take(path) == kept)
        {
            change = ExternalChange.None; // "Keep what I have": quiet until it changes again
        }

        entry.Changed = change;
        if (ReferenceEquals(entry, _active))
        {
            ShowChangedBar(entry);
        }
    }

    // What the banner shows now: it is rebuilt only when this changes (the chrome is refreshed on every scroll, and a banner rebuilt each time would lose the focus and flicker).
    private (TabEntry Entry, ExternalChange Change, bool Dirty, string Name)? _changedShown;

    private void ShowChangedBar(TabEntry? entry)
    {
        if (entry is null || entry.Changed == ExternalChange.None || entry.Session is not { IsOpen: true })
        {
            ChangedBar.IsOpen = false;
            _changedShown = null;
            return;
        }

        var name = TitleOf(entry.Model);
        var shown = (entry, entry.Changed, entry.Edits is { Model.IsDirty: true }, name);
        if (ChangedBar.IsOpen && _changedShown == shown)
        {
            return;
        }

        _changedShown = shown;
        var changed = entry.Changed == ExternalChange.Changed;
        ChangedBar.Title = Strings.Format(changed ? "ExternalChange_Changed" : "ExternalChange_Gone", name);
        ChangedDetail.Text = Strings.Get("ExternalChange_Edits");
        ChangedDetail.Visibility = changed && entry.Edits is { Model.IsDirty: true } ? Visibility.Visible : Visibility.Collapsed;
        ChangedActions.Children.Clear();
        if (changed)
        {
            var reload = new Button { Content = Strings.Get("ExternalChange_Reload") };
            AutomationProperties.SetAutomationId(reload, "ChangedReload");
            reload.Click += (_, _) => _ = RunCommandAsync("file.reload");
            ChangedActions.Children.Add(reload);
        }

        var keep = new Button { Content = Strings.Get("ExternalChange_Keep") };
        AutomationProperties.SetAutomationId(keep, "ChangedKeep");
        keep.Click += (_, _) =>
        {
            entry.KeptStamp = FileStamp.Take(entry.Model.Path!);
            entry.Changed = ExternalChange.None;
            ChangedBar.IsOpen = false;
            _changedShown = null;
        };
        ChangedActions.Children.Add(keep);
        ChangedBar.IsOpen = true;
    }
}
