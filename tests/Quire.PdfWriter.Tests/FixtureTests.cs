using Quire.PdfWriter.Tests.Support;

namespace Quire.PdfWriter.Tests;

/// <summary>
/// The PDFium test resources in <c>Fixtures/pdfium</c> (BSD-3-Clause, see MANIFEST.md): real files produced by many tools,
/// including some that are broken on purpose. Strict mode is expected to be stricter than PDFium, never looser.
/// </summary>
public sealed class FixtureTests
{
    /// <param name="File">Fixture file name.</param>
    /// <param name="StrictError">null when the strict open must succeed; otherwise the exact error.</param>
    /// <param name="ValidatorAccepts">validator verdict when the strict open succeeds.</param>
    /// <param name="Pages">page count PDFium reports (0 when PDFium refuses the file).</param>
    /// <param name="Why">what makes the file interesting.</param>
    public sealed record Expectation(string File, PdfErrorCode? StrictError, bool ValidatorAccepts, int Pages, string Why);

    public static readonly Expectation[] Expectations =
    [
        new("annotation_fileattachment.pdf", null, true, 1, "xref stream + object streams"),
        new("annots.pdf", null, true, 2, "classic table, many annotation types"),
        new("bookmarks.pdf", null, true, 2, "classic table"),
        new("bug_1324503.pdf", PdfErrorCode.InvalidTrailer, false, 0, "/XRefStm points outside the file; PDFium refuses it too"),
        new("bug_1399.pdf", null, true, 1, "xref stream + object stream"),
        new("bug_717.pdf", PdfErrorCode.HybridConflict, false, 1, "hybrid file whose /XRefStm lists uncompressed and free entries: ambiguous, so refused (PDFium opens it)"),
        new("bug_757705.pdf", null, false, 1, "linearized xref streams; the main stream's entry 1 points at itself, so the validator reports it"),
        new("bug_xrefv4_loop.pdf", PdfErrorCode.InvalidTrailer, false, 1, "trailer without /Size (PDFium rebuilds the xref)"),
        new("embedded_attachments.pdf", null, true, 1, "xref stream + object streams"),
        new("encrypted_hello_world_r3.pdf", PdfErrorCode.UnsupportedEncryption, false, 0, "encrypted: reported, never parsed"),
        new("feature_linearized_loading.pdf", null, true, 2, "linearized, xref streams"),
        new("hello_world.pdf", null, false, 1, "classic; object 6 has no /Length, which the validator reports"),
        new("hello_world_2_pages.pdf", null, true, 2, "classic table"),
        new("linearized.pdf", null, true, 3, "linearized, classic tables"),
        new("page_labels.pdf", null, true, 7, "linearized, xref streams"),
        new("rectangles_multi_pages.pdf", null, true, 5, "classic table"),
        new("text_form.pdf", null, true, 1, "AcroForm"),
        new("two_signatures.pdf", null, true, 1, "signed file: the original bytes must survive an update"),
    ];

    public static TheoryData<string> Names() => [.. Expectations.Select(e => e.File)];

    public static TheoryData<string> Openable() => [.. Expectations.Where(e => e.StrictError is null).Select(e => e.File)];

    internal static string Directory_ => Path.Combine(AppContext.BaseDirectory, "Fixtures", "pdfium");

    internal static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Directory_, name));

    [Fact]
    public void ExpectationTableCoversEveryFixtureAndNothingElse()
    {
        string[] onDisk = [.. Directory.GetFiles(Directory_, "*.pdf").Select(f => Path.GetFileName(f)).Order()];
        Assert.Equal(onDisk, Expectations.Select(e => e.File).Order());
        Assert.True(onDisk.Length <= 20);
        Assert.True(onDisk.Sum(f => new FileInfo(Path.Combine(Directory_, f)).Length) <= 2_000_000);
        Assert.True(File.Exists(Path.Combine(Directory_, "MANIFEST.md")));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void StrictOpenAndValidatorMatchTheExpectation(string name)
    {
        var e = Expectations.Single(x => x.File == name);
        var opened = StrictDocument.Open(Read(name));
        if (e.StrictError is { } code)
        {
            Assert.Equal(code, opened.Error.Code);
            return;
        }

        var doc = opened.Value;
        Assert.Equal(e.Pages, DocInfo.PageCount(doc));
        var report = StructureValidator.Validate(doc);
        Assert.Equal(e.ValidatorAccepts, report.IsValid);
        Assert.True(report.ObjectsChecked > 0);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void LenientOpenNeverContradictsStrict(string name)
    {
        var e = Expectations.Single(x => x.File == name);
        var lenient = LenientDocument.Open(Read(name));
        if (e.StrictError == PdfErrorCode.UnsupportedEncryption)
        {
            Assert.Equal(PdfErrorCode.UnsupportedEncryption, lenient.Error.Code);
            return;
        }

        // Lenient opens everything the strict parser opens, and records nothing for it.
        Assert.True(lenient.IsSuccess, lenient.IsSuccess ? "" : lenient.Error.ToString());
        if (e.StrictError is null)
            Assert.Empty(lenient.Value.Issues);
        else
            Assert.NotEmpty(lenient.Value.Issues);
    }

    [Fact]
    public void ValidatorReportsTheMissingLengthOfHelloWorld()
    {
        var report = StructureValidator.Validate(Read("hello_world.pdf"));
        Assert.False(report.IsValid);
        var issue = Assert.Single(report.Issues, i => i.Severity == ValidationSeverity.Error);
        Assert.Equal(ValidationCode.InvalidObject, issue.Code);
        Assert.Equal(6, issue.ObjectNumber);
        Assert.Contains("StreamLengthMismatch", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Openable))]
    public void IncrementalAppendWorksOnEveryOpenableFixture(string name)
    {
        byte[] original = Read(name);
        var doc = DocInfo.Strict(original);
        byte[] updated = DocInfo.AppendAnnotations(doc, 2);

        Assert.True(updated.AsSpan(0, original.Length).SequenceEqual(original), "the original bytes must be an exact prefix");
        var after = DocInfo.Strict(updated);
        Assert.Equal(doc.Revisions.Count + 1, after.Revisions.Count);
        Assert.Equal(DocInfo.PageCount(doc), DocInfo.PageCount(after));
        Assert.Equal(DocInfo.AnnotCount(doc, 0) + 2, DocInfo.AnnotCount(after, 0));
        Assert.Equal(doc.Latest.Form, after.Latest.Form);

        var before = StructureValidator.Validate(doc);
        var report = StructureValidator.Validate(after);
        Assert.Equal(before.IsValid, report.IsValid);

        // Updating introduces no kind of problem the original did not already have, except that the old final marker
        // now sits in a section it no longer terminates (a warning-level StartXrefMismatch).
        var known = before.Issues.Select(i => (i.Code, i.Severity)).ToHashSet();
        Assert.All(
            report.Issues,
            i => Assert.True(known.Contains((i.Code, i.Severity)) || (i.Code == ValidationCode.StartXrefMismatch && i.Severity == ValidationSeverity.Warning)));
    }

    [Fact]
    public void SignedFileKeepsItsSignedBytes()
    {
        byte[] original = Read("two_signatures.pdf");
        int byteRange = original.AsSpan().IndexOf("/ByteRange"u8);
        Assert.True(byteRange > 0);
        byte[] updated = DocInfo.AppendAnnotations(DocInfo.Strict(original));
        Assert.True(updated.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.Equal(byteRange, updated.AsSpan().IndexOf("/ByteRange"u8));
    }

    [Fact]
    public void UpdatingALinearizedFileChainsFromTheFinalStartxref()
    {
        var doc = DocInfo.Strict(Read("linearized.pdf"));
        var after = DocInfo.Strict(DocInfo.AppendAnnotations(doc));

        // /Prev names the section the old final startxref pointed at (the first-page table), not the end of the file.
        Assert.Equal(doc.StartXref, after.Latest.PrevOffset);
        Assert.Equal(doc.StartXref, after.Revisions[1].XrefOffset);
    }
}
