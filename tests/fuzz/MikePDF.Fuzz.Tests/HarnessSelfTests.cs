using System.Diagnostics;

namespace MikePDF.Fuzz.Tests;

/// <summary>
/// Proves the fuzz harness itself: a target that is made to fail on purpose is found, reported with exit code 1, and its reproducer is kept.
/// Without this, "the fuzzers found nothing" could just mean "the fuzzers cannot find anything".
/// </summary>
public class HarnessSelfTests
{
    /// <summary>The saved inputs (not their .txt notes).</summary>
    private static List<string> Reproducers(string findings, string pattern) =>
        [.. Directory.GetFiles(findings, pattern).Where(f => !f.EndsWith(".txt", StringComparison.Ordinal))];

    private static (int Exit, string Output, string Findings) Run(string kind, int timeoutMs)
    {
        string exe = FuzzAssets.ProtocolFuzzExe;
        Assert.True(File.Exists(exe), $"the protocol fuzz harness is not built at {exe}");
        string findings = Path.Combine(Path.GetTempPath(), "mikepdf-fuzz-selftest-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "--selftest", kind, "--mutate", "60", "--max-findings", "1", "--timeout-ms", timeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture), "--rng", "11", "--findings", findings })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "the harness did not stop");
        return (process.ExitCode, stdout.Result + stderr.Result, findings);
    }

    [Fact]
    public void An_exception_in_the_target_is_a_finding_with_a_reproducer()
    {
        var (exit, output, findings) = Run("throw", 5000);
        try
        {
            Assert.Equal(1, exit);
            Assert.Contains("FINDING crash", output);
            string reproducer = Assert.Single(Reproducers(findings, "crash-*"));
            Assert.StartsWith("FZZ", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(reproducer)), StringComparison.Ordinal);
            Assert.Contains("planted failure", File.ReadAllText(reproducer + ".txt"));
        }
        finally
        {
            try { Directory.Delete(findings, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void A_hang_is_a_finding_found_by_the_watchdog()
    {
        var (exit, output, findings) = Run("hang", 1500);
        try
        {
            Assert.Equal(1, exit);
            Assert.Contains("FINDING timeout", output);
            Assert.Single(Reproducers(findings, "timeout-*"));
        }
        finally
        {
            try { Directory.Delete(findings, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void A_stack_overflow_kills_the_worker_and_is_still_recorded_with_its_input()
    {
        var (exit, output, findings) = Run("overflow", 20_000);
        try
        {
            Assert.Equal(1, exit);
            Assert.Contains("FINDING crash", output);
            string reproducer = Assert.Single(Reproducers(findings, "crash-*"));
            Assert.StartsWith("FZZ", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(reproducer)), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(findings, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
