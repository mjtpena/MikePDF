// MikePDF.WriterHost (ADR-0008): the sandboxed process that parses untrusted PDF structure so the broker never does. Started by the broker inside an
// AppContainer through the same launcher and profile as the engine, with nothing inherited and no file path: the pipe name and the parent's
// pid are all it is told. Documents arrive as read-only file handles duplicated into the process.
namespace MikePDF.WriterHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        LogPipe.Connect();
        if (!Arguments.TryParse(args, out var parsed, out var problem))
        {
            return Host.Die(ExitCode.BadArguments, problem);
        }

        try
        {
            return await Host.RunAsync(parsed).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Fail closed: no exception crosses the pipe. The broker restarts the host.
            return Host.Die(ExitCode.InternalError, "unhandled " + e.GetType().Name);
        }
    }
}

internal static class ExitCode
{
    public const int Ok = 0;
    public const int BadArguments = 2;
    public const int PipeFailed = 4;
    public const int ProtocolViolation = 5;
    public const int BrokerGone = 8;
    public const int InternalError = 9;
}

internal sealed record Arguments(string PipeName, uint ParentProcessId)
{
    /// <summary>--pipe &lt;32 lower-case hex&gt; --parent &lt;pid&gt;, each exactly once; anything else is refused.</summary>
    public static bool TryParse(string[] args, out Arguments parsed, out string problem)
    {
        parsed = new Arguments(string.Empty, 0);
        problem = string.Empty;
        string? pipe = null;
        uint? parent = null;
        if (args.Length != 4)
        {
            problem = "expected --pipe <name> --parent <pid>";
            return false;
        }

        for (var i = 0; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pipe" when pipe is null && args[i + 1] is { Length: 32 } name && name.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')):
                    pipe = name;
                    break;
                case "--parent" when parent is null && uint.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid != 0:
                    parent = pid;
                    break;
                default:
                    problem = "bad option " + args[i];
                    return false;
            }
        }

        if (pipe is null || parent is null)
        {
            problem = "missing option";
            return false;
        }

        parsed = new Arguments(pipe, parent.Value);
        return true;
    }
}
