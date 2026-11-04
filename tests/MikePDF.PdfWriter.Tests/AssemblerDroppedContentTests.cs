using MikePDF.PdfWriter.Assembly;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>
/// Whatever the assembler leaves out is reported: embedded files and attachments on pages that are left out (<see cref="AssemblyWarningCode.AttachmentsDropped"/>), and
/// document scripts and the open action (<see cref="AssemblyWarningCode.ScriptsDropped"/>). Both are known before any page is chosen (<see cref="AssemblySourceSet.Warnings"/>)
/// where they do not depend on the pages, and repeated in the output's warnings for the sources that contributed pages.
/// </summary>
public sealed class AssemblerDroppedContentTests
{
    private static AssemblerSource Source(string name, byte[] bytes) => new(name, bytes);

    private static IEnumerable<AssemblyWarning> Of(IEnumerable<AssemblyWarning> warnings, AssemblyWarningCode code) => warnings.Where(w => w.Code == code);

    private static byte[] WithEmbeddedFile()
    {
        var document = new TinyDoc(2);
        var stream = document.Pdf.AddStream("/Type /EmbeddedFile", "hello");
        var spec = document.Pdf.Add($"<< /Type /Filespec /F (a.txt) /EF << /F {stream} 0 R >> >>");
        var tree = document.Pdf.Add($"<< /Names [(a.txt) {spec} 0 R] >>");
        return document.Build($"/Names << /EmbeddedFiles {tree} 0 R >>");
    }

    private static byte[] WithAttachmentAnnotations(params int[] perPage)
    {
        var document = new TinyDoc(perPage.Length);
        for (var page = 0; page < perPage.Length; page++)
        {
            var annotations = Enumerable.Range(0, perPage[page])
                .Select(i => document.Pdf.Add($"<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 30 30] /Name /Paperclip /FS << /Type /Filespec /F (p{page}-{i}.txt) >> >>"))
                .ToList();
            if (annotations.Count > 0)
                document.OnPage(page, $"/Annots [{string.Join(' ', annotations.Select(a => $"{a} 0 R"))}]");
        }

        return document.Build();
    }

    [Fact]
    [Trait("Spec", "OQ-PO-9")]
    public void EmbeddedFilesAreReportedBeforeAnyPageIsChosenAndInTheOutput()
    {
        var set = PageAssembler.Open([Source("with", WithEmbeddedFile()), Source("without", new TinyDoc(1).Build())], AssemblyFixtures.Fixed);
        Assert.True(set.Sources[0].Info!.HasEmbeddedFiles);
        Assert.False(set.Sources[1].Info!.HasEmbeddedFiles);
        Assert.Equal([(0, 1)], Of(set.Warnings, AssemblyWarningCode.AttachmentsDropped).Select(static w => (w.SourceIndex, w.Count)));

        var output = set.Assemble(AssemblyPlan.Merge((0, 2), (1, 1)), TestContext.Current.CancellationToken).Output;
        Assert.Equal([(0, 1)], Of(output.Warnings, AssemblyWarningCode.AttachmentsDropped).Select(static w => (w.SourceIndex, w.Count)));
        var parsed = AssemblyFixtures.CheckStructure(output);
        Assert.False(AssemblyChecks.Catalog(parsed).ContainsKey("Names"));

        // A source that contributes no page is not reported in the output.
        var other = set.Assemble(AssemblyPlan.Range(1, 0, 0), TestContext.Current.CancellationToken).Output;
        Assert.Empty(Of(other.Warnings, AssemblyWarningCode.AttachmentsDropped));
    }

    [Fact]
    [Trait("Spec", "OQ-PO-9")]
    public void AttachmentAnnotationsOnPagesLeftOutAreReportedAndOnesOnKeptPagesAreKept()
    {
        var set = PageAssembler.Open([Source("doc", WithAttachmentAnnotations(1, 2, 0))], AssemblyFixtures.Fixed);
        Assert.False(set.Sources[0].Info!.HasEmbeddedFiles);
        Assert.Empty(Of(set.Warnings, AssemblyWarningCode.AttachmentsDropped)); // depends on the pages, so not known yet

        // Page 1 only: the two attachments of page 2 are left out.
        var first = set.Assemble(AssemblyPlan.Range(0, 0, 0), TestContext.Current.CancellationToken).Output;
        Assert.Equal([2], Of(first.Warnings, AssemblyWarningCode.AttachmentsDropped).Select(static w => w.Count));
        // Pages 1 and 3: the same two. Page 3 has none.
        var skipping = set.Assemble(AssemblyPlan.Pick(0, [0, 2]), TestContext.Current.CancellationToken).Output;
        Assert.Equal([2], Of(skipping.Warnings, AssemblyWarningCode.AttachmentsDropped).Select(static w => w.Count));
        // Page 3 alone: all three are left out.
        var last = set.Assemble(AssemblyPlan.Range(0, 2, 2), TestContext.Current.CancellationToken).Output;
        Assert.Equal([3], Of(last.Warnings, AssemblyWarningCode.AttachmentsDropped).Select(static w => w.Count));
        // Everything kept: nothing dropped, nothing reported, and the annotations are in the output.
        var all = set.Assemble(AssemblyPlan.Range(0, 0, 2), TestContext.Current.CancellationToken).Output;
        Assert.Empty(Of(all.Warnings, AssemblyWarningCode.AttachmentsDropped));
        var parsed = AssemblyFixtures.CheckStructure(all);
        var kept = DocInfo.Pages(parsed).Sum(page => AssemblyChecks.Resolve(parsed, page.Dict["Annots"]) is PdfArray annots ? annots.Count : 0);
        Assert.Equal(3, kept);
    }

    [Fact]
    [Trait("Spec", "OQ-PO-9")]
    public void TheEmbeddedFileTreeAndLeftOutAttachmentsAreCountedTogether()
    {
        var document = new TinyDoc(2);
        var tree = document.Pdf.Add("<< /Names [] >>");
        var annotation = document.Pdf.Add("<< /Type /Annot /Subtype /FileAttachment /Rect [1 1 9 9] /FS << /Type /Filespec /F (x.txt) >> >>");
        document.OnPage(1, $"/Annots [{annotation} 0 R]");
        var output = AssemblyFixtures.Assemble([Source("doc", document.Build($"/Names << /EmbeddedFiles {tree} 0 R >>"))], AssemblyPlan.Range(0, 0, 0));
        Assert.Equal([2], Of(output.Warnings, AssemblyWarningCode.AttachmentsDropped).Select(static w => w.Count));
    }

    public static TheoryData<string, string, int> ScriptCatalogs() => new()
    {
        { "document JavaScript", "/Names << /JavaScript << /Names [(init) << /S /JavaScript /JS (app.alert\\(1\\)) >>] >> >>", 1 },
        { "an open action that runs a script", "/OpenAction << /S /JavaScript /JS (app.alert\\(1\\)) >>", 1 },
        { "an open action that goes to a page", "/OpenAction [3 0 R /Fit]", 1 },
        { "additional actions of the document", "/AA << /WC << /S /JavaScript /JS (x) >> >>", 1 },
        { "all three", "/Names << /JavaScript << /Names [] >> >> /OpenAction [3 0 R /Fit] /AA << /WS << /S /JavaScript /JS (x) >> >>", 3 },
    };

    [Theory]
    [MemberData(nameof(ScriptCatalogs))]
    [Trait("Spec", "OQ-PO-9")]
    public void DocumentScriptsAndTheOpenActionAreReportedAndNotCarried(string what, string catalog, int kinds)
    {
        var set = PageAssembler.Open([Source("doc", new TinyDoc(2).Build(catalog)), Source("plain", new TinyDoc(1).Build())], AssemblyFixtures.Fixed);
        Assert.True(set.Sources[0].Info!.HasDocumentScripts, what);
        Assert.False(set.Sources[1].Info!.HasDocumentScripts);
        Assert.Equal([(0, kinds)], Of(set.Warnings, AssemblyWarningCode.ScriptsDropped).Select(static w => (w.SourceIndex, w.Count)));

        var output = set.Assemble(AssemblyPlan.Merge((0, 2), (1, 1)), TestContext.Current.CancellationToken).Output;
        Assert.Equal([(0, kinds)], Of(output.Warnings, AssemblyWarningCode.ScriptsDropped).Select(static w => (w.SourceIndex, w.Count)));
        var catalogOut = AssemblyChecks.Catalog(AssemblyFixtures.CheckStructure(output));
        Assert.False(catalogOut.ContainsKey("OpenAction"));
        Assert.False(catalogOut.ContainsKey("AA"));
        Assert.False(catalogOut.ContainsKey("Names"));

        Assert.Empty(Of(set.Assemble(AssemblyPlan.Range(1, 0, 0), TestContext.Current.CancellationToken).Output.Warnings, AssemblyWarningCode.ScriptsDropped));
    }

    [Fact]
    [Trait("Spec", "OQ-PO-9")]
    public void ActionsOnLinksAreKeptSoTheyAreNotReportedAsScriptsDropped()
    {
        var document = new TinyDoc(1);
        var link = document.Pdf.Add("<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /A << /S /JavaScript /JS (app.alert\\(1\\)) >> >>");
        document.OnPage(0, $"/Annots [{link} 0 R]");
        var output = AssemblyFixtures.Assemble([Source("doc", document.Build())], AssemblyPlan.Range(0, 0, 0));
        Assert.Empty(Of(output.Warnings, AssemblyWarningCode.ScriptsDropped));
        Assert.Single(((PdfArray)AssemblyChecks.Resolve(AssemblyFixtures.CheckStructure(output), DocInfo.Pages(AssemblyFixtures.CheckStructure(output))[0].Dict["Annots"])!));
    }

    [Fact]
    [Trait("Spec", "OQ-PO-9")]
    public void TheSameReportsComeFromAnXrefStreamSource()
    {
        var bytes = AssemblyFixtures.AsXrefStream(WithEmbeddedFile());
        var output = AssemblyFixtures.Assemble([Source("doc", bytes)], AssemblyPlan.Range(0, 0, 1));
        Assert.Equal(1, Of(output.Warnings, AssemblyWarningCode.AttachmentsDropped).Single().Count);
    }
}
