using System.Text;
using MikePDF.PdfWriter.Editing;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>SP-26 (save-pipeline 14): the full rewrite and the flattened copy.</summary>
public sealed class DocumentRewriterTests
{
    private static readonly byte[] Box = Encoding.Latin1.GetBytes("0 0 100 20 re f");

    /// <summary>One page with a widget, a link, a highlight with a rotated appearance, a hidden square, and an object nothing reaches.</summary>
    private static List<PdfBuilder.Obj> Annotated(string highlightMatrix = "[1 0 0 1 0 0]", string pageResources = "/Resources << /Font << /F1 6 0 R >> >>") =>
    [
        new(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [8 0 R] /DA (/Helv 0 Tf 0 g) >> >>"),
        new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R {pageResources} /Annots [8 0 R 9 0 R 10 0 R 11 0 R] >>"),
        new(5, "<< >>", Encoding.Latin1.GetBytes("BT /F1 12 Tf 10 100 Td (Hello) Tj ET")),
        new(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        new(7, "<< /Producer (MikePDFTests) >>"),
        new(8, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /V (value) /Rect [10 10 110 30] /AP << /N 12 0 R >> /F 4 /P 3 0 R >>"),
        new(9, "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Border [0 0 0] /F 4 /P 3 0 R >>"),
        new(10, "<< /Type /Annot /Subtype /Highlight /Rect [100 100 120 140] /QuadPoints [100 140 120 140 100 100 120 100] /AP << /N 13 0 R >> /F 4 /P 3 0 R >>"),
        new(11, "<< /Type /Annot /Subtype /Square /Rect [50 50 80 80] /AP << /N 12 0 R >> /F 2 /P 3 0 R >>"),
        new(12, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] >>", Box),
        new(13, $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Matrix {highlightMatrix} >>", Encoding.Latin1.GetBytes("0 0 10 10 re f")),
        new(14, "<< /Orphan true >>"),
    ];

    private static byte[] Doc(List<PdfBuilder.Obj>? objects = null) => PdfBuilder.Classic(objects ?? Annotated()).Bytes;

    private static RewriteResult Rewrite(byte[] bytes, bool forms = false, bool comments = false)
    {
        var result = DocumentRewriter.Rewrite(DocInfo.Strict(bytes), new RewriteOptions { FlattenForms = forms, FlattenComments = comments });
        Assert.True(result.IsSuccess, result.TryGetError(out var error) ? error.ToString() : "");
        return result.Value;
    }

    private static List<string> Subtypes(PdfDocument doc)
    {
        var page = DocInfo.Pages(doc)[0].Dict;
        if (page["Annots"] is not { } annots)
        {
            return [];
        }

        return [.. ((PdfArray)doc.Resolve(annots).Value).Select(a => ((PdfDictionary)doc.Resolve(a).Value).GetName("Subtype")!)];
    }

    private static string PageContent(PdfDocument doc)
    {
        var contents = DocInfo.Pages(doc)[0].Dict["Contents"]!;
        var parts = contents is PdfArray array ? array : new PdfArray([contents]);
        return string.Join('\n', parts.Select(p => Encoding.Latin1.GetString(doc.DecodeStream((PdfStream)doc.Resolve(p).Value).Value)));
    }

    private static List<(string Name, double Sx, double Sy, double Tx, double Ty)> Draws(string content) =>
        [.. System.Text.RegularExpressions.Regex.Matches(content, @"q ([-\d.]+) 0 0 ([-\d.]+) ([-\d.]+) ([-\d.]+) cm /(MikeFlat\d+) Do Q").Select(static m => (
            m.Groups[5].Value,
            double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture)))];

    [Theory]
    [MemberData(nameof(DocumentTests.Layouts), MemberType = typeof(DocumentTests))]
    public void ARewriteKeepsEveryReachableObjectOnEveryLayoutAndWritesAClassicTable(string layout)
    {
        var original = DocInfo.Strict(DocumentTests.Build(layout).Bytes);

        var result = Rewrite(DocumentTests.Build(layout).Bytes);

        var after = DocInfo.Strict(result.Output);
        var report = StructureValidator.Validate(after, new ValidationOptions { DanglingReferencesAreErrors = true });
        Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(static i => i.Message)));
        Assert.Single(after.Revisions);
        Assert.Equal(PdfXrefForm.Table, after.Latest.Form);
        Assert.Equal(2, DocInfo.PageCount(after));
        Assert.True(PdfObjects.DeepEquals(DocInfo.Pages(original)[0].Dict, DocInfo.Pages(after)[0].Dict));
        var stream = (PdfStream)after.GetObject(5).Value;
        Assert.Equal("BT /F1 12 Tf 10 100 Td (Hello) Tj ET", Encoding.Latin1.GetString(after.DecodeStream(stream).Value));
        Assert.Equal(original.Trailer["ID"] is PdfArray o ? ((PdfString)o[0]).Value.ToArray() : null, after.Trailer["ID"] is PdfArray a ? ((PdfString)a[0]).Value.ToArray() : null);
    }

    [Fact]
    public void ARewriteDropsWhatNothingReachesSoOlderRevisionsAreGone()
    {
        var edited = AnnotationAppender.Append(DocInfo.Strict(Doc()), 1, [new AnnotationChange { Kind = AnnotationEditKind.Delete, Page = 0, TargetObject = 10, ExpectSubtype = "Highlight", ExpectRect = [100, 100, 120, 140] }]).Value.Output;

        var result = Rewrite(edited);

        var after = DocInfo.Strict(result.Output);
        Assert.Single(after.Revisions);
        Assert.IsType<PdfNull>(after.GetObject(14).Value); // the orphan
        Assert.IsType<PdfNull>(after.GetObject(10).Value); // the deleted highlight's object
        Assert.True(result.Output.Length < edited.Length);
        Assert.Equal(3, Subtypes(after).Count);
    }

    [Fact]
    public void FlatteningCommentsDrawsTheVisibleOnesAndRemovesAllOfThem()
    {
        var result = Rewrite(Doc(), comments: true);

        var after = DocInfo.Strict(result.Output);
        Assert.Equal(["Widget", "Link"], Subtypes(after)); // forms and links are kept
        Assert.Equal((1, 2, 1), (result.AnnotationsFlattened, result.AnnotationsRemoved, result.PagesChanged)); // the hidden square is removed, not drawn
        var content = PageContent(after);
        Assert.StartsWith("q\n", content, StringComparison.Ordinal);
        Assert.Contains("BT /F1 12 Tf 10 100 Td (Hello) Tj ET", content, StringComparison.Ordinal);
        Assert.Equal([("MikeFlat0", 2.0, 4.0, 100.0, 100.0)], Draws(content));
        var resources = (PdfDictionary)DocInfo.Pages(after)[0].Dict["Resources"]!;
        Assert.Equal(new PdfReference(13, 0), ((PdfDictionary)resources["XObject"]!)["MikeFlat0"]);
        Assert.NotNull(resources["Font"]); // what the page already had is kept
    }

    [Fact]
    public void FlatteningFormsDrawsTheWidgetsAndRemovesThemAndTheForm()
    {
        var result = Rewrite(Doc(), forms: true);

        var after = DocInfo.Strict(result.Output);
        Assert.Equal(["Link", "Highlight", "Square"], Subtypes(after));
        Assert.Equal((1, 1), (result.AnnotationsFlattened, result.AnnotationsRemoved));
        var catalog = (PdfDictionary)after.GetObject(1).Value;
        Assert.False(catalog.ContainsKey("AcroForm"));
        Assert.Equal([("MikeFlat0", 1.0, 1.0, 10.0, 10.0)], Draws(PageContent(after)));
    }

    [Fact]
    public void FlatteningBothLeavesOnlyTheLink()
    {
        var result = Rewrite(Doc(), forms: true, comments: true);

        var after = DocInfo.Strict(result.Output);
        Assert.Equal(["Link"], Subtypes(after));
        Assert.Equal(2, result.AnnotationsFlattened);
        Assert.Equal(3, result.AnnotationsRemoved);
        Assert.Equal(new PdfReference(13, 0), ((PdfDictionary)((PdfDictionary)DocInfo.Pages(after)[0].Dict["Resources"]!)["XObject"]!)["MikeFlat1"]);
    }

    [Fact]
    public void ThePlacementFitsTheTransformedBoxToTheRectangle()
    {
        // A 90 degree matrix turns the 10 x 10 box into [-10 0 0 10]; fitting that to [100 100 120 140] scales by 2 and 4 and moves by (120, 100).
        var result = Rewrite(Doc(Annotated(highlightMatrix: "[0 1 -1 0 0 0]")), comments: true);

        Assert.Equal([("MikeFlat0", 2.0, 4.0, 120.0, 100.0)], Draws(PageContent(DocInfo.Strict(result.Output))));
    }

    [Fact]
    public void ResourcesThePageInheritsAreCopiedOntoThePageSoTheNewNameIsFoundNextToTheOldOnes()
    {
        var objects = Annotated(pageResources: "");
        objects[1] = new PdfBuilder.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /Font << /F1 6 0 R >> >> >>");

        var result = Rewrite(Doc(objects), comments: true);

        var resources = (PdfDictionary)DocInfo.Pages(DocInfo.Strict(result.Output))[0].Dict["Resources"]!;
        Assert.NotNull(resources["Font"]);
        Assert.NotNull(((PdfDictionary)resources["XObject"]!)["MikeFlat0"]);
    }

    [Fact]
    public void AnExistingXObjectCalledMikeFlatIsNotOverwritten()
    {
        var objects = Annotated(pageResources: "/Resources << /XObject << /MikeFlat0 12 0 R >> >>");

        var result = Rewrite(Doc(objects), comments: true);

        var xobjects = (PdfDictionary)((PdfDictionary)DocInfo.Pages(DocInfo.Strict(result.Output))[0].Dict["Resources"]!)["XObject"]!;
        Assert.Equal(new PdfReference(12, 0), xobjects["MikeFlat0"]);
        Assert.Equal(new PdfReference(13, 0), xobjects["MikeFlat1"]);
    }

    [Fact]
    public void AnAppearanceThatIsADictionaryOfStatesUsesTheOneNamedByAS()
    {
        var objects = Annotated();
        objects[6] = new PdfBuilder.Obj(8, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (box) /Rect [10 10 30 30] /AS /On /AP << /N << /On 12 0 R /Off 13 0 R >> >> /F 4 /P 3 0 R >>");

        var result = Rewrite(Doc(objects), forms: true);

        var xobjects = (PdfDictionary)((PdfDictionary)DocInfo.Pages(DocInfo.Strict(result.Output))[0].Dict["Resources"]!)["XObject"]!;
        Assert.Equal(new PdfReference(12, 0), xobjects["MikeFlat0"]);
    }

    [Fact]
    public void AnAnnotationWithoutAnAppearanceIsRemovedWithoutAnythingDrawn()
    {
        var objects = Annotated();
        objects[8] = new PdfBuilder.Obj(10, "<< /Type /Annot /Subtype /Highlight /Rect [100 100 120 140] /F 4 /P 3 0 R >>");

        var result = Rewrite(Doc(objects), comments: true);

        Assert.Equal(0, result.AnnotationsFlattened);
        Assert.Equal(2, result.AnnotationsRemoved);
        Assert.DoesNotContain("MikeFlat", PageContent(DocInfo.Strict(result.Output)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncryptedDocumentIsNotRewritten()
    {
        // The strict parser refuses the file before a rewrite can start; the rewriter checks too, as a second line.
        var bytes = PdfBuilder.Classic(trailerExtra: "/Encrypt << /Filter /Standard /V 1 /R 2 >>").Bytes;

        Assert.Equal(PdfErrorCode.UnsupportedEncryption, DocInfo.StrictError(bytes).Code);
    }

    [Fact]
    public void PdfiumReadsTheFlattenedFileWithTheSamePagesAndNoMarkup()
    {
        Assert.SkipWhen(Pdfium.UnavailableReason is not null, Pdfium.UnavailableReason ?? "");
        var plain = Rewrite(Doc());
        var flat = Rewrite(Doc(), forms: true, comments: true);

        var before = Pdfium.Open(plain.Output);
        var after = Pdfium.Open(flat.Output);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before.PageCount, after.PageCount);
        Assert.Equal(4, before.Page0AnnotCount);
        Assert.Equal(1, after.Page0AnnotCount); // the link
    }
}
