#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using System.Text;
using MikePDF.PdfWriter.Editing;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>
/// The kinds the annotation work package added to stage 4 (annotation-core 3.9, text-markup 9, sticky-notes 9, free-text 10, shapes 10, stamps 3.6, ink-pen 10):
/// each subtype on every layout, replies and popups, Replace, the inherited /Rotate, and the refusals that keep a hostile edit from reaching the writer's objects.
/// </summary>
public sealed class AnnotationKindsTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 11, 4, 10, 15, 0, TimeSpan.Zero);
    private static readonly byte[] SecondId = Enumerable.Range(0, 16).Select(static i => (byte)(0x40 + i)).ToArray();

    private static AppendOptions Options => new() { Modified = Stamp, NewIdSecondElement = SecondId };

    private static AnnotationChange Add(NewAnnotationKind kind, string name, float[] rect, AnnotationChange? template = null) => (template ?? new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0 }) with
    {
        Kind = AnnotationEditKind.Add,
        Annotation = kind,
        Name = name,
        Rect = rect,
        Color = [0.2f, 0.4f, 0.8f],
    };

    private static IReadOnlyList<AnnotationChange> OfEveryKind() =>
    [
        Add(NewAnnotationKind.Highlight, "h", [10, 20, 110, 40], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Opacity = 0.8f, QuadPoints = [10, 40, 110, 40, 10, 20, 110, 20], Subject = "Highlight" }),
        Add(NewAnnotationKind.Underline, "u", [10, 50, 110, 60], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, QuadPoints = [10, 60, 110, 60, 10, 50, 110, 50], Width = 1.5f }),
        Add(NewAnnotationKind.StrikeOut, "s", [10, 70, 110, 80], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, QuadPoints = [10, 80, 110, 80, 10, 70, 110, 70] }),
        Add(NewAnnotationKind.Squiggly, "q", [10, 90, 110, 100], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, QuadPoints = [10, 100, 110, 100, 10, 90, 110, 90] }),
        Add(NewAnnotationKind.Text, "t", [150, 150, 170, 170], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, StandardName = "Help", Contents = "café ☃", PopupRect = [175, 70, 355, 170] }),
        Add(NewAnnotationKind.FreeText, "f", [120, 20, 280, 70], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, FontId = 5, FontSize = 12, TextColor = [0, 0, 0], Contents = "Hello\nthere", Align = 1, Width = 1, InteriorColor = [1, 1, 0.8f] }),
        Add(NewAnnotationKind.Ink, "i", [20, 120, 80, 160], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Width = 3, Strokes = [[20, 120, 50, 160, 80, 130], [30, 125]], InkWidths = [[2, 3, 2], [3]] }),
        Add(NewAnnotationKind.Square, "r", [100, 100, 140, 130], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Width = 2, Dash = 1, InteriorColor = [0.9f, 0.9f, 1] }),
        Add(NewAnnotationKind.Circle, "c", [150, 100, 190, 130], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Width = 2, Opacity = 0.5f }),
        Add(NewAnnotationKind.Line, "l", [100, 180, 190, 200], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Width = 2, Vertices = [102, 182, 188, 198], LineStart = 4, LineEnd = 2 }),
        Add(NewAnnotationKind.Polygon, "pg", [20, 170, 80, 200], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Width = 2, Vertices = [20, 170, 80, 170, 50, 200], Dash = 2 }),
        Add(NewAnnotationKind.PolyLine, "pl", [90, 10, 150, 60], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Width = 2, Vertices = [90, 10, 120, 60, 150, 10], LineEnd = 1 }),
        Add(NewAnnotationKind.Stamp, "st", [10, 105, 90, 125], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, StandardName = "TopSecret" }),
    ];

    private static byte[] Append(byte[] original, params AnnotationChange[] edits)
    {
        var result = AnnotationAppender.Append(DocInfo.Strict(original), 2, edits, Options);
        Assert.True(result.IsSuccess, result.TryGetError(out var error) ? error.ToString() : string.Empty);
        return result.Value.Output;
    }

    private static PdfError Refused(byte[] original, params AnnotationChange[] edits)
    {
        var result = AnnotationAppender.Append(DocInfo.Strict(original), 2, edits, Options);
        Assert.True(result.TryGetError(out var error), "the stage should have been refused");
        return error;
    }

    private static void AssertSound(byte[] original, byte[] updated)
    {
        Assert.True(updated.AsSpan(0, original.Length).SequenceEqual(original), "the original bytes are an exact prefix");
        var report = StructureValidator.Validate(updated, options: new ValidationOptions { DanglingReferencesAreErrors = true });
        Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(static i => i.Message)));
        Assert.Empty(report.Issues);
    }

    private static List<PdfDictionary> Annotations(PdfDocument doc, int page)
    {
        var (_, pageDictionary) = DocInfo.Pages(doc)[page];
        var annots = (PdfArray)doc.Resolve(pageDictionary["Annots"]).Value;
        return [.. annots.Select(item => (PdfDictionary)doc.Resolve(item).Value)];
    }

    [Theory]
    [MemberData(nameof(DocumentTests.Layouts), MemberType = typeof(DocumentTests))]
    public void AC21_ThirteenSubtypesAreWrittenSoundlyOnEveryLayoutWithTheirAppearances(string layout)
    {
        var original = DocumentTests.Build(layout).Bytes;

        var updated = Append(original, [.. OfEveryKind()]);

        AssertSound(original, updated);
        var doc = DocInfo.Strict(updated);
        var annotations = Annotations(doc, 0);
        Assert.Equal(
            ["Highlight", "Underline", "StrikeOut", "Squiggly", "Text", "Popup", "FreeText", "Ink", "Square", "Circle", "Line", "Polygon", "PolyLine", "Stamp"],
            annotations.Select(static a => a.GetName("Subtype")));
        foreach (var annotation in annotations.Where(static a => a.GetName("Subtype") != "Popup"))
        {
            var ap = (PdfDictionary)doc.Resolve(annotation["AP"]).Value;
            var form = (PdfStream)doc.Resolve(ap["N"]).Value;
            Assert.Equal("Form", form.Dictionary.GetName("Subtype"));
            Assert.NotEmpty(doc.DecodeStream(form).Value);
        }
    }

    [Fact]
    public void ANoteWithAPopupHasItInAnnotsAndItPointsBackAtTheNote()
    {
        var original = DocumentTests.Build("classic").Bytes;

        var updated = Append(original, Add(NewAnnotationKind.Text, "t", [150, 150, 170, 170], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, PopupRect = [175, 70, 355, 170], Contents = "c" }));

        var doc = DocInfo.Strict(updated);
        var (_, page) = DocInfo.Pages(doc)[0];
        var annots = (PdfArray)doc.Resolve(page["Annots"]).Value;
        var note = (PdfDictionary)doc.Resolve(annots[0]).Value;
        var popup = (PdfDictionary)doc.Resolve(annots[1]).Value;
        Assert.Equal(annots[1], note["Popup"]);
        Assert.Equal(annots[0], popup["Parent"]);
        Assert.Equal(page["Type"], doc.Resolve(page["Type"]).Value); // the page is untouched but for /Annots
        AssertSound(original, updated);
    }

    [Fact]
    public void ARepliesIRTIsAReferenceToTheNoteItAnswersWhetherTheNoteIsNewOrAlreadyInTheFile()
    {
        var original = DocumentTests.Build("classic").Bytes;
        var withNote = Append(original, Add(NewAnnotationKind.Text, "root", [150, 150, 170, 170], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0 }));
        var reply = Add(NewAnnotationKind.Text, "answer", [150, 150, 170, 170], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, ParentName = "root", ReplyKind = 1, Contents = "ok" });
        var state = Add(NewAnnotationKind.Text, "state", [150, 150, 170, 170], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, ParentName = "root", ReplyKind = 2, State = 3 });

        // In one save with the note, and in a later save against the file that holds it.
        var together = Append(original, Add(NewAnnotationKind.Text, "root", [150, 150, 170, 170], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0 }), reply);
        var later = Append(withNote, reply, state);

        foreach (var bytes in new[] { together, later })
        {
            var doc = DocInfo.Strict(bytes);
            var annotations = Annotations(doc, 0);
            var (_, page) = DocInfo.Pages(doc)[0];
            var list = (PdfArray)doc.Resolve(page["Annots"]).Value;
            Assert.Equal(list[0], annotations[1]["IRT"]);
            Assert.Equal("R", annotations[1].GetName("RT"));
        }

        var stateDictionary = Annotations(DocInfo.Strict(later), 0)[2];
        Assert.Equal("Review", Encoding.Latin1.GetString(((PdfString)stateDictionary["StateModel"]!).Value));
        Assert.Equal("Cancelled", Encoding.Latin1.GetString(((PdfString)stateDictionary["State"]!).Value));
        AssertSound(withNote, later);
    }

    [Fact]
    public void AReplyToAParentThatIsNotOnItsPageIsRefused()
    {
        var original = DocumentTests.Build("classic").Bytes;

        var error = Refused(original, Add(NewAnnotationKind.Text, "answer", [1, 1, 21, 21], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, ParentName = "nobody", ReplyKind = 1 }));

        Assert.Equal(PdfErrorCode.InvalidEdit, error.Code);
        var elsewhere = Add(NewAnnotationKind.Text, "root", [150, 150, 170, 170], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 1 });
        var answer = Add(NewAnnotationKind.Text, "answer", [1, 1, 21, 21], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, ParentName = "root", ReplyKind = 1 });
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(original, elsewhere, answer).Code); // the parent is on another page
    }

    [Fact]
    public void ADeleteTakesTheNotesPopupOffThePageToo()
    {
        var original = DocumentTests.Build("classic").Bytes;
        var first = Append(original, Add(NewAnnotationKind.Text, "root", [150, 150, 170, 170], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, PopupRect = [175, 70, 355, 170] }));

        var second = Append(first, new AnnotationChange { Kind = AnnotationEditKind.Delete, Page = 0, Name = "root", ExpectSubtype = "Text", ExpectRect = [150, 150, 170, 170] });

        Assert.Empty(Annotations(DocInfo.Strict(second), 0));
        AssertSound(first, second);
    }

    [Fact]
    public void AReplaceRebuildsTheObjectInPlaceKeepsItsPopupAndRefusesAWrongExpectation()
    {
        var original = DocumentTests.Build("classic").Bytes;
        var first = Append(original,
            Add(NewAnnotationKind.Square, "r", [100, 100, 140, 130], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Width = 2 }),
            Add(NewAnnotationKind.Text, "n", [150, 150, 170, 170], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, PopupRect = [175, 70, 355, 170] }));
        var before = DocInfo.Strict(first);
        var (_, beforePage) = DocInfo.Pages(before)[0];
        var beforeList = (PdfArray)before.Resolve(beforePage["Annots"]).Value;

        var second = Append(first,
            Add(NewAnnotationKind.Square, "r", [100, 100, 150, 140], new AnnotationChange { Kind = AnnotationEditKind.Replace, Page = 0, Width = 5, ExpectSubtype = "Square", ExpectRect = [100, 100, 140, 130] }) with { Kind = AnnotationEditKind.Replace },
            Add(NewAnnotationKind.Text, "n", [160, 160, 180, 180], new AnnotationChange { Kind = AnnotationEditKind.Replace, Page = 0, StandardName = "Key", PopupRect = [185, 80, 365, 180] }) with { Kind = AnnotationEditKind.Replace, ExpectSubtype = "Text", ExpectRect = [150, 150, 170, 170] });

        var after = DocInfo.Strict(second);
        var (_, afterPage) = DocInfo.Pages(after)[0];
        var afterList = (PdfArray)after.Resolve(afterPage["Annots"]).Value;
        Assert.Equal(beforeList.Select(static x => x.ToString()), afterList.Select(static x => x.ToString())); // same objects in the same places
        var square = (PdfDictionary)after.Resolve(afterList[0]).Value;
        Assert.Equal(5, ((PdfReal)((PdfDictionary)square["BS"]!)["W"]!).Value);
        var note = (PdfDictionary)after.Resolve(afterList[1]).Value;
        Assert.Equal("Key", note.GetName("Name"));
        Assert.Equal(afterList[2], note["Popup"]); // the popup the note had is still the note's
        AssertSound(first, second);

        var wrong = Add(NewAnnotationKind.Square, "r", [1, 1, 5, 5], new AnnotationChange { Kind = AnnotationEditKind.Replace, Page = 0, Width = 1, ExpectSubtype = "Square", ExpectRect = [0, 0, 1, 1] }) with { Kind = AnnotationEditKind.Replace };
        Assert.Equal(PdfErrorCode.ExpectationMismatch, Refused(first, wrong).Code);
        var wrongKind = wrong with { ExpectRect = [100, 100, 140, 130], ExpectSubtype = "Circle" };
        Assert.Equal(PdfErrorCode.ExpectationMismatch, Refused(first, wrongKind).Code);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public void FT05_APagesRotateIsFoundOnThePageOrInheritedFromItsParentAndTurnsTheText(int rotate, bool inherited)
    {
        var objects = PdfBuilder.TwoPageObjects();
        var pages = objects.Single(static o => o.Num == 2);
        var page = objects.Single(static o => o.Num == 3);
        if (inherited)
        {
            objects[objects.IndexOf(pages)] = new PdfBuilder.Obj(2, pages.Text.Replace("/Count 2", $"/Count 2 /Rotate {rotate}", StringComparison.Ordinal));
        }
        else if (rotate != 0)
        {
            objects[objects.IndexOf(page)] = new PdfBuilder.Obj(3, page.Text.Replace("/MediaBox", $"/Rotate {rotate} /MediaBox", StringComparison.Ordinal));
        }

        var original = PdfBuilder.Classic(objects).Bytes;

        var updated = Append(original, Add(NewAnnotationKind.FreeText, "f", [100, 100, 200, 140], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, FontSize = 12, TextColor = [0, 0, 0], Contents = "x" }));

        var doc = DocInfo.Strict(updated);
        var form = (PdfStream)doc.Resolve(((PdfDictionary)doc.Resolve(Annotations(doc, 0)[0]["AP"]).Value)["N"]).Value;
        if (rotate == 0)
        {
            Assert.Null(form.Dictionary["Matrix"]);
        }
        else
        {
            var matrix = ((PdfArray)form.Dictionary["Matrix"]!).Select(static v => v is PdfReal r ? r.Value : ((PdfInteger)v).Value).Take(4).ToArray();
            var expected = rotate switch { 90 => new[] { 0.0, 1, -1, 0 }, 180 => [-1.0, 0, 0, -1], _ => [0.0, -1, 1, 0] };
            Assert.Equal(expected, matrix);
        }
    }

    [Fact]
    public void AnEditThatTheModelCannotDrawIsRefusedAsInvalidAndNeverThrows()
    {
        var original = DocumentTests.Build("classic").Bytes;
        var bad = new[]
        {
            Add(NewAnnotationKind.Stamp, "a", [1, 1, 50, 20], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, StandardName = "NotAStamp" }),
            Add(NewAnnotationKind.Square, "b", [float.NaN, 1, 50, 20], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0 }),
            Add(NewAnnotationKind.FreeText, "c", [1, 1, 50, 20], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, FontSize = float.PositiveInfinity, TextColor = [0, 0, 0] }),
            Add(NewAnnotationKind.FreeText, "d", [1, 1, 50, 20], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, FontSize = 12, TextColor = [0, 0, 0], FontId = 40 }),
            Add(NewAnnotationKind.Line, "e", [1, 1, 50, 20], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Vertices = [1, 2, 3] }),
            Add(NewAnnotationKind.Polygon, "f", [1, 1, 50, 20], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Vertices = [1, 2, 3, 4] }),
            Add(NewAnnotationKind.Ink, "g", [1, 1, 50, 20], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Strokes = [[1, 2, 3]] }),
            Add(NewAnnotationKind.Highlight, "h", [1, 1, 50, 20], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, QuadPoints = [1, 2, 3] }),
            Add(NewAnnotationKind.Text, "i", [1, 1, 21, 21], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, ReplyKind = 7 }),
            Add(NewAnnotationKind.Square, "j", [1, 1, 50, 20], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Dash = 9 }),
        };

        foreach (var edit in bad)
        {
            Assert.Equal(PdfErrorCode.InvalidEdit, Refused(original, edit).Code);
        }
    }

    [Fact]
    public void AnAppearanceOverTwoMegabytesIsRefusedNotWritten()
    {
        var original = DocumentTests.Build("classic").Bytes;
        var points = Enumerable.Range(0, 60_000).SelectMany(i => new float[] { (i % 500) * 1f, ((i / 500) * 2) + ((i % 2) * 7f) }).ToArray();

        var error = Refused(original, Add(NewAnnotationKind.Ink, "big", [0, 0, 600, 300], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Width = 3, Strokes = [points], InkWidths = [Enumerable.Repeat(3f, points.Length / 2).ToArray()] }));

        Assert.Equal(PdfErrorCode.InvalidEdit, error.Code);
    }

    [Fact]
    public void TwoNewAnnotationsOnOnePageMayNotShareAName()
    {
        var original = DocumentTests.Build("classic").Bytes;

        var error = Refused(original, Add(NewAnnotationKind.Square, "same", [1, 1, 20, 20]), Add(NewAnnotationKind.Circle, "same", [30, 1, 50, 20]));

        Assert.Equal(PdfErrorCode.InvalidEdit, error.Code);
    }

    [Fact]
    public void AnInkDotIsOneFilledCircleAndTheHighlighterMultiplies()
    {
        var original = DocumentTests.Build("classic").Bytes;
        var dot = Add(NewAnnotationKind.Ink, "d", [10, 10, 30, 30], new AnnotationChange { Kind = AnnotationEditKind.Add, Page = 0, Width = 8, Strokes = [[20, 20]], InkWidths = [[8]], Traits = 2 });

        var updated = Append(original, dot);

        var doc = DocInfo.Strict(updated);
        var annotation = Annotations(doc, 0)[0];
        var form = (PdfStream)doc.Resolve(((PdfDictionary)doc.Resolve(annotation["AP"]).Value)["N"]).Value;
        var content = Encoding.ASCII.GetString(doc.DecodeStream(form).Value);
        Assert.Equal(4, content.Split('\n').Count(static l => l.EndsWith(" c", StringComparison.Ordinal))); // four curves make the circle
        var gs = (PdfDictionary)((PdfDictionary)((PdfDictionary)form.Dictionary["Resources"]!)["ExtGState"]!)["GS"]!;
        Assert.Equal("Multiply", gs.GetName("BM"));
        Assert.NotNull(annotation["InkList"]);
    }
}
