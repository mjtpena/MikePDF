using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MikePDF.Perf.Documents;

/// <summary>
/// A document the generator can build. Everything about its bytes follows from these values: the same spec gives the same file on any machine and
/// any .NET version (plain ASCII, no compression, no dates, a fixed pseudo-random generator).
/// </summary>
/// <param name="Id">Short name used in metric ids and file names.</param>
/// <param name="Pages">Number of pages.</param>
/// <param name="Seed">Seeds the choice of words.</param>
/// <param name="Needles">Page (zero based) to the number of times <see cref="PerfDocuments.Needle"/> is written on it; empty for none.</param>
public sealed record DocumentSpec(string Id, int Pages, int Seed, IReadOnlyDictionary<int, int> Needles)
{
    public string FileName => $"{Id}.pdf";

    /// <summary>The number of matches a search for <see cref="PerfDocuments.Needle"/> must find.</summary>
    public int ExpectedNeedleMatches => Needles.Values.Sum();

    /// <summary>The first page (zero based) holding the needle, or -1.</summary>
    public int FirstNeedlePage => Needles.Count == 0 ? -1 : Needles.Keys.Min();
}

/// <summary>A document written to disk.</summary>
public sealed record GeneratedDocument(DocumentSpec Spec, string Path, long Bytes, string Sha256);

/// <summary>
/// The generated test documents of the performance harness (master prompt 11): ordinary text pages of US Letter size, Helvetica 10 pt, 46 lines of
/// about 12 words under a heading, with a rule and a shaded band, so that a page costs PDFium roughly what a page of a report does. Generated, never
/// committed (docs/perf/README.md).
/// </summary>
public static class PerfDocuments
{
    /// <summary>A word that occurs nowhere in the ordinary vocabulary, planted on known pages of the search document.</summary>
    public const string Needle = "quokkafish";

    private const int LinesPerPage = 46;
    private const int WordsPerLine = 12;
    private const int NeedleLine = 9;
    private const int PagesPerNode = 50;

    private static readonly string[] Vocabulary =
    [
        "the", "of", "and", "to", "in", "is", "that", "for", "it", "as", "was", "with", "be", "by", "on", "not", "he", "this", "are", "or",
        "his", "from", "at", "which", "but", "have", "an", "had", "they", "you", "were", "their", "one", "all", "we", "can", "her", "has", "there", "been",
        "if", "more", "when", "will", "would", "who", "so", "no", "report", "system", "service", "document", "reader", "page", "render", "engine", "memory", "budget", "window", "scroll",
        "annual", "quarterly", "revenue", "forecast", "summary", "analysis", "chapter", "section", "figure", "table", "appendix", "reference", "contract", "clause", "party", "agreement", "schedule", "payment", "notice", "term",
        "network", "storage", "latency", "throughput", "cluster", "database", "index", "query", "cache", "thread", "process", "request", "response", "session", "token", "policy", "access", "audit", "backup", "restore",
        "market", "customer", "product", "feature", "release", "version", "support", "update", "install", "package", "device", "driver", "kernel", "module", "library", "compiler", "runtime", "binary", "source", "build",
        "north", "south", "east", "west", "river", "mountain", "valley", "harbour", "bridge", "garden", "market", "street", "station", "airport", "museum", "library",
    ];

    public static DocumentSpec Text1 { get; } = new("text1", 1, 11, new Dictionary<int, int>());

    /// <summary>The "typical 20-page PDF" of the warm-open budget.</summary>
    public static DocumentSpec Text20 { get; } = new("text20", 20, 12, new Dictionary<int, int>());

    /// <summary>The search document: 500 pages, the needle on pages 5, 55, 105 ... 455 (one time each, twice on page 5): 11 matches, the first on page 5.</summary>
    public static DocumentSpec Text500 { get; } = new("text500", 500, 13, Enumerable.Range(0, 10).ToDictionary(i => (i * 50) + 4, i => i == 0 ? 2 : 1));

    public static DocumentSpec Text2000 { get; } = new("text2000", 2000, 14, new Dictionary<int, int>());

    public static IReadOnlyList<DocumentSpec> All { get; } = [Text1, Text20, Text500, Text2000];

    public static DocumentSpec? Find(string id) => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>The bytes of the document.</summary>
    public static byte[] Build(DocumentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.Pages, 1);
        foreach (var page in spec.Needles.Keys)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(page);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, spec.Pages);
        }

        var useNodes = spec.Pages > PagesPerNode;
        var nodeCount = useNodes ? (spec.Pages + PagesPerNode - 1) / PagesPerNode : 0;
        var firstNode = 4;
        var firstPage = firstNode + nodeCount;
        int PageObject(int page) => firstPage + (2 * page);
        int ContentObject(int page) => PageObject(page) + 1;
        var objectCount = firstPage + (2 * spec.Pages) - 1;

        var pdf = new StringBuilder(spec.Pages * 4400);
        var offsets = new int[objectCount + 1];

        void Begin(int number)
        {
            offsets[number] = pdf.Length;
            pdf.Append(number.ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n");
        }

        void End() => pdf.Append("\nendobj\n");

        pdf.Append("%PDF-1.7\n");
        Begin(1);
        pdf.Append("<< /Type /Catalog /Pages 2 0 R >>");
        End();

        Begin(2);
        pdf.Append("<< /Type /Pages /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Count ").Append(spec.Pages.ToString(CultureInfo.InvariantCulture)).Append(" /Kids [");
        if (useNodes)
        {
            for (var n = 0; n < nodeCount; n++)
            {
                pdf.Append(n == 0 ? string.Empty : " ").Append(firstNode + n).Append(" 0 R");
            }
        }
        else
        {
            for (var p = 0; p < spec.Pages; p++)
            {
                pdf.Append(p == 0 ? string.Empty : " ").Append(PageObject(p)).Append(" 0 R");
            }
        }

        pdf.Append("] >>");
        End();

        Begin(3);
        pdf.Append("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        End();

        for (var n = 0; n < nodeCount; n++)
        {
            var first = n * PagesPerNode;
            var last = Math.Min(spec.Pages, first + PagesPerNode);
            Begin(firstNode + n);
            pdf.Append("<< /Type /Pages /Parent 2 0 R /Count ").Append((last - first).ToString(CultureInfo.InvariantCulture)).Append(" /Kids [");
            for (var p = first; p < last; p++)
            {
                pdf.Append(p == first ? string.Empty : " ").Append(PageObject(p)).Append(" 0 R");
            }

            pdf.Append("] >>");
            End();
        }

        for (var p = 0; p < spec.Pages; p++)
        {
            var parent = useNodes ? firstNode + (p / PagesPerNode) : 2;
            Begin(PageObject(p));
            pdf.Append("<< /Type /Page /Parent ").Append(parent.ToString(CultureInfo.InvariantCulture)).Append(" 0 R /Contents ").Append(ContentObject(p)).Append(" 0 R >>");
            End();

            var content = PageContent(spec, p);
            Begin(ContentObject(p));
            pdf.Append("<< /Length ").Append(content.Length.ToString(CultureInfo.InvariantCulture)).Append(" >>\nstream\n").Append(content).Append("\nendstream");
            End();
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append((objectCount + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        for (var i = 1; i <= objectCount; i++)
        {
            pdf.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append((objectCount + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>Writes every missing or stale document of <paramref name="specs"/> into <paramref name="directory"/>. A file with the right content is left alone.</summary>
    public static IReadOnlyList<GeneratedDocument> Materialize(string directory, IEnumerable<DocumentSpec> specs)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(specs);
        Directory.CreateDirectory(directory);
        var written = new List<GeneratedDocument>();
        foreach (var spec in specs)
        {
            var bytes = Build(spec);
            var path = Path.Combine(directory, spec.FileName);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                File.WriteAllBytes(path, bytes);
            }

            written.Add(new GeneratedDocument(spec, path, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        return written;
    }

    /// <summary>The text of page <paramref name="page"/> as one string per line, in the order the lines are drawn (the heading first).</summary>
    public static IReadOnlyList<string> PageLines(DocumentSpec spec, int page)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var random = new SplitMix64(((ulong)(uint)spec.Seed << 32) ^ (uint)page ^ 0x9E3779B97F4A7C15UL);
        var lines = new List<string>(LinesPerPage + 1)
        {
            string.Create(CultureInfo.InvariantCulture, $"MikePDF performance document {spec.Id} - page {page + 1} of {spec.Pages}"),
        };
        var needles = spec.Needles.TryGetValue(page, out var count) ? count : 0;
        for (var line = 0; line < LinesPerPage; line++)
        {
            var words = new string[WordsPerLine];
            for (var w = 0; w < WordsPerLine; w++)
            {
                words[w] = Vocabulary[(int)(random.Next() % (ulong)Vocabulary.Length)];
            }

            if (line == NeedleLine && needles > 0)
            {
                for (var n = 0; n < needles; n++)
                {
                    words[2 + (n * 5)] = Needle;
                }
            }

            lines.Add(string.Join(' ', words));
        }

        return lines;
    }

    private static string PageContent(DocumentSpec spec, int page)
    {
        var lines = PageLines(spec, page);
        var content = new StringBuilder(4200);
        content.Append("q 0.93 0.94 0.96 rg 60 736 492 36 re f Q\n");
        content.Append("q 0.2 0.3 0.5 RG 0.75 w 72 732 m 540 732 l S Q\n");
        content.Append("BT /F1 16 Tf 0.1 0.15 0.3 rg 72 748 Td (").Append(lines[0]).Append(") Tj ET\n");
        content.Append("BT /F1 10 Tf 0 g 72 712 Td 14 TL\n");
        for (var i = 1; i < lines.Count; i++)
        {
            content.Append('(').Append(lines[i]).Append(") Tj T*\n");
        }

        content.Append("ET\n");
        content.Append("BT /F1 8 Tf 0.4 g 290 40 Td (").Append((page + 1).ToString(CultureInfo.InvariantCulture)).Append(") Tj ET");
        return content.ToString();
    }

    /// <summary>SplitMix64: a tiny generator whose output is fixed by its definition, unlike System.Random across runtimes.</summary>
    private struct SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
