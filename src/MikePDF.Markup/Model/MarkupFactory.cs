using MikePDF.Ink;
using MikePDF.Markup.Fonts;

namespace MikePDF.Markup;

/// <summary>Computes the rectangle that geometry-driven kinds have to carry so that their appearance never draws outside it (shapes 3.5, text-markup 2.3, ink-pen 3.8; SH-04).</summary>
public static class MarkupGeometry
{
    /// <summary>The padding added to an ink annotation's rectangle: the Bezier fit may sit up to 0.1 point inside the ideal outline, so the box is generous.</summary>
    public const double InkPadding = 0.5;

    public static PdfRect RectFor(Annotation a)
    {
        ArgumentNullException.ThrowIfNull(a);
        switch (a.Payload)
        {
            case TextMarkupPayload markup:
            {
                var union = markup.Quads.Select(static q => q.Bounds()).Aggregate(Geo.Union);
                return a.Kind == MarkupKind.Highlight ? union : Geo.Inflate(union, a.Width / 2);
            }

            case LinePayload line:
            {
                var (_, _, extent) = ShapeGeometry.WithEnds([line.Start, line.End], line.StartEnding, line.EndEnding, a.Width, a.Color, a.Interior);
                return Geo.Inflate(Geo.BoundsOf(extent), a.Width / 2);
            }

            case PolyPayload poly:
            {
                if (a.Kind == MarkupKind.Polygon)
                {
                    return Geo.Inflate(Geo.BoundsOf(poly.Vertices), a.Width / 2);
                }

                var (_, _, extent) = ShapeGeometry.WithEnds(poly.Vertices, poly.StartEnding, poly.EndEnding, a.Width, a.Color, a.Interior);
                return Geo.Inflate(Geo.BoundsOf(extent), a.Width / 2);
            }

            case InkPayload ink:
            {
                var result = InkAppearance.Build([.. ink.Strokes.Select(static s => new SimplifiedStroke(s.Xy, s.Widths))], ink.Highlighter ? InkEnds.Flat : InkEnds.Round);
                return Geo.Inflate(new PdfRect(result.Left, result.Bottom, result.Right, result.Top), InkPadding);
            }

            default:
                return a.Rect;
        }
    }

    /// <summary>The annotation with its rectangle recomputed from its geometry.</summary>
    public static Annotation Refit(Annotation a) => a with { Rect = RectFor(a) };
}

/// <summary>Moves and resizes the geometry an annotation carries (annotation-core 3.4). Everything is in user space.</summary>
public static class AnnotationTransform
{
    public static Annotation Translate(Annotation a, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(a);
        PointD Move(PointD p) => new(p.X + dx, p.Y + dy);
        var payload = a.Payload switch
        {
            TextMarkupPayload m => m with { Quads = [.. m.Quads.Select(q => new Quad(Move(q.TopLeft), Move(q.TopRight), Move(q.BottomLeft), Move(q.BottomRight)))] },
            LinePayload l => l with { Start = Move(l.Start), End = Move(l.End) },
            PolyPayload p => p with { Vertices = [.. p.Vertices.Select(Move)] },
            InkPayload i => i with { Strokes = [.. i.Strokes.Select(s => s with { Xy = Shift(s.Xy, dx, dy) })] },
            _ => a.Payload,
        };
        return a with { Rect = Geo.Translate(a.Rect, dx, dy), Payload = payload, PopupRect = a.PopupRect is { } popup ? Geo.Translate(popup, dx, dy) : null };
    }

    /// <summary>Scales the geometry from the annotation's rectangle onto a new one (vertices and the rectangle; widths and text sizes do not change). Text-anchored kinds and ink are not resized.</summary>
    public static Annotation ResizeTo(Annotation a, PdfRect target)
    {
        ArgumentNullException.ThrowIfNull(a);
        var old = a.Rect;
        var sx = old.Width > Geo.Epsilon ? target.Width / old.Width : 1;
        var sy = old.Height > Geo.Epsilon ? target.Height / old.Height : 1;
        PointD Map(PointD p) => new(target.Left + ((p.X - old.Left) * sx), target.Bottom + ((p.Y - old.Bottom) * sy));
        var payload = a.Payload switch
        {
            LinePayload l => l with { Start = Map(l.Start), End = Map(l.End) },
            PolyPayload p => p with { Vertices = [.. p.Vertices.Select(Map)] },
            _ => a.Payload,
        };
        return a with { Rect = target, Payload = payload };
    }

    private static float[] Shift(float[] xy, double dx, double dy)
    {
        var shifted = new float[xy.Length];
        for (var i = 0; i + 1 < xy.Length; i += 2)
        {
            shifted[i] = (float)(xy[i] + dx);
            shifted[i + 1] = (float)(xy[i + 1] + dy);
        }

        return shifted;
    }
}

/// <summary>
/// Makes new owned annotations with their defaults (text-markup 2.1, sticky-notes 2.1, shapes 3.2, annotation-core 3.5): the id, author, dates, subject,
/// and a rectangle that follows from the geometry. The clock and the id source are injectable, so tests are exact.
/// </summary>
public sealed class MarkupFactory(string author, TimeProvider? time = null, Func<string>? ids = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string> _ids = ids ?? AnnotationIds.New;

    public string Author { get; } = author ?? throw new ArgumentNullException(nameof(author));

    /// <summary>The local time, whole seconds (PDF dates have no fraction).</summary>
    public DateTimeOffset Now()
    {
        var now = _time.GetLocalNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), now.Offset);
    }

    private Annotation Base(MarkupKind kind, int page, PdfRect rect, Rgb color, AnnotationPayload payload, string contents = "")
    {
        var now = Now();
        return new Annotation
        {
            Id = _ids(),
            Kind = kind,
            Page = page,
            Rect = Geo.Normalised(rect),
            Color = color,
            Contents = CommentText.Normalise(contents),
            Author = Author,
            Subject = MarkupDefaults.SubjectFor(kind),
            Created = now,
            Modified = now,
            Payload = payload,
        };
    }

    /// <summary>A highlight, underline, strike-out or squiggly over quads. Highlights are yellow, the others red, by default (text-markup 2.1).</summary>
    public Annotation TextMarkup(MarkupKind kind, int page, IReadOnlyList<Quad> quads, Rgb? color = null, double opacity = 1, double thickness = 1, string contents = "", PdfRect? popupRect = null)
    {
        if (kind is not (MarkupKind.Highlight or MarkupKind.Underline or MarkupKind.StrikeOut or MarkupKind.Squiggly))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (quads.Count == 0)
        {
            throw new ArgumentException("A text markup needs at least one quadrilateral.", nameof(quads));
        }

        var a = Base(kind, page, new PdfRect(0, 0, 0, 0), color ?? (kind == MarkupKind.Highlight ? Rgb.Yellow : Rgb.Red), new TextMarkupPayload([.. quads]), contents) with
        {
            Opacity = Math.Clamp(opacity, MarkupDefaults.MinOpacity, 1),
            Width = kind == MarkupKind.Highlight ? 1 : Math.Clamp(thickness, MarkupDefaults.MinThickness, MarkupDefaults.MaxThickness),
        };
        var fitted = MarkupGeometry.Refit(a);
        return popupRect is { } popup && fitted.Contents.Length > 0 ? fitted with { PopupRect = popup } : fitted;
    }

    /// <summary>A note whose icon, as displayed, is in <paramref name="rect"/> (user space), with its popup beside it (sticky-notes 2.2). <paramref name="pageRotation"/> is the page's /Rotate.</summary>
    public Annotation NoteAt(int page, PdfRect rect, PdfRect popupRect, int pageRotation, string contents, NoteIcon icon = NoteIcon.Comment, Rgb? color = null) =>
        Base(MarkupKind.Note, page, rect, color ?? Rgb.Yellow, new NotePayload(icon, pageRotation), contents) with { PopupRect = popupRect };

    /// <summary>A reply to an annotation: a Text annotation that is not drawn, at the parent's rectangle (sticky-notes 2.3).</summary>
    public Annotation Reply(Annotation parent, string contents)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return Base(MarkupKind.Note, parent.Page, parent.FileRect, parent.Color, new NotePayload(NoteIcon.Comment), contents) with
        {
            Parent = parent.Id,
            ReplyKind = ReplyKind.Reply,
            Flags = MarkupDefaults.DefaultFlags(MarkupKind.Note),
            Subject = "Reply",
        };
    }

    /// <summary>A review state change on an annotation (sticky-notes 2.3).</summary>
    public Annotation ReviewStateChange(Annotation parent, ReviewState state)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return Reply(parent, string.Empty) with { ReplyKind = ReplyKind.State, State = state, Subject = "Review state" };
    }

    /// <summary>A text box over a user-space rectangle. A typewriter has no border and no fill.</summary>
    public Annotation FreeText(int page, PdfRect rect, string text, FontFamily family = FontFamily.Sans, bool bold = false, bool italic = false, double size = 12,
        Rgb? textColor = null, TextAlign align = TextAlign.Left, bool typewriter = false, double borderWidth = 0, Rgb? borderColor = null, Rgb? fill = null, int pageRotation = 0)
    {
        var (clean, _) = WinAnsi.Filter(text);
        var payload = new FreeTextPayload(family, bold, italic, Math.Clamp(size, 6, 144), textColor ?? Rgb.Black, align, typewriter, pageRotation);
        var a = Base(MarkupKind.FreeText, page, rect, borderColor ?? Rgb.Black, payload, clean) with
        {
            Width = typewriter ? 0 : Math.Clamp(borderWidth, 0, 12),
            Interior = typewriter ? null : fill,
            Subject = typewriter ? "Typewriter" : "Text Box",
        };
        return GrowToFit(a);
    }

    /// <summary>The box grown, never shrunk, so that its text fits (free-text 3.3). It grows down in the displayed page, whatever the page's rotation.</summary>
    public static Annotation GrowToFit(Annotation a)
    {
        if (a.Payload is not FreeTextPayload text)
        {
            return a;
        }

        var rotation = ((text.PageRotation % 360) + 360) % 360;
        var odd = rotation % 180 != 0;
        var border = text.Typewriter ? 0 : a.Width;
        var fontId = StandardFonts.FontId(text.Family, text.Bold, text.Italic);
        var displayWidth = odd ? a.Rect.Height : a.Rect.Width;
        var displayHeight = odd ? a.Rect.Width : a.Rect.Height;
        var layout = TextLayout.Layout(a.Contents, fontId, text.Size, TextLayout.InnerWidth(displayWidth, border), text.Align);
        var needed = TextLayout.MinimumBoxHeight(layout, border);
        if (needed <= displayHeight)
        {
            return a;
        }

        var r = a.Rect;
        var grown = rotation switch
        {
            90 => new PdfRect(r.Left, r.Bottom, r.Left + needed, r.Top), // display down is user right
            180 => new PdfRect(r.Left, r.Bottom, r.Right, r.Bottom + needed), // display down is user up
            270 => new PdfRect(r.Right - needed, r.Bottom, r.Right, r.Top), // display down is user left
            _ => new PdfRect(r.Left, r.Top - needed, r.Right, r.Top),
        };
        return a with { Rect = grown };
    }

    public Annotation Box(MarkupKind kind, int page, PdfRect rect, Rgb? color = null, double width = 2, Rgb? fill = null, DashStyle dash = DashStyle.Solid, double opacity = 1)
    {
        if (kind is not (MarkupKind.Rectangle or MarkupKind.Ellipse))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return Base(kind, page, rect, color ?? Rgb.Red, new BoxPayload()) with
        {
            Width = Math.Clamp(width, ShapeGeometry.MinWidth, ShapeGeometry.MaxWidth),
            Interior = fill,
            Dash = dash,
            Opacity = Math.Clamp(opacity, MarkupDefaults.MinOpacity, 1),
        };
    }

    /// <summary>A line, or an arrow (a line with an open arrow at its end).</summary>
    public Annotation Line(int page, PointD start, PointD end, bool arrow = false, LineEnding? startEnding = null, LineEnding? endEnding = null, Rgb? color = null,
        double width = 2, Rgb? interior = null, DashStyle dash = DashStyle.Solid, double opacity = 1)
    {
        var endShape = endEnding ?? (arrow ? LineEnding.OpenArrow : LineEnding.None);
        var startShape = startEnding ?? LineEnding.None;
        var kind = startShape == LineEnding.None && endShape == LineEnding.OpenArrow ? MarkupKind.Arrow : MarkupKind.Line;
        var a = Base(kind, page, new PdfRect(0, 0, 0, 0), color ?? Rgb.Red, new LinePayload(start, end, startShape, endShape)) with
        {
            Width = Math.Clamp(width, ShapeGeometry.MinWidth, ShapeGeometry.MaxWidth),
            Interior = interior,
            Dash = dash,
            Opacity = Math.Clamp(opacity, MarkupDefaults.MinOpacity, 1),
        };
        return MarkupGeometry.Refit(a);
    }

    public Annotation Polygon(int page, IReadOnlyList<PointD> vertices, Rgb? color = null, double width = 2, Rgb? fill = null, DashStyle dash = DashStyle.Solid, double opacity = 1)
    {
        var clean = Dedupe(vertices, closed: true);
        if (clean.Count < ShapeGeometry.MinPolygonVertices || clean.Count > ShapeGeometry.MaxVertices)
        {
            throw new ArgumentException($"A polygon has {ShapeGeometry.MinPolygonVertices} to {ShapeGeometry.MaxVertices} vertices.", nameof(vertices));
        }

        var a = Base(MarkupKind.Polygon, page, new PdfRect(0, 0, 0, 0), color ?? Rgb.Red, new PolyPayload(clean, LineEnding.None, LineEnding.None)) with
        {
            Width = Math.Clamp(width, ShapeGeometry.MinWidth, ShapeGeometry.MaxWidth),
            Interior = fill,
            Dash = dash,
            Opacity = Math.Clamp(opacity, MarkupDefaults.MinOpacity, 1),
        };
        return MarkupGeometry.Refit(a);
    }

    public Annotation PolyLine(int page, IReadOnlyList<PointD> vertices, LineEnding start = LineEnding.None, LineEnding end = LineEnding.None, Rgb? color = null, double width = 2,
        Rgb? interior = null, DashStyle dash = DashStyle.Solid, double opacity = 1)
    {
        var clean = Dedupe(vertices);
        if (clean.Count < ShapeGeometry.MinPolylineVertices || clean.Count > ShapeGeometry.MaxVertices)
        {
            throw new ArgumentException($"A polyline has {ShapeGeometry.MinPolylineVertices} to {ShapeGeometry.MaxVertices} vertices.", nameof(vertices));
        }

        var a = Base(MarkupKind.PolyLine, page, new PdfRect(0, 0, 0, 0), color ?? Rgb.Red, new PolyPayload(clean, start, end)) with
        {
            Width = Math.Clamp(width, ShapeGeometry.MinWidth, ShapeGeometry.MaxWidth),
            Interior = interior,
            Dash = dash,
            Opacity = Math.Clamp(opacity, MarkupDefaults.MinOpacity, 1),
        };
        return MarkupGeometry.Refit(a);
    }

    /// <summary>A built-in stamp fitted into a box with its proportions kept (stamps 3.4): the native size scaled down to fit 200 x 100 points at a click, or to the dragged box.</summary>
    public Annotation Stamp(int page, string name, PointD topLeft, PdfRect? dragged = null, int userTurns = 0, double opacity = 1)
    {
        if (!StampCatalog.IsStandard(name))
        {
            throw new ArgumentException("Not a standard stamp name.", nameof(name));
        }

        var (w, h) = StampArtwork.NativeSize(name);
        var turns = ((userTurns % 4) + 4) % 4;
        var (nw, nh) = turns % 2 == 0 ? (w, h) : (h, w);
        var fit = StampFit(nw, nh, dragged is { } d ? d.Width : 200, dragged is { } e ? e.Height : 100);
        var rect = dragged is { } box
            ? new PdfRect(box.Left, box.Top - fit.Height, box.Left + fit.Width, box.Top)
            : new PdfRect(topLeft.X, topLeft.Y - fit.Height, topLeft.X + fit.Width, topLeft.Y);
        return Base(MarkupKind.Stamp, page, rect, StampCatalog.RgbOf(name), new StampPayload(name, turns)) with { Opacity = Math.Clamp(opacity, MarkupDefaults.MinOpacity, 1), Subject = name };
    }

    /// <summary>A built-in stamp in a user-space rectangle (the enclosing rectangle of its turned form; stamps 3.5), with the user's quarter turns.</summary>
    public Annotation StampAt(int page, string name, PdfRect rect, int userTurns = 0, double opacity = 1)
    {
        if (!StampCatalog.IsStandard(name))
        {
            throw new ArgumentException("Not a standard stamp name.", nameof(name));
        }

        var turns = ((userTurns % 4) + 4) % 4;
        return Base(MarkupKind.Stamp, page, rect, StampCatalog.RgbOf(name), new StampPayload(name, turns)) with { Opacity = Math.Clamp(opacity, MarkupDefaults.MinOpacity, 1), Subject = name };
    }

    /// <summary>The size a stamp takes as displayed when it is fitted into a box (200 x 100 points at a click): its native size, turned by the user's quarters, scaled down to fit.</summary>
    public static (double Width, double Height) StampSizeFor(string name, int userTurns, double boxWidth = 200, double boxHeight = 100)
    {
        var (w, h) = StampArtwork.NativeSize(name);
        var (nw, nh) = ((userTurns % 4) + 4) % 2 == 0 ? (w, h) : (h, w);
        return StampFit(nw, nh, boxWidth, boxHeight);
    }

    /// <summary>The size a native stamp takes in a box: scaled down (never up) to fit with its proportions kept.</summary>
    public static (double Width, double Height) StampFit(double nativeWidth, double nativeHeight, double boxWidth, double boxHeight)
    {
        var scale = Math.Min(1, Math.Min(boxWidth / nativeWidth, boxHeight / nativeHeight));
        return (nativeWidth * scale, nativeHeight * scale);
    }

    /// <summary>An ink batch from strokes (already smoothed): simplified at 0.1 point with their widths from the style's pressure curve.</summary>
    public Annotation Ink(int page, IReadOnlyList<InkStroke> strokes, InkStyle style, Rgb color, double opacity = 1)
    {
        if (strokes.Count == 0)
        {
            throw new ArgumentException("An ink annotation needs at least one stroke.", nameof(strokes));
        }

        var clamped = style.Clamped();
        var data = strokes.Select(s =>
        {
            var simplified = Simplifier.Simplify(s.Samples, clamped);
            return new InkStrokeData(simplified.Xy, simplified.Widths);
        }).ToList();
        var a = Base(MarkupKind.Ink, page, new PdfRect(0, 0, 0, 0), color, new InkPayload(data, clamped.Tool == InkTool.Highlighter)) with
        {
            Width = clamped.BaseWidth,
            Opacity = Math.Clamp(opacity, MarkupDefaults.MinOpacity, 1),
        };
        return MarkupGeometry.Refit(a);
    }

    /// <summary>Vertices with consecutive duplicates (within 0.01 point) merged (shapes 7).</summary>
    public static IReadOnlyList<PointD> Dedupe(IReadOnlyList<PointD> vertices, bool closed = false)
    {
        var result = new List<PointD>(vertices.Count);
        foreach (var p in vertices)
        {
            if (result.Count == 0 || Geo.Distance(result[^1], p) > 0.01)
            {
                result.Add(p);
            }
        }

        if (closed && result.Count > 1 && Geo.Distance(result[0], result[^1]) <= 0.01)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }
}
