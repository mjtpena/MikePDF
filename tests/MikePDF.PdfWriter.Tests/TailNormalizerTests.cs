using System.Globalization;
using System.Text;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>
/// NormalizeTail (save-pipeline 3.3 stage 3, criteria SP-12, FM-05, FM-19): the tail PDFium appends in its incremental save is read with the strict parser and written again, with a
/// cross-reference section in the form of the original's newest one. The tails here are written the way PDFium writes them (S4 F9): objects as <c>N G obj CRLF ... endobj CRLF</c>, then
/// for a stream original a cross-reference stream with no <c>/Type /XRef</c>, no <c>endobj</c> and no entry for itself, and for a table original a table and a trailer. The same code is
/// run on real PDFium output by the integration tests (<c>Schema7EndToEndTests</c>).
/// </summary>
public sealed class TailNormalizerTests
{
    private static readonly ValidationOptions Pedantic = new() { DanglingReferencesAreErrors = true };

    private sealed record TailObject(int Number, string Text, byte[]? Stream = null, int Generation = 0);

    // What PDFium appends after `original`: the objects, then its cross-reference section. `xrefStream` writes the stream form with the defects S4 found.
    private static byte[] PdfiumSave(PdfBuilder.Built original, IReadOnlyList<TailObject> objects, bool xrefStream, string? rootOverride = null, string? infoOverride = null, bool duplicateEnd = false)
    {
        var baseDoc = DocInfo.Strict(original.Bytes);
        var ms = new MemoryStream();
        ms.Write(original.Bytes);
        var offsets = new SortedDictionary<int, int>();
        Append(ms, "\r\n");
        foreach (var o in objects)
        {
            offsets[o.Number] = (int)ms.Length;
            if (o.Stream is null)
            {
                Append(ms, $"{o.Number} {o.Generation} obj\r\n{o.Text}\r\nendobj\r\n");
            }
            else
            {
                var dict = o.Text.TrimEnd()[..^2].TrimEnd() + $" /Length {o.Stream.Length} >>";
                Append(ms, $"{o.Number} {o.Generation} obj\r\n{dict}\r\nstream\r\n");
                ms.Write(o.Stream);
                Append(ms, "\r\nendstream\r\nendobj\r\n");
            }
        }

        var size = Math.Max(baseDoc.Size, objects.Max(o => o.Number) + 1);
        var root = rootOverride ?? "1 0 R";
        var info = infoOverride ?? "7 0 R";
        var xrefOffset = (int)ms.Length;
        if (xrefStream)
        {
            var xrefNumber = size;
            var raw = new byte[offsets.Count * 6];
            var index = new StringBuilder();
            var p = 0;
            foreach (var (number, offset) in offsets)
            {
                index.Append(number).Append(" 1 ");
                raw[p] = 1;
                raw[p + 1] = (byte)(offset >> 24);
                raw[p + 2] = (byte)(offset >> 16);
                raw[p + 3] = (byte)(offset >> 8);
                raw[p + 4] = (byte)offset;
                raw[p + 5] = 0;
                p += 6;
            }

            Append(ms, $"{xrefNumber} 0 obj\r\n<</Info {info} /Root {root} /Size {xrefNumber}/Prev {baseDoc.StartXref}/ID[<0123456789ABCDEF0123456789ABCDEF><FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF>]/W[1 4 1]/Index[{index}]/Length {raw.Length}>>stream\r\n");
            ms.Write(raw);
            Append(ms, "\r\nendstream\r\n"); // no endobj, no /Type /XRef, no entry for itself: PDFium 8125
        }
        else
        {
            Append(ms, "xref\r\n");
            foreach (var (number, offset) in offsets)
            {
                Append(ms, $"{number} 1\r\n{offset:D10} 00000 n\r\n");
            }

            Append(ms, $"trailer\r\n<</Info {info} /Root {root} /Size {size}/Prev {baseDoc.StartXref}/ID[<0123456789ABCDEF0123456789ABCDEF><FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF>]>>\r\n");
        }

        Append(ms, $"startxref\r\n{xrefOffset}\r\n%%EOF\r\n");
        if (duplicateEnd)
        {
            Append(ms, "%%EOF\r\n");
        }

        return ms.ToArray();
    }

    private static void Append(MemoryStream ms, string text) => ms.Write(Encoding.Latin1.GetBytes(text));

    private static bool IsStreamLayout(string layout) => layout.StartsWith("xref-stream", StringComparison.Ordinal);

    private static PdfResult<TailNormalizeResult> Normalize(byte[] original, byte[] pdfium, TailNormalizeOptions? options = null) =>
        TailNormalizer.Normalize(original, pdfium, options ?? new TailNormalizeOptions { NewIdSecondElement = new byte[16] });

    private static void AssertSound(byte[] original, byte[] output)
    {
        Assert.True(output.Length > original.Length);
        Assert.True(output.AsSpan(0, original.Length).SequenceEqual(original), "the original bytes must be an exact prefix");
        var report = StructureValidator.Validate(output, options: Pedantic);
        Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(i => i.Message)));
        Assert.Empty(report.Issues);
    }

    private static readonly TailObject[] PageAndNew =
    [
        new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Resources << /Font << /F1 6 0 R >> >> /Annots [20 0 R] /Rotate 90 >>"),
        new(20, "<< /Type /Annot /Subtype /Square /Rect [10 10 50 50] /P 3 0 R /PDFIUM_HasGeneratedAP true /F 4 >>"),
    ];

    [Theory]
    [MemberData(nameof(DocumentTests.Layouts), MemberType = typeof(DocumentTests))]
    public void ATailInTheOriginalsFormBecomesAValidUpdateInThatForm(string layout)
    {
        var original = DocumentTests.Build(layout);
        var pdfium = PdfiumSave(original, PageAndNew, xrefStream: IsStreamLayout(layout));

        var result = Normalize(original.Bytes, pdfium);

        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.ToString());
        var output = result.Value.Output;
        AssertSound(original.Bytes, output);
        var before = DocInfo.Strict(original.Bytes);
        var after = DocInfo.Strict(output);
        Assert.Equal(2, after.Revisions.Count);
        Assert.Equal(before.Latest.Form == PdfXrefForm.Stream ? PdfXrefForm.Stream : PdfXrefForm.Table, after.Latest.Form); // a hybrid original gets a plain table
        Assert.Equal(before.StartXref, after.Latest.PrevOffset);
        Assert.Equal(new PdfReference(1, 0), after.Root);
        Assert.Equal("Square", Assert.IsType<PdfDictionary>(after.GetObject(20).Value).GetName("Subtype"));
        Assert.Equal(90, Assert.IsType<PdfInteger>(Assert.IsType<PdfDictionary>(after.GetObject(3).Value)["Rotate"]).Value);
        Assert.Equal(1, result.Value.ObjectsRewritten);
        Assert.Equal(1, result.Value.ObjectsAdded);
        // The first element of the /ID is kept, the second is new.
        var oldId = Assert.IsType<PdfArray>(before.Trailer["ID"]);
        var newId = Assert.IsType<PdfArray>(after.Trailer["ID"]);
        Assert.True(((PdfString)oldId[0]).Value.SequenceEqual(((PdfString)newId[0]).Value));
        Assert.False(((PdfString)oldId[1]).Value.SequenceEqual(((PdfString)newId[1]).Value));
        // The file is the original's own object 5 (the content stream) untouched: it was not part of the tail.
        Assert.True(PdfObjects.DeepEquals(before.GetObject(5).Value, after.GetObject(5).Value));
    }

    [Theory]
    [InlineData("xref-stream objstm flate+predictor")]
    [InlineData("xref-stream no-objstm")]
    public void PDFiumsOwnStreamTailIsNotAValidFileAndTheSameObjectsAreAfterwards(string layout)
    {
        var original = DocumentTests.Build(layout);
        var pdfium = PdfiumSave(original, PageAndNew, xrefStream: true);

        // As PDFium wrote it the file does not parse strictly (S4 F9): the new section has no /Type /XRef.
        Assert.True(StrictDocumentFails(pdfium));

        var result = Normalize(original.Bytes, pdfium);
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.ToString());
        AssertSound(original.Bytes, result.Value.Output);
        var after = DocInfo.Strict(result.Value.Output);
        Assert.Equal(PdfXrefForm.Stream, after.Latest.Form);
        // The new section has an entry for itself: the cross-reference stream is an in-use object of the new section.
        var xrefStreamNumber = after.Size - 1;
        Assert.True(after.Latest.TryGetEntry(xrefStreamNumber, out var own));
        Assert.Equal(XrefEntryType.InUse, own.Type);
    }

    private static bool StrictDocumentFails(byte[] bytes) => StrictDocument.Open(bytes).TryGetError(out _);

    [Fact]
    public void PDFiumsPrivateKeysAreRemovedAtEveryDepthAndOnlyWhenAsked()
    {
        var original = DocumentTests.Build("classic");
        TailObject[] objects =
        [
            new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [20 0 R] /PDFIUM_Page true >>"),
            new(20, "<< /Type /Annot /Subtype /Square /Rect [1 2 3 4] /PDFIUM_HasGeneratedAP true /BS << /W 1 /PDFIUM_Deep [1 << /PDFIUM_Deeper 1 /Keep 2 >>] /Style /S >> >>"),
        ];
        var pdfium = PdfiumSave(original, objects, xrefStream: false);

        var stripped = Normalize(original.Bytes, pdfium, new TailNormalizeOptions { StripPdfiumKeys = true, NewIdSecondElement = new byte[16] });
        Assert.True(stripped.IsSuccess, stripped.IsSuccess ? "" : stripped.Error.ToString());
        Assert.Equal(3, stripped.Value.KeysStripped); // /PDFIUM_Page, /PDFIUM_HasGeneratedAP and /PDFIUM_Deep (whose own nested key goes with its value)
        Assert.DoesNotContain("PDFIUM_", Encoding.Latin1.GetString(stripped.Value.Output.AsSpan(original.Bytes.Length)));
        AssertSound(original.Bytes, stripped.Value.Output);
        var annotation = Assert.IsType<PdfDictionary>(DocInfo.Strict(stripped.Value.Output).GetObject(20).Value);
        Assert.Equal("Square", annotation.GetName("Subtype"));
        Assert.True(((PdfDictionary)annotation["BS"]!).ContainsKey("W"));

        var kept = Normalize(original.Bytes, pdfium, new TailNormalizeOptions { StripPdfiumKeys = false, NewIdSecondElement = new byte[16] });
        Assert.True(kept.IsSuccess);
        Assert.Equal(0, kept.Value.KeysStripped);
        Assert.Contains("PDFIUM_HasGeneratedAP", Encoding.Latin1.GetString(kept.Value.Output.AsSpan(original.Bytes.Length)));
    }

    // ---- check boxes and radio buttons (forms 10.2) -----------------------------------------------------------------------------

    private static PdfBuilder.Built Checkbox() => PdfBuilder.Classic(
    [
        new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [8 0 R] >>"),
        new(7, "<< /Producer (MikePDFTests) >>"),
        new(8, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (box) /V /Off /AS /Off /Rect [10 10 30 30] /F 4 /AP << /N << /Yes 9 0 R /Off 10 0 R >> >> >>"),
        new(9, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "AUTHORS-YES"u8.ToArray()),
        new(10, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "AUTHORS-OFF"u8.ToArray()),
    ]);

    private static string StreamText(StrictDocument document, int number) => Encoding.Latin1.GetString(document.DecodeStream((PdfStream)document.GetObject(number).Value).Value);

    [Fact]
    public void ACheckBoxKeepsTheAuthorsArtworkWhenPDFiumMakesNewStreams()
    {
        var original = Checkbox();
        TailObject[] objects =
        [
            new(8, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (box) /V /Yes /AS /Yes /Rect [10 10 30 30] /F 4 /AP << /N << /Yes 11 0 R /Off 12 0 R >> >> /PDFIUM_HasGeneratedAP true >>"),
            new(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "PDFIUM-YES"u8.ToArray()),
            new(12, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "PDFIUM-OFF"u8.ToArray()),
        ];
        var pdfium = PdfiumSave(original, objects, xrefStream: false);

        var result = Normalize(original.Bytes, pdfium);

        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.ToString());
        Assert.Equal(1, result.Value.WidgetsRestored);
        Assert.Equal(2, result.Value.ObjectsDropped); // PDFium's two new streams, which nothing refers to now
        AssertSound(original.Bytes, result.Value.Output);
        var after = DocInfo.Strict(result.Value.Output);
        var widget = Assert.IsType<PdfDictionary>(after.GetObject(8).Value);
        Assert.Equal("Yes", widget.GetName("AS"));   // PDFium's state stays
        Assert.Equal("Yes", widget.GetName("V"));
        var states = Assert.IsType<PdfDictionary>(Assert.IsType<PdfDictionary>(widget["AP"])["N"]);
        Assert.Equal(new PdfReference(9, 0), states["Yes"]);   // the author's stream objects
        Assert.Equal(new PdfReference(10, 0), states["Off"]);
        Assert.Equal("AUTHORS-YES", StreamText(after, 9));
        Assert.DoesNotContain(11, after.Latest.Entries.Keys);
    }

    [Fact]
    public void ACheckBoxKeepsTheAuthorsArtworkWhenPDFiumRewritesTheStreamsInPlace()
    {
        var original = Checkbox();
        TailObject[] objects =
        [
            new(8, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (box) /V /Yes /AS /Yes /Rect [10 10 30 30] /F 4 /AP << /N << /Yes 9 0 R /Off 10 0 R >> >> >>"),
            new(9, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "PDFIUM-YES"u8.ToArray()),
            new(10, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "PDFIUM-OFF"u8.ToArray()),
        ];
        var pdfium = PdfiumSave(original, objects, xrefStream: false);

        var result = Normalize(original.Bytes, pdfium);

        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.ToString());
        Assert.Equal(1, result.Value.WidgetsRestored);
        AssertSound(original.Bytes, result.Value.Output);
        var after = DocInfo.Strict(result.Value.Output);
        Assert.Equal("AUTHORS-YES", StreamText(after, 9));
        Assert.Equal("AUTHORS-OFF", StreamText(after, 10));
        Assert.Equal("Yes", Assert.IsType<PdfDictionary>(after.GetObject(8).Value).GetName("AS"));
    }

    [Fact]
    public void AWidgetWhoseStateNamesChangedKeepsPDFiumsOutputAndSoDoesTheRestoreSwitchedOff()
    {
        var original = Checkbox();
        TailObject[] changed =
        [
            new(8, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (box) /V /On /AS /On /Rect [10 10 30 30] /F 4 /AP << /N << /On 11 0 R /Off 12 0 R >> >> >>"),
            new(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "PDFIUM-ON"u8.ToArray()),
            new(12, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "PDFIUM-OFF"u8.ToArray()),
        ];
        var result = Normalize(original.Bytes, PdfiumSave(original, changed, xrefStream: false));
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.ToString());
        Assert.Equal(0, result.Value.WidgetsRestored);
        Assert.Equal("PDFIUM-ON", StreamText(DocInfo.Strict(result.Value.Output), 11));

        TailObject[] sameNames =
        [
            new(8, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (box) /V /Yes /AS /Yes /Rect [10 10 30 30] /F 4 /AP << /N << /Yes 11 0 R /Off 12 0 R >> >> >>"),
            new(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "PDFIUM-YES"u8.ToArray()),
            new(12, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "PDFIUM-OFF"u8.ToArray()),
        ];
        var off = Normalize(original.Bytes, PdfiumSave(original, sameNames, xrefStream: false), new TailNormalizeOptions { RestoreCheckAndRadioStreams = false, NewIdSecondElement = new byte[16] });
        Assert.True(off.IsSuccess);
        Assert.Equal(0, off.Value.WidgetsRestored);
        Assert.Equal("PDFIUM-YES", StreamText(DocInfo.Strict(off.Value.Output), 11));
    }

    [Fact]
    public void AnAppearanceThatIsASingleStreamIsLeftToPDFium()
    {
        // A text field's /AP /N is a stream, not a dictionary of states: nothing to restore.
        var original = PdfBuilder.Classic(
        [
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [8 0 R] >>"),
            new(7, "<< /Producer (MikePDFTests) >>"),
            new(8, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (t) /V (a) /Rect [10 10 30 30] /AP << /N 9 0 R >> >>"),
            new(9, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "OLD"u8.ToArray()),
        ]);
        TailObject[] objects =
        [
            new(8, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (t) /V (b) /Rect [10 10 30 30] /AP << /N 9 0 R >> >>"),
            new(9, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] >>", "NEW"u8.ToArray()),
        ];
        var result = Normalize(original.Bytes, PdfiumSave(original, objects, xrefStream: false));
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.ToString());
        Assert.Equal(0, result.Value.WidgetsRestored);
        Assert.Equal("NEW", StreamText(DocInfo.Strict(result.Value.Output), 9));
    }

    // ---- what is refused ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFileThatIsNotAnExtensionOfTheOriginalIsRefused()
    {
        var original = DocumentTests.Build("classic");
        var pdfium = PdfiumSave(original, PageAndNew, xrefStream: false);
        var damaged = pdfium.ToArray();
        damaged[20] ^= 0xFF; // a byte inside the original's range
        var result = Normalize(original.Bytes, damaged);
        Assert.False(result.IsSuccess);
        Assert.Equal(PdfErrorCode.InvalidUpdate, result.Error.Code);

        Assert.Equal(PdfErrorCode.InvalidUpdate, Normalize(original.Bytes, original.Bytes).Error.Code);      // nothing appended
        Assert.Equal(PdfErrorCode.InvalidUpdate, Normalize(original.Bytes, original.Bytes[..^10]).Error.Code); // shorter than the original
    }

    [Theory]
    [InlineData("root", "99 0 R", null)]
    [InlineData("info", null, "5 0 R")]
    public void ATrailerThatNamesAnotherRootOrInfoIsRefused(string what, string? root, string? info)
    {
        var original = DocumentTests.Build("classic");
        var pdfium = PdfiumSave(original, PageAndNew, xrefStream: false, rootOverride: root, infoOverride: info);
        var result = Normalize(original.Bytes, pdfium);
        Assert.False(result.IsSuccess, what);
        Assert.Equal(PdfErrorCode.InvalidTrailer, result.Error.Code);
    }

    [Fact]
    public void AnAppendedObjectThePDFiumFileCannotReadStrictlyRefusesTheWholeFile()
    {
        var original = DocumentTests.Build("classic");
        foreach (var (name, text) in new (string, string)[]
        {
            ("duplicate key", "<< /Type /Page /Parent 2 0 R /Rotate 90 /Rotate 180 >>"),
            ("unterminated dictionary", "<< /Type /Page /Parent 2 0 R"),
            ("a name with a bad escape", "<< /Type /Pa#zge >>"),
        })
        {
            var pdfium = PdfiumSave(original, [new TailObject(3, text)], xrefStream: false);
            var result = Normalize(original.Bytes, pdfium);
            Assert.False(result.IsSuccess, name);
        }
    }

    [Fact]
    public void AnObjectTheOriginalDoesNotHaveButWhoseNumberIsBelowItsSizeIsRefused()
    {
        // The original's table has object 4 free, as a deleted one: PDFium has no business rewriting it.
        var original = PdfBuilder.Classic(
        [
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
            new(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            new(7, "<< /Producer (MikePDFTests) >>"),
        ]);
        var result = Normalize(original.Bytes, PdfiumSave(original, [new TailObject(4, "<< /Foo 1 >>")], xrefStream: false));
        Assert.False(result.IsSuccess);
        Assert.Equal(PdfErrorCode.InvalidUpdate, result.Error.Code);

        // A generation that is not the original's.
        var generation = Normalize(original.Bytes, PdfiumSave(original, [new TailObject(3, "<< /Type /Page /Parent 2 0 R >>", Generation: 2)], xrefStream: false));
        Assert.False(generation.IsSuccess);
    }

    [Fact]
    public void TheSameObjectTwiceAndAStartxrefIntoTheOriginalAreRefused()
    {
        var original = DocumentTests.Build("classic");
        var twice = Normalize(original.Bytes, PdfiumSave(original, [new TailObject(3, "<< /Type /Page /Parent 2 0 R >>"), new TailObject(3, "<< /Type /Page /Parent 2 0 R /Rotate 90 >>")], xrefStream: false));
        Assert.False(twice.IsSuccess);
        Assert.Equal(PdfErrorCode.DuplicateXrefEntry, twice.Error.Code);

        var pdfium = PdfiumSave(original, PageAndNew, xrefStream: false);
        var text = Encoding.Latin1.GetString(pdfium);
        var at = text.LastIndexOf("startxref\r\n", StringComparison.Ordinal) + 11;
        var broken = Encoding.Latin1.GetBytes(text[..at] + original.XrefOffset.ToString(CultureInfo.InvariantCulture) + "\r\n%%EOF\r\n");
        var result = Normalize(original.Bytes, broken);
        Assert.False(result.IsSuccess);
        Assert.Equal(PdfErrorCode.InvalidStartXref, result.Error.Code);
    }

    [Fact]
    public void AClippedOrGarbledTailNeverThrowsAndNeverYieldsAnUnsoundFile()
    {
        var rng = new Random(20261104);
        foreach (var layout in new[] { "classic", "xref-stream objstm flate+predictor", "hybrid" })
        {
            var original = DocumentTests.Build(layout);
            var pdfium = PdfiumSave(original, PageAndNew, xrefStream: IsStreamLayout(layout));
            var tail = pdfium.AsSpan(original.Bytes.Length).ToArray();

            for (var i = 0; i < 150; i++)
            {
                var mutated = Mutator.Mutate(rng, tail);
                var file = original.Bytes.Concat(mutated).ToArray();
                var result = Normalize(original.Bytes, file);
                if (result.IsSuccess)
                {
                    // Whatever got through is a file the strict parser reads, with the original as its prefix.
                    Assert.True(result.Value.Output.AsSpan(0, original.Bytes.Length).SequenceEqual(original.Bytes));
                    Assert.True(StrictDocument.Open(result.Value.Output).IsSuccess);
                }
            }

            for (var cut = 1; cut < tail.Length; cut += Math.Max(1, tail.Length / 60))
            {
                var file = original.Bytes.Concat(tail.AsSpan(0, cut).ToArray()).ToArray();
                Assert.False(Normalize(original.Bytes, file).IsSuccess, $"a tail cut at {cut} of {tail.Length}");
            }
        }
    }

    [Fact]
    public void ARefusalIsAnErrorValueNotAnException()
    {
        var original = DocumentTests.Build("classic");
        Assert.False(TailNormalizer.Normalize(Array.Empty<byte>(), Array.Empty<byte>()).IsSuccess);
        Assert.False(TailNormalizer.Normalize(original.Bytes, Array.Empty<byte>()).IsSuccess);
        Assert.False(TailNormalizer.Normalize("not a pdf"u8.ToArray(), "not a pdf, either"u8.ToArray()).IsSuccess);
    }
}
