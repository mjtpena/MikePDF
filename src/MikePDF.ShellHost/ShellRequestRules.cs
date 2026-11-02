using MikePDF.Protocol;

namespace MikePDF.ShellHost;

/// <summary>
/// What ShellHost decides about a request from the shape of the request alone, with no process, no handle and no state, so that the rules can be tested and fuzzed (SHELL-18).
/// The generated decoder has already applied every schema limit; these are the rules the schema cannot express.
/// </summary>
internal static class ShellRequestRules
{
    /// <summary>The longest budget a request may carry (the preview's hard limit, spec 3.4).</summary>
    public const uint MaxDeadlineMs = 5000;

    /// <summary>The envelope's <c>deadlineMs</c> as a budget: 0 would mean "no deadline" and is not allowed, and nothing is longer than the preview's limit.</summary>
    public static TimeSpan Budget(uint deadlineMs) => TimeSpan.FromMilliseconds(Math.Clamp(deadlineMs, 1u, MaxDeadlineMs));

    /// <summary>The first message of a connection must be the Hello of a shell handler. An engine's, a broker's, and anything that is not a Hello are not handlers.</summary>
    public static bool IsHandlerHello(RequestEnvelope request, out Hello hello)
    {
        if (request.Payload is Hello h && h.ProcessKind == ProcessKind.ShellHandler)
        {
            hello = h;
            return true;
        }

        hello = null!;
        return false;
    }

    /// <summary>The messages a handler may send once the handshake is done. Everything else (Hello again, Shutdown, the engine's messages) is not served on this pipe.</summary>
    public static bool IsServedAfterHello(IRequestMessage payload) => payload is Ping or ShellOpen or ShellRenderPage or ShellClose;

    /// <summary>
    /// The pixel section the handler promised is at least as large as the box it asked for: <c>maxWidth x maxHeight x 4 &lt;= pixelBytes</c>. The schema bounds each field on its own; the
    /// relation between them is checked here, in 64 bits, so nothing wraps.
    /// </summary>
    public static bool PixelsCoverTheBox(ShellRenderPage render) => (ulong)render.MaxWidth * render.MaxHeight * 4 <= render.PixelBytes;
}
