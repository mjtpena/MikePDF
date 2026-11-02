using System.Text;

namespace S12;

internal enum ScaleMode { Fit, Shrink, Actual, Custom }

/// <summary>Pure functions the production code would own as MikePDF.Core.Printing.PrintLayout and the range grammar of print.md 3.3.</summary>
internal static class PrintLayout
{
    public readonly record struct Placement(bool Rotate90, double Scale, double X, double Y, double W, double H, bool Clipped);

    /// <summary>pageW/pageH in points (displayed box); printable area in device pixels at <paramref name="dpi"/>. Scale is device pixels per point. X and Y are relative to the printable area's top-left.</summary>
    public static Placement Place(double pageW, double pageH, double printW, double printH, double dpi, ScaleMode mode, double customPct, bool autoRotate)
    {
        var rotate = autoRotate && pageW != pageH && (pageW > pageH) != (printW > printH);
        var w = rotate ? pageH : pageW; var h = rotate ? pageW : pageH;
        var actual = dpi / 72.0; var fit = Math.Min(printW / w, printH / h);
        var scale = mode switch { ScaleMode.Actual => actual, ScaleMode.Custom => actual * customPct / 100.0, ScaleMode.Fit => fit, ScaleMode.Shrink => Math.Min(actual, fit), _ => actual };
        var dw = w * scale; var dh = h * scale;
        return new Placement(rotate, scale, (printW - dw) / 2, (printH - dh) / 2, dw, dh, dw > printW + 0.5 || dh > printH + 0.5);
    }

    /// <summary>print.md 3.3: list = item {"," item}; item = n | n "-" n | n "-" | "-" n. Errors are returned, not thrown.</summary>
    public static (List<int>? pages, string? error) ParseRange(string text, int pageCount, string subset = "all")
    {
        if (text.Length > 256) return (null, "too long");
        var set = new SortedSet<int>(); var items = 0;
        if (string.IsNullOrWhiteSpace(text)) { for (var i = 1; i <= pageCount; i++) set.Add(i); }
        else foreach (var raw in text.Replace(" ", "").Split(','))
            {
                if (raw.Length == 0 || ++items > 4096) return (null, "syntax");
                int a, b;
                var dash = raw.IndexOf('-');
                if (dash < 0) { if (!int.TryParse(raw, out a)) return (null, "syntax"); b = a; }
                else
                {
                    var l = raw[..dash]; var r = raw[(dash + 1)..];
                    if (l.Length == 0 && r.Length == 0) return (null, "syntax");
                    a = l.Length == 0 ? 1 : int.TryParse(l, out var x) ? x : -1; b = r.Length == 0 ? pageCount : int.TryParse(r, out var y) ? y : -1;
                    if (a < 0 || b < 0) return (null, "syntax");
                    if (a > b) return (null, "order");
                }
                if (a < 1 || b > pageCount) return (null, $"The document has {pageCount} pages.");
                for (var i = a; i <= b; i++) set.Add(i);
            }
        IEnumerable<int> e = set;
        if (subset == "odd") e = e.Where(p => p % 2 == 1); else if (subset == "even") e = e.Where(p => p % 2 == 0);
        return (e.ToList(), null);
    }
}
