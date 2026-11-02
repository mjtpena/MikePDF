using MikePDF.Broker.Saving;
using MikePDF.Core.Editing.Journal;
using MikePDF.Core.Editing.Recovery;

namespace MikePDF.Integration.Tests.Saving;

/// <summary>
/// SP-16, SP-29 (save-pipeline 14): a kill at every step of a save, for both replace mechanisms. A kill is simulated by throwing from the step's
/// fault point: nothing in the save catches it, so nothing cleans up, exactly what a dead process leaves. After it, the next start's recovery must
/// leave the old file or the new one, never anything else, and no file the save created.
/// </summary>
public sealed class SaveCrashMatrixTests
{
    public static TheoryData<SaveStep, string> Steps
    {
        get
        {
            var data = new TheoryData<SaveStep, string>();
            foreach (var mechanism in new[] { "posix-rename", "replace-file-fallback" })
            {
                foreach (var step in Enum.GetValues<SaveStep>())
                {
                    data.Add(step, mechanism);
                }
            }

            return data;
        }
    }

    private static ScriptedNative Mechanism(string name)
    {
        var native = new ScriptedNative(new WindowsReplaceNative());
        if (name == "replace-file-fallback")
        {
            native.PosixAnswers.Enqueue(87); // what SMB answers: the fallback runs
        }

        return native;
    }

    private static bool IsNewFile(byte[] current, byte[] old) =>
        current.Length > old.Length
        && current.AsSpan(0, old.Length).SequenceEqual(old)
        && Soundness.NoNewErrors(current, old)
        && InProcessSaveHost.Count(current, SaveCoordinatorTests.Rig.PagesOf(current), [0])![0] == 1;

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task AKillAtAnyStepLeavesTheOldFileOrTheNewOneAndRecoveryRemovesWhatTheSaveCreated(SaveStep step, string mechanism)
    {
        var crash = new CrashAt(step);
        using var rig = new SaveCoordinatorTests.Rig(faults: crash, native: Mechanism(mechanism));
        rig.Session.Model.Apply(Commands.Highlight());

        await Assert.ThrowsAsync<SimulatedCrash>(() => rig.SaveAsync());
        rig.Session.Dispose(); // the process is gone: its handles are closed, and that is all that happened

        // The next start.
        var service = new RecoveryService(rig.Store);
        var candidates = service.Scan();

        var current = File.ReadAllBytes(rig.Doc);
        var isOld = current.AsSpan().SequenceEqual(rig.Original);
        var isNew = IsNewFile(current, rig.Original);
        Assert.True(isOld || isNew, "the target is neither the old file nor a valid new one");
        Assert.Equal(step >= SaveStep.Replaced, isNew);
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing()); // no temporary file, no backup: "no temporary file left after the next start"
        if (isOld)
        {
            // The edits were never written to the file: they are offered back.
            var candidate = Assert.Single(candidates);
            Assert.Equal(RecoveryAction.Replay, candidate.Action);
            Assert.Equal(1, candidate.Summary.AnnotationChanges);
        }
        else
        {
            Assert.Empty(candidates); // the edits are in the file: the journal is cleared without a word
            Assert.Empty(rig.Store.List());
        }

        // And the document can be saved again, from the recovered edits if there were any.
        if (isOld)
        {
            var (restored, report) = service.Restore(candidates[0]);
            Assert.Equal(1, report.Applied);
            using (restored)
            {
                using var held = File.OpenHandle(rig.Doc, FileMode.Open, FileAccess.Read, MikePDF.Broker.EngineSupervisor.DocumentShare);
                var again = new SaveCoordinator(rig.Host, rig.Engine, new FileReplacer(5, TimeSpan.FromMilliseconds(5)));
                var result = await again.SaveSessionAsync(restored, rig.Doc, rig.Doc, SaveMode.Save, rig.PageCount, held);
                Assert.True(result.IsSaved, result.Reason + " " + result.Detail);
            }

            Assert.True(IsNewFile(File.ReadAllBytes(rig.Doc), rig.Original));
            Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
        }
    }

    [Fact]
    public async Task AReplaceFileKilledBetweenItsTwoRenamesLeavesNoFileAtTheTargetUntilRecoveryRestoresTheOldOne()
    {
        var native = new ScriptedNative(new WindowsReplaceNative());
        native.PosixAnswers.Enqueue(87);
        native.ReplaceFileOverride = (target, replacement, backup) =>
        {
            // ReplaceFileW renames the original to the backup name, then moves the replacement in. Here the process dies between the two.
            File.Move(target, backup!);
            throw new SimulatedCrash("ReplaceFileW, between its renames");
        };
        using var rig = new SaveCoordinatorTests.Rig(native: native);
        rig.Session.Model.Apply(Commands.Highlight());

        await Assert.ThrowsAsync<SimulatedCrash>(() => rig.SaveAsync());
        rig.Session.Dispose();
        rig.Held.Dispose(); // a dead process holds nothing

        Assert.False(File.Exists(rig.Doc), "S10: the target really is missing in this window");
        var left = rig.Workspace.Listing();
        Assert.Equal(2, left.Length); // the old file under the recorded backup name, and the complete new file under the temporary one
        Assert.All(left, static n => Assert.Contains(".mikepdf-", n, StringComparison.Ordinal));

        var candidates = new RecoveryService(rig.Store).Scan();

        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc)); // restored from the backup
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
        Assert.Single(candidates); // and the edits are offered back
    }

    [Fact]
    public async Task TheLiteralJournalLeaksTheTemporaryFileThatTheSaveIntentJournalCleansUp()
    {
        // S10 section 8.1. The spec as first written recorded the temporary file only in SaveBegin, which cannot exist until the output is complete.
        // A kill between the creation of the file and SaveBegin therefore left a hidden file nothing would ever remove.
        foreach (var step in new[] { SaveStep.TempCreated, SaveStep.OutputWritten })
        {
            using var rig = new SaveCoordinatorTests.Rig(faults: new CrashAt(step));
            rig.Session.Model.Apply(Commands.Highlight());
            await Assert.ThrowsAsync<SimulatedCrash>(() => rig.SaveAsync());
            rig.Session.Dispose();
            var journal = rig.Store.Read(rig.Store.List().Single().Path);
            var leftover = Directory.GetFiles(rig.Workspace.Documents, ".*", SearchOption.TopDirectoryOnly).Single();

            var literal = LiteralRecovery(journal);

            Assert.Empty(literal); // the literal design knows no name to remove
            Assert.True(File.Exists(leftover), $"the literal design leaks the temporary file after a kill at {step}");

            var report = SaveRecovery.Run(journal);

            Assert.Equal(InterruptedSave.RolledBack, report.Outcome);
            Assert.Equal([leftover], report.RemovedFiles);
            Assert.False(File.Exists(leftover));
        }
    }

    /// <summary>What recovery could remove if it knew only what a <c>SaveBegin</c> record names (the design S10 found leaking).</summary>
    private static List<string> LiteralRecovery(JournalReadResult journal) =>
        journal.Entries.OfType<SaveBeginEntry>().Any() ? [.. journal.Entries.OfType<SaveIntentEntry>().Select(static i => i.Temp)] : [];

    [Fact]
    public async Task AKillWhileTheOutputIsBeingWrittenLeavesAPartialFileThatRecoveryRemoves()
    {
        using var rig = new SaveCoordinatorTests.Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        rig.Host.Corrupt = bytes => throw new SimulatedCrash("the host, halfway through writing");

        await Assert.ThrowsAsync<SimulatedCrash>(() => rig.SaveAsync());
        rig.Session.Dispose();

        Assert.Equal(2, rig.Workspace.Listing().Length); // the document and the (empty or partial) temporary file
        var candidates = new RecoveryService(rig.Store).Scan();
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
        Assert.Single(candidates);
    }

    [Fact]
    public async Task ASaveAsKilledAtAnyStepNeverCreatesAHalfWrittenFileAtTheNewName()
    {
        foreach (var step in Enum.GetValues<SaveStep>())
        {
            using var rig = new SaveCoordinatorTests.Rig(faults: new CrashAt(step));
            rig.Session.Model.Apply(Commands.Highlight());
            var copy = Path.Combine(rig.Workspace.Documents, "copy.pdf");

            await Assert.ThrowsAsync<SimulatedCrash>(() => rig.SaveAsync(SaveMode.SaveAs, copy));
            rig.Session.Dispose();
            new RecoveryService(rig.Store).Scan();

            Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
            if (File.Exists(copy))
            {
                Assert.True(IsNewFile(File.ReadAllBytes(copy), rig.Original), $"a kill at {step} left a broken file at the new name");
                Assert.Equal(["copy.pdf", "doc.pdf"], rig.Workspace.Listing());
            }
            else
            {
                Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
            }
        }
    }
}
