using MikePDF.Core.Editing;
using MikePDF.Markup.Editing;
using CoreGeo = MikePDF.Core.Geometry;

namespace MikePDF.Markup.Tests.Support;

/// <summary>The editing half of the samples: an edit model, a session over it, and page frames.</summary>
internal sealed partial class Samples
{
    private MarkupSession? _session;

    public EditModel Model { get; } = new();

    public MarkupSession Session => _session ??= new MarkupSession(Model, Factory, Time);

    public static PageFrame Frame(int rotate = 0, double[]? crop = null, double[]? media = null)
    {
        var m = media ?? [0, 0, 612, 792];
        CoreGeo.PdfRect? c = crop is null ? null : new CoreGeo.PdfRect(crop[0], crop[1], crop[2], crop[3]);
        return new PageFrame(new CoreGeo.PageGeometry(new CoreGeo.PdfRect(m[0], m[1], m[2], m[3]), c, rotate));
    }
}
