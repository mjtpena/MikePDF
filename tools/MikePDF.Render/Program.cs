using MikePDF.Render.Runner;

namespace MikePDF.Render;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C: stop after the current file and still write the report. The second one ends the process the usual way.
            if (!cancel.IsCancellationRequested)
            {
                e.Cancel = true;
                cancel.Cancel();
                Console.Error.WriteLine("Stopping after the current file; the report will cover what was done. Press Ctrl+C again to quit now.");
            }
        };

        try
        {
            if (args.Length > 0 && args[0] == "corpus")
            {
                return await CorpusCommand.RunAsync(args[1..], Console.Out, Console.Error, http: null, cancel.Token).ConfigureAwait(false);
            }

            return await RenderCommand.RunAsync(args.Length > 0 && args[0] == "render" ? args[1..] : args, Console.Out, Console.Error, cancel.Token).ConfigureAwait(false);
        }
        catch (FormatException e)
        {
            await Console.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return RenderCommand.CouldNotRun;
        }
    }
}
