using System.Text;
using MikePDF.PdfWriter.Editing;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>SP-09 and SP-13 (save-pipeline 14): stage 4 on classic, xref-stream and hybrid bases, and the checks that make it refuse rather than edit the wrong object.</summary>
public sealed class AnnotationAppenderTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 11, 3, 10, 15, 0, TimeSpan.Zero);
    private static readonly byte[] SecondId = Enumerable.Range(0, 16).Select(static i => (byte)(0x40 + i)).ToArray();

    private static AppendOptions Options => new() { Modified = Stamp, NewIdSecondElement = SecondId };

    private static AnnotationChange Highlight(string name = "h1", int page = 0) => new()
    {
        Kind = AnnotationEditKind.Add,
        Page = page,
        Annotation = NewAnnotationKind.Highlight,
        Rect = [10, 20, 110, 40],
        Color = [1, 0.9f, 0],
        Opacity = 0.5f,
        Name = name,
        Author = "Mike",
        Contents = "check this",
        QuadPoints = [10, 40, 110, 40, 10, 20, 110, 20],
    };

    private static AnnotationChange Note(string name = "t1", int page = 0) => new()
    {
        Kind = AnnotationEditKind.Add,
        Page = page,
        Annotation = NewAnnotationKind.Text,
        Rect = [150, 150, 170, 170],
        Color = [1, 0.8f, 0.1f],
        Name = name,
        Contents = "café ☃",
    };

    private static AnnotationChange Ink(string name = "i1") => new()
    {
        Kind = AnnotationEditKind.Add,
        Page = 0,
        Annotation = NewAnnotationKind.Ink,
        Rect = [20, 60, 80, 100],
        Color = [0, 0, 1],
        Width = 3,
        Name = name,
        Strokes = [[20, 60, 50, 100, 80, 70], [30, 65, 40, 65]],
    };

    private static AnnotationChange Shape(NewAnnotationKind kind, string name) => new()
    {
        Kind = AnnotationEditKind.Add,
        Page = 0,
        Annotation = kind,
        Rect = [100, 100, 140, 130],
        Color = [1, 0, 0],
        Width = 2,
        Name = name,
    };

    private static StrictDocument Reopen(byte[] bytes) => DocInfo.Strict(bytes);

    private static byte[] Append(byte[] baseBytes, params AnnotationChange[] edits)
    {
        var result = AnnotationAppender.Append(DocInfo.Strict(baseBytes), 2, edits, Options);
        Assert.True(result.IsSuccess, result.TryGetError(out var error) ? error.ToString() : "");
        return result.Value.Output;
    }

    private static PdfError Refused(byte[] baseBytes, params AnnotationChange[] edits)
    {
        var result = AnnotationAppender.Append(DocInfo.Strict(baseBytes), 2, edits, Options);
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

    private static PdfDictionary Annotation(PdfDocument doc, int page, int index)
    {
        var (_, pageDictionary) = DocInfo.Pages(doc)[page];
        var annots = (PdfArray)doc.Resolve(pageDictionary["Annots"]).Value;
        return (PdfDictionary)doc.Resolve(annots[index]).Value;
    }

    [Theory]
    [MemberData(nameof(DocumentTests.Layouts), MemberType = typeof(DocumentTests))]
    public void EveryKindIsWrittenWithItsAppearanceOnEveryLayout(string layout)
    {
        var original = DocumentTests.Build(layout).Bytes;

        var updated = Append(original, Highlight(), Note(), Ink(), Shape(NewAnnotationKind.Square, "s1"), Shape(NewAnnotationKind.Circle, "c1"));

        AssertSound(original, updated);
        var after = Reopen(updated);
        Assert.Equal(2, after.Revisions.Count);
        Assert.Equal(5, DocInfo.AnnotCount(after, 0));
        Assert.Equal(DocInfo.Strict(original).Latest.Form, after.Latest.Form); // a table after a table, a stream after a stream
        var subtypes = Enumerable.Range(0, 5).Select(i => Annotation(after, 0, i).GetName("Subtype")).ToList();
        Assert.Equal(["Highlight", "Text", "Ink", "Square", "Circle"], subtypes);
        for (var i = 0; i < 5; i++)
        {
            var annotation = Annotation(after, 0, i);
            Assert.Equal("Annot", annotation.GetName("Type"));
            Assert.Equal(DocInfo.Pages(after)[0].Ref, annotation["P"]); // every annotation is an indirect object with /P
            Assert.IsType<PdfString>(annotation["NM"]);
            Assert.IsType<PdfString>(annotation["M"]);
            var ap = Assert.IsType<PdfDictionary>(after.Resolve(annotation["AP"]).Value);
            var stream = Assert.IsType<PdfStream>(after.Resolve(ap["N"]).Value);
            Assert.Equal("Form", stream.Dictionary.GetName("Subtype"));
            Assert.IsType<PdfArray>(stream.Dictionary["BBox"]);
            Assert.NotEmpty(after.DecodeStream(stream).Value);
        }
    }

    [Fact]
    public void ThePrintFlagAndTheKindSpecificEntriesAreWritten()
    {
        var updated = Append(PdfBuilder.Classic().Bytes, Highlight(), Note(), Ink());
        var doc = Reopen(updated);

        var highlight = Annotation(doc, 0, 0);
        Assert.Equal(4, ((PdfInteger)highlight["F"]!).Value);
        Assert.Equal(8, ((PdfArray)highlight["QuadPoints"]!).Count);
        Assert.Equal(0.5, ((PdfReal)highlight["CA"]!).Value);
        var form = (PdfStream)doc.Resolve(((PdfDictionary)doc.Resolve(highlight["AP"]).Value)["N"]).Value;
        var graphicsStates = (PdfDictionary)((PdfDictionary)form.Dictionary["Resources"]!)["ExtGState"]!;
        Assert.Equal("Multiply", ((PdfDictionary)graphicsStates["GS"]!).GetName("BM")); // a highlight darkens the text under it
        var contents = Encoding.ASCII.GetString(doc.DecodeStream(form).Value);
        Assert.Contains("/GS gs", contents, StringComparison.Ordinal);
        Assert.Contains(" f\n", contents, StringComparison.Ordinal);

        var note = Annotation(doc, 0, 1);
        Assert.Equal(28, ((PdfInteger)note["F"]!).Value); // Print, NoZoom, NoRotate
        Assert.Equal("Note", note.GetName("Name"));
        var title = Assert.IsType<PdfString>(note["Contents"]);
        Assert.True(title.Value.StartsWith(new byte[] { 0xFE, 0xFF })); // not ASCII: UTF-16BE with a byte order mark

        var ink = Annotation(doc, 0, 2);
        Assert.Equal(2, ((PdfArray)ink["InkList"]!).Count);
        Assert.Equal(3, ((PdfReal)((PdfDictionary)ink["BS"]!)["W"]!).Value);
    }

    [Fact]
    public void ThePageGetsAnAnnotsArrayWhenItHadNone()
    {
        var original = PdfBuilder.Classic().Bytes;

        var updated = Append(original, Highlight(page: 1));

        var doc = Reopen(updated);
        Assert.Equal(1, DocInfo.AnnotCount(doc, 1));
        Assert.Equal(0, DocInfo.AnnotCount(doc, 0));
        AssertSound(original, updated);
    }

    [Fact]
    public void AnIndirectAnnotsArrayIsRewrittenAndThePageObjectIsLeftAlone()
    {
        var objects = PdfBuilder.TwoPageObjects();
        objects[2] = new PdfBuilder.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Resources << /Font << /F1 6 0 R >> >> /Annots 8 0 R >>");
        objects.Add(new PdfBuilder.Obj(8, "[]"));
        var original = PdfBuilder.Classic(objects).Bytes;
        var before = DocInfo.Strict(original);

        var result = AnnotationAppender.Append(before, 2, [Highlight()], Options);

        var doc = Reopen(result.Value.Output);
        Assert.Equal(1, DocInfo.AnnotCount(doc, 0));
        Assert.Equal(DocInfo.Pages(before)[0].Dict["Annots"], DocInfo.Pages(doc)[0].Dict["Annots"]); // still 8 0 R
        Assert.Contains(8, doc.Latest.Entries.Keys); // the array object is in the update
        Assert.DoesNotContain(3, doc.Latest.Entries.Keys); // the page is not
        AssertSound(original, result.Value.Output);
    }

    [Fact]
    public void ExistingAnnotationsAreKeptInOrderWhenAnotherIsAdded()
    {
        var first = Append(PdfBuilder.Classic().Bytes, Highlight("a"), Highlight("b"));

        var second = Append(first, Note("c"));

        var doc = Reopen(second);
        Assert.Equal(3, DocInfo.AnnotCount(doc, 0));
        Assert.Equal(3, doc.Revisions.Count);
        Assert.Equal(["a", "b", "c"], Enumerable.Range(0, 3).Select(i => Encoding.ASCII.GetString(((PdfString)Annotation(doc, 0, i)["NM"]!).Value)));
        AssertSound(first, second);
    }

    [Fact]
    public void TheTrailerIdKeepsItsFirstElementAndGetsANewSecond()
    {
        var original = DocInfo.Strict(PdfBuilder.Classic().Bytes);
        var updated = Append(PdfBuilder.Classic().Bytes, Highlight());

        var before = (PdfArray)original.Trailer["ID"]!;
        var after = (PdfArray)Reopen(updated).Trailer["ID"]!;

        Assert.True(((PdfString)before[0]).Value.SequenceEqual(((PdfString)after[0]).Value));
        Assert.False(((PdfString)before[1]).Value.SequenceEqual(((PdfString)after[1]).Value));
        Assert.True(((PdfString)after[1]).Value.SequenceEqual(SecondId));
    }

    [Fact]
    public void DeletingALoadedAnnotationByObjectNumberTakesItOffThePageAndKeepsItsObject()
    {
        var withTwo = Append(PdfBuilder.Classic().Bytes, Highlight("a"), Highlight("b"));
        var doc = Reopen(withTwo);
        var target = ((PdfReference)((PdfArray)DocInfo.Pages(doc)[0].Dict["Annots"]!)[0]).Number;

        var removed = AnnotationAppender.Append(doc, 2, [new AnnotationChange { Kind = AnnotationEditKind.Delete, Page = 0, TargetObject = target, ExpectSubtype = "Highlight", ExpectRect = [10, 20, 110, 40] }], Options);

        var after = Reopen(removed.Value.Output);
        Assert.Equal(1, DocInfo.AnnotCount(after, 0));
        Assert.Equal("b", Encoding.ASCII.GetString(((PdfString)Annotation(after, 0, 0)["NM"]!).Value));
        Assert.Equal(1, removed.Value.AnnotationsRemoved);
        Assert.Equal(new PageDelta(0, 2, 1), Assert.Single(removed.Value.PagesTouched));
        AssertSound(withTwo, removed.Value.Output);
    }

    [Fact]
    public void DeletingByNameFindsTheAnnotationOnThatPage()
    {
        var withTwo = Append(PdfBuilder.Classic().Bytes, Highlight("a"), Note("b"));

        var removed = AnnotationAppender.Append(DocInfo.Strict(withTwo), 2, [new AnnotationChange { Kind = AnnotationEditKind.Delete, Page = 0, Name = "b", ExpectSubtype = "Text", ExpectRect = [150, 150, 170, 170] }], Options);

        Assert.Equal("Highlight", Annotation(Reopen(removed.Value.Output), 0, 0).GetName("Subtype"));
        Assert.Equal(1, DocInfo.AnnotCount(Reopen(removed.Value.Output), 0));
    }

    [Fact]
    public void UpdatingMovesTheRectangleAndChangesTheTextAndTouchesNothingElse()
    {
        var withOne = Append(PdfBuilder.Classic().Bytes, Highlight("a"));
        var doc = Reopen(withOne);
        var number = ((PdfReference)((PdfArray)DocInfo.Pages(doc)[0].Dict["Annots"]!)[0]).Number;
        var original = Annotation(doc, 0, 0);

        var moved = AnnotationAppender.Append(doc, 2, [new AnnotationChange { Kind = AnnotationEditKind.Update, Page = 0, TargetObject = number, ExpectSubtype = "Highlight", ExpectRect = [10, 20, 110, 40], Rect = [30, 40, 130, 60], Contents = "new text" }], Options);

        var after = Annotation(Reopen(moved.Value.Output), 0, 0);
        Assert.Equal([30, 40, 130, 60], ((PdfArray)after["Rect"]!).Select(static v => (float)((v as PdfReal)?.Value ?? ((PdfInteger)v).Value)));
        Assert.Equal("new text", Encoding.ASCII.GetString(((PdfString)after["Contents"]!).Value));
        Assert.Equal(original["QuadPoints"], after["QuadPoints"], PdfObjectComparer.Instance);
        Assert.Equal(original["AP"], after["AP"], PdfObjectComparer.Instance);
        Assert.Equal(0, moved.Value.AnnotationsAdded + moved.Value.AnnotationsRemoved);
        AssertSound(withOne, moved.Value.Output);
    }

    [Theory]
    [InlineData("subtype")]
    [InlineData("rect")]
    [InlineData("missing")]
    public void AnExpectationThatDoesNotHoldRefusesTheWholeStageAndWritesNothing(string what)
    {
        var withOne = Append(PdfBuilder.Classic().Bytes, Highlight("a"));
        var doc = Reopen(withOne);
        var number = ((PdfReference)((PdfArray)DocInfo.Pages(doc)[0].Dict["Annots"]!)[0]).Number;
        var change = new AnnotationChange
        {
            Kind = AnnotationEditKind.Delete,
            Page = 0,
            TargetObject = what == "missing" ? number + 50 : number,
            ExpectSubtype = what == "subtype" ? "Underline" : "Highlight",
            ExpectRect = what == "rect" ? [10, 20, 110, 40.02f] : [10, 20, 110, 40],
        };

        var result = AnnotationAppender.Append(doc, 2, [Note("fine"), change], Options);

        Assert.True(result.TryGetError(out var error));
        Assert.Equal(what == "missing" ? PdfErrorCode.InvalidEdit : PdfErrorCode.ExpectationMismatch, error.Code);
    }

    [Fact]
    public void ARectangleWithinAHundredthOfAPointStillMatches()
    {
        var withOne = Append(PdfBuilder.Classic().Bytes, Highlight("a"));
        var doc = Reopen(withOne);

        var result = AnnotationAppender.Append(doc, 2, [new AnnotationChange { Kind = AnnotationEditKind.Delete, Page = 0, Name = "a", ExpectSubtype = "Highlight", ExpectRect = [10.009f, 19.991f, 110, 40] }], Options);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ThePageCountTheEditsWereMadeAgainstMustMatch()
    {
        Assert.Equal(PdfErrorCode.PageCountMismatch, AnnotationAppender.Append(DocInfo.Strict(PdfBuilder.Classic().Bytes), 3, [Highlight()], Options).Error.Code);
    }

    [Fact]
    public void AnEditNamingAPageThatDoesNotExistIsRefused()
    {
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic().Bytes, Highlight(page: 2)).Code);
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic().Bytes, Highlight(page: -1)).Code);
    }

    [Fact]
    public void TwoNewAnnotationsWithTheSameNameOnAPageAreRefused()
    {
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic().Bytes, Highlight("same"), Note("same")).Code);
        _ = Append(PdfBuilder.Classic().Bytes, Highlight("same", page: 0), Highlight("same", page: 1)); // the same name on two pages is fine
    }

    [Fact]
    public void NoEditsIsNotAnUpdate()
    {
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic().Bytes).Code);
    }

    [Fact]
    public void AnAddWithoutWhatItNeedsIsRefused()
    {
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic().Bytes, Highlight() with { QuadPoints = null }).Code);
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic().Bytes, Highlight() with { QuadPoints = [1, 2, 3] }).Code);
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic().Bytes, Ink() with { Strokes = null }).Code);
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic().Bytes, Highlight() with { Name = null }).Code);
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic().Bytes, Highlight() with { Color = null }).Code);
    }

    [Fact]
    public void AnEncryptedDocumentNeverReachesTheAppenderBecauseTheStrictParserRefusesIt()
    {
        var encrypted = PdfBuilder.Classic(trailerExtra: "/Encrypt << /Filter /Standard /V 1 /R 2 >>").Bytes;

        Assert.Equal(PdfErrorCode.UnsupportedEncryption, DocInfo.StrictError(encrypted).Code);
    }

    [Fact]
    public void ASharedOrCyclicPageTreeIsRefusedNotWalkedForever()
    {
        var cyclic = PdfBuilder.TwoPageObjects();
        cyclic[1] = new PdfBuilder.Obj(2, "<< /Type /Pages /Kids [3 0 R 2 0 R] /Count 2 >>");
        var shared = PdfBuilder.TwoPageObjects();
        shared[1] = new PdfBuilder.Obj(2, "<< /Type /Pages /Kids [3 0 R 3 0 R] /Count 2 >>");

        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic(cyclic).Bytes, Highlight()).Code);
        Assert.Equal(PdfErrorCode.InvalidEdit, Refused(PdfBuilder.Classic(shared).Bytes, Highlight()).Code);
    }

    [Fact]
    public void ADeepNestedPageTreeThatIsWithinTheLimitIsWalked()
    {
        var objects = new List<PdfBuilder.Obj> { new(1, "<< /Type /Catalog /Pages 2 0 R >>") };
        const int depth = 30;
        for (var i = 0; i < depth; i++)
        {
            objects.Add(new PdfBuilder.Obj(2 + i, $"<< /Type /Pages /Kids [{3 + i} 0 R] /Count 1 >>"));
        }

        objects.Add(new PdfBuilder.Obj(2 + depth, "<< /Type /Page /MediaBox [0 0 100 100] >>"));
        var bytes = PdfBuilder.Classic(objects).Bytes;

        var result = AnnotationAppender.Append(DocInfo.Strict(bytes), 1, [Highlight()], Options);

        Assert.True(result.IsSuccess, result.TryGetError(out var e) ? e.ToString() : "");
    }

    [Fact]
    public void EditsOnSeveralPagesAreWrittenInPageOrderRegardlessOfTheOrderTheyArrivedIn()
    {
        var original = PdfBuilder.Classic().Bytes;

        var a = AnnotationAppender.Append(DocInfo.Strict(original), 2, [Highlight("x", 1), Highlight("y", 0)], Options).Value.Output;
        var b = AnnotationAppender.Append(DocInfo.Strict(original), 2, [Highlight("y", 0), Highlight("x", 1)], Options).Value.Output;

        Assert.True(a.AsSpan().SequenceEqual(b));
    }

    [Fact]
    public void TheOutputIsByteForByteReproducibleWithAFixedTimeAndId()
    {
        var original = PdfBuilder.XrefStream().Bytes;

        var first = Append(original, Highlight(), Ink());
        var second = Append(original, Highlight(), Ink());

        Assert.True(first.AsSpan().SequenceEqual(second));
    }

    [Fact]
    public void PdfiumReadsEveryAnnotationTheWriterWrote()
    {
        Assert.SkipWhen(Pdfium.UnavailableReason is not null, Pdfium.UnavailableReason ?? "");
        foreach (var layout in new[] { "classic", "xref-stream objstm flate+predictor", "hybrid" })
        {
            var updated = Append(DocumentTests.Build(layout).Bytes, Highlight(), Note(), Ink(), Shape(NewAnnotationKind.Square, "s"), Shape(NewAnnotationKind.Circle, "c"));

            var info = Pdfium.Open(updated);

            Assert.NotNull(info);
            Assert.Equal(2, info.PageCount);
            Assert.Equal(5, info.Page0AnnotCount);
        }
    }

    private sealed class PdfObjectComparer : IEqualityComparer<PdfObject?>
    {
        public static PdfObjectComparer Instance { get; } = new();

        public bool Equals(PdfObject? x, PdfObject? y) => PdfObjects.DeepEquals(x, y);

        public int GetHashCode(PdfObject? obj) => 0;
    }
}
