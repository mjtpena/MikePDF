using System.Diagnostics;
using System.Globalization;
using MikePDF.Broker;

namespace MikePDF.Integration.Tests.Support;

/// <summary>
/// AppContainer profiles for one test run, named for that run, and the sweep that removes what a run killed in the middle left behind. A profile
/// is a machine-wide object (a SID derived from its name, a folder under %LOCALAPPDATA%\Packages), so a name shared between runs is a lock that
/// the first run to finish breaks under all the others.
/// </summary>
internal static class TestProfiles
{
    private const string OwnerFile = "owner.txt";

    /// <summary>A name no other run, in this checkout or any other, will use: the prefix, the process id and a random suffix. Fits the 64 characters a profile name may have.</summary>
    public static string NewName(string prefix) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}.{Environment.ProcessId}.{Guid.NewGuid().ToString("N")[..12]}");

    /// <summary>Records who owns a deployment directory (this process, identified by its id and start time, so a reused id is not mistaken for it) and which profile belongs to it.</summary>
    public static void WriteOwner(string deployDirectory, string profileName) =>
        File.WriteAllText(Path.Combine(deployDirectory, OwnerFile), string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId} {Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks}\n{profileName}\n"));

    /// <summary>
    /// Deletes the deployment directories under <paramref name="parent"/> (and their profiles) whose owner is no longer running. A directory whose owner is
    /// alive is left alone, so a second run that starts now never takes the first one's engine away; one with no owner record is left for two hours, in case
    /// the run that made it has not written the record yet.
    /// </summary>
    public static int SweepStale(string parent)
    {
        var removed = 0;
        foreach (var directory in Directory.GetDirectories(parent))
        {
            if (!IsStale(directory, out var profileName))
            {
                continue;
            }

            if (profileName is not null)
            {
                TryDo(() => AppContainerProfile.Delete(profileName));
            }

            TryDo(() => Directory.Delete(directory, recursive: true));
            removed++;
        }

        return removed;
    }

    public static void TryDo(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Best effort: a leftover is reclaimed by the next run's sweep.
        }
    }

    private static bool IsStale(string directory, out string? profileName)
    {
        profileName = null;
        var owner = Path.Combine(directory, OwnerFile);
        if (!File.Exists(owner))
        {
            return DateTime.UtcNow - Directory.GetCreationTimeUtc(directory) > TimeSpan.FromHours(2);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(owner);
        }
        catch (IOException)
        {
            return false;
        }

        if (lines.Length < 2 || lines[0].Split(' ') is not [var pidText, var startText]
            || !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks))
        {
            return false; // not ours to judge
        }

        profileName = lines[1].Trim();
        return !IsRunning(pid, startTicks);
    }

    private static bool IsRunning(int pid, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false; // exited meanwhile
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // cannot be inspected: assume it is somebody's, not ours to remove
        }
    }
}
