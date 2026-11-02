using System.Text;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

public sealed class IncrementalUpdateTests
{
    private static readonly ValidationOptions Pedantic = new() { DanglingReferencesAreErrors = true };

    private static PdfBuilder.Built Layout(string layout) => DocumentTests.Build(layout);

    private static void AssertCleanAndPrefixed(byte[] original, byte[] updated)
    {
        Assert.True(updated.Length > original.Length);
        Assert.True(updated.AsSpan(0, original.Length).SequenceEqual(original), "the original bytes must be an exact prefix");
        var report = StructureValidator.Validate(updated, options: Pedantic);
        Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(i => i.Message)));
        Assert.Empty(report.Issues);
    }

    [Theory]
    [MemberData(nameof(DocumentTests.Layouts), MemberType = typeof(DocumentTests))]
    public void AppendKeepsThePrefixAndAddsTheObjects(string layout)
    {
        var built = Layout(layout);
        var doc = DocInfo.Strict(built.Bytes);
        byte[] updated = DocInfo.AppendAnnotations(doc, 2);
        AssertCleanAndPrefixed(built.Bytes, updated);

        var after = DocInfo.Strict(updated);
        Assert.Equal(2, after.Revisions.Count);
        Assert.Equal(doc.Latest.Form, after.Latest.Form);
        Assert.Equal(doc.StartXref, after.Latest.PrevOffset);
        Assert.Equal(2, DocInfo.PageCount(after));
        Assert.Equal(2, DocInfo.AnnotCount(after, 0));
        Assert.Equal(0, DocInfo.AnnotCount(after.Revisions[1], 0));

        // New objects are 8.. for classic and 10.. for the stream layouts (Size + 0, 1), each an in-use entry in the new section.
        foreach (var (number, entry) in after.Latest.Entries)
            Assert.Equal(XrefEntryType.InUse, entry.Type);
        Assert.Contains(doc.Size, after.Latest.Entries.Keys);
        Assert.Contains(doc.Size + 1, after.Latest.Entries.Keys);
        var annot = Assert.IsType<PdfDictionary>(after.GetObject(doc.Size).Value);
        Assert.Equal("Square", annot.GetName("Subtype"));
        Assert.Equal(doc.Size + (doc.Latest.Form == PdfXrefForm.Stream ? 3 : 2), after.Size);
    }

    [Theory]
    [MemberData(nameof(DocumentTests.Layouts), MemberType = typeof(DocumentTests))]
    public void RepeatedUpdatesStack(string layout)
    {
        byte[] current = Layout(layout).Bytes;
        for (int i = 1; i <= 3; i++)
        {
            byte[] next = DocInfo.AppendAnnotations(DocInfo.Strict(current), 1);
            AssertCleanAndPrefixed(current, next);
            current = next;
            var doc = DocInfo.Strict(current);
            Assert.Equal(i + 1, doc.Revisions.Count);
            Assert.Equal(i, DocInfo.AnnotCount(doc, 0));
        }
    }

    [Fact]
    public void NewSectionMatchesThePreviousSectionForm()
    {
        var table = DocInfo.Strict(DocInfo.AppendAnnotations(DocInfo.Strict(PdfBuilder.Classic().Bytes)));
        Assert.Equal(PdfXrefForm.Table, table.Latest.Form);
        string tableTail = Encoding.Latin1.GetString(table.Data.Span[(int)table.Revisions[1].EndOffset..]);
        Assert.Contains("\nxref\n", tableTail, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /XRef", tableTail, StringComparison.Ordinal);

        var stream = DocInfo.Strict(DocInfo.AppendAnnotations(DocInfo.Strict(PdfBuilder.XrefStream().Bytes)));
        Assert.Equal(PdfXrefForm.Stream, stream.Latest.Form);
        string streamTail = Encoding.Latin1.GetString(stream.Data.Span[(int)stream.Revisions[1].EndOffset..]);
        Assert.DoesNotContain("\nxref\n", streamTail, StringComparison.Ordinal);
        Assert.Contains("/Type /XRef", streamTail, StringComparison.Ordinal);

        // The xref stream lists itself, at the offset startxref points to, and the stream object is complete.
        Assert.True(stream.Latest.Entries.TryGetValue(stream.Size - 1, out var self));
        Assert.Equal(XrefEntryType.InUse, self.Type);
        Assert.Equal(stream.StartXref, self.Offset);
        Assert.Contains("endstream\nendobj\nstartxref\n", streamTail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void XrefStreamEncodingFollowsThePreviousStream(bool flate, bool predictor)
    {
        var baseBytes = PdfBuilder.XrefStream(flate: flate, predictor: predictor).Bytes;
        var updated = DocInfo.Strict(DocInfo.AppendAnnotations(DocInfo.Strict(baseBytes)));
        var dict = updated.Latest.Trailer;
        Assert.Equal("XRef", dict.GetName("Type"));
        Assert.Equal(flate, dict.GetName("Filter") == "FlateDecode");
        var parms = dict["DecodeParms"] as PdfDictionary;
        Assert.Equal(predictor, parms is not null);
        if (parms is not null)
        {
            Assert.Equal(12, parms.GetInteger("Predictor"));
            Assert.Equal(7, parms.GetInteger("Columns"));
        }

        Assert.Equal(["Type", "Size", "W", "Index", "Root", "Prev"], dict.Keys.Take(6).Select(k => k.Value));
    }

    [Fact]
    public void XrefStreamWithAnArrayFilterIsStillMatched()
    {
        // The same dictionary spelled "/Filter[/FlateDecode]/DecodeParms": a one-element array instead of a name.
        var built = PdfBuilder.XrefStream(flate: true, predictor: true);
        byte[] arrayForm = PdfBuilder.Patch(built.Bytes, "/Filter /FlateDecode /DecodeParms", "/Filter[/FlateDecode]/DecodeParms");
        var updated = DocInfo.Strict(DocInfo.AppendAnnotations(DocInfo.Strict(arrayForm)));
        Assert.Equal("FlateDecode", updated.Latest.Trailer.GetName("Filter"));
        Assert.IsType<PdfDictionary>(updated.Latest.Trailer["DecodeParms"]);

        // ...and as a /DecodeParms array.
        byte[] parmsArray = PdfBuilder.Patch(built.Bytes, "/DecodeParms << /Columns 6 /Predictor 12 >> ", "/DecodeParms[<< /Columns 6 /Predictor 12 >>]");
        Assert.True(StrictDocument.Open(parmsArray).IsSuccess);
        var viaArray = DocInfo.Strict(DocInfo.AppendAnnotations(DocInfo.Strict(parmsArray)));
        Assert.IsType<PdfDictionary>(viaArray.Latest.Trailer["DecodeParms"]);
    }
    [Fact]
    public void DocumentIdKeepsTheFirstElementAndChangesTheSecond()
    {
        foreach (var built in new[] { PdfBuilder.Classic(), PdfBuilder.XrefStream() })
        {
            var doc = DocInfo.Strict(built.Bytes);
            var oldId = Assert.IsType<PdfArray>(doc.Trailer["ID"]);

            byte[] fixedSecond = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
            var updated = DocInfo.Strict(DocInfo.AppendAnnotations(doc, options: new IncrementalOptions { NewIdSecondElement = fixedSecond }));
            var id = Assert.IsType<PdfArray>(updated.Trailer["ID"]);
            Assert.True(PdfObjects.DeepEquals(oldId[0], id[0]));
            Assert.Equal(fixedSecond, ((PdfString)id[1]).Value.ToArray());
            Assert.Equal(PdfStringFormat.Hex, ((PdfString)id[1]).Format);

            // Default: random, so two builds differ from each other and from the original second element.
            var a = (PdfArray)DocInfo.Strict(DocInfo.AppendAnnotations(doc, options: new IncrementalOptions())).Trailer["ID"]!;
            var b = (PdfArray)DocInfo.Strict(DocInfo.AppendAnnotations(doc, options: new IncrementalOptions())).Trailer["ID"]!;
            Assert.False(PdfObjects.DeepEquals(a[1], b[1]));
            Assert.False(PdfObjects.DeepEquals(a[1], oldId[1]));
            Assert.Equal(16, ((PdfString)a[1]).Length);
        }
    }

    [Fact]
    public void TrailerKeepsRootAndInfoAndDropsNothingElseItNeeds()
    {
        foreach (var built in new[] { PdfBuilder.Classic(), PdfBuilder.XrefStream(), PdfBuilder.Hybrid() })
        {
            var doc = DocInfo.Strict(built.Bytes);
            var updated = DocInfo.Strict(DocInfo.AppendAnnotations(doc));
            Assert.Equal(doc.Root, updated.Root);
            Assert.Equal(new PdfReference(7, 0), updated.Trailer["Info"]);
            Assert.Equal(doc.StartXref, updated.Latest.PrevOffset);
            Assert.Null(updated.Latest.HybridStreamOffset);
        }
    }

    [Fact]
    public void MissingIdIsNotInvented()
    {
        var objs = PdfBuilder.TwoPageObjects();
        var built = PdfBuilder.ClassicCustom(objs, (o, pos) => $"xref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{pos}\n%%EOF\n");
        var updated = DocInfo.Strict(DocInfo.AppendAnnotations(DocInfo.Strict(built.Bytes)));
        Assert.Null(updated.Trailer["ID"]);
        Assert.Null(updated.Trailer["Info"]);
    }

    [Theory]
    [InlineData("[(a)]")]
    [InlineData("[(a) 1]")]
    [InlineData("(a)")]
    public void MalformedIdIsRefused(string id)
    {
        var objs = PdfBuilder.TwoPageObjects();
        var built = PdfBuilder.ClassicCustom(objs, (o, pos) => $"xref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n<< /Size 8 /Root 1 0 R /ID {id} >>\nstartxref\n{pos}\n%%EOF\n");
        var doc = DocInfo.Strict(built.Bytes);
        var update = new IncrementalUpdate(doc);
        Assert.True(update.Add(new PdfDictionary([])).IsSuccess);
        Assert.Equal(PdfErrorCode.InvalidTrailer, update.Build().Error.Code);
    }

    // Found by the append fuzz target (tests/fuzz/regressions/pdfwriter/tiny-real-append-selfcheck.pdf): numbers with more than 15
    // fractional digits were cut when written, so the writer's own read-back check refused to save the object.
    [Fact]
    public void ObjectsHoldingLongRealsAreSavedWithoutChangingThem()
    {
        var doc = DocInfo.Strict(PdfBuilder.Classic().Bytes);
        var update = new IncrementalUpdate(doc);
        var reals = new PdfArray([new PdfReal(0), new PdfReal(595.2755905511812), new PdfReal(841.8897637795277), new PdfReal(0.1 + 0.2), new PdfReal(1e-19), new PdfReal(-8.5e-7)]);
        var added = update.Add(new PdfDictionary([new(new PdfName("Box"), reals)])).Value;

        var built = update.Build();

        Assert.True(built.IsSuccess, built.IsSuccess ? "" : built.Error.ToString());
        var after = DocInfo.Strict(built.Value);
        Assert.True(PdfObjects.DeepEquals(after.GetObject(added.Number).Value, new PdfDictionary([new(new PdfName("Box"), reals)])));
    }

    [Fact]
    public void StreamObjectsGetTheirLength()
    {
        var doc = DocInfo.Strict(PdfBuilder.Classic().Bytes);
        var update = new IncrementalUpdate(doc);
        byte[] content = Encoding.Latin1.GetBytes("q 1 0 0 rg 0 0 50 50 re f Q");
        var dict = new PdfDictionary(
        [
            new(new PdfName("Type"), new PdfName("XObject")),
            new(new PdfName("Subtype"), new PdfName("Form")),
            new(new PdfName("Length"), new PdfInteger(9999)), // wrong on purpose
            new(new PdfName("BBox"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(50), new PdfInteger(50)])),
        ]);
        var r = update.Add(new PdfStream(dict, content)).Value;
        byte[] updated = update.Build().Value;

        var after = DocInfo.Strict(updated);
        var stream = Assert.IsType<PdfStream>(after.GetObject(r.Number).Value);
        Assert.Equal(content.Length, stream.Dictionary.GetInteger("Length"));
        Assert.Equal(content, after.DecodeStream(stream).Value);
        Assert.Equal(["Type", "Subtype", "Length", "BBox"], stream.Dictionary.Keys.Select(k => k.Value)); // order preserved, /Length replaced in place
        Assert.Contains("\nstream\nq 1 0 0 rg", Encoding.Latin1.GetString(updated), StringComparison.Ordinal);
        Assert.True(StructureValidator.Validate(updated).IsValid);
    }

    [Fact]
    public void ReplacingACompressedObjectWritesAnOrdinaryOneAndLeavesTheOldView()
    {
        foreach (var built in new[] { PdfBuilder.XrefStream(), PdfBuilder.Hybrid() })
        {
            var doc = DocInfo.Strict(built.Bytes);
            Assert.Equal(XrefEntryType.Compressed, doc.Latest.Entries[6].Type);
            var update = new IncrementalUpdate(doc);
            var replaced = update.Replace(6, new PdfDictionary([new(new PdfName("BaseFont"), new PdfName("Courier"))])).Value;
            Assert.Equal(new PdfReference(6, 0), replaced);
            byte[] updated = update.Build().Value;

            var after = DocInfo.Strict(updated);
            Assert.Equal(XrefEntryType.InUse, after.Latest.Entries[6].Type);
            Assert.Equal("Courier", ((PdfDictionary)after.GetObject(6).Value).GetName("BaseFont"));
            Assert.Equal("Helvetica", ((PdfDictionary)after.Revisions[1].GetObject(6).Value).GetName("BaseFont"));
            Assert.Equal("Helvetica", ((PdfDictionary)after.Revisions[1].GetObject(6).Value).GetName("BaseFont"));
            Assert.True(StructureValidator.Validate(updated, options: Pedantic).IsValid);
        }
    }

    [Fact]
    public void GenerationOfAReplacedObjectIsKept()
    {
        var objs = PdfBuilder.TwoPageObjects();
        objs.Add(new(8, "<< /Marker 1 >>", Gen: 3));
        var doc = DocInfo.Strict(PdfBuilder.Classic(objs).Bytes);
        var update = new IncrementalUpdate(doc);
        Assert.Equal(new PdfReference(8, 3), update.Replace(8, new PdfDictionary([new(new PdfName("Marker"), new PdfInteger(2))])).Value);
        byte[] updated = update.Build().Value;
        Assert.Contains("\n8 3 obj\n", Encoding.Latin1.GetString(updated), StringComparison.Ordinal);
        var after = DocInfo.Strict(updated);
        Assert.Equal(3, after.Latest.Entries[8].Generation);
        Assert.Equal(2, ((PdfDictionary)after.GetObject(8).Value).GetInteger("Marker"));
    }

    [Fact]
    public void NewObjectsMayReferenceEachOtherAndExistingOnes()
    {
        var doc = DocInfo.Strict(PdfBuilder.Classic().Bytes);
        var update = new IncrementalUpdate(doc);
        Assert.Equal(8, update.NextObjectNumber);
        var a = update.Add(PdfNull.Instance).Value;
        var b = update.Add(new PdfArray([a, new PdfReference(3, 0)])).Value;
        Assert.Equal(new PdfReference(9, 0), b);
        Assert.Equal(10, update.NextObjectNumber);
        Assert.Equal([8, 9], update.ChangedObjectNumbers.Order());

        // Replacing an object this update added keeps its number.
        Assert.Equal(b, update.Replace(9, new PdfArray([a])).Value);
        var built = update.Build().Value;
        var after = DocInfo.Strict(built);
        Assert.Single((PdfArray)after.GetObject(9).Value);
        Assert.Equal(10, after.Size);
    }

    [Fact]
    public void UpdateOfAFileWithoutATrailingNewlineStartsOnAFreshLine()
    {
        byte[] noEol = PdfBuilder.Classic().Bytes[..^1];
        Assert.Equal((byte)'F', noEol[^1]);
        var doc = DocInfo.Strict(noEol);
        byte[] updated = DocInfo.AppendAnnotations(doc);
        Assert.True(updated.AsSpan(0, noEol.Length).SequenceEqual(noEol));
        Assert.Equal((byte)'\n', updated[noEol.Length]);
        Assert.True(StructureValidator.Validate(updated, options: Pedantic).IsValid);
    }

    [Fact]
    public void AnnotsArrayShapes()
    {
        // No /Annots at all.
        var objs = PdfBuilder.TwoPageObjects();
        objs[2] = new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        byte[] a = DocInfo.AppendAnnotations(DocInfo.Strict(PdfBuilder.Classic(objs).Bytes));
        Assert.Equal(1, DocInfo.AnnotCount(DocInfo.Strict(a), 0));

        // An indirect /Annots array: the array object is replaced, the page is left alone.
        objs[2] = new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots 8 0 R >>");
        objs.Add(new(8, "[]"));
        byte[] b = DocInfo.AppendAnnotations(DocInfo.Strict(PdfBuilder.Classic(objs).Bytes), 2);
        var after = DocInfo.Strict(b);
        Assert.Equal(2, DocInfo.AnnotCount(after, 0));
        Assert.Contains(8, after.Latest.Entries.Keys);
        Assert.DoesNotContain(3, after.Latest.Entries.Keys);
        Assert.True(StructureValidator.Validate(b, options: Pedantic).IsValid);
    }

    // ---- refusals ------------------------------------------------------------------------------------------------

    [Fact]
    public void EmptyUpdateIsRefused()
    {
        var update = new IncrementalUpdate(DocInfo.Strict(PdfBuilder.Classic().Bytes));
        Assert.Equal(PdfErrorCode.InvalidUpdate, update.Build().Error.Code);
    }

    [Fact]
    public void ReplaceRejectsObjectsThatAreNotInUse()
    {
        var doc = DocInfo.Strict(PdfBuilder.XrefStream().Bytes);
        var update = new IncrementalUpdate(doc);
        var obj = new PdfDictionary([]);
        Assert.Equal(PdfErrorCode.InvalidUpdate, update.Replace(0, obj).Error.Code);
        Assert.Equal(PdfErrorCode.InvalidUpdate, update.Replace(-4, obj).Error.Code);
        Assert.Equal(PdfErrorCode.InvalidUpdate, update.Replace(500, obj).Error.Code);
        Assert.Equal(PdfErrorCode.InvalidUpdate, update.Replace(8, obj).Error.Code); // the object stream
        Assert.Equal(PdfErrorCode.InvalidUpdate, update.Replace(9, obj).Error.Code); // the xref stream
        Assert.Empty(update.ChangedObjectNumbers);
    }

    [Fact]
    public void ReplaceSurfacesAnUnreadableExistingObject()
    {
        var built = PdfBuilder.Classic();
        var doc = DocInfo.Strict(PdfBuilder.Patch(built.Bytes, "\n4 0 obj", "\n9 0 obj"));
        var update = new IncrementalUpdate(doc);
        Assert.Equal(PdfErrorCode.ObjectHeaderMismatch, update.Replace(4, PdfNull.Instance).Error.Code);
    }

    [Fact]
    public void StructuralStreamsCannotBeSupplied()
    {
        var update = new IncrementalUpdate(DocInfo.Strict(PdfBuilder.Classic().Bytes));
        var xref = new PdfStream(new PdfDictionary([new(new PdfName("Type"), new PdfName("XRef"))]), new byte[] { 1 });
        var objstm = new PdfStream(new PdfDictionary([new(new PdfName("Type"), new PdfName("ObjStm"))]), new byte[] { 1 });
        Assert.Equal(PdfErrorCode.InvalidUpdate, update.Add(xref).Error.Code);
        Assert.Equal(PdfErrorCode.InvalidUpdate, update.Add(objstm).Error.Code);
        Assert.Equal(PdfErrorCode.InvalidUpdate, update.Replace(3, xref).Error.Code);
        Assert.Throws<ArgumentNullException>(() => update.Add(null!));
        Assert.Throws<ArgumentNullException>(() => update.Replace(3, null!));
        Assert.Throws<ArgumentNullException>(() => new IncrementalUpdate(null!));
    }

    [Fact]
    public void DanglingAndMismatchedReferencesInNewObjectsFailTheSelfCheck()
    {
        var doc = DocInfo.Strict(PdfBuilder.Classic().Bytes);

        var update = new IncrementalUpdate(doc);
        update.Add(new PdfArray([new PdfReference(999, 0)]));
        var error = update.Build().Error;
        Assert.Equal(PdfErrorCode.InvalidUpdate, error.Code);
        Assert.Contains("999 0 R", error.Message, StringComparison.Ordinal);

        var wrongGen = new IncrementalUpdate(doc);
        wrongGen.Add(new PdfArray([new PdfReference(3, 5)]));
        Assert.Equal(PdfErrorCode.InvalidUpdate, wrongGen.Build().Error.Code);

        // With the self-check off the output is written, and the validator (not the writer) flags the dangling reference.
        var unchecked_ = new IncrementalUpdate(doc);
        unchecked_.Add(new PdfArray([new PdfReference(999, 0)]));
        byte[] output = unchecked_.Build(new IncrementalOptions { VerifyOutput = false }).Value;
        Assert.False(StructureValidator.Validate(output, options: Pedantic).IsValid);
        Assert.True(StructureValidator.Validate(output).IsValid); // a warning by default
    }

    [Fact]
    public void LimitsApplyToTheOutput()
    {
        var bytes = PdfBuilder.Classic().Bytes;

        var tooDeep = new IncrementalUpdate(DocInfo.Strict(bytes));
        PdfObject deep = PdfNull.Instance;
        for (int i = 0; i < 100; i++)
            deep = new PdfArray([deep]);
        tooDeep.Add(deep);
        Assert.Equal(PdfLimit.NestingDepth, tooDeep.Build().Error.Limit);

        var smallDoc = DocInfo.Strict(bytes, new ParserLimits { MaxFileSize = bytes.Length + 10 });
        var big = new IncrementalUpdate(smallDoc);
        big.Add(PdfString.FromAscii(new string('x', 200)));
        Assert.Equal(PdfLimit.OutputSize, big.Build().Error.Limit);

        var full = new IncrementalUpdate(DocInfo.Strict(bytes, new ParserLimits { MaxFileSize = bytes.Length }));
        full.Add(PdfNull.Instance);
        Assert.Equal(PdfLimit.OutputSize, full.Build().Error.Limit);

        var numbers = new IncrementalUpdate(DocInfo.Strict(bytes, new ParserLimits { MaxObjectNumber = 8 }));
        Assert.Equal(PdfLimit.ObjectNumber, numbers.Add(PdfNull.Instance).Error.Limit);
    }

    [Fact]
    public void OutputThatTheSelfCheckCannotReopenIsAnError()
    {
        // A tiny parser limit that the base satisfies but the grown output does not: the self-check re-parses with the same limits.
        var bytes = PdfBuilder.Classic().Bytes;
        var doc = DocInfo.Strict(bytes, new ParserLimits { MaxObjectCount = 9 });
        var update = new IncrementalUpdate(doc);
        update.Add(PdfNull.Instance);
        update.Add(PdfNull.Instance);
        var error = update.Build().Error;
        Assert.Equal(PdfErrorCode.InvalidUpdate, error.Code);
        Assert.Contains("does not reparse", error.Message, StringComparison.Ordinal);
    }
}
