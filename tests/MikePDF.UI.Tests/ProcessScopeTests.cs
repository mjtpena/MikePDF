using System.Diagnostics;
using MikePDF.UI.Tests.Pages;

namespace MikePDF.UI.Tests;

/// <summary>The journeys end only the processes of the app under test: never a MikePDF.Engine that belongs to somebody else.</summary>
public sealed class ProcessScopeTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mikepdf-scope-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A decoy's executable can stay locked for a moment after its process ends; the folder is in the temp directory.
        }
    }

    [Fact]
    public void TheDescendantsOfAProcessAreItsChildrenAndTheirsAndNobodyElses()
    {
        // cmd starts ping, which is a child; the decoy engine is unrelated to cmd.
        using var decoy = DecoyEngine.Start(_work);
        var start = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true };
        using var parent = Process.Start(start)!;
        try
        {
            var found = new List<(int Id, string Exe)>();
            for (var attempt = 0; attempt < 50 && !found.Any(d => string.Equals(d.Exe, "PING.EXE", StringComparison.OrdinalIgnoreCase)); attempt++)
            {
                found = [.. ProcessTree.DescendantsOf(parent.Id)];
                Thread.Sleep(100);
            }

            Assert.Contains(found, d => string.Equals(d.Exe, "PING.EXE", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(found, d => d.Id == decoy.Id);
            Assert.DoesNotContain(found, d => d.Id == parent.Id);
            Assert.DoesNotContain(ProcessTree.DescendantsOf(decoy.Id), d => d.Id == parent.Id);
            Assert.Empty(ProcessTree.DescendantsOf(int.MaxValue));
        }
        finally
        {
            parent.Kill(entireProcessTree: true);
        }
    }

    [UiFact]
    public void KillAllEndsTheAppAndItsEnginesButLeavesAnotherEngineAlone()
    {
        using var decoy = DecoyEngine.Start(_work);
        using (var app = MikePDFApp.Launch(MikePDFApp.Fixture("hello_world_2_pages.pdf")))
        {
            app.WaitForDocument(2);
            var engines = app.EngineIds();
            Assert.NotEmpty(engines);
            Assert.DoesNotContain(decoy.Id, engines);

            MikePDFApp.KillAll();
            var gone = FlaUI.Core.Tools.Retry.WhileFalse(() => engines.All(id => !Exists(id)), TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(200));
            Assert.True(gone.Success, "the app's engines were still running after KillAll");
        }

        Assert.False(decoy.HasExited, "KillAll ended an engine that is not an app's");
    }

    private static bool Exists(int id)
    {
        try
        {
            using var p = Process.GetProcessById(id);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
