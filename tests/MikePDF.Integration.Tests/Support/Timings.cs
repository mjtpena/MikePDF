namespace MikePDF.Integration.Tests.Support;

/// <summary>What the timing-sensitive tests measured, for the completion note: a line in the test output and in <c>engine-timings.txt</c> next to the test assembly.</summary>
internal static class Timings
{
    private static readonly object Gate = new();

    public static void Record(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        lock (Gate)
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "engine-timings.txt"), line + Environment.NewLine);
        }
    }
}
