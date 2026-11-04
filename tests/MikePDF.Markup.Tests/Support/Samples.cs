using MikePDF.Core.Editing;
using MikePDF.Core.Geometry;
using MikePDF.Ink;

namespace MikePDF.Markup.Tests.Support;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private long _ticks;

    public ManualTime()
        : this(new DateTimeOffset(2026, 11, 4, 9, 30, 15, TimeSpan.FromHours(1)))
    {
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        _ticks += by.Ticks;
    }

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test", _now.Offset, "test", "test");

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}

/// <summary>Annotations of every kind, made the way the tools make them, and a session to put them in.</summary>
internal sealed class Samples
{
    private int _next;

    public Samples()
    {
        Time = new ManualTime();
        Factory = new MarkupFactory("Mike", Time, () => AnnotationIds.Prefix + (++_next).ToString("x32", System.Globalization.CultureInfo.InvariantCulture));
        Model = new EditModel();
        Session = new MarkupSession(Model, Factory, Time);
    }

    public ManualTime Time { get; }

    public MarkupFactory Factory { get; }

    public EditModel Model { get; }

    public MarkupSession Session { get; }

    public static PageFrame Frame(int rotate = 0, double[]? crop = null, double[]? media = null)
    {
        var m = media ?? [0, 0, 612, 792];
        var c = crop is null ? (PdfRect?)null : new PdfRect(crop[0], crop[1], crop[2], crop[3]);
        return new PageFrame(new PageGeometry(new PdfRect(m[0], m[1], m[2], m[3]), c, rotate));
    }

    public Annotation Highlight(int page = 0, string contents = "") =>
        Factory.TextMarkup(MarkupKind.Highlight, page, [Quad.FromRect(new PdfRect(72, 700, 272, 716)), Quad.FromRect(new PdfRect(72, 684, 172, 700))], opacity: 0.8, contents: contents);

    public Annotation Underline(int page = 0) => Factory.TextMarkup(MarkupKind.Underline, page, [Quad.FromRect(new PdfRect(72, 600, 272, 616))], thickness: 1.5);

    public Annotation StrikeOut(int page = 0) => Factory.TextMarkup(MarkupKind.StrikeOut, page, [Quad.FromRect(new PdfRect(72, 580, 272, 596))], thickness: 1);

    public Annotation Squiggly(int page = 0) => Factory.TextMarkup(MarkupKind.Squiggly, page, [Quad.FromRect(new PdfRect(72, 560, 272, 576))], thickness: 1);

    public Annotation Note(int page = 0, string contents = "A note") => Factory.Note(page, Frame(), new PointD(300, 100), contents, NoteIcon.Key);

    public Annotation FreeText(int page = 0) => Factory.FreeText(page, new PdfRect(300, 500, 480, 540), "Hello\nWorld, a rather long line that has to wrap", FontFamily.Serif, bold: true, size: 14,
        textColor: new Rgb(0, 0, 0.5), align: TextAlign.Center, borderWidth: 1.5, borderColor: Rgb.Red, fill: new Rgb(1, 1, 0.85));

    public Annotation Typewriter(int page = 0) => Factory.FreeText(page, new PdfRect(300, 450, 420, 470), "Typed", FontFamily.Mono, typewriter: true, size: 12);

    public Annotation Rectangle(int page = 0) => Factory.Box(MarkupKind.Rectangle, page, new PdfRect(100, 300, 220, 360), Rgb.Red, 3, new Rgb(0.9, 0.9, 1), DashStyle.Dashed, 0.6);

    public Annotation Ellipse(int page = 0) => Factory.Box(MarkupKind.Ellipse, page, new PdfRect(240, 300, 340, 360), new Rgb(0, 0.5, 0), 2);

    public Annotation Line(int page = 0) => Factory.Line(page, new PointD(100, 250), new PointD(220, 280), startEnding: LineEnding.Circle, endEnding: LineEnding.ClosedArrow, color: new Rgb(0, 0, 1), width: 2, interior: Rgb.Yellow);

    public Annotation Arrow(int page = 0) => Factory.Line(page, new PointD(260, 250), new PointD(360, 250), arrow: true);

    public Annotation Polygon(int page = 0) => Factory.Polygon(page, [new PointD(100, 150), new PointD(180, 150), new PointD(200, 210), new PointD(120, 230)], Rgb.Red, 2, new Rgb(1, 0.8, 0.8), DashStyle.Dotted);

    public Annotation PolyLine(int page = 0) => Factory.PolyLine(page, [new PointD(240, 150), new PointD(300, 210), new PointD(360, 150)], LineEnding.OpenArrow, LineEnding.Diamond, Rgb.Black, 1.5);

    public Annotation Stamp(int page = 0, string name = "Approved") => Factory.Stamp(page, name, new PointD(400, 400));

    public Annotation Ink(int page = 0, bool highlighter = false)
    {
        var samples = Enumerable.Range(0, 40).Select(i => new InkSample(100 + (i * 4), 100 + (float)(15 * Math.Sin(i / 5.0)), 0.3f + (0.7f * i / 40))).ToArray();
        var style = highlighter ? InkStyle.DefaultHighlighter : InkStyle.DefaultPen;
        return Factory.Ink(page, [new InkStroke("a", samples), new InkStroke("b", [new InkSample(300, 120, 1), new InkSample(330, 140, 1)])], style, highlighter ? Rgb.Yellow : new Rgb(0.1, 0.1, 0.6));
    }

    /// <summary>One of every kind, on a page, in drawing order.</summary>
    public IReadOnlyList<Annotation> OfEveryKind(int page = 0) =>
    [
        Highlight(page, "a comment"), Underline(page), StrikeOut(page), Squiggly(page), Note(page), FreeText(page), Typewriter(page), Rectangle(page), Ellipse(page),
        Line(page), Arrow(page), Polygon(page), PolyLine(page), Stamp(page), Ink(page),
    ];
}
