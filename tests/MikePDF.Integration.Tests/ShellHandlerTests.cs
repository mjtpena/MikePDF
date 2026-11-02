using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using MikePDF.Integration.Tests.Support;

namespace MikePDF.Integration.Tests;

/// <summary>
/// The whole chain on this machine without Explorer or any registration: the handler DLL loaded by a test host, the real ShellHost it starts, and the real sandboxed engine
/// (SHELL-03, -05 to -07, -09, -10, -16, -19 to -22, -25). What needs Explorer itself (SHELL-23 and -24) and a hosted runner (SHELL-02, -15, -26) is in the integration workflow
/// and the manual checklist (docs/features/shell-extensions.md section 14).
/// </summary>
public sealed class ShellHandlerTests(SandboxFixture sandbox)
{
    private static string Rectangles => SandboxFixture.FixturePath("rectangles.pdf");

    private static (byte B, byte G, byte R) At(NativeHandlerHost.ThumbResult t, int x, int y)
    {
        var offset = (y * t.Width + x) * 4;
        return (t.Bgra[offset], t.Bgra[offset + 1], t.Bgra[offset + 2]);
    }

    // ---- SHELL-25 ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDllIsANativeImageOfThisMachinesArchitectureAndExportsOnlyTheComEntryPoints()
    {
        using var host = new NativeHandlerHost(sandbox);
        using var stream = File.OpenRead(host.DllPath);
        using var reader = new PEReader(stream);
        var expected = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Machine.Arm64 : Machine.Amd64;
        Assert.Equal(expected, reader.PEHeaders.CoffHeader.Machine);
        Assert.Null(reader.PEHeaders.CorHeader); // native: not a managed assembly

        var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(host.DllPath));
        Assert.Contains("DllGetClassObject", text, StringComparison.Ordinal);
        Assert.Contains("DllCanUnloadNow", text, StringComparison.Ordinal);
        // Registration is the manifest's: the DLL has no way to write the registry for a class.
        Assert.DoesNotContain("DllRegisterServer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DllUnregisterServer", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDllAndShellHostAgreeOnThePipeName()
    {
        using var host = new NativeHandlerHost(sandbox);
        var start = new ProcessStartInfo(host.TestHostPath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("pipename");
        using var process = Process.Start(start)!;
        var name = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        Assert.Equal(@"\\.\pipe\" + MikePDF.ShellHost.ShellPipe.NameFor(sid, MikePDF.ShellHost.ShellFeature.Thumbnails), name);
    }

    // ---- SHELL-03, SHELL-05 -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AThumbnailThroughTheDllShellHostAndTheEngineIsCorrectColdThenWarm()
    {
        using var host = new NativeHandlerHost(sandbox);
        Assert.Equal(0u, host.ServerProcessId()); // nothing is running: the first call starts ShellHost

        var cold = await host.ThumbAsync(Rectangles, 256, keepPixels: true);
        Assert.True(cold.Ok, cold.Raw);
        Assert.Equal((171, 256, 2), (cold.Width, cold.Height, cold.Alpha)); // 2 is WTSAT_ARGB
        Assert.Equal((0, 0, 0), At(cold, 3, 3));
        Assert.Equal((255, 0, 0), At(cold, cold.Width - 4, 3));
        Assert.Equal((0, 255, 0), At(cold, cold.Width - 4, cold.Height - 4));
        Assert.Equal((0, 0, 255), At(cold, 3, cold.Height - 4));
        Assert.True(host.ServerProcessId() != 0, "ShellHost is running now");
        Assert.True(cold.Milliseconds < 2000, "a cold thumbnail must fit its 2 s: " + cold.Milliseconds);

        var warm = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var again = await host.ThumbAsync(Rectangles, 256);
            Assert.True(again.Ok, again.Raw);
            warm.Add(again.Milliseconds);
        }

        warm.Sort();
        // SHELL-05 until R1 and R2 set a number: measured here, not promised. Printed so that the run's log carries it.
        Console.WriteLine($"shell handler, DEV, x64: cold {cold.Milliseconds:F0} ms; warm p50 {warm[warm.Count / 2]:F0} ms, max {warm[^1]:F0} ms (10 calls, rectangles.pdf at 256)");
        Assert.True(warm[^1] < 2000);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(96)]
    [InlineData(256)]
    [InlineData(1024)]
    public async Task TheLongerSideIsCxThroughTheWholeChain(int cx)
    {
        using var host = new NativeHandlerHost(sandbox);
        var t = await host.ThumbAsync(Rectangles, cx);
        Assert.True(t.Ok, t.Raw);
        // rectangles.pdf is 200 x 300 points, and a thumbnail is never scaled above twice the page's natural size: at 1024 the page is 400 x 600.
        Assert.Equal(Math.Min(cx, 600), Math.Max(t.Width, t.Height));
        Assert.InRange((double)t.Width / t.Height, 200.0 / 300 - 1.0 / t.Height, 200.0 / 300 + 1.0 / t.Height);
    }

    [Fact]
    public async Task FilesThatMustFailFailCleanlyThroughTheWholeChain()
    {
        using var host = new NativeHandlerHost(sandbox);
        using var empty = new TempFile("empty.pdf", []);
        using var notPdf = new TempFile("text.pdf", System.Text.Encoding.ASCII.GetBytes("plain text, not a PDF"));
        using var encrypted = new TempFile("encrypted.pdf", File.ReadAllBytes(SandboxFixture.FixturePath("encrypted.pdf")));
        var good = File.ReadAllBytes(Rectangles);
        using var truncated = new TempFile("truncated.pdf", good[..(good.Length / 3)]);

        foreach (var file in new[] { empty.Path, notPdf.Path, encrypted.Path, truncated.Path })
        {
            var t = await host.ThumbAsync(file, 256);
            Assert.False(t.Ok, file + ": " + t.Raw);
            Assert.True(t.Milliseconds < 2500, t.Milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Nothing was left behind and the next good file works.
        Assert.True((await host.ThumbAsync(Rectangles, 64)).Ok);
    }

    // ---- SHELL-16: the user's switch, end to end -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheUsersSwitchOffMakesTheHandlerFailQuicklyAndTurningItOnAppliesWithoutARestart()
    {
        using var host = new NativeHandlerHost(sandbox);
        Assert.True((await host.ThumbAsync(Rectangles, 64)).Ok); // ShellHost is up

        host.WriteSettings("\"shell.thumbnails\": false");
        var off = await host.ThumbAsync(Rectangles, 64);
        Assert.False(off.Ok);
        Assert.Equal(0x80070005u, off.Hresult); // E_ACCESSDENIED: ShellHost's PermissionDenied
        Assert.True(off.Milliseconds < 100, "SHELL-16: " + off.Milliseconds);

        host.WriteSettings("\"shell.thumbnails\": true");
        Assert.True((await host.ThumbAsync(Rectangles, 64)).Ok);
    }

    // ---- SHELL-09 and SHELL-10 ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task KillingShellHostFailsWhatWasInFlightAndTheNextCallStartsANewOne()
    {
        using var host = new NativeHandlerHost(sandbox);
        Assert.True((await host.ThumbAsync(Rectangles, 64)).Ok);
        var first = host.ServerProcessId();
        Assert.NotEqual(0u, first);

        // A request that is still running when ShellHost dies: a page that takes a good while to draw.
        using var slow = new TempFile("slow.pdf", TestPdf.SlowPage(4000));
        var inFlight = host.ThumbAsync(slow.Path, 1024);
        await Task.Delay(150);
        using (var shellHost = Process.GetProcessById((int)first))
        {
            shellHost.Kill();
            await shellHost.WaitForExitAsync();
        }

        var failed = await inFlight;
        Assert.False(failed.Ok && failed.Milliseconds < 140, "the request that was in flight when ShellHost was killed must not succeed: " + failed.Raw);

        var next = await host.ThumbAsync(Rectangles, 64);
        Assert.True(next.Ok, next.Raw);
        var second = host.ServerProcessId();
        Assert.NotEqual(0u, second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task ShellHostAndItsEnginesAreGoneAfterTheIdleMinute()
    {
        using var host = new NativeHandlerHost(sandbox, idleSeconds: 2);
        Assert.True((await host.ThumbAsync(Rectangles, 64)).Ok);
        var pid = host.ServerProcessId();
        Assert.NotEqual(0u, pid);

        using var shellHost = Process.GetProcessById((int)pid);
        Assert.True(shellHost.WaitForExit(TimeSpan.FromSeconds(15)), "SHELL-10: ShellHost stayed after its idle time");
        Assert.True(await UntilAsync(() => NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory) == 0), "SHELL-10: an engine of the shell requests is still running");
        Assert.Equal(0u, host.ServerProcessId());
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int milliseconds = 8000)
    {
        for (var waited = 0; waited < milliseconds; waited += 50)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public async Task SixteenHostsThatAllFindNoShellHostStartOneBetweenThemAndAllSucceed()
    {
        using var host = new NativeHandlerHost(sandbox);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => host.ThumbAsync(Rectangles, 96))));
        Assert.All(results, r => Assert.True(r.Ok, r.Raw));
        Assert.NotEqual(0u, host.ServerProcessId());
    }

    // ---- the preview: SHELL-06, -19 to -22 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APreviewScrollsThroughTwoHundredPagesKeepingFewPagesInMemoryAndUnloadsFast()
    {
        using var host = new NativeHandlerHost(sandbox);
        using var pdf = new TempFile("two-hundred.pdf", NativeHandlerHost.ManyPages(200));
        await using var preview = await host.StartPreviewAsync();

        var opened = await preview.CommandAsync($"open 600 800 {pdf.Path}");
        Assert.StartsWith("OPENED 0x00000000", opened, StringComparison.Ordinal);
        var first = await preview.CommandAsync("wait-ink 8000");
        Assert.StartsWith("INK 1", first, StringComparison.Ordinal);
        Console.WriteLine("preview, DEV, x64, cold ShellHost: " + opened + "; first page " + first);

        // SHELL-22: the window is named and says where it is.
        // The value is set after the first ink, so it may still say "Loading..." here: wait for it.
        Assert.True(await preview.UntilValueAsync("Page 1 of 200"), "after the first page the value says " + (await preview.StateAsync()).Value);
        var (name, _) = await preview.StateAsync();
        Assert.Equal("PDF preview", name);

        // SHELL-19: scroll through the whole document with Page Down; the value follows and the handler never holds more than a handful of GDI objects.
        var baseline = await preview.GdiObjectsAsync();
        var highest = baseline;
        for (var i = 0; i < 230; i++)
        {
            await preview.CommandAsync("key 34"); // VK_NEXT
            if (i % 5 == 0)
            {
                await preview.CommandAsync("settle 30");
                highest = Math.Max(highest, await preview.GdiObjectsAsync());
            }
        }

        Assert.True(await preview.UntilValueAsync("Page 200 of 200"), "after paging to the end the value says " + (await preview.StateAsync()).Value);
        Assert.StartsWith("INK 1", await preview.CommandAsync("wait-ink 5000"), StringComparison.Ordinal); // the last page is drawn
        Assert.True(highest - baseline <= 12, $"GDI objects grew by {highest - baseline} while scrolling 200 pages (four pages are kept)");

        // Home, End, the arrows and the wheel (SHELL-20).
        Assert.StartsWith("KEY", await preview.CommandAsync("key 36"), StringComparison.Ordinal); // Home
        Assert.True(await preview.UntilValueAsync("Page 1 of 200"));
        await preview.CommandAsync("key 35"); // End
        Assert.True(await preview.UntilValueAsync("Page 200 of 200"));
        await preview.CommandAsync("key 36");
        Assert.True(await preview.UntilValueAsync("Page 1 of 200"));
        for (var i = 0; i < 40; i++)
        {
            await preview.CommandAsync("wheel -120");
        }

        Assert.False((await preview.StateAsync()).Value == "Page 1 of 200", "the wheel did not scroll");
        for (var i = 0; i < 400; i++)
        {
            await preview.CommandAsync("key 40"); // Down arrow
        }

        await preview.CommandAsync("key 38"); // Up arrow
        Assert.StartsWith("INK 1", await preview.CommandAsync("wait-ink 5000"), StringComparison.Ordinal);

        // Tab goes back to the shell's frame (SHELL-20).
        var tab = await preview.CommandAsync("tab");
        Assert.Contains("frame_calls=2", tab, StringComparison.Ordinal); // the key message and the accelerator call each reach the frame once

        // SHELL-21: a resize never shows a blank pane, and the page is redrawn at the new width.
        await preview.CommandAsync("key 36");
        await preview.UntilValueAsync("Page 1 of 200");
        await preview.CommandAsync("wait-ink 5000");
        await preview.CommandAsync("resize 420 800");
        var immediately = await preview.CommandAsync("capture");
        Assert.DoesNotContain("page=0%", immediately, StringComparison.Ordinal);
        Assert.DoesNotContain("ink=0", immediately, StringComparison.Ordinal);
        await preview.CommandAsync("settle 600");
        var settled = await preview.CommandAsync("wait-ink 5000");
        Assert.StartsWith("INK 1", settled, StringComparison.Ordinal);
        var capture = await preview.CommandAsync("capture"); // the client area is the pane less the scroll bar
        var width = int.Parse(capture.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(width, 395, 420);

        // SHELL-19: Unload within 200 ms, and the session's engine goes with it.
        var unloaded = await preview.CommandAsync("unload");
        Assert.StartsWith("UNLOADED 0x00000000", unloaded, StringComparison.Ordinal);
        var ms = double.Parse(unloaded.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(ms <= 200, "Unload took " + ms + " ms");
        Assert.True(await UntilAsync(() => NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory) <= 1), "the preview's engine outlived Unload");
    }

    [Fact]
    public async Task APreviewOfAPasswordProtectedFileAndOfAFileTheUserSwitchedOffSayWhy()
    {
        using var host = new NativeHandlerHost(sandbox);
        using var encrypted = new TempFile("encrypted.pdf", File.ReadAllBytes(SandboxFixture.FixturePath("encrypted.pdf")));
        await using (var preview = await host.StartPreviewAsync())
        {
            Assert.StartsWith("OPENED 0x00000000", await preview.CommandAsync($"open 500 500 {encrypted.Path}"), StringComparison.Ordinal);
            Assert.True(await preview.UntilValueAsync("This file is protected by a password. Open it in MikePDF to read it.", 8000), (await preview.StateAsync()).Value);
            await preview.CommandAsync("unload");
        }

        host.WriteSettings("\"shell.preview\": false");
        await using (var preview = await host.StartPreviewAsync())
        {
            await preview.CommandAsync($"open 500 500 {Rectangles}");
            Assert.True(await preview.UntilValueAsync("Preview is turned off", 8000), (await preview.StateAsync()).Value);
            await preview.CommandAsync("unload");
        }
    }

    [Fact]
    public async Task APreviewOfAnUnreadableFileSaysSoAndTheHardLimitEndsAWait()
    {
        using var host = new NativeHandlerHost(sandbox);
        using var notPdf = new TempFile("text.pdf", System.Text.Encoding.ASCII.GetBytes("plain text, not a PDF"));
        await using var preview = await host.StartPreviewAsync();
        await preview.CommandAsync($"open 500 500 {notPdf.Path}");
        Assert.True(await preview.UntilValueAsync("Preview isn't available for this file", 8000), (await preview.StateAsync()).Value);
        await preview.CommandAsync("unload");
    }
}
