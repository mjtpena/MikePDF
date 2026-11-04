#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Markup.Tests.Support;
using MikePDF.PdfWriter;
using MikePDF.PdfWriter.Editing;

namespace MikePDF.Markup.Tests;

/// <summary>
/// The model through the save core's mapper, blob codec and stage 4, and back out of the bytes: every kind is written as the specs say (annotation-core 3.9
/// rules 1 to 9; TM-10, SN-07, SH-07, FT-06, INK-10, ST-02, ST-11 as far as this process can check them; the sandboxed host and PDFium are in the integration tests).
/// </summary>
public sealed class SavedStructureTests
{
    private static (byte[] Original, byte[] Saved, StrictDocument Document, IReadOnlyList<Annotation> Added, IReadOnlyList<(PdfReference? Reference, PdfDictionary Dictionary)> Annotations) SaveEveryKind(int rotate = 0)
    {
        var samples = new Samples();
        var original = TestDocuments.Build(new PageSpec([0, 0, 612, 792], null, rotate));
        foreach (var a in samples.OfEveryKind())
        {
            samples.Session.Add(a);
        }

        var saved = TestDocuments.Save(original, samples.Model, 1);
        var document = TestDocuments.Open(saved);
        return (original, saved, document, samples.Session.Store.OnPage(0), TestDocuments.Annotations(document, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void EveryKindIsAnIndirectAnnotationWithItsSubtypeItsRectAndAnAppearance(int rotate)
    {
        var (original, saved, document, added, annotations) = SaveEveryKind(rotate);

        TestDocuments.AssertSound(original, saved);
        var annotationsOnly = annotations.Where(static a => a.Dictionary.GetName("Subtype") != "Popup").ToList();
        Assert.Equal(added.Count, annotationsOnly.Count);
        Assert.All(annotationsOnly, item => Assert.NotNull(item.Reference)); // rule 2: indirect objects
        for (var i = 0; i < added.Count; i++)
        {
            var model = added[i];
            var dictionary = annotationsOnly[i].Dictionary;
            Assert.Equal(model.Subtype, dictionary.GetName("Subtype"));
            var expectedRect = model.Kind == MarkupKind.Note ? NoteGeometry.SavedRect(model.Rect, rotate) : model.Rect; // an icon is anchored at its upper-left corner of the unturned page
            Assert.True(Geo.NearlyEqual(expectedRect, TestDocuments.Rect(document, dictionary), 0.01), $"{model.Kind}: /Rect");
            Assert.Equal(model.Id, System.Text.Encoding.Latin1.GetString(((PdfString)dictionary["NM"]!).Value));
            Assert.Equal(PageReference(document), dictionary["P"]); // rule 2: /P is the page
            Assert.True((((PdfInteger)dictionary["F"]!).Value & 4) != 0, $"{model.Kind}: Print"); // annotation-core 3.5
            Assert.DoesNotContain(dictionary.Keys, static key => key.Value.StartsWith("PDFIUM", StringComparison.Ordinal)); // rule 8

            // Rule 1 and 3: a form XObject with its own /Resources, /BBox, and a content stream.
            var form = TestDocuments.Appearance(document, dictionary);
            Assert.Equal("XObject", form.Dictionary.GetName("Type"));
            Assert.Equal("Form", form.Dictionary.GetName("Subtype"));
            Assert.NotNull(form.Dictionary["BBox"]);
            Assert.IsType<PdfDictionary>(form.Dictionary["Resources"]);
            Assert.NotEmpty(TestDocuments.StreamText(document, form));
        }
    }

    private static PdfReference PageReference(StrictDocument document) => PageTree.Enumerate(document).Value[0].Reference;

    [Fact]
    public void NameValuesAreNameObjectsNotStrings()
    {
        var (_, _, document, added, annotations) = SaveEveryKind();

        // Rule 7 (S4 F3): /Name of a note and of a stamp, /LE entries, /IT.
        var byId = added.Zip(annotations.Where(static a => a.Dictionary.GetName("Subtype") != "Popup"), static (m, a) => (m, a.Dictionary)).GroupBy(static x => x.m.Kind).ToDictionary(static g => g.Key, static g => g.First().Dictionary);
        Assert.Equal("Key", byId[MarkupKind.Note].GetName("Name"));
        Assert.Equal("Approved", byId[MarkupKind.Stamp].GetName("Name"));
        var arrow = (PdfArray)byId[MarkupKind.Arrow]["LE"]!;
        Assert.Equal(["None", "OpenArrow"], arrow.Cast<PdfName>().Select(static n => n.Value));
        var line = (PdfArray)byId[MarkupKind.Line]["LE"]!;
        Assert.Equal(["Circle", "ClosedArrow"], line.Cast<PdfName>().Select(static n => n.Value));
        Assert.Equal("FreeTextTypeWriter", annotations.Select(static a => a.Dictionary).Single(static d => d["IT"] is not null).GetName("IT"));
        Assert.NotNull(document);
    }

    [Fact]
    public void TheKindSpecificKeysAreThereAndOnlyWhereTheyBelong()
    {
        var (_, _, document, added, annotations) = SaveEveryKind();
        var byKind = added.Zip(annotations.Where(static a => a.Dictionary.GetName("Subtype") != "Popup"), static (m, a) => (m, a.Dictionary)).GroupBy(static x => x.m.Kind).ToDictionary(static g => g.Key, static g => g.First().Dictionary);

        Assert.Equal(16, ((PdfArray)byKind[MarkupKind.Highlight]["QuadPoints"]!).Count); // TM-10: two quads of eight numbers
        Assert.Equal(8, ((PdfArray)byKind[MarkupKind.Underline]["QuadPoints"]!).Count);
        Assert.NotNull(byKind[MarkupKind.Squiggly]["QuadPoints"]);
        Assert.Equal(4, ((PdfArray)byKind[MarkupKind.Line]["L"]!).Count); // SH-07
        Assert.NotNull(byKind[MarkupKind.Line]["IC"]);
        Assert.Equal(8, ((PdfArray)byKind[MarkupKind.Polygon]["Vertices"]!).Count);
        Assert.Equal(6, ((PdfArray)byKind[MarkupKind.PolyLine]["Vertices"]!).Count);
        Assert.Null(byKind[MarkupKind.Polygon]["LE"]); // a polygon is closed: no endings
        Assert.NotNull(byKind[MarkupKind.PolyLine]["LE"]);
        Assert.Equal(2, ((PdfArray)byKind[MarkupKind.Ink]["InkList"]!).Count); // INK-10
        Assert.NotNull(byKind[MarkupKind.Ink]["BS"]);
        var widths = (PdfArray)byKind[MarkupKind.Ink][AnnotationSpecBuilder.InkWidthsKey]!;
        Assert.Equal(2, widths.Count); // the private per-point widths, one list for each stroke (OQ-INK-3)
        var rectangle = TestDocuments.Dictionary(document, byKind[MarkupKind.Rectangle]["BS"]);
        Assert.Equal("D", rectangle.GetName("S")); // SH-07: dashed, with its array
        Assert.NotNull(rectangle["D"]);
        Assert.NotNull(byKind[MarkupKind.Rectangle]["IC"]);

        var text = byKind[MarkupKind.FreeText];
        Assert.Contains("Tf", System.Text.Encoding.Latin1.GetString(((PdfString)text["DA"]!).Value), StringComparison.Ordinal); // FT-06
        Assert.Equal(1, ((PdfInteger)text["Q"]!).Value);
        Assert.Equal(1.5, ((PdfReal)TestDocuments.Dictionary(document, text["BS"])["W"]!).Value);
        Assert.Contains((byte)'\r', ((PdfString)text["Contents"]!).Value.ToArray()); // line breaks are \r in /Contents
        Assert.Null(text["IT"]); // only the typewriter has /IT

        Assert.Equal(1.0, ((PdfReal)byKind[MarkupKind.Rectangle]["CA"]!).Value, 1.0);
        Assert.Equal("Underline", byKind[MarkupKind.Underline].GetName("Subtype"));
    }

    [Fact]
    public void ANoteHasARightHandPopupThatPointsBackAtIt()
    {
        var samples = new Samples();
        var original = TestDocuments.Build(PageSpec.Letter);
        var note = samples.Session.Add(samples.Note());
        var saved = TestDocuments.Save(original, samples.Model, 1);
        var document = TestDocuments.Open(saved);

        var annotations = TestDocuments.Annotations(document, 0);

        Assert.Equal(2, annotations.Count); // the note, then its popup, both in /Annots
        var noteDictionary = annotations[0].Dictionary;
        var popupReference = Assert.IsType<PdfReference>(noteDictionary["Popup"]);
        Assert.Equal(popupReference, annotations[1].Reference);
        var popup = annotations[1].Dictionary;
        Assert.Equal("Popup", popup.GetName("Subtype"));
        Assert.Equal(annotations[0].Reference, popup["Parent"]); // a reference, never a string (S4 F2)
        Assert.Equal(PdfBoolean.False, popup["Open"]);
        Assert.Equal(PdfBoolean.False, noteDictionary["Open"]);
        var rect = TestDocuments.Rect(document, popup);
        Assert.Equal((180, 100), (Math.Round(rect.Width), Math.Round(rect.Height)));
        Assert.True(rect.Left >= note.Rect.Right); // to the right of the icon
        Assert.Equal(28, ((PdfInteger)noteDictionary["F"]!).Value); // Print, NoZoom, NoRotate
    }

    [Fact]
    public void ARepliesAndAStateAreLinkedByReferenceAndDrawNothing()
    {
        var samples = new Samples();
        var original = TestDocuments.Build(PageSpec.Letter);
        var note = samples.Session.Add(samples.Note());
        var reply = samples.Session.Reply(note.Id, "I agree")!;
        var state = samples.Session.SetReviewState(note.Id, ReviewState.Accepted)!;
        var saved = TestDocuments.Save(original, samples.Model, 1);
        var document = TestDocuments.Open(saved);

        var annotations = TestDocuments.Annotations(document, 0).Where(static a => a.Dictionary.GetName("Subtype") != "Popup").ToList();

        Assert.Equal(3, annotations.Count);
        var root = annotations[0];
        foreach (var answer in annotations.Skip(1))
        {
            Assert.Equal("Text", answer.Dictionary.GetName("Subtype"));
            Assert.Equal(root.Reference, answer.Dictionary["IRT"]); // a reference
            Assert.Equal("R", answer.Dictionary.GetName("RT"));
            Assert.Equal(28, ((PdfInteger)answer.Dictionary["F"]!).Value);
            Assert.True(Geo.NearlyEqual(note.Rect, TestDocuments.Rect(document, answer.Dictionary), 0.01)); // at the parent's rectangle (OQ-SN-2, proposal)
            Assert.Equal(string.Empty, TestDocuments.StreamText(document, TestDocuments.Appearance(document, answer.Dictionary)).Replace("/GS gs\n", string.Empty, StringComparison.Ordinal));
        }

        var stateDictionary = annotations[2].Dictionary;
        Assert.Equal("Review", System.Text.Encoding.Latin1.GetString(((PdfString)stateDictionary["StateModel"]!).Value));
        Assert.Equal("Accepted", System.Text.Encoding.Latin1.GetString(((PdfString)stateDictionary["State"]!).Value));
        Assert.Equal("I agree", System.Text.Encoding.Latin1.GetString(((PdfString)annotations[1].Dictionary["Contents"]!).Value));
        Assert.Equal(reply.Parent, state.Parent);
    }

    [Fact]
    public void AnAppearanceHasItsOwnFontsAndNeverReliesOnThePage()
    {
        var (_, _, document, added, annotations) = SaveEveryKind();
        var withText = annotations.Where(a => a.Dictionary.GetName("Subtype") is "FreeText" or "Stamp").ToList();

        Assert.Equal(3, withText.Count);
        foreach (var annotation in withText)
        {
            var resources = TestDocuments.Dictionary(document, TestDocuments.Appearance(document, annotation.Dictionary).Dictionary["Resources"]);
            var fonts = TestDocuments.Dictionary(document, resources["Font"]);
            Assert.NotEmpty(fonts); // rule 6 (S4 F4): declared here, not inherited from the page
            foreach (var pair in fonts)
            {
                var font = (PdfDictionary)pair.Value;
                Assert.Equal("WinAnsiEncoding", font.GetName("Encoding"));
                Assert.Equal("Type1", font.GetName("Subtype"));
            }

            Assert.NotNull(TestDocuments.Dictionary(document, resources["ExtGState"])["GS"]);
        }

        Assert.NotEmpty(added);
    }

    [Fact]
    public void OpacityIsInTheAnnotationAndInTheGraphicsStateAndAHighlightMultiplies()
    {
        var (_, _, document, added, annotations) = SaveEveryKind();
        var byKind = added.Zip(annotations.Where(static a => a.Dictionary.GetName("Subtype") != "Popup"), static (m, a) => (m, a.Dictionary)).GroupBy(static x => x.m.Kind).ToDictionary(static g => g.Key, static g => g.First().Dictionary);

        foreach (var kind in new[] { MarkupKind.Highlight, MarkupKind.Rectangle })
        {
            var model = added.First(a => a.Kind == kind);
            var dictionary = byKind[kind];
            var gs = TestDocuments.Dictionary(document, TestDocuments.Dictionary(document, TestDocuments.Appearance(document, dictionary).Dictionary["Resources"])["ExtGState"]);
            var state = TestDocuments.Dictionary(document, gs["GS"]);
            Assert.Equal(model.Opacity, ((PdfReal)dictionary["CA"]!).Value, 6); // rule 4
            Assert.Equal(model.Opacity, ((PdfReal)state["CA"]!).Value, 6);
            Assert.Equal(model.Opacity, ((PdfReal)state["ca"]!).Value, 6);
            Assert.Equal(kind == MarkupKind.Highlight ? "Multiply" : "Normal", state.GetName("BM")); // rule 5
        }

        // SH 3.5: a transparency group exactly when the opacity is below 100%.
        var rectangle = TestDocuments.Appearance(document, byKind[MarkupKind.Rectangle]);
        Assert.NotNull(rectangle.Dictionary["Group"]);
        var ellipse = TestDocuments.Appearance(document, byKind[MarkupKind.Ellipse]);
        Assert.Null(ellipse.Dictionary["Group"]);
    }

    [Fact]
    public void ADeleteOfAnAnnotationTheFileAlreadyHoldsTakesItAndItsPopupOffThePage()
    {
        var samples = new Samples();
        var original = TestDocuments.Build(PageSpec.Letter);
        var note = samples.Session.Add(samples.Note());
        var first = TestDocuments.Save(original, samples.Model, 1);
        samples.Model.MarkSaved(samples.Model.BeginSave());
        Assert.Equal(2, TestDocuments.Annotations(TestDocuments.Open(first), 0).Count);

        Assert.True(samples.Session.Delete(note.Id));
        var second = TestDocuments.Save(first, samples.Model, 1);

        TestDocuments.AssertSound(first, second);
        Assert.Empty(TestDocuments.Annotations(TestDocuments.Open(second), 0)); // the popup went with it
    }

    [Fact]
    public void ARestyleOfASavedAnnotationReplacesItInPlaceWithANewAppearance()
    {
        var samples = new Samples();
        var original = TestDocuments.Build(PageSpec.Letter);
        var rectangle = samples.Session.Add(samples.Rectangle());
        var other = samples.Session.Add(samples.Ellipse());
        var first = TestDocuments.Save(original, samples.Model, 1);
        samples.Model.MarkSaved(samples.Model.BeginSave());
        var before = TestDocuments.Annotations(TestDocuments.Open(first), 0);

        Assert.True(samples.Session.Update(rectangle.Id, a => a with { Color = new Rgb(0, 1, 0), Width = 5 }));
        var second = TestDocuments.Save(first, samples.Model, 1);

        TestDocuments.AssertSound(first, second);
        var after = TestDocuments.Annotations(TestDocuments.Open(second), 0);
        Assert.Equal(before.Select(static b => b.Reference), after.Select(static a => a.Reference)); // same objects, same order
        var document = TestDocuments.Open(second);
        Assert.Equal(5.0, ((PdfReal)TestDocuments.Dictionary(document, after[0].Dictionary["BS"])["W"]!).Value);
        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, TestDocuments.Numbers(document, after[0].Dictionary["C"]));
        Assert.NotEqual(before[0].Dictionary["AP"], after[0].Dictionary["AP"]); // a new appearance stream
        Assert.Equal(before[1].Dictionary["AP"], after[1].Dictionary["AP"]); // the other annotation is untouched
        Assert.NotNull(other);
    }

    [Fact]
    public void AMovedForeignAnnotationKeepsItsAppearanceStreamByteForByte()
    {
        // A foreign annotation (not ours): the file has it with an appearance of someone else's making. A move changes /Rect only (AC-24).
        var samples = new Samples();
        var original = TestDocuments.Build(PageSpec.Letter);
        var foreign = samples.Session.Add(samples.Rectangle());
        var first = TestDocuments.Save(original, samples.Model, 1);
        samples.Model.MarkSaved(samples.Model.BeginSave());
        var document = TestDocuments.Open(first);
        var dictionary = TestDocuments.Annotations(document, 0)[0].Dictionary;

        var other = new Samples();
        var foreignId = "someone-else-".PadRight(foreign.Id.Length, 'x');
        var loaded = foreign with { Id = foreignId, Origin = AnnotationOrigin.Loaded };
        other.Session.Load([loaded]);
        Assert.False(AnnotationRules.Classify(loaded) == AnnotationClass.Owned);
        // Name the annotation the way a foreign one is named in the file: rewrite /NM so the model's id matches the file.
        var renamed = RenameInFile(first, foreign.Id, foreignId);
        Assert.True(other.Session.Move(loaded.Id, 10, 20));
        var second = TestDocuments.Save(renamed, other.Model, 1);

        var after = TestDocuments.Annotations(TestDocuments.Open(second), 0)[0].Dictionary;
        Assert.Equal(TestDocuments.Appearance(TestDocuments.Open(renamed), TestDocuments.Annotations(TestDocuments.Open(renamed), 0)[0].Dictionary).RawData.ToArray(),
            TestDocuments.Appearance(TestDocuments.Open(second), after).RawData.ToArray());
        Assert.Equal(new[] { 110.0, 320.0, 230.0, 380.0 }, TestDocuments.Numbers(TestDocuments.Open(second), after["Rect"]));
        Assert.NotNull(dictionary);
    }

    private static byte[] RenameInFile(byte[] file, string from, string to)
    {
        // The two ids have the same length in this test (a different prefix of the same size), so the bytes can be patched in place.
        var text = System.Text.Encoding.Latin1.GetString(file);
        var patched = text.Replace(from, to, StringComparison.Ordinal);
        return System.Text.Encoding.Latin1.GetBytes(patched);
    }
}
