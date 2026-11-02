using System.Diagnostics;
using MikePDF.Broker;
using MikePDF.Broker.Saving;
using MikePDF.Core.Editing.Journal;
using MikePDF.Core.Editing.Recovery;

namespace MikePDF.Integration.Tests.Saving;

/// <summary>
/// SP-16 with a real kill: a child process (this test assembly, running one test) saves a document and, at a chosen step, stops and waits; the parent
/// ends it with <c>TerminateProcess</c>, which is what a crash, a task manager kill or a power button on a desktop looks like to the file system, and
/// runs the next start's recovery. <see cref="SaveCrashMatrixTests"/> covers every step by throwing; this proves the same invariants after a process
/// really dies with its handles open, its caches unflushed and its journal held.
/// </summary>
public sealed class SaveKillTests
{
    private const string DirectoryVariable = "MIKEPDF_TEST_KILL_DIR";
    private const string StepVariable = "MIKEPDF_TEST_KILL_STEP";
    private const string MechanismVariable = "MIKEPDF_TEST_KILL_MECHANISM";

    public static TheoryData<SaveStep, string> Cases
    {
        get
        {
            var data = new TheoryData<SaveStep, string>();
            foreach (var mechanism in new[] { "posix-rename", "replace-file-fallback" })
            {
                foreach (var step in new[] { SaveStep.IntentWritten, SaveStep.TempCreated, SaveStep.OutputWritten, SaveStep.BeginWritten, SaveStep.Validated, SaveStep.Replaced, SaveStep.CommittedWritten })
                {
                    data.Add(step, mechanism);
                }
            }

            return data;
        }
    }

    /// <summary>The child. Does nothing unless the parent started it for a kill test.</summary>
    [Fact]
    public async Task ChildSaveThatWaitsToBeKilled()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (directory is null)
        {
            return;
        }

        var step = Enum.Parse<SaveStep>(Environment.GetEnvironmentVariable(StepVariable)!);
        var native = new ScriptedNative(new WindowsReplaceNative());
        if (Environment.GetEnvironmentVariable(MechanismVariable) == "replace-file-fallback")
        {
            native.PosixAnswers.Enqueue(87);
        }

        var document = Path.Combine(directory, "docs", "doc.pdf");
        var store = new JournalStore(Path.Combine(directory, "private", "journals"));
        using var session = new EditSession(store, document, "1.0.0.0");
        session.Model.Apply(Commands.Highlight());
        using var held = File.OpenHandle(document, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        var faults = new RecordingFaults
        {
            Observe = reached =>
            {
                if (reached == step)
                {
                    File.WriteAllText(Path.Combine(directory, "reached.txt"), reached.ToString());
                    Thread.Sleep(Timeout.Infinite); // the parent ends this process
                }
            },
        };
        var coordinator = new SaveCoordinator(new InProcessSaveHost(), new OkEngineCheck(), new FileReplacer(native, 5, TimeSpan.FromMilliseconds(5)), faults);

        await coordinator.SaveSessionAsync(session, document, document, SaveMode.Save, 2, held);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AProcessKilledAtAStepOfASaveLeavesTheOldFileOrTheNewOneAndRecoveryCleansUp(SaveStep step, string mechanism)
    {
        using var workspace = new SaveWorkspace();
        var document = workspace.Copy("hello_world_2_pages.pdf");
        var original = File.ReadAllBytes(document);
        var reached = Path.Combine(workspace.Directory, "reached.txt");

        var start = new ProcessStartInfo(Environment.ProcessPath!, "-method MikePDF.Integration.Tests.Saving.SaveKillTests.ChildSaveThatWaitsToBeKilled")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.Environment[DirectoryVariable] = workspace.Directory;
        start.Environment[StepVariable] = step.ToString();
        start.Environment[MechanismVariable] = mechanism;
        using var child = Process.Start(start)!;
        _ = child.StandardOutput.ReadToEndAsync();
        _ = child.StandardError.ReadToEndAsync();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!File.Exists(reached) && !child.HasExited && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(File.Exists(reached), "the child never reached the step; it exited with " + (child.HasExited ? child.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(still running)"));
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();

        // The next start.
        var store = new JournalStore(workspace.Journals);
        var service = new RecoveryService(store);
        var candidates = service.Scan();

        var current = File.ReadAllBytes(document);
        var isOld = current.AsSpan().SequenceEqual(original);
        var isNew = current.Length > original.Length
            && current.AsSpan(0, original.Length).SequenceEqual(original)
            && Soundness.NoNewErrors(current, original)
            && InProcessSaveHost.Count(current, 2, [0])![0] == 1;
        Assert.True(isOld || isNew, "the target is neither the old file nor a valid new one");
        Assert.Equal(step >= SaveStep.Replaced, isNew);
        Assert.Equal(["doc.pdf"], workspace.Listing());
        if (isOld)
        {
            var candidate = Assert.Single(candidates);
            Assert.Equal(RecoveryAction.Replay, candidate.Action);
            Assert.Equal(1, candidate.Summary.AnnotationChanges);
        }
        else
        {
            Assert.Empty(candidates);
            Assert.Empty(store.List());
        }
    }
}
