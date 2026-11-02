using System.IO.Pipes;
using System.Text;

namespace MikePDF.WriterHost;

/// <summary>
/// Diagnostics to the broker. No handle is inherited into a sandboxed process, so there is no stderr: the broker names a log pipe in
/// MIKEPDF_LOG_PIPE (32 lower-case hex digits) and this opens it once, early. Never log document content, passwords or paths.
/// </summary>
internal static class LogPipe
{
    private static readonly object Gate = new();
    private static NamedPipeClientStream? s_pipe;

    public static void Connect()
    {
        var name = Environment.GetEnvironmentVariable("MIKEPDF_LOG_PIPE");
        if (name is not { Length: 32 } || !name.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            return;
        }

        try
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Identification);
            pipe.Connect(2000);
            lock (Gate)
            {
                s_pipe = pipe;
            }
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // No log is better than no host.
        }
    }

    public static void Write(string line)
    {
        lock (Gate)
        {
            if (s_pipe is null)
            {
                return;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                s_pipe.Write(bytes, 0, bytes.Length);
                s_pipe.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                s_pipe = null;
            }
        }
    }
}
