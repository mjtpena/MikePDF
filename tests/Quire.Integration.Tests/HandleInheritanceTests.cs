using Quire.Broker;
using Quire.Integration.Tests.Support;

namespace Quire.Integration.Tests;

/// <summary>
/// R1: nothing of the broker's is ever inheritable. A sandboxed process is created suspended with bInheritHandles = FALSE and no handle list;
/// handles reach it by DuplicateHandle (a document, the slot ring) and diagnostics through a named pipe. So there is no window in which another
/// CreateProcess in the broker (a .NET Process.Start with redirected streams, say) could hand an unrelated child a handle of ours.
/// <para>
/// Checked two ways, both with <c>GetHandleInformation</c> (chosen over NtQuerySystemInformation: no privilege, no undocumented structure):
/// (1) every kernel handle the broker holds for a sandboxed process (process, job, pipes, the slot-ring section) is looked at by value and must be live and
/// not inheritable; (2) a scan of every handle value in this process (the table is dense, multiples of 4, so probing up to well past the handle count
/// finds them all) before and after a series of launches must find no new inheritable handle. A positive control proves the scan can see one.
/// There is nothing to watch <i>during</i> a launch, because no handle ever becomes inheritable, which is the point.
/// </para>
/// </summary>
public sealed class HandleInheritanceTests(SandboxFixture sandbox)
{
    [Fact]
    public void TheScanCanSeeAnInheritableHandle()
    {
        using var marker = new ManualResetEvent(false);
        var value = marker.SafeWaitHandle.DangerousGetHandle();
        Assert.False(NativeTestMethods.IsInheritable(value));
        NativeTestMethods.SetInheritable(value, true);
        try
        {
            Assert.True(NativeTestMethods.IsInheritable(value));
            Assert.Contains(value, NativeTestMethods.InheritableHandles());
        }
        finally
        {
            NativeTestMethods.SetInheritable(value, false);
        }
    }

    [Fact]
    public async Task EveryHandleTheBrokerHoldsForASandboxedProcessIsLiveAndNotInheritable()
    {
        await using var supervisor = await sandbox.StartAsync();
        await using var host = await sandbox.StartWriterHostAsync();
        await using var raw = await RawEngine.StartAsync(sandbox);
        var held = new List<nint>(supervisor.BrokerHandles);
        held.AddRange(host.Supervisor.BrokerHandles);
        held.AddRange(raw.BrokerHandles);

        Assert.True(held.Count >= 3 * 3, $"expected at least a process, a job and a pipe per process, got {held.Count} handles");
        foreach (var handle in held)
        {
            Assert.True(NativeTestMethods.IsLive(handle), $"handle {handle} is not a live handle");
            Assert.False(NativeTestMethods.IsInheritable(handle), $"handle {handle} is inheritable");
        }
    }

    [Fact]
    public async Task NoNewInheritableHandleSurvivesAnythingTheBrokerDoes()
    {
        var baseline = NativeTestMethods.InheritableHandles();

        await using (var supervisor = await sandbox.StartAsync())
        {
            await using var document = (await supervisor.OpenDocumentAsync(SandboxFixture.FixturePath("rectangles.pdf"))).Value!;
            using var tile = (await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, 64, 64), 1)).Value!;
            AssertNothingNew(baseline);
        }

        await using (var host = await sandbox.StartWriterHostAsync())
        {
            AssertNothingNew(baseline);
        }

        using (var runner = new ProbeRunner(sandbox))
        {
            var report = await runner.RunSandboxedAsync();
            Assert.True(report.Done);
        }

        await using (var raw = await RawEngine.StartAsync(sandbox))
        {
            await raw.HelloAsync();
            AssertNothingNew(baseline);
        }

        AssertNothingNew(baseline);
    }

    // Look twice: a handle that an unrelated thread of the test host opens and closes in between is not a leak of ours, one that stays is.
    private static void AssertNothingNew(HashSet<nint> baseline)
    {
        if (NativeTestMethods.InheritableHandles().Except(baseline).Any())
        {
            Thread.Sleep(250);
        }

        var added = NativeTestMethods.InheritableHandles().Except(baseline).ToList();
        Assert.True(added.Count == 0, "inheritable handles appeared: " + string.Join(", ", added.Select(h => $"{h} (file type {NativeTestMethods.FileTypeOf(h)})")));
    }
}
