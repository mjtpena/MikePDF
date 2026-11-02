namespace MikePDF.Integration.Tests.Printing;

/// <summary>
/// Measurements a print test takes (SSIM scores, how long a cancel took), appended to <c>build/out/print-evidence.txt</c> so they can be quoted in the completion note instead of being
/// lost when a test passes. Best effort: a failure to write never fails a test.
/// </summary>
internal static class PrintEvidence
{
    private static readonly object Gate = new();

    public static void Note(string line)
    {
        try
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MikePDF.slnx")))
                {
                    var path = Path.Combine(directory.FullName, "build", "out", "print-evidence.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    lock (Gate)
                    {
                        File.AppendAllText(path, $"{DateTimeOffset.Now:u} {line}{Environment.NewLine}");
                    }

                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Evidence is a convenience.
        }
    }
}
