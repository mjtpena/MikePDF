using MikePDF.Perf.Cli;

namespace MikePDF.Perf;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false); // the reports contain "§", "≤" and "≥"
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        try
        {
            return await Commands.RunAsync(args, Console.Out, Console.Error, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("cancelled");
            return 130;
        }
    }
}
