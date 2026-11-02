#if QUIRE_SELFTEST
using System.Globalization;
using System.Text;
using Quire.App.Services;
using Quire.Broker;

namespace Quire.App;

/// <summary>
/// TEST BUILDS ONLY (build/package.ps1 -TestBuild defines QUIRE_SELFTEST). <c>Quire.exe --selftest &lt;pdf&gt; &lt;result.json&gt;</c> runs the real
/// pipeline of the installed package without a window: stage and verify the engine, start the sandboxed engine, open the PDF by duplicated
/// handle, read the page sizes and render a tile, then run the sandbox probe in the same AppContainer and check its rows against ADR-0004 A2
/// (<see cref="SandboxProbeCheck"/>). A release package contains neither this class nor the sandbox probe; build/package.ps1 checks.
/// </summary>
internal static class SelfTest
{
    public static bool Requested(string[] args) => args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 2 >= args.Length)
        {
            return 2;
        }

        var pdf = args[index + 1];
        var output = args[index + 2];
        var json = new StringBuilder("{\n");
        void Field(string name, string value) => json.Append("  \"").Append(name).Append("\": \"").Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append("\",\n");
        var ok = false;
        var rendered = false;
        try
        {
            Field("packaged", AppPaths.IsPackaged.ToString());
            Field("baseDirectory", AppContext.BaseDirectory);
            Field("dataDirectory", AppPaths.DataDirectory);
            Field("version", AppPaths.Version);
            var host = EngineHost.Create();
            using var supervisor = new Holder(EngineSupervisor.StartAsync(host.DocumentOptions()).GetAwaiter().GetResult());
            Field("engineProcessId", supervisor.Value.EngineProcessId.ToString(CultureInfo.InvariantCulture));
            var opened = supervisor.Value.OpenDocumentAsync(pdf).GetAwaiter().GetResult();
            Field("openStatus", opened.Status.ToString());
            if (opened.Value is { } document)
            {
                Field("pageCount", document.PageCount.ToString(CultureInfo.InvariantCulture));
                var sizes = document.GetPageSizesAsync(0, 1).GetAwaiter().GetResult();
                Field("page0", sizes.Value is { } s ? $"{s.Widths[0]} x {s.Heights[0]}" : sizes.Status.ToString());
                if (sizes.Value is { } size)
                {
                    var tile = document.RenderTileAsync(new RenderTileArgs(0, 1.0f, 0, 0, 0, (uint)Math.Min(512, size.Widths[0]), (uint)Math.Min(512, size.Heights[0])), 1).GetAwaiter().GetResult();
                    Field("renderStatus", tile.Status.ToString());
                    if (tile.Value is { } lease)
                    {
                        using (lease)
                        {
                            var nonWhite = 0;
                            var pixels = lease.Pixels;
                            for (var i = 0; i + 3 < pixels.Length; i += 4)
                            {
                                if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255)
                                {
                                    nonWhite++;
                                }
                            }

                            Field("nonWhitePixels", nonWhite.ToString(CultureInfo.InvariantCulture));
                            rendered = nonWhite > 0;
                        }
                    }
                }

                document.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            // The sandbox as ADR-0004 amendment A2 records it, measured from inside this installed package, in the engine's own AppContainer.
            var problems = SandboxProbeCheck.Run(host.DocumentOptions(), pdf, Field);
            Field("sandboxProbe", problems.Count == 0 ? "ok" : string.Join(" | ", problems));
            ok = rendered && problems.Count == 0;
        }
        catch (Exception e)
        {
            Field("error", e.ToString());
        }

        json.Append("  \"ok\": ").Append(ok ? "true" : "false").Append("\n}\n");
        File.WriteAllText(output, json.ToString());
        return ok ? 0 : 1;
    }

    private sealed class Holder(EngineSupervisor value) : IDisposable
    {
        public EngineSupervisor Value { get; } = value;

        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
#endif
