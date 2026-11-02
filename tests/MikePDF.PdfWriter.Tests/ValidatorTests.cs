using System.Text;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

public sealed class ValidatorTests
{
    private static readonly ValidationOptions Pedantic = new() { DanglingReferencesAreErrors = true };

    private static ValidationReport Validate(byte[] bytes, ValidationOptions? options = null, ParserLimits? limits = null) =>
        StructureValidator.Validate(bytes, limits, options);

    private static ValidationIssue Single(ValidationReport report, ValidationCode code) =>
        Assert.Single(report.Issues, i => i.Code == code);

    [Theory]
    [MemberData(nameof(DocumentTests.Layouts), MemberType = typeof(DocumentTests))]
    public void WellFormedFilesHaveNoIssues(string layout)
    {
        var report = Validate(DocumentTests.Build(layout).Bytes, Pedantic);
        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
        Assert.True(report.ObjectsChecked >= 7);
        Assert.True(report.ReferencesChecked > 5);
    }

    [Fact]
    public void ParseFailuresBecomeErrors()
    {
        var report = Validate("not a pdf"u8.ToArray());
        Assert.False(report.IsValid);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(ValidationCode.ParseFailed, issue.Code);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Contains("InvalidHeader", issue.Message, StringComparison.Ordinal);

        var limit = Validate(PdfBuilder.Classic().Bytes, limits: new ParserLimits { MaxFileSize = 10 });
        Assert.Equal(ValidationCode.LimitExceeded, Assert.Single(limit.Issues).Code);
        Assert.Equal(0, limit.ObjectsChecked);
    }

    [Fact]
    public void NullDocumentIsAnArgumentError() =>
        Assert.Throws<ArgumentNullException>(() => StructureValidator.Validate((StrictDocument)null!));

    [Fact]
    public void WrongEntryOffsetIsReported()
    {
        var built = PdfBuilder.Classic();
        int at = built.Find("xref\n0 8\n") + "xref\n0 8\n".Length + (3 * 20);
        var bytes = (byte[])built.Bytes.Clone();
        Encoding.Latin1.GetBytes(built.Offsets[2].ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).CopyTo(bytes, at);

        var report = Validate(bytes);
        Assert.False(report.IsValid);
        var issue = Single(report, ValidationCode.InvalidObject);
        Assert.Equal(3, issue.ObjectNumber);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Equal(built.Offsets[2], issue.Offset);
    }

    [Fact]
    public void ShadowedObjectsInOlderRevisionsAreStillChecked()
    {
        var baseBytes = PdfBuilder.Classic();
        byte[] updated = DocInfo.AppendAnnotations(DocInfo.Strict(baseBytes.Bytes)); // replaces page 3
        Assert.True(Validate(updated).IsValid);

        // Break the OLD table entry of page 3. The newer revision supersedes it, but the stale entry is still wrong.
        int at = PdfBuilder.IndexOf(updated, "xref\n0 8\n") + "xref\n0 8\n".Length + (3 * 20);
        var bad = (byte[])updated.Clone();
        Encoding.Latin1.GetBytes("0000000100").CopyTo(bad, at);
        var report = Validate(bad);
        Assert.False(report.IsValid);
        Assert.Equal(3, Single(report, ValidationCode.InvalidObject).ObjectNumber);
    }

    [Fact]
    public void OverlappingObjectsAreReported()
    {
        var objs = PdfBuilder.TwoPageObjects();
        objs[2] = new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Hidden (\n4 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n) >>");
        objs.RemoveAt(3);
        var built = PdfBuilder.ClassicCustom(objs, (offsets, pos) =>
        {
            string body = objs.Single(o => o.Num == 3).Text;
            int inner = offsets[3] + "3 0 obj\n".Length + body.IndexOf("\n4 0 obj", StringComparison.Ordinal) + 1;
            var all = new Dictionary<int, int>(offsets) { [4] = inner };
            return $"xref\n0 8\n{PdfBuilder.TableBody(all, 8)}trailer\n<< /Size 8 /Root 1 0 R /Info 7 0 R >>\nstartxref\n{pos}\n%%EOF\n";
        });
        var report = Validate(built.Bytes);
        Assert.False(report.IsValid);
        var overlap = Single(report, ValidationCode.ObjectOverlap);
        Assert.Contains("object 4", overlap.Message, StringComparison.Ordinal);
        Assert.Contains("object 3", overlap.Message, StringComparison.Ordinal);
        Assert.Equal(4, overlap.ObjectNumber);
    }

    [Fact]
    public void TwoEntriesAtTheSameOffsetAreAHeaderMismatchNotASilentOverlap()
    {
        var built = PdfBuilder.Classic();
        int at = built.Find("xref\n0 8\n") + "xref\n0 8\n".Length + (4 * 20);
        var bad = (byte[])built.Bytes.Clone();
        Encoding.Latin1.GetBytes(built.Offsets[3].ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).CopyTo(bad, at);
        Assert.Equal(4, Single(Validate(bad), ValidationCode.InvalidObject).ObjectNumber);
    }
    [Fact]
    public void StartxrefOfAnOlderRevisionIsAWarning()
    {
        byte[] updated = DocInfo.AppendAnnotations(DocInfo.Strict(PdfBuilder.Classic().Bytes));
        string oldValue = DocInfo.Strict(updated).Revisions[1].XrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string newValue = (int.Parse(oldValue, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(oldValue.Length, newValue.Length);
        byte[] bad = PdfBuilder.Patch(updated, "startxref\n" + oldValue, "startxref\n" + newValue);

        var report = Validate(bad);
        Assert.True(report.IsValid); // whitespace-only offset difference is tolerated by Open, but still reported
        var issue = Single(report, ValidationCode.StartXrefMismatch);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);

        // Pointing into the middle of something else is also a warning for an older revision.
        string far = (int.Parse(oldValue, System.Globalization.CultureInfo.InvariantCulture) + 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(ValidationSeverity.Warning, Single(Validate(PdfBuilder.Patch(updated, "startxref\n" + oldValue, "startxref\n" + far)), ValidationCode.StartXrefMismatch).Severity);
    }

    [Fact]
    public void ReferencesAreChecked()
    {
        var objs = PdfBuilder.TwoPageObjects();
        objs[2] = new(3, "<< /Type /Page /Parent 2 0 R /Contents 99 0 R /Annots [99 0 R 5 3 R] >>");
        byte[] bytes = PdfBuilder.Classic(objs).Bytes;

        var relaxed = Validate(bytes);
        Assert.True(relaxed.IsValid);
        var dangling = Single(relaxed, ValidationCode.DanglingReference); // two uses of 99 0 R, one report
        Assert.Equal(ValidationSeverity.Warning, dangling.Severity);
        Assert.Equal(3, dangling.ObjectNumber);
        var gen = Single(relaxed, ValidationCode.ReferenceGenerationMismatch);
        Assert.Equal(ValidationSeverity.Warning, gen.Severity);

        var strict = Validate(bytes, Pedantic);
        Assert.False(strict.IsValid);
        Assert.Equal(ValidationSeverity.Error, Single(strict, ValidationCode.DanglingReference).Severity);
    }

    [Fact]
    public void ReferencesToObjectZeroAndFreeObjects()
    {
        var objs = PdfBuilder.TwoPageObjects();
        objs[2] = new(3, "<< /Type /Page /Parent 2 0 R /Contents 0 0 R /Annots [0 0 R] >>");
        Assert.Empty(Validate(PdfBuilder.Classic(objs).Bytes, Pedantic).Issues);

        // Object 9 is a hole in the table (free), so a reference to it dangles.
        objs[2] = new(3, "<< /Type /Page /Parent 2 0 R /Contents 9 0 R >>");
        objs.Add(new(10, "(x)"));
        Assert.Single(Validate(PdfBuilder.Classic(objs).Bytes).Issues, i => i.Code == ValidationCode.DanglingReference);
    }

    [Fact]
    public void CatalogAndTrailerChecks()
    {
        // /Type of the root is wrong: an error.
        var objs = PdfBuilder.TwoPageObjects();
        objs[0] = new(1, "<< /Type /Pages /Pages 2 0 R >>");
        var wrongType = Validate(PdfBuilder.Classic(objs).Bytes);
        Assert.False(wrongType.IsValid);
        Assert.Equal(ValidationCode.RootInvalid, Assert.Single(wrongType.Issues).Code);

        // No /Type and no usable /Pages: warnings only.
        objs[0] = new(1, "<< /Pages 7 0 R >>");
        objs[6] = new(7, "[1 2 3]");
        var sparse = Validate(PdfBuilder.Classic(objs).Bytes);
        Assert.True(sparse.IsValid);
        Assert.Equal(2, sparse.Issues.Count(i => i.Code == ValidationCode.CatalogIncomplete));
        Assert.Single(sparse.Issues, i => i.Code == ValidationCode.TrailerInvalid); // /Info 7 0 R is an array

        // Malformed /ID.
        var built = PdfBuilder.ClassicCustom(PdfBuilder.TwoPageObjects(), (o, pos) =>
            $"xref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n<< /Size 8 /Root 1 0 R /ID [(a)] >>\nstartxref\n{pos}\n%%EOF\n");
        var id = Validate(built.Bytes);
        Assert.True(id.IsValid);
        Assert.Equal(ValidationCode.TrailerInvalid, Assert.Single(id.Issues).Code);
    }

    [Fact]
    public void UnreferencedDataBetweenObjectsIsAWarning()
    {
        // An object nothing lists in the xref: a reader never sees it, so it is a place to hide data.
        var objs = PdfBuilder.TwoPageObjects();
        objs.Add(new(8, "<< /Hidden true >>"));
        var unlisted = PdfBuilder.ClassicCustom(objs, (o, pos) =>
            $"xref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{pos}\n%%EOF\n");
        var report = Validate(unlisted.Bytes);
        Assert.True(report.IsValid);
        var issue = Single(report, ValidationCode.UnreferencedData);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Equal(unlisted.Offsets[8], issue.Offset);

        // Junk between the last object and the xref section.
        var junk = PdfBuilder.ClassicCustom(PdfBuilder.TwoPageObjects(), (o, pos) =>
            $"JUNK\nxref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{pos + 5}\n%%EOF\n");
        Assert.Single(Validate(junk.Bytes).Issues, i => i.Code == ValidationCode.UnreferencedData);

        // Whitespace, comments and startxref markers between sections are ordinary.
        var fine = PdfBuilder.ClassicCustom(PdfBuilder.TwoPageObjects(), (o, pos) =>
            $"% a comment\n\n  \nxref\n0 8\n{PdfBuilder.TableBody(o, 8)}trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{pos + 16}\n%%EOF\n");
        Assert.Empty(Validate(fine.Bytes).Issues);
    }

    [Fact]
    public void PageTreeIsWalkedAndCheckedAgainstCount()
    {
        string? One(string pagesBody, Action<List<PdfBuilder.Obj>>? more = null, ParserLimits? limits = null)
        {
            var objs = PdfBuilder.TwoPageObjects();
            objs[1] = new(2, pagesBody);
            more?.Invoke(objs);
            var report = Validate(PdfBuilder.Classic(objs).Bytes, limits: limits);
            Assert.True(report.IsValid); // page tree findings are warnings
            var messages = report.Issues.Where(i => i.Code == ValidationCode.PageTreeInconsistent).Select(i => i.Message).ToList();
            return messages.Count == 0 ? null : string.Join("; ", messages);
        }

        Assert.Null(One("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"));

        // PDFium reports the root /Count without walking the tree, so a /Count that lies is a reader differential.
        Assert.Contains("root /Count is 5 but the tree has 2 pages", One("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 5 >>")!, StringComparison.Ordinal);
        Assert.Contains("missing", One("<< /Type /Pages /Kids [3 0 R 4 0 R] >>")!, StringComparison.Ordinal);

        // The same page listed twice, and a node that contains itself.
        Assert.Contains("more than once", One("<< /Type /Pages /Kids [3 0 R 3 0 R] /Count 2 >>")!, StringComparison.Ordinal);
        Assert.Contains("more than once", One("<< /Type /Pages /Kids [2 0 R] /Count 1 >>")!, StringComparison.Ordinal);

        Assert.Contains("not an array", One("<< /Type /Pages /Kids (x) /Count 1 >>")!, StringComparison.Ordinal);
        Assert.Contains("not a dictionary", One("<< /Type /Pages /Kids [8 0 R] /Count 1 >>", o => o.Add(new(8, "(x)")))!, StringComparison.Ordinal);

        // A flood of direct nodes stops at the object-count limit instead of walking forever.
        string flood = "<< /Type /Pages /Kids [" + string.Concat(Enumerable.Repeat("<< >> ", 40)) + "] /Count 40 >>";
        Assert.Contains("more nodes", One(flood, limits: new ParserLimits { MaxObjectCount = 8 })!, StringComparison.Ordinal);

        // An unreadable page object is reported where it is read, and the page-tree walk degrades to a warning.
        var objs2 = PdfBuilder.TwoPageObjects();
        var broken = Validate(PdfBuilder.Patch(PdfBuilder.Classic(objs2).Bytes, "\n3 0 obj", "\n9 0 obj"));
        Assert.False(broken.IsValid);
        Assert.Contains(broken.Issues, i => i.Code == ValidationCode.PageTreeInconsistent && i.Message.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void RootThatIsAFreeObjectCannotOpenAndOneThatBreaksMidWalkIsReported()
    {
        // Root resolves (opening checks that), but a later look at the catalog's /Pages hits a reference-generation violation.
        var objs = PdfBuilder.TwoPageObjects();
        objs[0] = new(1, "<< /Type /Catalog /Pages 2 5 R >>");
        var report = Validate(PdfBuilder.Classic(objs).Bytes);
        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code == ValidationCode.RootInvalid && i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void IssueCapAddsOneFinalNotice()
    {
        var objs = PdfBuilder.TwoPageObjects();
        objs[2] = new(3, "<< /Type /Page /Parent 2 0 R /Annots [" + string.Join(" ", Enumerable.Range(100, 20).Select(n => $"{n} 0 R")) + "] >>");
        var report = Validate(PdfBuilder.Classic(objs).Bytes, new ValidationOptions { MaxIssues = 3 });
        Assert.Equal(4, report.Issues.Count);
        Assert.Equal(ValidationCode.TooManyIssues, report.Issues[^1].Code);
        Assert.Equal(3, report.Issues.Count(i => i.Code == ValidationCode.DanglingReference));
    }

    [Fact]
    public void CompressedEntriesAreCheckedAgainstTheirObjectStream()
    {
        var built = PdfBuilder.XrefStream(flate: false);
        int objStm = built.Offsets.Keys.Max() - 1;
        byte[] p6 = [2, 0, 0, (byte)objStm, 0, 4];
        byte[] p7 = [2, 0, 0, (byte)objStm, 0, 5];
        int a6 = built.Bytes.AsSpan().IndexOf(p6);
        int a7 = built.Bytes.AsSpan().IndexOf(p7);
        var bad = (byte[])built.Bytes.Clone();
        bad[a6 + 5] = 5; // 6 and 7 trade slots; the object stream header says otherwise
        bad[a7 + 5] = 4;
        var report = Validate(bad);
        Assert.False(report.IsValid);
        Assert.Equal([6, 7], report.Issues.Where(i => i.Code == ValidationCode.InvalidObjectStream).Select(i => i.ObjectNumber).Order());
    }

    [Fact]
    public void ExtendedHybridChainValidates()
    {
        // A hybrid base extended twice: hybrid section, then two plain tables.
        var hybrid = PdfBuilder.Hybrid().Bytes;
        byte[] chain = DocInfo.AppendAnnotations(DocInfo.Strict(DocInfo.AppendAnnotations(DocInfo.Strict(hybrid))));
        var report = Validate(chain, Pedantic);
        Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(i => i.Message)));
    }
}
