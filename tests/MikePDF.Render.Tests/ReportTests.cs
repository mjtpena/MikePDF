using System.Text.Json;
using MikePDF.Render.Reporting;

namespace MikePDF.Render.Tests;

public sealed class ReportTests
{
    private static RunReport Build(bool failing)
    {
        var settings = new RunSettings { Tier = "smoke", Scales = [1.0, 2.0], Pages = "all", Reference = true, SsimThreshold = 0.98, SsimPassTarget = 98 };
        var good = new FileReport { Id = "pdfium/hello_world.pdf", Path = "x/hello_world.pdf", SizeBytes = 840, Open = new OpenReport("Ok", null, 3), PageCount = 1, Reference = "Ok" };
        good.Pages.Add(new PageReport { Page = 1, Scale = 1, ScaleApplied = 1, Status = "Ok", Ms = 4, Width = 200, Height = 200, Tiles = 1, Ssim = 1.0, ChangedPercent = 0, ReferenceStatus = "Ok" });
        var files = new List<FileReport> { good };
        if (failing)
        {
            var bad = new FileReport { Id = "generated/bad.pdf", Path = "x/bad.pdf", Open = new OpenReport("Ok", null, 3), PageCount = 2, Reference = "Ok" };
            bad.Pages.Add(new PageReport { Page = 1, Scale = 1, ScaleApplied = 0.5, Status = "Ok", Ssim = 0.91, ChangedPercent = 12.5, ReferenceStatus = "Ok", DiffImage = "diff/generated/bad.pdf/p00001@1x.png" });
            bad.Pages.Add(new PageReport { Page = 2, Scale = 1, ScaleApplied = 1, Status = "EngineCrashed", Detail = "The engine closed the pipe.", Retries = 1 });
            bad.EngineEvents.Add(new EngineEvent { Kind = "crashed", Reason = "ProcessExited", ExitCode = "0xC0000005", Detail = "The engine process exited.", RestartNumber = 1, AtMs = 1500, During = "page 2@1" });
            bad.EngineEvents.Add(new EngineEvent { Kind = "restored", RestartNumber = 1, RestoreMs = 42, DocumentsRestored = 1, DocumentsLost = 0, AtMs = 1542 });
            files.Add(bad);
        }

        var gate = GateEvaluator.Evaluate(files, settings, null);
        return new RunReport
        {
            Tool = new ToolInfo("mikepdf-render", "1.2.3", 3),
            StartedUtc = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero),
            DurationMs = 2500,
            Machine = new MachineInfo("Windows 11", "x64", ".NET 10", "158.0.8125.0"),
            Settings = settings,
            Files = files,
            Summary = Summarizer.Summarize(files, settings),
            Gate = gate,
        };
    }

    [Fact]
    public void TheJsonCarriesEveryFieldAMachineReaderNeeds()
    {
        using var json = JsonDocument.Parse(ReportJson.Serialize(Build(failing: true)));
        var root = json.RootElement;
        Assert.Equal("mikepdf-render-report/1", root.GetProperty("schema").GetString());
        Assert.Equal("158.0.8125.0", root.GetProperty("machine").GetProperty("pdfiumVersion").GetString());
        Assert.Equal(3, root.GetProperty("tool").GetProperty("protocolSchemaVersion").GetInt32());
        Assert.Equal("smoke", root.GetProperty("settings").GetProperty("tier").GetString());

        var files = root.GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        var bad = files[1];
        Assert.Equal("generated/bad.pdf", bad.GetProperty("id").GetString());
        Assert.Equal("Ok", bad.GetProperty("open").GetProperty("status").GetString());
        Assert.Equal(2, bad.GetProperty("pageCount").GetInt32());
        var pages = bad.GetProperty("pages");
        Assert.Equal(0.91, pages[0].GetProperty("ssim").GetDouble());
        Assert.Equal(0.5, pages[0].GetProperty("scaleApplied").GetDouble());
        Assert.Equal("diff/generated/bad.pdf/p00001@1x.png", pages[0].GetProperty("diffImage").GetString());
        Assert.Equal("EngineCrashed", pages[1].GetProperty("status").GetString());
        Assert.Equal(1, pages[1].GetProperty("retries").GetInt32());
        var events = bad.GetProperty("engineEvents");
        Assert.Equal("crashed", events[0].GetProperty("kind").GetString());
        Assert.Equal("0xC0000005", events[0].GetProperty("exitCode").GetString());
        Assert.Equal("page 2@1", events[0].GetProperty("during").GetString());
        Assert.Equal(42, events[1].GetProperty("restoreMs").GetInt32());

        Assert.False(root.GetProperty("gate").GetProperty("passed").GetBoolean());
        Assert.Contains(root.GetProperty("gate").GetProperty("failures").EnumerateArray(), f => f.GetProperty("kind").GetString() == "EngineCrash");
        Assert.Equal(2, root.GetProperty("summary").GetProperty("files").GetInt32());
    }

    [Fact]
    public void NullsAreLeftOutOfTheJson()
    {
        using var json = JsonDocument.Parse(ReportJson.Serialize(Build(failing: false)));
        var page = json.RootElement.GetProperty("files")[0].GetProperty("pages")[0];
        Assert.False(page.TryGetProperty("detail", out _));
        Assert.False(page.TryGetProperty("diffImage", out _));
    }

    [Fact]
    public void TheMarkdownLeadsWithTheVerdictAndNamesWhatFailed()
    {
        var text = MarkdownReport.Write(Build(failing: true));
        Assert.StartsWith("# mikepdf-render report", text, StringComparison.Ordinal);
        Assert.Contains("**Gate: FAILED**", text, StringComparison.Ordinal);
        Assert.Contains("## Gate failures", text, StringComparison.Ordinal);
        Assert.Contains("**EngineCrash** `generated/bad.pdf`", text, StringComparison.Ordinal);
        Assert.Contains("| `generated/bad.pdf` | 1@1 | Ok | 0.9100 | 12.5% | diff/generated/bad.pdf/p00001@1x.png |", text, StringComparison.Ordinal);
        Assert.Contains("| 1.50 s | `generated/bad.pdf` | crashed | ProcessExited | 0xC0000005 |", text, StringComparison.Ordinal);
        Assert.Contains("PDFium 158.0.8125.0", text, StringComparison.Ordinal);
        Assert.Contains("retried after an engine crash", text, StringComparison.Ordinal);
        Assert.Contains("scale reduced to the pixel budget", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanRunSaysPassedAndListsNoFailures()
    {
        var text = MarkdownReport.Write(Build(failing: false));
        Assert.Contains("**Gate: PASSED**", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Gate failures", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Engine events", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ABaselineRoundTripsThroughAFileWithSortedKeysAndSixPlaces()
    {
        var report = Build(failing: true);
        var baseline = SsimBaseline.FromReport(report.Files, report.Settings, "x64", "158.0.8125.0");
        Assert.Equal(["generated/bad.pdf#1@1", "pdfium/hello_world.pdf#1@1"], baseline.Entries.Keys);
        Assert.Equal(0.91, baseline.Entries["generated/bad.pdf#1@1"]);

        var path = Path.Combine(Path.GetTempPath(), "mikepdf-render-tests", "baseline-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            baseline.Save(path);
            var loaded = SsimBaseline.Load(path);
            Assert.Equal(baseline.Entries, loaded.Entries);
            Assert.Equal(("x64", "158.0.8125.0", "smoke", 0.98), (loaded.Architecture, loaded.Pdfium, loaded.Tier, loaded.Threshold));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ABaselineWithAnotherSchemaIsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), "mikepdf-render-tests", "baseline-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "schema": "something-else/9", "entries": {} }""");
        try
        {
            Assert.Throws<InvalidDataException>(() => SsimBaseline.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
