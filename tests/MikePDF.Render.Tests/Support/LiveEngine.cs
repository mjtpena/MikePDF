using System.Diagnostics;
using MikePDF.Render.Engine;

namespace MikePDF.Render.Tests.Support;

/// <summary>
/// The real, freshly built sandboxed engine for the tests that drive the CLI against it. In CI a missing engine is a failure, never a skip: these
/// tests are the evidence that the tool renders through the sandbox.
/// </summary>
internal static class LiveEngine
{
    private static readonly Lazy<(string? Directory, string? Reason)> Located = new(() =>
    {
        try
        {
            return (Locations.EngineDirectory(null), null);
        }
        catch (FileNotFoundException e)
        {
            return (null, e.Message);
        }
    });

    public static string? UnavailableReason => Located.Value.Reason is { } reason && Environment.GetEnvironmentVariable("CI") != "true" ? reason : null;

    public static string EngineDirectory => Located.Value.Directory ?? throw new FileNotFoundException(Located.Value.Reason);

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string NewScratchDirectory(string purpose)
    {
        var path = Path.Combine(Path.GetTempPath(), "mikepdf-render-tests", purpose + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A host with its own AppContainer profile (named for this process) so tests running in other checkouts at the same time do not delete each other's.</summary>
    public static SandboxedEngineHost NewHost(string workDirectory, TimeSpan? openDeadline = null) =>
        SandboxedEngineHost.Create(EngineDirectory, workDirectory, openDeadline ?? TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), Stopwatch.StartNew(), $"MikePDF.Render.Tests.{Environment.ProcessId}");

    public static void CleanUp(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The OS temp directory is cleaned eventually.
        }
    }
}

/// <summary>Skips a test when there is no engine to run it against (never in CI).</summary>
internal static class Requires
{
    public static void Engine() => Assert.SkipWhen(LiveEngine.UnavailableReason is not null, LiveEngine.UnavailableReason ?? "");
}
