using System.Globalization;
using MikePDF.Broker;
using MikePDF.Core.Settings;

namespace MikePDF.ShellHost;

/// <summary>
/// MikePDF.ShellHost.exe (ADR-0018): started on demand by the thumbnail provider or the preview handler, one per user session, gone 60 seconds after the last request.
/// Packaged, everything comes from the package: the engine in <c>..\Engine</c>, the data in LocalState. Unpackaged (only the tests run it so) a handful of environment
/// variables stand in, and a packaged ShellHost never reads them.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitAlreadyServing = 0;
    private const int ExitCannotServe = 1;

    private static async Task<int> Main(string[] args)
    {
        var family = Native.CurrentPackageFamily();
        var packaged = family is not null;
        var sid = ShellPipe.CurrentUserSid();

        string? pipeBase = null;
        if (!packaged)
        {
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--pipe" && IsSafePipeName(args[i + 1]))
                {
                    pipeBase = args[i + 1];
                }
            }
        }

        // One ShellHost per user session (and per pipe name, for a test): a second one has nothing to do.
        using var single = new Mutex(true, @"Local\MikePDF.ShellHost." + (pipeBase ?? sid), out var first);
        if (!first)
        {
            return ExitAlreadyServing;
        }

        var dataDirectory = DataDirectory(family);
        var privateDirectory = Path.Combine(dataDirectory, "private");
        PrivateDirectory.Ensure(privateDirectory);

        AppContainerProfile profile;
        string enginePath;
        try
        {
            (profile, enginePath) = Sandbox(packaged);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ArgumentException or FileNotFoundException)
        {
            return ExitCannotServe;
        }

        var policy = new PolicyProvider(RegistryPolicySource.ForSystem());
        var settingsPath = Path.Combine(privateDirectory, "settings.json");
        var gate = new object();
        var options = new ShellHostOptions
        {
            UserSid = sid,
            StagingDirectory = Path.Combine(privateDirectory, "shell-staging"),
            Engines = new SandboxedShellEngineFactory(enginePath, profile),
            PipeName = pipeBase is null ? null : feature => pipeBase + (feature == ShellFeature.Preview ? ".Preview" : string.Empty),
            Switches = () =>
            {
                lock (gate)
                {
                    policy.Refresh();
                    return ShellSwitches.Read(settingsPath, policy);
                }
            },
            IdleTimeout = packaged ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(double.Parse(Environment.GetEnvironmentVariable("MIKEPDF_SHELL_TEST_IDLE_SECONDS") ?? "60", CultureInfo.InvariantCulture)),
            RequireClientPackageFamily = packaged,
        };

        await using var server = new ShellHostServer(options);
        try
        {
            await server.RunAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // A pipe name that is already taken (FILE_FLAG_FIRST_PIPE_INSTANCE) or cannot be made: ShellHost does not serve. The handler then finds no server it trusts.
            return ExitCannotServe;
        }

        return ExitOk;
    }

    private static bool IsSafePipeName(string name) => name.Length is > 0 and <= 128 && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static string DataDirectory(string? family)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (family is not null)
        {
            // LocalState of the package, the same folder the app's ApplicationData.LocalFolder names.
            return Path.Combine(local, "Packages", family, "LocalState");
        }

        return Environment.GetEnvironmentVariable("MIKEPDF_SHELL_TEST_DATA") is { Length: > 0 } test ? test : Path.Combine(local, "MikePDF");
    }

    private static (AppContainerProfile Profile, string EnginePath) Sandbox(bool packaged)
    {
        if (packaged)
        {
            // The engine runs in place from the package (ADR-0004 amendment A2), next to this ShellHost: <package>\ShellHost\MikePDF.ShellHost.exe, <package>\Engine\.
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Engine", "MikePDF.Engine.exe"));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The engine is missing from this installation.", path);
            }

            return (AppContainerProfile.CreateOrOpen("MikePDF.Shell", "MikePDF shell engine", "MikePDF sandboxed engine for File Explorer thumbnails and previews"), path);
        }

        var directory = Environment.GetEnvironmentVariable("MIKEPDF_SHELL_TEST_ENGINE");
        var profileName = Environment.GetEnvironmentVariable("MIKEPDF_SHELL_TEST_PROFILE");
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(profileName))
        {
            throw new ArgumentException("Unpackaged, ShellHost needs MIKEPDF_SHELL_TEST_ENGINE and MIKEPDF_SHELL_TEST_PROFILE (a test run).");
        }

        // The test's profile already exists (the fixture made it and granted it the engine directory), so this only derives its SID.
        return (AppContainerProfile.CreateOrOpen(profileName), Path.Combine(directory, "MikePDF.Engine.exe"));
    }
}
