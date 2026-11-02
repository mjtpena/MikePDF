using System.Text;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

public sealed class DocumentTests
{
    public static TheoryData<string> Layouts() =>
    [
        "classic",
        "xref-stream objstm flate+predictor",
        "xref-stream objstm flate",
        "xref-stream objstm plain",
        "xref-stream no-objstm",
        "hybrid",
    ];

    internal static PdfBuilder.Built Build(string layout) => layout switch
    {
        "classic" => PdfBuilder.Classic(),
        "xref-stream objstm flate+predictor" => PdfBuilder.XrefStream(),
        "xref-stream objstm flate" => PdfBuilder.XrefStream(predictor: false),
        "xref-stream objstm plain" => PdfBuilder.XrefStream(flate: false),
        "xref-stream no-objstm" => PdfBuilder.XrefStream(objectStreams: false),
        "hybrid" => PdfBuilder.Hybrid(),
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    // ---- opening each layout -------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Layouts))]
    public void OpensEveryLayoutAndReadsTheObjectGraph(string layout)
    {
        var built = Build(layout);
        var doc = DocInfo.Strict(built.Bytes);

        Assert.Single(doc.Revisions);
        Assert.Equal(built.XrefOffset, doc.StartXref);
        Assert.Equal(new PdfReference(1, 0), doc.Root);
        Assert.Equal(2, DocInfo.PageCount(doc));
        Assert.Equal(0, DocInfo.AnnotCount(doc, 0));
        Assert.Empty(doc.Issues);

        // Objects 6 and 7 live in an object stream for the compressed layouts and in the file body otherwise.
        Assert.Equal("Helvetica", Assert.IsType<PdfDictionary>(doc.GetObject(6).Value).GetName("BaseFont"));
        Assert.IsType<PdfString>(Assert.IsType<PdfDictionary>(doc.GetObject(7).Value)["Title"]);

        var stream = Assert.IsType<PdfStream>(doc.GetObject(5).Value);
        Assert.Equal("BT /F1 12 Tf 10 100 Td (Hello) Tj ET", Encoding.Latin1.GetString(doc.DecodeStream(stream).Value));

        Assert.IsType<PdfNull>(doc.GetObject(0).Value);
        Assert.IsType<PdfNull>(doc.GetObject(500).Value);
    }

    [Fact]
    public void ClassicFileEntries()
    {
        var built = PdfBuilder.Classic();
        var doc = DocInfo.Strict(built.Bytes);
        var rev = doc.Latest;
        Assert.Equal(PdfXrefForm.Table, rev.Form);
        Assert.Equal(8, doc.Size);
        Assert.Equal(XrefEntryType.Free, rev.Entries[0].Type);
        Assert.Equal(built.Offsets[3], rev.Entries[3].Offset);
        Assert.Equal(0, rev.Entries[3].Generation);
        Assert.Null(rev.PrevOffset);
        Assert.Null(rev.HybridStreamOffset);
        Assert.Equal(built.Bytes.Length - 1, rev.EndOffset); // one past "%%EOF", before the final newline
        Assert.Equal(built.XrefOffset, rev.DeclaredStartXref);
        Assert.Equal(built.XrefOffset, rev.XrefOffset);
        Assert.Equal(built.XrefOffset, rev.ExtentStart);
        Assert.True(rev.ExtentEnd > rev.ExtentStart);
        Assert.Empty(rev.HybridObjects);
        Assert.Same(doc, rev.Document);
        Assert.Equal(8, doc.XrefEntryCount);
        Assert.Equal(0, doc.DecompressedBytes);
        Assert.Equal(doc.Trailer, rev.Trailer);
        Assert.False(doc.IssuesTruncated);
    }

    [Fact]
    public void XrefStreamEntriesIncludeCompressedObjects()
    {
        var doc = DocInfo.Strict(PdfBuilder.XrefStream().Bytes);
        var rev = doc.Latest;
        Assert.Equal(PdfXrefForm.Stream, rev.Form);
        Assert.Equal(XrefEntryType.Compressed, rev.Entries[1].Type);
        Assert.Equal(0, rev.Entries[1].Generation);
        Assert.Equal(8, rev.Entries[1].ObjectStreamNumber);
        Assert.Equal(XrefEntryType.InUse, rev.Entries[5].Type);
        Assert.True(doc.DecompressedBytes > 0);
        Assert.Equal(PdfXrefForm.Stream, rev.Form);
    }

    [Fact]
    public void HybridSectionMergesTheXrefStream()
    {
        var built = PdfBuilder.Hybrid();
        var doc = DocInfo.Strict(built.Bytes);
        var rev = doc.Latest;
        Assert.Equal(PdfXrefForm.Table, rev.Form);
        Assert.NotNull(rev.HybridStreamOffset);
        Assert.Equal([6, 7], rev.HybridObjects.Order());
        Assert.Equal(XrefEntryType.Compressed, rev.Entries[6].Type);
        Assert.Equal(1, rev.Entries[7].IndexInObjectStream);
        Assert.Equal("Helvetica", Assert.IsType<PdfDictionary>(doc.GetObject(6).Value).GetName("BaseFont"));
        Assert.Equal(2, DocInfo.PageCount(doc));
    }

    [Fact]
    public void EffectiveEntriesAreEnumerable()
    {
        var doc = DocInfo.Strict(PdfBuilder.Classic().Bytes);
        Assert.Equal(8, doc.Latest.EnumerateEffectiveEntries().Count());
        Assert.False(doc.Latest.TryGetEntry(99, out _));
    }

    [Fact]
    public void ResolveFollowsChainsAndRejectsLongOnes()
    {
        var objs = PdfBuilder.TwoPageObjects();
        objs.Add(new(8, "9 0 R"));
        objs.Add(new(9, "10 0 R"));
        objs.Add(new(10, "(done)"));
        var doc = DocInfo.Strict(PdfBuilder.Classic(objs).Bytes);
        Assert.Equal("done", Encoding.Latin1.GetString(Assert.IsType<PdfString>(doc.Resolve(new PdfReference(8, 0)).Value).Value));
        Assert.IsType<PdfInteger>(doc.Resolve(new PdfInteger(5)).Value);
        Assert.IsType<PdfNull>(doc.Resolve(null).Value);

        // 8 -> 9 -> 8 forms a loop; the bounded chain turns it into an error instead of a hang.
        var loop = PdfBuilder.TwoPageObjects();
        loop.Add(new(8, "9 0 R"));
        loop.Add(new(9, "8 0 R"));
        var loopDoc = DocInfo.Strict(PdfBuilder.Classic(loop).Bytes);
        Assert.Equal(PdfErrorCode.InvalidReference, loopDoc.Resolve(new PdfReference(8, 0)).Error.Code);
    }

    [Fact]
    public void IndirectLengthIsResolved()
    {
        // PdfBuilder always writes its own direct /Length, so the stream with an indirect one is written by hand.
        PdfBuilder.Built Make(string lengthObjectBody)
        {
            var objs = PdfBuilder.TwoPageObjects().Where(o => o.Num != 5).ToList();
            objs.Add(new(8, lengthObjectBody));
            return PdfBuilder.ClassicCustom(objs, (offsets, pos) =>
            {
                const string obj5 = "5 0 obj\n<< /Length 8 0 R >>\nstream\n0123456789\nendstream\nendobj\n";
                var all = new Dictionary<int, int>(offsets) { [5] = pos };
                int xref = pos + obj5.Length;
                return obj5 + $"xref\n0 9\n{PdfBuilder.TableBody(all, 9)}trailer\n<< /Size 9 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n";
            });
        }

        var doc = DocInfo.Strict(Make("10").Bytes);
        Assert.Equal(10, Assert.IsType<PdfStream>(doc.GetObject(5).Value).RawData.Length);

        // The resolved length is checked against "endstream" like a direct one.
        var wrong = DocInfo.Strict(Make("9").Bytes);
        Assert.Equal(PdfErrorCode.StreamLengthMismatch, wrong.GetObject(5).Error.Code);
        var notANumber = DocInfo.Strict(Make("(ten)").Bytes);
        Assert.Equal(PdfErrorCode.StreamLengthMismatch, notANumber.GetObject(5).Error.Code);
    }

    // ---- structural failures -------------------------------------------------------------------------------------

    private static PdfError Fail(byte[] bytes, ParserLimits? limits = null) => DocInfo.StrictError(bytes, limits);

    [Fact]
    public void EmptyAndTinyInputs()
    {
        Assert.Equal(PdfErrorCode.InvalidHeader, Fail([]).Code);
        Assert.Equal(PdfErrorCode.InvalidHeader, Fail("%PDF"u8.ToArray()).Code);
        Assert.Equal(PdfErrorCode.MissingStartXref, DocInfo.StrictError("%PDF-1.7\n"u8.ToArray()).Code);
        Assert.Equal(PdfErrorCode.MissingStartXref, LenientDocument.Open("%PDF-1.7\n"u8.ToArray()).Error.Code);
    }

    [Theory]
    [InlineData("%PDX-1.7")]
    [InlineData("%PDF-3.0")]
    [InlineData("%PDF-1,7")]
    public void MalformedHeaderIsRejected(string header)
    {
        var bytes = PdfBuilder.Patch(PdfBuilder.Classic().Bytes, "%PDF-1.7", header);
        Assert.Equal(PdfErrorCode.InvalidHeader, Fail(bytes).Code);
        Assert.True(LenientDocument.Open(bytes).IsSuccess);
        Assert.Contains(DocInfo.Lenient(bytes).Issues, i => i.Code == PdfErrorCode.InvalidHeader);
    }

    [Fact]
    public void HeaderNotAtOffsetZeroIsRejected()
    {
        // PDFium would shift every offset by the junk length; we never do, so a prefix is a hard failure.
        var junk = Encoding.Latin1.GetBytes("junk\n");
        var bytes = junk.Concat(PdfBuilder.Classic().Bytes).ToArray();
        var error = Fail(bytes);
        Assert.Equal(PdfErrorCode.InvalidHeader, error.Code);
        Assert.Equal(5, error.Offset);
    }

    [Fact]
    public void HeaderMustBeFollowedByWhitespace()
    {
        var bytes = PdfBuilder.Patch(PdfBuilder.Classic().Bytes, "%PDF-1.7\n", "%PDF-1.7X");
        Assert.Equal(PdfErrorCode.InvalidHeader, Fail(bytes).Code);
    }

    [Fact]
    public void MissingOrBrokenStartxref()
    {
        var good = PdfBuilder.Classic().Bytes;
        Assert.Equal(PdfErrorCode.MissingStartXref, Fail(PdfBuilder.Patch(good, "startxref", "startxreX")).Code);

        int at = PdfBuilder.LastIndexOf(good, "startxref") + "startxref\n".Length;
        var noOffset = (byte[])good.Clone();
        for (int i = at; i < good.Length; i++)
            noOffset[i] = (byte)'x';
        Assert.Equal(PdfErrorCode.InvalidStartXref, Fail(noOffset).Code);

        string digits = PdfBuilder.Classic().XrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(PdfErrorCode.InvalidStartXref, Fail(PdfBuilder.Patch(good, "startxref\n" + digits, "startxref\n" + new string('9', digits.Length))).Code);
        Assert.Equal(PdfErrorCode.InvalidStartXref, Fail(PdfBuilder.Patch(good, "startxref\n" + digits, "startxref\n" + new string('0', digits.Length))).Code);
    }

    [Fact]
    public void StartxrefPointingAtTheWrongPlace()
    {
        var built = PdfBuilder.Classic();
        string digits = built.XrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string off = (built.XrefOffset + 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(digits.Length, off.Length);
        var bytes = PdfBuilder.Patch(built.Bytes, "startxref\n" + digits, "startxref\n" + off);
        // Five bytes into "xref": neither the keyword nor an object header.
        Assert.False(StrictDocument.Open(bytes).IsSuccess);
        Assert.False(LenientDocument.Open(bytes).IsSuccess);
    }

    [Fact]
    public void StartxrefPointingAtAnObjectInsteadOfAnXref()
    {
        var built = PdfBuilder.Classic();
        string digits = built.XrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string off = built.Offsets[3].ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits.Length, '0');
        var bytes = PdfBuilder.Patch(built.Bytes, "startxref\n" + digits, "startxref\n" + off);
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(bytes).Code);
    }

    [Fact]
    public void MissingEofAndTrailingData()
    {
        var good = PdfBuilder.Classic().Bytes;
        Assert.Equal(PdfErrorCode.MissingEof, Fail(PdfBuilder.Patch(good, "%%EOF", "%%EOX")).Code);

        var trailing = good.Concat("garbage"u8.ToArray()).ToArray();
        Assert.Equal(PdfErrorCode.TrailingData, Fail(trailing).Code);
        Assert.True(LenientDocument.Open(trailing).IsSuccess);
        Assert.Contains(DocInfo.Lenient(trailing).Issues, i => i.Code == PdfErrorCode.TrailingData);

        // Whitespace after the marker is fine.
        Assert.True(StrictDocument.Open(good.Concat(" \r\n\0\t"u8.ToArray()).ToArray()).IsSuccess);
    }

    [Fact]
    public void XrefTableEntryMustBeExactly20Bytes()
    {
        var built = PdfBuilder.Classic();
        int entry3 = built.Find("xref\n0 8\n") + "xref\n0 8\n".Length + (3 * 20);
        // " \n" -> "\n\n": still 20 bytes, but not one of the three permitted two-byte terminators.
        var bad = (byte[])built.Bytes.Clone();
        bad[entry3 + 18] = (byte)'\n';
        Assert.Equal(PdfErrorCode.InvalidXrefEntry, Fail(bad).Code);
        Assert.True(LenientDocument.Open(bad).IsSuccess);

        // Dropping a byte shifts every later record: a token-based reader would still cope, PDFium's fixed-width one would not.
        var shifted = built.Bytes.Take(entry3 + 18).Concat(built.Bytes.Skip(entry3 + 19)).ToArray();
        Assert.False(StrictDocument.Open(shifted).IsSuccess);
    }

    [Theory]
    [InlineData("\r\n")] // CR LF
    [InlineData(" \r")] // SP CR
    [InlineData(" \n")] // SP LF
    public void PermittedEntryTerminators(string terminator)
    {
        var built = PdfBuilder.Classic();
        int entry1 = built.Find("xref\n0 8\n") + "xref\n0 8\n".Length + 20;
        var bytes = (byte[])built.Bytes.Clone();
        Encoding.Latin1.GetBytes(terminator).CopyTo(bytes, entry1 + 18);
        Assert.True(StrictDocument.Open(bytes).IsSuccess);
    }

    [Fact]
    public void WrongXrefOffsetForAnObject()
    {
        var built = PdfBuilder.Classic();
        int at = built.Find("xref\n0 8\n") + "xref\n0 8\n".Length + (3 * 20);
        // Entry 3 now points one object too early: it lands on "2 0 obj".
        string wrong = built.Offsets[2].ToString("D10", System.Globalization.CultureInfo.InvariantCulture);
        var bytes = (byte[])built.Bytes.Clone();
        Encoding.Latin1.GetBytes(wrong).CopyTo(bytes, at);

        var doc = DocInfo.Strict(bytes); // entries are checked when objects are read
        var error = doc.GetObject(3).Error;
        Assert.Equal(PdfErrorCode.ObjectHeaderMismatch, error.Code);
        Assert.Contains("points at object 2", error.Message, StringComparison.Ordinal);

        // The same file read leniently records the problem and reads the null object.
        var lenient = DocInfo.Lenient(bytes);
        Assert.IsType<PdfNull>(lenient.GetObject(3).Value);
        Assert.Contains(lenient.Issues, i => i.Code == PdfErrorCode.ObjectHeaderMismatch);
    }

    [Fact]
    public void OffsetOutsideTheFileOrBeforeTheHeader()
    {
        var built = PdfBuilder.Classic();
        int at = built.Find("xref\n0 8\n") + "xref\n0 8\n".Length + (3 * 20);
        var beyond = (byte[])built.Bytes.Clone();
        Encoding.Latin1.GetBytes("0099999999").CopyTo(beyond, at);
        Assert.Equal(PdfErrorCode.XrefOffsetOutOfRange, Fail(beyond).Code);
        var before = (byte[])built.Bytes.Clone();
        Encoding.Latin1.GetBytes("0000000003").CopyTo(before, at);
        Assert.Equal(PdfErrorCode.XrefOffsetOutOfRange, Fail(before).Code);

        // Lenient mode keeps the entry; reading the object then fails with a typed error.
        var doc = DocInfo.Lenient(beyond);
        Assert.Equal(PdfErrorCode.XrefOffsetOutOfRange, doc.GetObject(3).Error.Code);
    }

    [Fact]
    public void ObjectNumberAndGenerationMismatch()
    {
        var built = PdfBuilder.Classic();
        var renumbered = PdfBuilder.Patch(built.Bytes, "\n4 0 obj", "\n9 0 obj");
        var doc = DocInfo.Strict(renumbered);
        Assert.Equal(PdfErrorCode.ObjectHeaderMismatch, doc.GetObject(4).Error.Code);

        var regenerated = PdfBuilder.Patch(built.Bytes, "\n4 0 obj", "\n4 1 obj");
        Assert.Equal(PdfErrorCode.ObjectHeaderMismatch, DocInfo.Strict(regenerated).GetObject(4).Error.Code);

        // The catalog is checked while opening, so a bad catalog header makes the whole open fail.
        var badRoot = PdfBuilder.Patch(built.Bytes, "1 0 obj", "8 0 obj");
        Assert.Equal(PdfErrorCode.ObjectHeaderMismatch, Fail(badRoot).Code);
    }

    [Fact]
    public void ReferenceGenerationMustMatchTheEntry()
    {
        var objs = PdfBuilder.TwoPageObjects();
        objs[0] = new(1, "<< /Type /Catalog /Pages 2 3 R >>");
        var doc = DocInfo.Strict(PdfBuilder.Classic(objs).Bytes);
        var catalog = Assert.IsType<PdfDictionary>(doc.GetObject(1).Value);
        Assert.Equal(PdfErrorCode.InvalidReference, doc.Resolve(catalog["Pages"]).Error.Code);
    }

    [Fact]
    public void SizeChecks()
    {
        var good = PdfBuilder.Classic().Bytes;
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(PdfBuilder.Patch(good, "/Size 8", "/Size 7")).Code);
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(PdfBuilder.Patch(good, "/Size 8", "/Sizx 8")).Code);
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(PdfBuilder.Patch(good, "/Size 8", "/Size 0")).Code);
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(PdfBuilder.Patch(good, "/Root 1 0 R", "/Root 1    ")).Code);
    }

    [Fact]
    public void ObjectZeroMustBeFree()
    {
        var built = PdfBuilder.Classic();
        var bytes = PdfBuilder.Patch(built.Bytes, "0000000000 65535 f ", "0000000009 00000 n ");
        Assert.Equal(PdfErrorCode.InvalidXrefEntry, Fail(bytes).Code);
    }

    [Fact]
    public void GenerationAbove65535InTheTable()
    {
        var bytes = PdfBuilder.Patch(PdfBuilder.Classic().Bytes, "00000 n", "99999 n", 1);
        Assert.Equal(PdfErrorCode.InvalidXrefEntry, Fail(bytes).Code);
    }

    [Fact]
    public void SubsectionProblems()
    {
        var objs = PdfBuilder.TwoPageObjects();

        PdfBuilder.Built Custom(Func<IReadOnlyDictionary<int, int>, string> body) =>
            PdfBuilder.ClassicCustom(objs, (offsets, pos) => $"xref\n{body(offsets)}trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{pos}\n%%EOF\n");

        // A well-formed split into two subsections is accepted.
        var ok = Custom(o => $"0 1\n0000000000 65535 f \n1 7\n{PdfBuilder.TableBody(o, 8)[20..]}");
        Assert.True(StrictDocument.Open(ok.Bytes).IsSuccess);

        // The same object listed twice.
        var dup = Custom(o => $"0 8\n{PdfBuilder.TableBody(o, 8)}3 1\n{o[3]:D10} 00000 n \n");
        Assert.Equal(PdfErrorCode.DuplicateXrefEntry, Fail(dup.Bytes).Code);
        var lenientDup = DocInfo.Lenient(dup.Bytes);
        Assert.Contains(lenientDup.Issues, i => i.Code == PdfErrorCode.DuplicateXrefEntry);
        Assert.IsType<PdfDictionary>(lenientDup.GetObject(3).Value);

        // A count that promises more entries than the file holds.
        var huge = Custom(o => "0 999999\n" + PdfBuilder.TableBody(o, 8));
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(huge.Bytes).Code);

        // Garbage where a subsection header belongs.
        var junk = Custom(o => $"0 8\n{PdfBuilder.TableBody(o, 8)}hello\n");
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(junk.Bytes).Code);
        var noWs = Custom(o => "0,8\n" + PdfBuilder.TableBody(o, 8));
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(noWs.Bytes).Code);
        var longNumber = Custom(o => "00000000000 8\n" + PdfBuilder.TableBody(o, 8));
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(longNumber.Bytes).Code);

        // An empty subsection is harmless.
        var empty = Custom(o => $"0 0\n0 8\n{PdfBuilder.TableBody(o, 8)}");
        Assert.True(StrictDocument.Open(empty.Bytes).IsSuccess);
    }

    [Fact]
    public void LenientReadsWhitespaceSeparatedEntries()
    {
        var objs = PdfBuilder.TwoPageObjects();
        var built = PdfBuilder.ClassicCustom(objs, (o, pos) =>
        {
            var sb = new StringBuilder("xref\n0 8\n0 65535 f\n");
            for (int n = 1; n < 8; n++)
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{o[n]} 0 n\n"); // 19-byte "entries" with short numbers
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{pos}\n%%EOF\n");
            return sb.ToString();
        });
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(built.Bytes).Code); // 8 entries need 160 bytes; the short entries are 19 or fewer
        var doc = DocInfo.Lenient(built.Bytes);
        Assert.Equal(8, doc.Issues.Count(i => i.Code == PdfErrorCode.InvalidXrefEntry));
        Assert.IsType<PdfDictionary>(doc.GetObject(3).Value);

        // A bad entry type letter is fatal even leniently.
        var bad = PdfBuilder.ClassicCustom(objs, (o, pos) => $"xref\n0 2\n0 65535 f\n9 0 q\ntrailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{pos}\n%%EOF\n");
        Assert.Equal(PdfErrorCode.InvalidXrefEntry, LenientDocument.Open(bad.Bytes).Error.Code);
    }

    [Fact]
    public void TrailerProblems()
    {
        var objs = PdfBuilder.TwoPageObjects();
        PdfBuilder.Built Custom(string trailer, string eof = "%%EOF\n") =>
            PdfBuilder.ClassicCustom(objs, (o, pos) => $"xref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n{trailer}\nstartxref\n{pos}\n{eof}");

        Assert.True(StrictDocument.Open(Custom("<< /Size 8 /Root 1 0 R >>").Bytes).IsSuccess);
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(Custom("[1 2]").Bytes).Code);
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(Custom("<< /Size 8 >>").Bytes).Code);
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(Custom("<< /Size 8 /Root 1 0 R /Prev 1.5 >>").Bytes).Code);
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(Custom("<< /Size 8 /Root 1 0 R /Prev 0 >>").Bytes).Code);
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(Custom("<< /Size 8 /Root 1 0 R /XRefStm 99999 >>").Bytes).Code);
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(Custom("<< /Size 8 /Root (x) >>").Bytes).Code);

        // Junk between the trailer and the final startxref: the newest section is not followed directly by it.
        var junk = PdfBuilder.ClassicCustom(PdfBuilder.TwoPageObjects(), (o, pos) =>
            $"xref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{pos}\n%%EOF\n% pad\nstartxref\n{pos}\n%%EOF\n");
        Assert.False(StrictDocument.Open(junk.Bytes).IsSuccess);
    }

    [Fact]
    public void RootMustResolveToADictionary()
    {
        var objs = PdfBuilder.TwoPageObjects();
        objs[0] = new(1, "[1 2 3]");
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(PdfBuilder.Classic(objs).Bytes).Code);
        Assert.Contains(DocInfo.Lenient(PdfBuilder.Classic(objs).Bytes).Issues, i => i.Code == PdfErrorCode.InvalidTrailer);
    }

    [Fact]
    public void DuplicateKeysInAnyObjectAreRejected()
    {
        var objs = PdfBuilder.TwoPageObjects();
        objs[2] = new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /MediaBox [0 0 1 1] >>");
        var bytes = PdfBuilder.Classic(objs).Bytes;
        var doc = DocInfo.Strict(bytes); // page 3 is not read while opening
        Assert.Equal(PdfErrorCode.DuplicateKey, doc.GetObject(3).Error.Code);

        // The S8 "sneaky" shape: the dictionary a reader would see differs depending on which duplicate wins.
        var sneaky = PdfBuilder.TwoPageObjects();
        sneaky[0] = new(1, "<< /Type /Catalog /Pages 2 0 R /Pages 4 0 R >>");
        Assert.Equal(PdfErrorCode.DuplicateKey, Fail(PdfBuilder.Classic(sneaky).Bytes).Code);
        var lenient = DocInfo.Lenient(PdfBuilder.Classic(sneaky).Bytes);
        Assert.Contains(lenient.Issues, i => i.Code == PdfErrorCode.DuplicateKey);
    }

    [Fact]
    public void EncryptedDocumentsAreReportedAsUnsupported()
    {
        var table = PdfBuilder.Classic(trailerExtra: "/Encrypt << /Filter /Standard >>").Bytes;
        Assert.Equal(PdfErrorCode.UnsupportedEncryption, Fail(table).Code);
        Assert.Equal(PdfErrorCode.UnsupportedEncryption, Assert.IsType<PdfError>(LenientDocument.Open(table).Error).Code);

        var stream = PdfBuilder.XrefStream(xrefDictExtra: "/Encrypt << /Filter /Standard >>").Bytes;
        Assert.Equal(PdfErrorCode.UnsupportedEncryption, Fail(stream).Code);
    }

    // ---- chains --------------------------------------------------------------------------------------------------

    [Fact]
    public void PrevCycleOnTheSameSection()
    {
        var objs = PdfBuilder.TwoPageObjects();
        var built = PdfBuilder.ClassicCustom(objs, (o, pos) =>
            $"xref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n<< /Size 8 /Root 1 0 R /Prev {pos:D10} >>\nstartxref\n{pos}\n%%EOF\n");
        var error = Fail(built.Bytes);
        Assert.Equal(PdfErrorCode.XrefCycle, error.Code);

        // Lenient mode stops the walk and records it.
        var doc = DocInfo.Lenient(built.Bytes);
        Assert.Single(doc.Revisions);
        Assert.Contains(doc.Issues, i => i.Code == PdfErrorCode.XrefCycle);
    }

    [Fact]
    public void PrevCycleAcrossTwoSections()
    {
        var objs = PdfBuilder.TwoPageObjects();
        // Section A (older position) and B (newer) point at each other. Ten-digit /Prev values keep the text width fixed.
        var built = PdfBuilder.ClassicCustom(objs, (o, pos) =>
        {
            string Section(int prev, int startxref) =>
                $"xref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n<< /Size 8 /Root 1 0 R /Prev {prev:D10} >>\nstartxref\n{startxref:D10}\n%%EOF\n";
            int lenA = Section(0, 0).Length;
            return Section(pos + lenA, pos) + Section(pos, pos + lenA); // A -> B -> A
        });
        Assert.Equal(PdfErrorCode.XrefCycle, Fail(built.Bytes).Code);
        Assert.Equal(2, DocInfo.Lenient(built.Bytes).Revisions.Count);
    }

    [Fact]
    public void PrevPointingOutsideTheFile()
    {
        var objs = PdfBuilder.TwoPageObjects();
        var built = PdfBuilder.ClassicCustom(objs, (o, pos) =>
            $"xref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n<< /Size 8 /Root 1 0 R /Prev 99999 >>\nstartxref\n{pos}\n%%EOF\n");
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(built.Bytes).Code);
    }

    [Fact]
    public void ChainLengthLimit()
    {
        var doc = DocInfo.Strict(PdfBuilder.Classic().Bytes);
        var two = DocInfo.AppendAnnotations(doc);
        var three = DocInfo.AppendAnnotations(DocInfo.Strict(two));
        Assert.Equal(3, DocInfo.Strict(three).Revisions.Count);

        var error = Fail(three, new ParserLimits { MaxXrefSections = 2 });
        Assert.Equal(PdfErrorCode.LimitExceeded, error.Code);
        Assert.Equal(PdfLimit.XrefSections, error.Limit);
        Assert.True(StrictDocument.Open(three, new ParserLimits { MaxXrefSections = 3 }).IsSuccess);
    }

    [Fact]
    public void SizeMayNotShrinkInNewerSections()
    {
        var doc = DocInfo.Strict(PdfBuilder.Classic().Bytes);
        var two = DocInfo.AppendAnnotations(doc);
        // The new trailer says /Size 9 (one annotation added); make it 7 by editing same-length text.
        var bytes = PdfBuilder.Patch(two, "/Size 9", "/Size 7", 0);
        Assert.False(StrictDocument.Open(bytes).IsSuccess);
    }

    [Fact]
    public void RevisionViewsSeeOnlyTheirOwnPast()
    {
        var baseBytes = PdfBuilder.XrefStream().Bytes;
        var two = DocInfo.AppendAnnotations(DocInfo.Strict(baseBytes), 2);
        var doc = DocInfo.Strict(two);
        Assert.Equal(2, doc.Revisions.Count);
        Assert.Equal(2, DocInfo.AnnotCount(doc, 0));

        var older = doc.Revisions[1];
        Assert.Equal(baseBytes.Length - 1, older.EndOffset);
        var oldPage = Assert.IsType<PdfDictionary>(older.GetObject(3).Value);
        var newPage = Assert.IsType<PdfDictionary>(doc.Latest.GetObject(3).Value);
        Assert.Empty(Assert.IsType<PdfArray>(oldPage["Annots"]));
        Assert.Equal(2, Assert.IsType<PdfArray>(newPage["Annots"]).Count);
        Assert.IsType<PdfNull>(older.GetObject(8 + 2).Value); // objects added later do not exist in the old view
        Assert.Equal(doc.Revisions[1].XrefOffset, doc.Latest.PrevOffset);
        Assert.Equal(0, doc.Latest.Index);
        Assert.Equal(1, older.Index);
    }

    // ---- hybrid-reference conflicts ------------------------------------------------------------------------------

    [Fact]
    public void HybridConflictsAreRejected()
    {
        var built = PdfBuilder.Hybrid();
        int entry6 = built.Find("xref\n0 10\n") + "xref\n0 10\n".Length + (6 * 20);

        // Table says in-use, stream says compressed: which one wins depends on the reader.
        var inUse = (byte[])built.Bytes.Clone();
        Encoding.Latin1.GetBytes("0000000100 00000 n ").CopyTo(inUse, entry6);
        Assert.Equal(PdfErrorCode.HybridConflict, Fail(inUse).Code);
        var lenient = DocInfo.Lenient(inUse);
        Assert.Contains(lenient.Issues, i => i.Code == PdfErrorCode.HybridConflict);
        Assert.Equal(XrefEntryType.InUse, lenient.Latest.Entries[6].Type); // lenient: table wins

        Assert.Equal(PdfErrorCode.HybridConflict, Fail(PdfBuilder.Hybrid(xstmDictExtra: "/Prev 9").Bytes).Code);
        Assert.Equal(PdfErrorCode.HybridConflict, Fail(PdfBuilder.Hybrid(xstmDictExtra: "/XRefStm 9").Bytes).Code);
    }

    [Fact]
    public void HybridStreamAgainstATableThatOmitsOrFreesTheObjects()
    {
        byte[] compressed = [2, 0, 0, 8, 0, 2, 0, 0, 8, 1];
        byte[] free = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] inUse = [1, 0, 0, 9, 0, 1, 0, 0, 9, 0];

        // Table omits 6 and 7; the stream supplies them as compressed objects: fine.
        var omitted = PdfBuilder.Hybrid(omitHiddenFromTable: true, xstmRecords: compressed);
        var doc = DocInfo.Strict(omitted.Bytes);
        Assert.Equal(XrefEntryType.Compressed, doc.Latest.Entries[6].Type);
        Assert.Equal([6, 7], doc.Latest.HybridObjects.Order());

        // Table omits them and the stream says free (or in use): the stream may only add compressed objects.
        Assert.Equal(PdfErrorCode.HybridConflict, Fail(PdfBuilder.Hybrid(omitHiddenFromTable: true, xstmRecords: free).Bytes).Code);
        Assert.Equal(PdfErrorCode.HybridConflict, Fail(PdfBuilder.Hybrid(omitHiddenFromTable: true, xstmRecords: inUse).Bytes).Code);

        // Table frees them and the stream agrees: harmless, and the objects stay free.
        var agreeing = DocInfo.Strict(PdfBuilder.Hybrid(xstmRecords: free).Bytes);
        Assert.IsType<PdfNull>(agreeing.GetObject(6).Value);
        Assert.Empty(agreeing.Latest.HybridObjects);

        // Table frees them and the stream claims they are uncompressed objects: conflict.
        Assert.Equal(PdfErrorCode.HybridConflict, Fail(PdfBuilder.Hybrid(xstmRecords: inUse).Bytes).Code);

        // Entries at or above the table's /Size.
        Assert.False(StrictDocument.Open(PdfBuilder.Hybrid(omitHiddenFromTable: true, xstmRecords: compressed, xstmIndex: "[20 2]").Bytes).IsSuccess);
    }

    [Fact]
    public void HybridStreamPointingAtTheTableIsACycle()
    {
        var bytes = PdfBuilder.Hybrid(xrefStmText: (_, xref) => xref.ToString(System.Globalization.CultureInfo.InvariantCulture)).Bytes;
        Assert.Equal(PdfErrorCode.XrefCycle, Fail(bytes).Code);
        Assert.False(StrictDocument.Open(PdfBuilder.Hybrid(xrefStmText: (_, _) => "5").Bytes).IsSuccess);
    }

    // ---- xref stream problems ------------------------------------------------------------------------------------

    [Fact]
    public void XrefStreamDictionaryProblems()
    {
        var good = PdfBuilder.XrefStream(flate: false).Bytes;
        Assert.True(StrictDocument.Open(good).IsSuccess);

        Assert.Equal(PdfErrorCode.InvalidXref, Fail(PdfBuilder.Patch(good, "/W [1 3 2]", "/W [1 3 9]")).Code);
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(PdfBuilder.Patch(good, "/W [1 3 2]", "/W [1 3  ]")).Code);
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(PdfBuilder.Patch(good, "/W [1 3 2]", "/W [0 0 0]")).Code);
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(PdfBuilder.Patch(good, "/Type /XRef", "/Type /Xref")).Code);
        Assert.Equal(PdfErrorCode.InvalidTrailer, Fail(PdfBuilder.Patch(good, "/Size 10", "/Size  0")).Code);

        // Declared size no longer matches the data: /Size 11 implies 11 records.
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(PdfBuilder.Patch(good, "/Size 10", "/Size 11")).Code);

        Assert.Equal(PdfErrorCode.InvalidXref, Fail(PdfBuilder.XrefStream(flate: false, xrefDictExtra: "/Index [0 5 1]").Bytes).Code);
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(PdfBuilder.XrefStream(flate: false, xrefDictExtra: "/Index [0 -5]").Bytes).Code);
        Assert.Equal(PdfErrorCode.InvalidXref, Fail(PdfBuilder.XrefStream(flate: false, xrefDictExtra: "/Index 5").Bytes).Code);
        Assert.Equal(PdfErrorCode.DuplicateKey, Fail(PdfBuilder.XrefStream(flate: false, xrefDictExtra: "/Size 10").Bytes).Code);
        Assert.Equal(PdfErrorCode.HybridConflict, Fail(PdfBuilder.XrefStream(flate: false, xrefDictExtra: "/XRefStm 9").Bytes).Code);
    }

    [Fact]
    public void XrefStreamFilterProblems()
    {
        var good = PdfBuilder.XrefStream(flate: true, predictor: true).Bytes;
        Assert.Equal(PdfErrorCode.UnsupportedFilter, Fail(PdfBuilder.Patch(good, "/Filter /FlateDecode", "/Filter /LZWDecode  ")).Code);
        Assert.Equal(PdfErrorCode.UnsupportedFilter, Fail(PdfBuilder.Patch(good, "/Predictor 12", "/Predictor  2")).Code);
        Assert.Equal(PdfErrorCode.InvalidStream, Fail(PdfBuilder.Patch(good, "/Columns 6", "/Columns 0")).Code);
        Assert.False(StrictDocument.Open(PdfBuilder.Patch(good, "/Columns 6", "/Columns 5")).IsSuccess);
        Assert.Equal(PdfErrorCode.UnsupportedFeature, Fail(PdfBuilder.XrefStream(xrefDictExtra: "/F (other.bin)").Bytes).Code);

        // Not zlib data at all.
        Assert.Equal(PdfErrorCode.InvalidStream, Fail(PdfBuilder.XrefStream(xrefPayload: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).Bytes).Code);
    }

    [Fact]
    public void XrefStreamWithUnknownEntryTypeOrFields()
    {
        // Rewrite record 2 (object 2, compressed) to type 9; the stream is uncompressed so the bytes can be patched.
        var built = PdfBuilder.XrefStream(flate: false);
        int objStm = built.Offsets.Keys.Max() - 1;
        byte[] pattern = [2, 0, 0, (byte)objStm, 0, 1];
        int at = built.Bytes.AsSpan().IndexOf(pattern);
        Assert.True(at > 0);
        var bad = (byte[])built.Bytes.Clone();
        bad[at] = 9;
        Assert.Equal(PdfErrorCode.InvalidXrefEntry, Fail(bad).Code);

        // Type 1 with an offset beyond the file.
        var beyond = (byte[])built.Bytes.Clone();
        beyond[at] = 1;
        beyond[at + 1] = 0xFF;
        Assert.Equal(PdfErrorCode.XrefOffsetOutOfRange, Fail(beyond).Code);

        // Compressed entry whose container number is 0.
        var zero = (byte[])built.Bytes.Clone();
        zero[at + 3] = 0;
        Assert.Equal(PdfErrorCode.InvalidXrefEntry, Fail(zero).Code);
    }

    // ---- object streams ------------------------------------------------------------------------------------------

    [Fact]
    public void ObjectStreamHeaderProblems()
    {
        // Build from the generated xref-stream file: object stream number is 8, with objects 1,2,3,4,6,7 inside.
        var plain = PdfBuilder.XrefStream(flate: false);
        Assert.True(StrictDocument.Open(plain.Bytes).IsSuccess);
        int stmAt = plain.Find("/Type /ObjStm");
        Assert.True(stmAt > 0);

        // /Type missing from the container.
        Assert.False(AllObjectsLoad(PdfBuilder.Patch(plain.Bytes, "/Type /ObjStm", "/Type /ObjStx")));

        // /First beyond the data.
        string first = "/First " + new string('9', 3);
        string realFirst = "/First " + Encoding.Latin1.GetString(plain.Bytes, plain.Find("/First ") + 7, 3);
        Assert.False(AllObjectsLoad(PdfBuilder.Patch(plain.Bytes, realFirst, first)));

        // /N beyond the data.
        Assert.False(AllObjectsLoad(PdfBuilder.Patch(plain.Bytes, "/N 6", "/N 9")));
    }

    private static bool AllObjectsLoad(byte[] bytes)
    {
        var r = StrictDocument.Open(bytes);
        if (!r.TryGetValue(out var doc))
            return false;
        return Enumerable.Range(1, 7).All(n => doc.GetObject(n).IsSuccess);
    }

    [Fact]
    public void ObjectStreamIndexMismatch()
    {
        // Swap the "index in stream" of objects 6 and 7 in the xref stream: the header no longer agrees with the xref.
        var built = PdfBuilder.XrefStream(flate: false);
        int objStm = built.Offsets.Keys.Max() - 1;
        byte[] p6 = [2, 0, 0, (byte)objStm, 0, 4];
        byte[] p7 = [2, 0, 0, (byte)objStm, 0, 5];
        int a6 = built.Bytes.AsSpan().IndexOf(p6);
        int a7 = built.Bytes.AsSpan().IndexOf(p7);
        Assert.True(a6 > 0 && a7 > 0);
        var bad = (byte[])built.Bytes.Clone();
        bad[a6 + 5] = 5;
        bad[a7 + 5] = 4;
        var doc = DocInfo.Strict(bad);
        Assert.Equal(PdfErrorCode.InvalidObjectStream, doc.GetObject(6).Error.Code);
        Assert.Equal(PdfErrorCode.InvalidObjectStream, doc.GetObject(7).Error.Code);

        // Beyond the stream's object count.
        var beyond = (byte[])built.Bytes.Clone();
        beyond[a6 + 5] = 99;
        Assert.Equal(PdfErrorCode.InvalidObjectStream, DocInfo.Strict(beyond).GetObject(6).Error.Code);
        Assert.IsType<PdfNull>(DocInfo.Lenient(beyond).GetObject(6).Value);
    }

    [Fact]
    public void ObjectStreamMustBeAnUncompressedObject()
    {
        var built = PdfBuilder.XrefStream(flate: false);
        int objStm = built.Offsets.Keys.Max() - 1;
        // Point object 6's container at a number that is itself compressed (object 1).
        byte[] p6 = [2, 0, 0, (byte)objStm, 0, 4];
        int a6 = built.Bytes.AsSpan().IndexOf(p6);
        var bad = (byte[])built.Bytes.Clone();
        bad[a6 + 3] = 1;
        Assert.Equal(PdfErrorCode.InvalidObjectStream, DocInfo.Strict(bad).GetObject(6).Error.Code);

        // Point it at a number nobody defines.
        var missing = (byte[])built.Bytes.Clone();
        missing[a6 + 3] = 200;
        Assert.Equal(PdfErrorCode.InvalidObjectStream, DocInfo.Strict(missing).GetObject(6).Error.Code);

        // Point it at an ordinary stream (object 5 is a content stream, not an object stream).
        var notObjStm = (byte[])built.Bytes.Clone();
        notObjStm[a6 + 3] = 5;
        Assert.Equal(PdfErrorCode.InvalidObjectStream, DocInfo.Strict(notObjStm).GetObject(6).Error.Code);
    }

    [Fact]
    public void ObjectStreamWithDuplicateNumbersOrLimits()
    {
        var plain = PdfBuilder.XrefStream(flate: false);
        // Header "1 0 2 .. " -> make the second pair repeat object 1 (same width: "1 0 2 <n>" -> "1 0 1 <n>").
        var dup = PdfBuilder.Patch(plain.Bytes, "stream\n1 0 2 ", "stream\n1 0 1 ");
        Assert.Equal(PdfErrorCode.InvalidObjectStream, Fail(dup).Code); // the catalog lives in this stream, so the open fails

        var limited = new ParserLimits { MaxObjectStreamObjects = 3 };
        Assert.Equal(PdfLimit.ObjectStreamObjects, StrictDocument.Open(plain.Bytes, limited).Error.Limit);

        // Header numbers above the object-number limit.
        Assert.False(StrictDocument.Open(plain.Bytes, new ParserLimits { MaxObjectNumber = 5 }).IsSuccess);
    }

    // ---- limits --------------------------------------------------------------------------------------------------

    [Fact]
    public void FileSizeLimit()
    {
        var bytes = PdfBuilder.Classic().Bytes;
        var error = Fail(bytes, new ParserLimits { MaxFileSize = bytes.Length - 1 });
        Assert.Equal(PdfLimit.FileSize, error.Limit);
        Assert.True(StrictDocument.Open(bytes, new ParserLimits { MaxFileSize = bytes.Length }).IsSuccess);
    }

    [Fact]
    public void ObjectCountAndNumberLimits()
    {
        var bytes = PdfBuilder.Classic().Bytes;
        Assert.Equal(PdfLimit.ObjectCount, Fail(bytes, new ParserLimits { MaxObjectCount = 7 }).Limit);
        Assert.True(StrictDocument.Open(bytes, new ParserLimits { MaxObjectCount = 8 }).IsSuccess);
        Assert.Equal(PdfLimit.ObjectNumber, Fail(bytes, new ParserLimits { MaxObjectNumber = 6 }).Limit);

        var stream = PdfBuilder.XrefStream().Bytes;
        Assert.Equal(PdfLimit.ObjectCount, Fail(stream, new ParserLimits { MaxObjectCount = 5 }).Limit);
        Assert.Equal(PdfLimit.ObjectNumber, Fail(stream, new ParserLimits { MaxObjectNumber = 5 }).Limit);
    }

    [Fact]
    public void IndexClaimingAHugeRangeIsALimitErrorNotAnAllocation()
    {
        var error = Fail(PdfBuilder.XrefStream(flate: false, xrefDictExtra: "/Index [0 2000000000]").Bytes);
        Assert.Equal(PdfErrorCode.LimitExceeded, error.Code);
        Assert.Equal(PdfErrorCode.LimitExceeded, Fail(PdfBuilder.XrefStream(flate: false, xrefDictExtra: "/Index [2000000000 1]").Bytes).Code);
    }

    [Fact]
    public void DecompressionBombsStopAtTheLimit()
    {
        const int plainSize = 24 * 1024 * 1024;
        byte[] bomb = PdfBuilder.Deflate(new byte[plainSize]);
        Assert.True(bomb.Length < 100_000);

        var objs = PdfBuilder.TwoPageObjects();
        objs.Add(new(8, "<< /Filter /FlateDecode >>", bomb));
        objs.Add(new(9, "<< /Filter /FlateDecode >>", bomb));
        var bytes = PdfBuilder.Classic(objs).Bytes;

        // Per stream.
        var small = new ParserLimits { MaxDecompressedStreamBytes = 1 << 20 };
        var doc = DocInfo.Strict(bytes, small);
        var stream = Assert.IsType<PdfStream>(doc.GetObject(8).Value);
        var error = doc.DecodeStream(stream).Error;
        Assert.Equal(PdfLimit.DecompressedStreamBytes, error.Limit);
        Assert.True(doc.DecompressedBytes <= 1 << 20);

        // In total: each stream is within its own limit, together they are not.
        var total = new ParserLimits { MaxDecompressedStreamBytes = plainSize, MaxDecompressedTotalBytes = plainSize + 1000 };
        var doc2 = DocInfo.Strict(bytes, total);
        var firstDecode = doc2.DecodeStream((PdfStream)doc2.GetObject(8).Value);
        Assert.True(firstDecode.IsSuccess, firstDecode.IsSuccess ? "" : firstDecode.Error.ToString());
        var second = doc2.DecodeStream((PdfStream)doc2.GetObject(9).Value).Error;
        Assert.Equal(PdfLimit.DecompressedTotalBytes, second.Limit);
        Assert.True(doc2.DecompressedBytes <= total.MaxDecompressedTotalBytes);

        // An xref stream that inflates past the limit fails the open itself.
        var bombXref = PdfBuilder.XrefStream(xrefPayload: bomb).Bytes;
        Assert.Equal(PdfLimit.DecompressedStreamBytes, Fail(bombXref, small).Limit);
    }

    [Fact]
    public void UnfilteredStreamsCountAgainstTheBudgetToo()
    {
        var objs = PdfBuilder.TwoPageObjects();
        var bytes = PdfBuilder.Classic(objs).Bytes;
        var doc = DocInfo.Strict(bytes, new ParserLimits { MaxDecompressedStreamBytes = 10 });
        var error = doc.DecodeStream((PdfStream)doc.GetObject(5).Value).Error;
        Assert.Equal(PdfLimit.DecompressedStreamBytes, error.Limit);

        var doc2 = DocInfo.Strict(bytes, new ParserLimits { MaxDecompressedTotalBytes = 10 });
        Assert.Equal(PdfLimit.DecompressedTotalBytes, doc2.DecodeStream((PdfStream)doc2.GetObject(5).Value).Error.Limit);
    }

    [Fact]
    public void FilterChainsAndPredictors()
    {
        byte[] payload = Encoding.Latin1.GetBytes("hello hello hello");
        byte[] twice = PdfBuilder.Deflate(PdfBuilder.Deflate(payload));
        var objs = PdfBuilder.TwoPageObjects();
        objs.Add(new(8, "<< /Filter [/FlateDecode /FlateDecode] >>", twice));
        objs.Add(new(9, "<< /Filter [/Fl] >>", PdfBuilder.Deflate(payload)));
        objs.Add(new(10, "<< /Filter /ASCIIHexDecode >>", payload));
        objs.Add(new(11, "<< /Filter [/FlateDecode /FlateDecode /FlateDecode /FlateDecode /FlateDecode] >>", payload));
        objs.Add(new(12, "<< /Filter /FlateDecode /DecodeParms << /Predictor 2 >> >>", PdfBuilder.Deflate(payload)));
        objs.Add(new(13, "<< /Filter [/FlateDecode] /DecodeParms [null] >>", PdfBuilder.Deflate(payload)));
        objs.Add(new(14, "<< /Filter 7 >>", payload));
        objs.Add(new(15, "<< /Filter [/FlateDecode 5] >>", payload));
        objs.Add(new(16, "<< /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 4 >> >>", PdfBuilder.Deflate([2, 1, 2, 3, 4, 2, 1, 1, 1, 1])));
        objs.Add(new(17, "<< /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 4 >> >>", PdfBuilder.Deflate([9, 1, 2, 3, 4])));
        objs.Add(new(18, "<< /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 4 /Colors 99 >> >>", PdfBuilder.Deflate([2, 1, 2, 3, 4])));
        objs.Add(new(20, "<< /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 100000000 >> >>", PdfBuilder.Deflate([2, 1, 2, 3, 4])));
        objs.Add(new(21, "<< /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 4 >> >>", PdfBuilder.Deflate([2, 1, 2, 3, 4, 5, 6])));
        objs.Add(new(22, "<< /Filter /FlateDecode /DecodeParms 23 0 R >>", PdfBuilder.Deflate(payload)));
        objs.Add(new(23, "<< /Predictor 1 >>"));
        objs.Add(new(24, "<< /Filter 25 0 R >>", PdfBuilder.Deflate(payload)));
        objs.Add(new(25, "/FlateDecode"));
        var doc = DocInfo.Strict(PdfBuilder.Classic(objs).Bytes);

        PdfStream S(int n) => (PdfStream)doc.GetObject(n).Value;
        Assert.Equal(payload, doc.DecodeStream(S(8)).Value);
        Assert.Equal(payload, doc.DecodeStream(S(9)).Value);
        Assert.Equal(PdfErrorCode.UnsupportedFilter, doc.DecodeStream(S(10)).Error.Code);
        Assert.Equal(PdfLimit.FilterChainLength, doc.DecodeStream(S(11)).Error.Limit);
        Assert.Equal(PdfErrorCode.UnsupportedFilter, doc.DecodeStream(S(12)).Error.Code);
        Assert.Equal(payload, doc.DecodeStream(S(13)).Value);
        Assert.Equal(PdfErrorCode.UnsupportedFilter, doc.DecodeStream(S(14)).Error.Code);
        Assert.Equal(PdfErrorCode.UnsupportedFilter, doc.DecodeStream(S(15)).Error.Code);
        Assert.Equal<byte[]>([1, 2, 3, 4, 2, 3, 4, 5], doc.DecodeStream(S(16)).Value);
        Assert.Equal(PdfErrorCode.InvalidStream, doc.DecodeStream(S(17)).Error.Code); // bad row type
        Assert.Equal(PdfErrorCode.InvalidStream, doc.DecodeStream(S(18)).Error.Code);
        Assert.Equal(PdfLimit.DecompressedStreamBytes, doc.DecodeStream(S(20)).Error.Limit);
        Assert.Equal(PdfErrorCode.InvalidStream, doc.DecodeStream(S(21)).Error.Code); // partial row
        Assert.Equal(payload, doc.DecodeStream(S(22)).Value); // indirect DecodeParms
        Assert.Equal(payload, doc.DecodeStream(S(24)).Value); // indirect Filter
    }

    [Fact]
    public void AllPngPredictorRowTypesDecode()
    {
        // Two-byte rows; row types 0..4 each applied to known previous-row data.
        byte[] encoded =
        [
            0, 10, 20,
            1, 5, 5,
            2, 1, 1,
            3, 2, 2,
            4, 1, 1,
        ];
        var objs = PdfBuilder.TwoPageObjects();
        objs.Add(new(8, "<< /Filter /FlateDecode /DecodeParms << /Predictor 15 /Columns 2 >> >>", PdfBuilder.Deflate(encoded)));
        var doc = DocInfo.Strict(PdfBuilder.Classic(objs).Bytes);
        var decoded = doc.DecodeStream((PdfStream)doc.GetObject(8).Value).Value;

        // Row 0 raw; row 1 Sub (5, 5+5); row 2 Up (5+1, 10+1); row 3 Average (2+(0+6)/2, 2+(5+11)/2); row 4 Paeth (1+5, 1+10).
        Assert.Equal<byte[]>([10, 20, 5, 10, 6, 11, 5, 10, 6, 11], decoded);
    }

    [Fact]
    public void DeflateEncodeRoundTrip()
    {
        byte[] raw = Enumerable.Range(0, 70).Select(i => (byte)(i * 7)).ToArray();
        byte[] up = StreamDecoder.EncodePngUp(raw, 7);
        var objs = PdfBuilder.TwoPageObjects();
        objs.Add(new(8, "<< /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 7 >> >>", StreamDecoder.Deflate(up)));
        var doc = DocInfo.Strict(PdfBuilder.Classic(objs).Bytes);
        Assert.Equal(raw, doc.DecodeStream((PdfStream)doc.GetObject(8).Value).Value);
    }

    // ---- lenient mode --------------------------------------------------------------------------------------------

    [Fact]
    public void LenientDocumentRecordsWhatItRecoveredFrom()
    {
        var objs = PdfBuilder.TwoPageObjects();
        objs[2] = new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /MediaBox [0 0 1 1] /Contents 5 0 R >>");
        var bytes = PdfBuilder.Classic(objs).Bytes.Concat("trailing"u8.ToArray()).ToArray();
        Assert.False(StrictDocument.Open(bytes).IsSuccess);

        var doc = DocInfo.Lenient(bytes);
        Assert.IsType<PdfDictionary>(doc.GetObject(3).Value);
        Assert.Contains(doc.Issues, i => i.Code == PdfErrorCode.TrailingData);
        Assert.Contains(doc.Issues, i => i.Code == PdfErrorCode.DuplicateKey);
        Assert.Equal(2, DocInfo.PageCount(doc));
    }

    [Fact]
    public void LenientIssueListIsCapped()
    {
        var objs = PdfBuilder.TwoPageObjects();
        var sb = new StringBuilder("<< /Type /Page ");
        for (int i = 0; i < 1200; i++)
            sb.Append("/A 1 ");
        sb.Append(">>");
        objs[3] = new(4, sb.ToString());
        var doc = DocInfo.Lenient(PdfBuilder.Classic(objs).Bytes);
        Assert.IsType<PdfDictionary>(doc.GetObject(4).Value);
        Assert.Equal(1000, doc.Issues.Count);
        Assert.True(doc.IssuesTruncated);
    }

    [Fact]
    public void StrictAndLenientDocumentsAreDifferentTypes()
    {
        // The validator and the incremental writer take StrictDocument only: a lenient reading cannot become a verdict.
        Assert.False(typeof(StrictDocument).IsAssignableFrom(typeof(LenientDocument)));
        Assert.False(typeof(LenientDocument).IsAssignableFrom(typeof(StrictDocument)));

        var validate = typeof(StructureValidator).GetMethods().Where(m => m.Name == nameof(StructureValidator.Validate)).ToList();
        Assert.All(validate, m => Assert.NotEqual(typeof(LenientDocument), m.GetParameters()[0].ParameterType));
        Assert.All(validate, m => Assert.NotEqual(typeof(PdfDocument), m.GetParameters()[0].ParameterType));
        var ctor = Assert.Single(typeof(IncrementalUpdate).GetConstructors());
        Assert.Equal(typeof(StrictDocument), Assert.Single(ctor.GetParameters()).ParameterType);
    }
}
