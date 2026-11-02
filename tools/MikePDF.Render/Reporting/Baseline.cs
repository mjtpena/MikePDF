using System.Text.Json;

namespace MikePDF.Render.Reporting;

/// <summary>
/// The checked-in record of what each page scored the last time the gate was accepted (§12.3). A page that scores clearly lower than its entry is a regression
/// even if it is still above the 0.98 bar; a page that was already below the bar is tolerated as long as it does not get worse.
/// Per architecture, because font rasterisation and the PDFium build differ between x64 and ARM64.
/// </summary>
internal sealed record SsimBaseline
{
    public const string SchemaId = "mikepdf-render-baseline/1";

    /// <summary>How far below its baseline a page may score before it counts as a regression. Covers renderer noise, not behaviour changes.</summary>
    public const double Tolerance = 0.002;

    public string Schema { get; init; } = SchemaId;

    public string? Architecture { get; init; }

    public string? Pdfium { get; init; }

    public string? Tier { get; init; }

    public double Threshold { get; init; }

    /// <summary><c>file id#page@scale</c> to SSIM, rounded to six places.</summary>
    public Dictionary<string, double> Entries { get; init; } = [];

    public static string Key(string fileId, string pageKey) => fileId + "#" + pageKey;

    public static SsimBaseline FromReport(IEnumerable<FileReport> files, RunSettings settings, string architecture, string? pdfium)
    {
        var entries = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var file in files.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            foreach (var page in file.Pages.Where(p => p.Ssim is not null).OrderBy(p => p.Page).ThenBy(p => p.Scale))
            {
                entries[Key(file.Id, page.Key)] = Math.Round(page.Ssim!.Value, 6);
            }
        }

        return new SsimBaseline { Architecture = architecture, Pdfium = pdfium, Tier = settings.Tier, Threshold = settings.SsimThreshold, Entries = entries };
    }

    public static SsimBaseline Load(string path)
    {
        using var stream = File.OpenRead(path);
        var baseline = JsonSerializer.Deserialize(stream, ReportJson.Default.SsimBaseline) ?? throw new InvalidDataException("The baseline file is empty.");
        return baseline.Schema == SchemaId ? baseline : throw new InvalidDataException($"The baseline file has schema '{baseline.Schema}', expected '{SchemaId}'.");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, ReportJson.Default.SsimBaseline) + Environment.NewLine);
    }
}
