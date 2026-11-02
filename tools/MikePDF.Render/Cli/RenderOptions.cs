using System.Globalization;

namespace MikePDF.Render.Cli;

/// <summary>The command line of <c>mikepdf-render render</c>.</summary>
internal sealed class RenderOptions
{
    public const string Usage = """
        mikepdf-render [render] [options] <file.pdf | directory>...
        mikepdf-render [render] --tier smoke [options]
        mikepdf-render corpus (fetch | verify | check) [--tier smoke] [--manifest <file>] [--corpus-dir <dir>]

        Renders PDFs through the sandboxed engine (never in this process) and, unless --no-reference, compares each page with a
        PDFium render made outside the sandbox. Exit code: 0 the gate passed, 1 it failed, 2 the run could not start.

        Selecting what to render
          --tier <name>           Render a corpus tier from the manifest (smoke, core, full). Files must have been fetched: corpus fetch.
          --manifest <file>       Corpus manifest (default tests/corpus/manifest.json).
          --corpus-dir <dir>      Where fetched corpus files live (default build/out/corpus).
          --filter <text>         Only corpus files whose id contains the text.
          --pages <spec>          all (default), or pages and ranges: 1,3,5-8,10-  (1-based). A corpus file's own selection wins in a tier run.
          --password <text>       Password for the files named on the command line.

        How to render
          --scale <list>          Scale factors, comma separated (default 1; 2 is 144 dpi).
          --dpi <list>            Resolutions instead of --scale (scale = dpi / 72).
          --rotation <0|90|180|270>  Extra rotation, as the viewer's rotate command.
          --max-pixels <n>        A page larger than this is rendered at a smaller scale (default 50000000).

        Output
          --out <dir>             Report and images go here (default render-out).
          --png                   Also write every rendered page as a PNG.
          --no-reference          Skip the PDFium reference and the SSIM comparison.
          --ssim-threshold <x>    Per-page bar (default 0.98).
          --ssim-target <pct>     Share of compared pages that must reach the bar (default 98).
          --baseline <file>       Fail pages that score clearly below this baseline (default tests/corpus/baselines/<tier>-<arch>.json when it exists).
          --write-baseline <file> Write the scores of this run as a baseline.
          --no-gate               Always exit 0 after a run that started.

        Limits and locations
          --tile-deadline-ms <n>  Per tile (default 30000).      --open-deadline-ms <n>  Opening a document (default 30000).
          --file-timeout-s <n>    Whole file, including the reference (default 300).
          --hang-grace-ms <n>     How long past its deadline a request may go unanswered before the engine is checked for progress (default 2000). An engine is killed as hung
                                  only if it then makes no progress for 10 s or holds the request 90 s longer; a heavy page that overshoots its deadline while PDFium keeps drawing is left alone.
          --retries <n>           Render a page again this many times if the engine died under it (default 1).
          --engine-dir <dir>      Folder with MikePDF.Engine.exe and pdfium.dll (default $MIKEPDF_ENGINE_DIR, then build/out/cmake-<arch>/bin/Release).
          --pdfium-dir <dir>      Folder with pdfium.dll for the reference (default build/out/pdfium/<arch>/bin).
          --work-dir <dir>        Scratch space for the deployed engine (default build/out/render).
          --quiet                 Print only the summary.
        """;

    public List<string> Inputs { get; } = [];

    public string? Tier { get; private set; }

    public string? ManifestPath { get; private set; }

    public string? CorpusDirectory { get; private set; }

    public string? Filter { get; private set; }

    public PageSpec Pages { get; private set; } = PageSpec.All;

    public string? Password { get; private set; }

    public IReadOnlyList<double> Scales { get; private set; } = [1.0];

    public int Rotation { get; private set; }

    public long MaxPixels { get; private set; } = 50_000_000;

    public string OutDirectory { get; private set; } = "render-out";

    public bool WritePng { get; private set; }

    public bool Reference { get; private set; } = true;

    public double SsimThreshold { get; private set; } = 0.98;

    public double SsimTarget { get; private set; } = 98;

    public string? BaselinePath { get; private set; }

    public string? WriteBaselinePath { get; private set; }

    public bool NoGate { get; private set; }

    public TimeSpan TileDeadline { get; private set; } = TimeSpan.FromSeconds(30);

    public TimeSpan OpenDeadline { get; private set; } = TimeSpan.FromSeconds(30);

    public TimeSpan FileTimeout { get; private set; } = TimeSpan.FromSeconds(300);

    public int Retries { get; private set; } = 1;

    public TimeSpan HangGrace { get; private set; } = TimeSpan.FromSeconds(2);

    public string? EngineDirectory { get; private set; }

    public string? PdfiumDirectory { get; private set; }

    public string? WorkDirectory { get; private set; }

    public bool Quiet { get; private set; }

    public bool Help { get; private set; }

    /// <summary>Parses the arguments; throws <see cref="FormatException"/> with a message for the user when they are wrong.</summary>
    public static RenderOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new RenderOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            string Next() => ++i < args.Count ? args[i] : throw new FormatException($"{argument} needs a value.");
            switch (argument)
            {
                case "-h" or "--help" or "-?":
                    options.Help = true;
                    break;
                case "--tier": options.Tier = Next(); break;
                case "--manifest": options.ManifestPath = Next(); break;
                case "--corpus-dir": options.CorpusDirectory = Next(); break;
                case "--filter": options.Filter = Next(); break;
                case "--pages": options.Pages = PageSpec.Parse(Next()); break;
                case "--password": options.Password = Next(); break;
                case "--scale": options.Scales = ParseList(argument, Next(), dpi: false); break;
                case "--dpi": options.Scales = ParseList(argument, Next(), dpi: true); break;
                case "--rotation": options.Rotation = ParseRotation(Next()); break;
                case "--max-pixels": options.MaxPixels = ParseLong(argument, Next(), 1_000_000, 1L << 31); break;
                case "--out": options.OutDirectory = Next(); break;
                case "--png": options.WritePng = true; break;
                case "--no-reference": options.Reference = false; break;
                case "--ssim-threshold": options.SsimThreshold = ParseDouble(argument, Next(), 0, 1); break;
                case "--ssim-target": options.SsimTarget = ParseDouble(argument, Next(), 0, 100); break;
                case "--baseline": options.BaselinePath = Next(); break;
                case "--write-baseline": options.WriteBaselinePath = Next(); break;
                case "--no-gate": options.NoGate = true; break;
                case "--tile-deadline-ms": options.TileDeadline = TimeSpan.FromMilliseconds(ParseLong(argument, Next(), 1, 3_600_000)); break;
                case "--open-deadline-ms": options.OpenDeadline = TimeSpan.FromMilliseconds(ParseLong(argument, Next(), 1, 3_600_000)); break;
                case "--file-timeout-s": options.FileTimeout = TimeSpan.FromSeconds(ParseLong(argument, Next(), 1, 86_400)); break;
                case "--retries": options.Retries = (int)ParseLong(argument, Next(), 0, 5); break;
                case "--hang-grace-ms": options.HangGrace = TimeSpan.FromMilliseconds(ParseLong(argument, Next(), 100, 3_600_000)); break;
                case "--engine-dir": options.EngineDirectory = Next(); break;
                case "--pdfium-dir": options.PdfiumDirectory = Next(); break;
                case "--work-dir": options.WorkDirectory = Next(); break;
                case "--quiet": options.Quiet = true; break;
                default:
                    if (argument.StartsWith('-') && argument.Length > 1)
                    {
                        throw new FormatException($"Unknown option {argument}. Try --help.");
                    }

                    options.Inputs.Add(argument);
                    break;
            }
        }

        if (!options.Help && options.Tier is null && options.Inputs.Count == 0)
        {
            throw new FormatException("Nothing to render: name PDF files or folders, or pick a corpus tier with --tier.");
        }

        return options;
    }

    private static List<double> ParseList(string option, string text, bool dpi)
    {
        var values = new List<double>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var value = dpi ? ParseDouble(option, part, 1, 4608) / 72.0 : ParseDouble(option, part, 0.01, 64);
            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }

        return values.Count == 0 ? throw new FormatException($"{option} needs at least one value.") : values;
    }

    private static double ParseDouble(string option, string text, double min, double max) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new FormatException($"{option} wants a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}, not '{text}'.");

    private static long ParseLong(string option, string text, long min, long max) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new FormatException($"{option} wants a whole number from {min} to {max}, not '{text}'.");

    private static int ParseRotation(string text) => text switch
    {
        "0" => 0,
        "90" => 1,
        "180" => 2,
        "270" => 3,
        _ => throw new FormatException("--rotation is 0, 90, 180 or 270."),
    };
}
