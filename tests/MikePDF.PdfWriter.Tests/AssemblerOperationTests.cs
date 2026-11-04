#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using System.Text;
using MikePDF.PdfWriter.Assembly;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>
/// The five operations the page assembler produces the saved file for (extract, split, merge, insert from file, duplicate), on the S11 fixtures A (10 pages:
/// outline, links of every kind, named destinations, labels, form, tags, layers, annotations with a popup and a reply), B (5 pages that clash with A's names) and C
/// (rotated pages and offset boxes), read back in a fresh PDFium document. Criteria: PO-09, PO-10, PO-11, PO-13, PO-14, PO-15 (fresh PDFium, structure validator;
/// pdf.js and qpdf are not run here, see the report), PO-22 (warnings). PO-12's "dead references": decisions.md replaced "inert" by "removed".
/// </summary>
public sealed class AssemblerOperationTests
{
    public static TheoryData<string> Layouts => ["classic", "xref-stream"];

    private static AssemblerSource A(string layout = "classic") => layout == "classic" ? AssemblyFixtures.Source("A.pdf") : AssemblyFixtures.Source("A", AssemblyFixtures.AsXrefStream(AssemblyFixtures.Read("A.pdf")));

    private static AssemblerSource B(string layout = "classic") => layout == "classic" ? AssemblyFixtures.Source("B.pdf") : AssemblyFixtures.Source("B", AssemblyFixtures.AsXrefStream(AssemblyFixtures.Read("B.pdf")));

    private static AssemblerSource C(string layout = "classic") => layout == "classic" ? AssemblyFixtures.Source("C.pdf") : AssemblyFixtures.Source("C", AssemblyFixtures.AsXrefStream(AssemblyFixtures.Read("C.pdf")));

    private static PdfiumProfile ProfileA => AssemblyChecks.SourceProfile("A.pdf");

    private static PdfiumProfile ProfileB => AssemblyChecks.SourceProfile("B.pdf");

    private static PdfiumProfile ProfileC => AssemblyChecks.SourceProfile("C.pdf");

    private static void AssertLabelsFollow(PdfiumProfile output, params PdfiumProfile[] sources)
    {
        var labelled = output.Pages.Any(static p => p.Label.Length > 0);
        foreach (var page in output.Pages)
        {
            var origin = sources.SelectMany(static s => s.Pages).First(p => p.Key == page.Key);
            var expected = origin.Label.Length > 0 || !labelled ? origin.Label : int.Parse(page.Key![^2..], System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(expected == page.Label, $"{page.Key}: label '{page.Label}', expected '{expected}'");
        }
    }

    private static string Key(string doc, int page) => $"PAGE-{doc}-{page:D2}";

    // ---------------------------------------------------------------- extract

    [Theory]
    [MemberData(nameof(Layouts))]
    [Trait("Spec", "PO-13")]
    [Trait("Spec", "PO-15")]
    public void ExtractKeepsWhatBelongsToTheKeptPages(string layout)
    {
        var output = AssemblyFixtures.Assemble([A(layout)], AssemblyPlan.Range(0, 3, 5));
        AssemblyFixtures.CheckStructure(output);
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);

        Assert.Equal([Key("A", 4), Key("A", 5), Key("A", 6)], profile.Keys);
        AssemblyChecks.AssertPagesLookTheSame(profile, ProfileA);
        AssemblyChecks.AssertLinksFollow(profile, ProfileA);
        AssertLabelsFollow(profile, ProfileA);
        // A-04 had three links; the one to A-07 (GoTo, (i+3)%10+1) and the named one to A-06... stay or go by where they lead, never to a wrong page.
        Assert.All(profile.Pages.SelectMany(static p => p.Links), static l => Assert.Contains(l, new[] { "PAGE-A-04", "PAGE-A-05", "PAGE-A-06", "uri" }));
        Assert.Equal(
            [("Chapter 2", 0, Key("A", 4)), ("Section 2.1", 1, Key("A", 5)), ("Section 2.2 (named destination)", 1, Key("A", 6))],
            AssemblyChecks.Outline(profile));
        Assert.Equal(
            new SortedDictionary<string, string> { ["dest-p4"] = Key("A", 4), ["dest-p5"] = Key("A", 5), ["dest-p6"] = Key("A", 6) },
            profile.NamedDestinations);
    }

    [Fact]
    [Trait("Spec", "PO-12")]
    [Trait("Spec", "PO-13")]
    public void ExtractedPagesLinkToEachOtherAndDropLinksToPagesLeftOut()
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Pick(0, [0, 1, 2, 3]));
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);
        AssemblyChecks.AssertLinksFollow(profile, ProfileA);
        // A-01 links to A-02 (explicit), A-05 (GoTo), A-03 (name), A-10 (x2) and a URI: only A-02, A-03 and the URI remain.
        Assert.Equal([Key("A", 2), Key("A", 3), "uri"], profile.Pages[0].Links);
        Assert.True(output.Statistics.LinksRemoved >= 3);
        // Nothing is left that goes nowhere.
        Assert.DoesNotContain(profile.Pages.SelectMany(static p => p.Links), static l => l is "unresolved" or "none");
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2 }, new[] { "Chapter 1:0:PAGE-A-01", "Section 1.1:1:PAGE-A-02", "Section 1.2:1:PAGE-A-03" })]
    [InlineData(new[] { 2, 7, 9 }, new[] { "Section 1.2:0:PAGE-A-03", "Section 3.1:0:PAGE-A-08", "Section 3.3:0:PAGE-A-10" })]
    [InlineData(new[] { 6, 7, 8, 9 }, new[] { "Chapter 3:0:PAGE-A-07", "Section 3.1:1:PAGE-A-08", "Section 3.2 (URI):1:uri", "Section 3.3:1:PAGE-A-10" })]
    [Trait("Spec", "PO-12")]
    [Trait("Spec", "PO-13")]
    public void AnEntryForAPageLeftOutIsDroppedAndItsSurvivingChildrenMoveUp(int[] pages, string[] expected)
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Pick(0, pages));
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);
        Assert.Equal(expected, profile.Outline.Select(static b => $"{b.Title}:{b.Depth}:{b.Target}"));
    }

    [Fact]
    [Trait("Spec", "PO-13")]
    public void AClosedBranchStaysClosed()
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Range(0, 3, 5));
        var document = AssemblyFixtures.CheckStructure(output);
        var outlines = (PdfDictionary)AssemblyChecks.Resolve(document, AssemblyChecks.Catalog(document)["Outlines"])!;
        var chapter = (PdfDictionary)AssemblyChecks.Resolve(document, outlines["First"])!;
        Assert.Equal(-2, ((PdfInteger)chapter["Count"]!).Value); // chapter 2 was the closed one in A
        Assert.Equal(1, ((PdfInteger)outlines["Count"]!).Value); // only the chapter shows
    }

    [Theory]
    [InlineData(new[] { 0, 1 }, new[] { "i", "ii" })]
    [InlineData(new[] { 3, 4, 5 }, new[] { "A-2", "A-3", "A-4" })]
    [InlineData(new[] { 9, 0, 4 }, new[] { "A-8", "i", "A-3" })]
    [Trait("Spec", "PO-13")]
    public void PageLabelsFollowTheirPages(int[] pages, string[] labels)
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Pick(0, pages));
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);
        Assert.Equal(labels, profile.Pages.Select(static p => p.Label));
    }

    [Fact]
    [Trait("Spec", "PO-13")]
    [Trait("Spec", "PO-14")]
    public void FormFieldsOnKeptPagesKeepTheirValuesAndOthersAreNotCarried()
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Pick(0, [4, 7]));
        var document = AssemblyFixtures.CheckStructure(output);
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);

        Assert.Equal(["email", "agree", "address"], AssemblyChecks.RootFieldNames(document));
        AssemblyChecks.AssertFormIsConsistent(document);
        Assert.Equal(ProfileA.Pages[4].Widgets, profile.Pages[0].Widgets);
        Assert.Equal(ProfileA.Pages[7].Widgets, profile.Pages[1].Widgets);
        Assert.Equal(3, profile.Pages[1].Widgets.Count);
        Assert.Contains(profile.Pages[1].Widgets, static w => w.Name == "address.street" && w.Value == "1 Main St");
    }

    [Fact]
    [Trait("Spec", "PO-14")]
    public void ExtractingPagesWithoutFieldsLeavesNoForm()
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Range(0, 2, 2));
        var document = AssemblyFixtures.CheckStructure(output);
        Assert.Empty(AssemblyChecks.RootFieldNames(document));
        // The form's resources stay (annotations on the page, a free text, may use them), but no field.
        AssemblyChecks.AssertFormIsConsistent(document);
    }

    [Fact]
    [Trait("Spec", "PO-13")]
    public void AnnotationsTravelWithTheirPopupsAndReplies()
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Range(0, 0, 0));
        var document = AssemblyFixtures.CheckStructure(output);
        var page = DocInfo.Pages(document)[0].Dict;
        var annots = ((PdfArray)AssemblyChecks.Resolve(document, page["Annots"])!).OfType<PdfReference>().ToList();
        var dictionaries = annots.ToDictionary(static r => r.Number, r => (PdfDictionary)document.GetObject(r.Number).Value);

        var note = dictionaries.Single(static d => d.Value.GetName("Subtype") == "Text" && d.Value["IRT"] is null);
        var popup = dictionaries.Single(static d => d.Value.GetName("Subtype") == "Popup");
        var reply = dictionaries.Single(static d => d.Value["IRT"] is not null);
        Assert.Equal(popup.Key, ((PdfReference)note.Value["Popup"]!).Number);
        Assert.Equal(note.Key, ((PdfReference)popup.Value["Parent"]!).Number);
        Assert.Equal(note.Key, ((PdfReference)reply.Value["IRT"]!).Number);
        foreach (var annotation in new[] { note.Value, reply.Value })
            Assert.Equal(DocInfo.Pages(document)[0].Ref!.Number, ((PdfReference)annotation["P"]!).Number);
    }

    [Fact]
    [Trait("Spec", "OQ-PO-9")]
    public void TheStructureTreeIsDroppedWithAWarningTheUiCanShowBeforeTheSave()
    {
        var set = PageAssembler.Open([A(), C()], AssemblyFixtures.Fixed);
        Assert.Equal([(AssemblyWarningCode.TagsDropped, 0)], set.Warnings.Where(static w => w.Code == AssemblyWarningCode.TagsDropped).Select(static w => (w.Code, w.SourceIndex)));
        Assert.True(set.Sources[0].Info!.IsTagged);
        Assert.False(set.Sources[1].Info!.IsTagged);

        var result = set.Assemble(AssemblyPlan.Range(0, 0, 4), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Contains(result.Output.Warnings, static w => w.Code == AssemblyWarningCode.TagsDropped && w.SourceIndex == 0);
        var document = AssemblyFixtures.CheckStructure(result.Output);
        var catalog = AssemblyChecks.Catalog(document);
        Assert.False(catalog.ContainsKey("StructTreeRoot"));
        Assert.False(catalog.ContainsKey("MarkInfo"));
        Assert.All(DocInfo.Pages(document), static p => Assert.False(p.Dict.ContainsKey("StructParents")));

        // A source set that does not use the tagged source does not warn about it.
        var other = set.Assemble(AssemblyPlan.Range(1, 0, 1), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(other.Output.Warnings, static w => w.Code == AssemblyWarningCode.TagsDropped);
    }

    [Fact]
    [Trait("Spec", "PO-13")]
    public void OptionalContentKeepsItsLayerWiring()
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Range(0, 0, 2));
        var document = AssemblyFixtures.CheckStructure(output);
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);
        Assert.True(AssemblyChecks.Catalog(document).ContainsKey("OCProperties"));
        // Page 3 draws one layer ON and one OFF: the output draws the same pixels as the source (PDFium's own import draws the hidden layer).
        AssemblyChecks.AssertPagesLookTheSame(profile, ProfileA);
    }

    [Fact]
    public void TheNewDocumentCarriesTheSourcesTitleAndOurProducer()
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Range(0, 0, 0));
        var document = AssemblyFixtures.CheckStructure(output);
        var info = (PdfDictionary)AssemblyChecks.Resolve(document, document.Trailer["Info"])!;
        Assert.Equal("Fixture A", Encoding.Latin1.GetString(((PdfString)info["Title"]!).Value));
        Assert.Equal("MikePDF", Encoding.Latin1.GetString(((PdfString)info["Producer"]!).Value));
        Assert.Equal("D:20261104120000Z", Encoding.Latin1.GetString(((PdfString)info["ModDate"]!).Value));
        Assert.Equal("en-US", Encoding.Latin1.GetString(((PdfString)AssemblyChecks.Catalog(document)["Lang"]!).Value));
    }

    // ---------------------------------------------------------------- rotated pages and boxes

    [Theory]
    [MemberData(nameof(Layouts))]
    [Trait("Spec", "PO-09")]
    [Trait("Spec", "PO-10")]
    public void RotationBoxesAndRenderingSurviveReorderingAndMerging(string layout)
    {
        // C reversed, then A's rotated and cropped pages (6, 7) between C's pages, merged into one file.
        var sources = new[] { C(layout), A(layout) };
        var plan = new AssemblyPlan([new(0, 5), new(1, 5), new(0, 4), new(1, 6), new(0, 3), new(0, 2), new(0, 1), new(0, 0)]);
        var output = AssemblyFixtures.Assemble(sources, plan);
        AssemblyFixtures.CheckStructure(output);
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);

        Assert.Equal([Key("C", 6), Key("A", 6), Key("C", 5), Key("A", 7), Key("C", 4), Key("C", 3), Key("C", 2), Key("C", 1)], profile.Keys);
        AssemblyChecks.AssertPagesLookTheSame(profile, ProfileC, ProfileA);
        // The offsets and the negative origin are exact, and rotation is /Rotate, never a changed MediaBox.
        Assert.Equal("100,100,712,892", profile.Pages[0].Media);
        Assert.Equal("-100,-50,512,742", profile.Pages[2].Media);
        Assert.Equal([1, 1, 0, 0, 3, 2, 1, 0], profile.Pages.Select(static p => p.Rotation)); // PDFium reports quarter turns
        Assert.Equal("50,30,560,760", profile.Pages[3].Crop);
    }

    [Fact]
    [Trait("Spec", "PO-10")]
    public void PagesInheritTheirBoxesAndResourcesFromTheNodesAboveThem()
    {
        // A two-level tree: /Resources, /MediaBox and /Rotate on the root, /CropBox on the middle node, one page overriding the rotation.
        var pdf = new MiniPdf();
        var catalog = pdf.Reserve();
        var root = pdf.Reserve();
        var middle = pdf.Reserve();
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var pages = pdf.Reserve(3);
        var contents = pages.Select((_, i) => pdf.AddStream("", $"BT /F1 24 Tf 72 700 Td (PAGE-I-{i + 1:D2}) Tj ET")).ToArray();
        pdf.Set(pages[0], $"<< /Type /Page /Parent {middle} 0 R /Contents {contents[0]} 0 R >>");
        pdf.Set(pages[1], $"<< /Type /Page /Parent {middle} 0 R /Contents {contents[1]} 0 R /Rotate 180 >>");
        pdf.Set(pages[2], $"<< /Type /Page /Parent {root} 0 R /Contents {contents[2]} 0 R /MediaBox [0 0 300 400] >>");
        pdf.Set(middle, $"<< /Type /Pages /Parent {root} 0 R /Kids [{pages[0]} 0 R {pages[1]} 0 R] /Count 2 /CropBox [10 10 500 700] >>");
        pdf.Set(root, $"<< /Type /Pages /Kids [{middle} 0 R {pages[2]} 0 R] /Count 3 /Resources << /Font << /F1 {font} 0 R >> >> /MediaBox [0 0 612 792] /Rotate 90 >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {root} 0 R >>");
        var source = pdf.WriteClassic(catalog);

        var output = AssemblyFixtures.Assemble([AssemblyFixtures.Source("tree", source)], AssemblyPlan.Pick(0, [2, 1, 0]));
        var document = AssemblyFixtures.CheckStructure(output);
        var pageDictionaries = DocInfo.Pages(document).Select(static p => p.Dict).ToList();
        // Page 3 (first now): its own MediaBox, the root's rotation and resources, no CropBox. Page 2: the middle node's box, its own rotation.
        Assert.Equal(90, ((PdfInteger)pageDictionaries[0]["Rotate"]!).Value);
        Assert.False(pageDictionaries[0].ContainsKey("CropBox"));
        Assert.Equal(180, ((PdfInteger)pageDictionaries[1]["Rotate"]!).Value);
        Assert.NotNull(pageDictionaries[1]["CropBox"]);
        Assert.All(pageDictionaries, d => Assert.True(AssemblyChecks.Resolve(document, d["Resources"]) is PdfDictionary resources && resources.ContainsKey("Font"), "the page's resources are the inherited ones"));
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);
        Assert.Equal(["PAGE-I-03", "PAGE-I-02", "PAGE-I-01"], profile.Keys);
        Assert.Equal(["0,0,300,400", "0,0,612,792", "0,0,612,792"], profile.Pages.Select(static p => p.Media));
        Assert.Equal([1, 2, 1], profile.Pages.Select(static p => p.Rotation)); // quarter turns
        // The two pages that shared the root's direct /Resources share one object in the output.
        var shared = pageDictionaries.Select(static d => d["Resources"]).OfType<PdfReference>().Select(static r => r.Number).Distinct().ToList();
        Assert.Single(shared);
    }

    // ---------------------------------------------------------------- merge

    [Fact]
    [Trait("Spec", "PO-13")]
    [Trait("Spec", "PO-15")]
    [Trait("Spec", "PO-20")]
    public void MergeKeepsEveryLinkOutlineEntryAndNameOfBothFiles()
    {
        var output = AssemblyFixtures.Assemble([A(), B()], AssemblyPlan.Merge((0, 10), (1, 5)));
        AssemblyFixtures.CheckStructure(output);
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);

        Assert.Equal(15, output.PageCount);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => Key("A", i)).Concat(Enumerable.Range(1, 5).Select(i => Key("B", i))), profile.Keys);
        AssemblyChecks.AssertLinksFollow(profile, ProfileA, ProfileB);
        Assert.Equal(ProfileA.Pages.Sum(static p => p.Links.Count) + ProfileB.Pages.Sum(static p => p.Links.Count), profile.Pages.Sum(static p => p.Links.Count));
        AssertLabelsFollow(profile, ProfileA, ProfileB);
        AssemblyChecks.AssertPagesLookTheSame(profile, ProfileA, ProfileB);
        Assert.Equal(ProfileA.Outline.Count + ProfileB.Outline.Count, profile.Outline.Count);
        Assert.Equal(["Part I", "Part II", "Part III"], profile.Outline.Skip(ProfileA.Outline.Count).Select(static b => b.Title));
    }

    [Fact]
    [Trait("Spec", "PO-13")]
    public void MergeCanWrapEachSourcesOutlineUnderAnEntryNamedAfterIt()
    {
        var output = AssemblyFixtures.Assemble([A(), B()], AssemblyPlan.Merge((0, 10), (1, 5)), AssemblyFixtures.Fixed with { WrapSourceOutlines = true });
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);
        var outline = profile.Outline;
        Assert.Equal(("A", 0, Key("A", 1)), (outline[0].Title, outline[0].Depth, outline[0].Target));
        Assert.Equal(("Chapter 1", 1, Key("A", 1)), (outline[1].Title, outline[1].Depth, outline[1].Target));
        var second = outline.ToList().FindIndex(static b => b.Title == "B" && b.Depth == 0);
        Assert.Equal(("B", 0, Key("B", 1)), (outline[second].Title, outline[second].Depth, outline[second].Target));
        Assert.Equal(("Part I", 1, Key("B", 1)), (outline[second + 1].Title, outline[second + 1].Depth, outline[second + 1].Target));
        Assert.Equal(ProfileA.Outline.Count + ProfileB.Outline.Count + 2, outline.Count);
    }

    [Fact]
    [Trait("Spec", "PO-11")]
    [Trait("Spec", "PO-13")]
    public void NamedDestinationsThatClashAreRenamedAndEveryLinkStillGoesToItsOwnFile()
    {
        var output = AssemblyFixtures.Assemble([A(), B()], AssemblyPlan.Merge((0, 10), (1, 5)));
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, target) in ProfileA.NamedDestinations)
            expected[name] = target;
        foreach (var (name, target) in ProfileB.NamedDestinations)
            expected[name + "~2"] = target;
        Assert.Equal(expected, profile.NamedDestinations);
        Assert.Equal(5, output.Statistics.NamedDestinationsRenamed);
        Assert.Contains(output.Warnings, static w => w.Code == AssemblyWarningCode.DestinationsRenamed && w.Count == 5);
        // B's links that used a name still lead to B's pages (PDFium's own import sends them to A's).
        AssemblyChecks.AssertLinksFollow(profile, ProfileA, ProfileB);
        Assert.Equal([Key("B", 2), Key("B", 2)], profile.Pages[10].Links);
    }

    [Fact]
    [Trait("Spec", "PO-14")]
    [Trait("Spec", "OQ-PO-8")]
    public void FormFieldsWithTheSameNameAreRenamedAndKeepTheirOwnValues()
    {
        var output = AssemblyFixtures.Assemble([A(), B()], AssemblyPlan.Merge((0, 10), (1, 5)));
        var document = AssemblyFixtures.CheckStructure(output);
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);

        Assert.Equal(["name", "email", "agree", "address", "name_2", "agree_2", "address_2"], AssemblyChecks.RootFieldNames(document));
        AssemblyChecks.AssertFormIsConsistent(document);
        Assert.Equal(3, output.Statistics.FieldsRenamed);
        Assert.Contains(output.Warnings, static w => w.Code == AssemblyWarningCode.FieldsRenamed && w.Count == 3);

        // A's fields are untouched; B's are the same fields under new names, with B's values (PDFium's import shows "Alice" in B's name field).
        Assert.Equal(ProfileA.Pages[1].Widgets, profile.Pages[1].Widgets);
        static string Rename(string name) => name.Split('.')[0] + "_2" + name[name.Split('.')[0].Length..];
        foreach (var index in new[] { 1, 3 })
        {
            var expected = ProfileB.Pages[index].Widgets.Select(static w => (Rename(w.Name), w.Type, w.Value)).ToList();
            Assert.Equal(expected, profile.Pages[10 + index].Widgets);
        }

        Assert.Contains(profile.Pages[11].Widgets, static w => w is ("name_2", _, "Bruno"));
        Assert.Contains(profile.Pages[13].Widgets, static w => w is ("address_2.street", _, "9 Other Rd"));
    }

    [Fact]
    [Trait("Spec", "PO-14")]
    public void FormResourceNamesThatClashAreRenamedSoNothingDrawsInTheWrongFont()
    {
        // A and B both define /Helv in /AcroForm /DR /Font, Helvetica in A and Courier in B. A's free-text annotation (page 4) has no appearance and is drawn from
        // /DA (/Helv ...): in B-then-A the union used to keep B's, so A's text came out in Courier. It must look as it does in A.
        var output = AssemblyFixtures.Assemble([B(), A()], AssemblyPlan.Merge((0, 5), (1, 10)));
        var document = AssemblyFixtures.CheckStructure(output);
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);
        AssemblyChecks.AssertPagesLookTheSame(profile, ProfileA, ProfileB);
        Assert.Equal(1, output.Statistics.FormResourcesRenamed);
        Assert.Contains(output.Warnings, static w => w.Code == AssemblyWarningCode.FormResourcesRenamed);

        var form = (PdfDictionary)AssemblyChecks.Resolve(document, AssemblyChecks.Catalog(document)["AcroForm"])!;
        var fonts = (PdfDictionary)AssemblyChecks.Resolve(document, ((PdfDictionary)AssemblyChecks.Resolve(document, form["DR"])!)["Font"])!;
        Assert.Equal(["Helv", "Helv_2"], fonts.Keys.Select(static k => k.Value));
        Assert.Equal("Courier", ((PdfDictionary)AssemblyChecks.Resolve(document, fonts["Helv"])!).GetName("BaseFont"));
        Assert.Equal("Helvetica", ((PdfDictionary)AssemblyChecks.Resolve(document, fonts["Helv_2"])!).GetName("BaseFont"));

        var freeText = DocInfo.Pages(document)[8].Dict["Annots"] is { } annots
            ? ((PdfArray)AssemblyChecks.Resolve(document, annots)!).Select(a => AssemblyChecks.Resolve(document, a)).OfType<PdfDictionary>().Single(static d => d.GetName("Subtype") == "FreeText")
            : throw new InvalidOperationException();
        Assert.Contains("/Helv_2 ", Encoding.Latin1.GetString(((PdfString)freeText["DA"]!).Value));
        // A's text fields (renamed name_2 here, B came first) say /Helv_2 now (their /DA), where B's fields still say /Helv.
        string DefaultAppearance(int pageIndex, string name) => Encoding.Latin1.GetString(((PdfString)((PdfArray)AssemblyChecks.Resolve(document, DocInfo.Pages(document)[pageIndex].Dict["Annots"])!)
            .Select(a => AssemblyChecks.Resolve(document, a)).OfType<PdfDictionary>()
            .Single(d => d.GetName("Subtype") == "Widget" && d["T"] is PdfString t && Encoding.Latin1.GetString(t.Value) == name)["DA"]!).Value);
        Assert.Contains("/Helv_2 ", DefaultAppearance(6, "name_2"));
        Assert.Contains("/Helv ", DefaultAppearance(1, "name"));
    }

    [Fact]
    [Trait("Spec", "PO-14")]
    public void MergingAFileWithItselfRenamesWhatClashesAndKeepsBothCopiesWorking()
    {
        var output = AssemblyFixtures.Assemble([A(), A()], AssemblyPlan.Merge((0, 10), (1, 10)));
        var document = AssemblyFixtures.CheckStructure(output);
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);
        Assert.Equal(20, profile.PageCount);
        Assert.Equal(["name", "email", "agree", "address", "name_2", "email_2", "agree_2", "address_2"], AssemblyChecks.RootFieldNames(document));
        AssemblyChecks.AssertFormIsConsistent(document);
        Assert.Equal(profile.Pages[2].Links, profile.Pages[12].Links);
        Assert.Equal(ProfileA.NamedDestinations.Count * 2, profile.NamedDestinations.Count);
        Assert.Equal(ProfileA.NamedDestinations.Values.Order(), profile.NamedDestinations.Where(static n => !n.Key.EndsWith("~2", StringComparison.Ordinal)).Select(static n => n.Value).Order());
    }

    [Fact]
    [Trait("Spec", "PO-13")]
    public void PagesFromAnUnlabelledFileKeepTheNumbersTheyHadWhenOthersHaveLabels()
    {
        var output = AssemblyFixtures.Assemble([A(), C()], new AssemblyPlan([new(0, 0), new(1, 2), new(1, 3), new(0, 3)]));
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);
        Assert.Equal(["i", "3", "4", "A-2"], profile.Pages.Select(static p => p.Label));
    }

    // ---------------------------------------------------------------- insert from file, duplicate, split

    [Fact]
    [Trait("Spec", "PO-09")]
    [Trait("Spec", "PO-11")]
    [Trait("Spec", "PO-13")]
    public void InsertFromFilePutsTheChosenPagesWhereAskedAndLeavesTheRestAlone()
    {
        var output = AssemblyFixtures.Assemble([A(), B()], AssemblyPlan.Insert(0, 10, 3, 1, [0, 1]));
        var document = AssemblyFixtures.CheckStructure(output);
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);

        Assert.Equal(
            new[] { "A-01", "A-02", "A-03", "B-01", "B-02", "A-04", "A-05", "A-06", "A-07", "A-08", "A-09", "A-10" }.Select(static k => "PAGE-" + k),
            profile.Keys);
        AssemblyChecks.AssertLinksFollow(profile, ProfileA, ProfileB);
        AssertLabelsFollow(profile, ProfileA, ProfileB);
        AssemblyChecks.AssertPagesLookTheSame(profile, ProfileA, ProfileB);
        // B's field named like A's is renamed, not merged: the base document's values are untouched.
        Assert.Equal(["name", "email", "agree", "address", "name_2"], AssemblyChecks.RootFieldNames(document));
        Assert.Contains(profile.Pages[4].Widgets, static w => w is ("name_2", _, "Bruno"));
        // B's entry for page 1 comes along; the entries for pages that were not inserted do not.
        Assert.Contains(profile.Outline, static b => b.Title == "Part I" && b.Target == "PAGE-B-01");
        Assert.DoesNotContain(profile.Outline, static b => b.Title == "Part II");
    }

    [Fact]
    [Trait("Spec", "PO-09")]
    [Trait("Spec", "PO-11")]
    public void DuplicatingAPageGivesTheCopyItsOwnLinksAnnotationsAndFields()
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Duplicate(0, 10, [1]));
        var document = AssemblyFixtures.CheckStructure(output);
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);

        Assert.Equal(11, profile.PageCount);
        Assert.Equal(Key("A", 2), profile.Pages[1].Key);
        Assert.Equal(Key("A", 2), profile.Pages[2].Key);
        // PDFium's own duplicate drops the copy's explicit-destination links and leaves its widget outside /AcroForm.
        Assert.Equal(ProfileA.Pages[1].Links.Count, profile.Pages[2].Links.Count);
        Assert.Equal(profile.Pages[1].Links, profile.Pages[2].Links);
        AssemblyChecks.AssertLinksFollow(profile, ProfileA);
        Assert.Equal(["ii", "ii"], profile.Pages.Skip(1).Take(2).Select(static p => p.Label));

        var pages = DocInfo.Pages(document);
        var originalAnnots = ((PdfArray)pages[1].Dict["Annots"]!).OfType<PdfReference>().Select(static r => r.Number).ToHashSet();
        var copyAnnots = ((PdfArray)pages[2].Dict["Annots"]!).OfType<PdfReference>().Select(static r => r.Number).ToHashSet();
        Assert.Equal(originalAnnots.Count, copyAnnots.Count);
        Assert.Empty(originalAnnots.Intersect(copyAnnots));

        Assert.Equal(["name", "email", "agree", "address", "name_2"], AssemblyChecks.RootFieldNames(document));
        AssemblyChecks.AssertFormIsConsistent(document);
        Assert.Contains(profile.Pages[2].Widgets, static w => w is ("name_2", _, "Alice"));
    }

    [Fact]
    [Trait("Spec", "PO-13")]
    public void ADuplicatedNoteGetsItsOwnPopupAndReply()
    {
        var output = AssemblyFixtures.Assemble([A()], AssemblyPlan.Pick(0, [0, 0]));
        var document = AssemblyFixtures.CheckStructure(output);
        var pages = DocInfo.Pages(document);
        var sets = pages.Select(page => ((PdfArray)page.Dict["Annots"]!).OfType<PdfReference>().Select(r => (r.Number, (PdfDictionary)document.GetObject(r.Number).Value)).ToList()).ToList();
        Assert.Empty(sets[0].Select(static a => a.Number).Intersect(sets[1].Select(static a => a.Number)));
        foreach (var set in sets)
        {
            var note = set.Single(static a => a.Item2.GetName("Subtype") == "Text" && a.Item2["IRT"] is null);
            var popup = set.Single(static a => a.Item2.GetName("Subtype") == "Popup");
            var reply = set.Single(static a => a.Item2["IRT"] is not null);
            Assert.Equal(popup.Number, ((PdfReference)note.Item2["Popup"]!).Number);
            Assert.Equal(note.Number, ((PdfReference)popup.Item2["Parent"]!).Number);
            Assert.Equal(note.Number, ((PdfReference)reply.Item2["IRT"]!).Number);
        }
    }

    [Fact]
    [Trait("Spec", "PO-13")]
    [Trait("Spec", "PO-19")]
    public void SplitMakesOneFilePerPartFromSourcesParsedOnce()
    {
        var set = PageAssembler.Open([A()], AssemblyFixtures.Fixed);
        var parts = new[] { (0, 2), (3, 5), (6, 9) };
        var expectedOutlines = new[]
        {
            new[] { "Chapter 1:0:PAGE-A-01", "Section 1.1:1:PAGE-A-02", "Section 1.2:1:PAGE-A-03" },
            new[] { "Chapter 2:0:PAGE-A-04", "Section 2.1:1:PAGE-A-05", "Section 2.2 (named destination):1:PAGE-A-06" },
            new[] { "Chapter 3:0:PAGE-A-07", "Section 3.1:1:PAGE-A-08", "Section 3.2 (URI):1:uri", "Section 3.3:1:PAGE-A-10" },
        };
        for (var i = 0; i < parts.Length; i++)
        {
            var result = set.Assemble(AssemblyPlan.Range(0, parts[i].Item1, parts[i].Item2), TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess);
            AssemblyFixtures.CheckStructure(result.Output);
            var profile = AssemblyChecks.AssertOpensInFreshEngine(result.Output);
            Assert.Equal(Enumerable.Range(parts[i].Item1 + 1, parts[i].Item2 - parts[i].Item1 + 1).Select(p => Key("A", p)), profile.Keys);
            Assert.Equal(expectedOutlines[i], profile.Outline.Select(static b => $"{b.Title}:{b.Depth}:{b.Target}"));
            AssemblyChecks.AssertLinksFollow(profile, ProfileA);
            AssemblyChecks.AssertPagesLookTheSame(profile, ProfileA);
        }

        // The same part again, from the same set: the same bytes.
        var first = set.Assemble(AssemblyPlan.Range(0, 3, 5), TestContext.Current.CancellationToken).Output.Bytes.ToArray();
        var again = set.Assemble(AssemblyPlan.Range(0, 3, 5), TestContext.Current.CancellationToken).Output.Bytes.ToArray();
        Assert.Equal(first, again);
    }

    [Fact]
    [Trait("Spec", "PO-26")]
    public void ALargeFlatDocumentGetsABalancedPageTree()
    {
        var output = AssemblyFixtures.Assemble([AssemblyFixtures.Source("big", MiniPdf.Large("L", 300, 1))], AssemblyPlan.Range(0, 0, 299));
        var document = AssemblyFixtures.CheckStructure(output);
        var catalog = AssemblyChecks.Catalog(document);
        var root = (PdfDictionary)document.GetObject(((PdfReference)catalog["Pages"]!).Number).Value;
        Assert.Equal(300, ((PdfInteger)root["Count"]!).Value);
        Assert.Equal(5, ((PdfArray)root["Kids"]!).Count); // 300 pages, 64 per node
        var profile = AssemblyChecks.AssertOpensInFreshEngine(output);
        Assert.Equal(Key("L", 300), profile.Pages[299].Key);
    }
}
