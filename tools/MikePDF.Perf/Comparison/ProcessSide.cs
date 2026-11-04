using System.Diagnostics;
using System.Globalization;
using MikePDF.Perf.Measurement;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Comparison;

/// <summary>
/// One side of the A/B run: a built copy of the harness (<c>MikePDF.Perf.exe</c>) with its own built engine. Each measurement is a separate
/// <c>run</c> in a new process of that side's own harness, so the side is measured exactly as it was built: its broker, its protocol code and its engine.
/// </summary>
public sealed record ProcessSide(string Name, string ExePath, string EngineDirectory, string WorkDirectory, string RunnerId, string Profile, string ContainerPrefix, double PlantedSlowdown = 0, string? PackagePath = null)
{
    /// <summary>The longest one run may take (the whole ci suite takes about a minute on a hosted runner).</summary>
    public static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(15);

    public async Task<PerfResults> RunAsync(string outputDirectory, IReadOnlySet<string>? only, Action<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        Directory.CreateDirectory(outputDirectory);
        var info = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in Arguments(outputDirectory, only))
        {
            info.ArgumentList.Add(argument);
        }

        // The planted slowdown is for the head only, and the base must never inherit one from the environment of this process.
        info.Environment.Remove(Clock.PlantVariable);
        if (PlantedSlowdown > 0)
        {
            info.Environment[Clock.PlantVariable] = PlantedSlowdown.ToString("0.####", CultureInfo.InvariantCulture);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {ExePath}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(RunLimit);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "run.log"), await output + await error, cancellationToken);
        var resultsPath = Path.Combine(outputDirectory, "perf-results.json");

        // 0: passed. 3: a metric could not be measured (the file is still written and says which). Anything else, or no file: this side produced nothing.
        if (process.ExitCode is not (0 or 3) || !File.Exists(resultsPath))
        {
            throw new InvalidOperationException($"{Name}: {ExePath} run exited with {process.ExitCode} and {(File.Exists(resultsPath) ? "wrote" : "did not write")} {resultsPath}. See {Path.Combine(outputDirectory, "run.log")}.");
        }

        log($"{Name}: done");
        return ResultsJson.Load(resultsPath);
    }

    internal IEnumerable<string> Arguments(string outputDirectory, IReadOnlySet<string>? only)
    {
        yield return "run";
        yield return "--profile";
        yield return Profile;
        yield return "--out";
        yield return outputDirectory;
        yield return "--runner-id";
        yield return RunnerId;
        yield return "--engine-dir";
        yield return EngineDirectory;
        yield return "--docs-dir";
        yield return Path.Combine(WorkDirectory, "documents");
        yield return "--appcontainer";
        yield return ContainerPrefix + "." + Name;
        if (PackagePath is not null)
        {
            // Only this side's own package: the other side's metric is then "not measured on the base" (no baseline), never a number from the wrong build.
            yield return "--package";
            yield return PackagePath;
        }

        if (only is not null)
        {
            yield return "--only";
            yield return string.Join(',', only.Order(StringComparer.Ordinal));
        }
    }
}
