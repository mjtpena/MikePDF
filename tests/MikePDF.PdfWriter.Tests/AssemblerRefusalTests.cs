#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.PdfWriter.Assembly;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>
/// What the assembler refuses and why (R-39, SP-34, PO-27): sources the strict parser refuses, encrypted ones, page trees that two readers could count
/// differently, bad plans, cancellation, and the optional structures that are left out with a warning instead of refusing the whole assembly.
/// </summary>
public sealed class AssemblerRefusalTests
{
    private static AssemblerSourceStatus Open(byte[] bytes, int? expectedPages = null, AssemblerOptions? options = null) =>
        PageAssembler.Open([new AssemblerSource("source", bytes) { ExpectedPageCount = expectedPages }], options ?? AssemblyFixtures.Fixed).Sources[0];

    // ---------------------------------------------------------------- sources the strict parser refuses (R-39)

    [Fact]
    [Trait("Spec", "PO-21")]
    [Trait("Spec", "R-39")]
    public void AnEncryptedSourceIsRefusedWithATypedReason()
    {
        var status = Open(FixtureTests.Read("encrypted_hello_world_r3.pdf"));
        Assert.False(status.IsUsable);
        Assert.Equal(AssemblyRefusalReason.SourceEncrypted, status.Refusal!.Reason);
        Assert.Equal(PdfErrorCode.UnsupportedEncryption, status.Refusal.Error!.Code);
        Assert.Equal(0, status.Refusal.SourceIndex);
        Assert.Equal("source", status.Refusal.SourceName);
    }

    public static TheoryData<string> StrictRefusals() =>
        [.. FixtureTests.Expectations.Where(static e => e.StrictError is not null and not PdfErrorCode.UnsupportedEncryption).Select(static e => e.File)];

    [Theory]
    [MemberData(nameof(StrictRefusals))]
    [Trait("Spec", "PO-27")]
    [Trait("Spec", "R-39")]
    public void AFileTheStrictParserRefusesCannotBeASourceAndSaysWhy(string file)
    {
        var expected = FixtureTests.Expectations.Single(e => e.File == file).StrictError;
        var status = Open(FixtureTests.Read(file));
        Assert.False(status.IsUsable);
        Assert.Equal(AssemblyRefusalReason.SourceNotReadable, status.Refusal!.Reason);
        Assert.Equal(expected, status.Refusal.Error!.Code);
    }

    [Fact]
    [Trait("Spec", "PO-27")]
    public void DamagedBytesAreRefusedAndNeverThrow()
    {
        var good = AssemblyFixtures.Read("C.pdf");
        Assert.Equal(AssemblyRefusalReason.SourceNotReadable, Open([]).Refusal!.Reason);
        Assert.Equal(AssemblyRefusalReason.SourceNotReadable, Open(good[..(good.Length / 2)]).Refusal!.Reason);
        Assert.Equal(AssemblyRefusalReason.SourceNotReadable, Open([.. "junk\n"u8, .. good]).Refusal!.Reason); // header not at byte 0
        Assert.Equal(AssemblyRefusalReason.SourceNotReadable, Open(PdfBuilder.Patch(good, "startxref", "startxrex")).Refusal!.Reason);
    }

    [Fact]
    [Trait("Spec", "PO-27")]
    [Trait("Spec", "R-39")]
    public void AStreamFilterTheParserCannotDecodeIsReportedAsUnsupported()
    {
        var bytes = AssemblyFixtures.AsXrefStream(AssemblyFixtures.Read("C.pdf"));
        var status = Open(PdfBuilder.Patch(bytes, "/FlateDecode", "/LZWDecode  ", occurrence: 1));
        Assert.Equal(AssemblyRefusalReason.SourceUnsupported, status.Refusal!.Reason);
        Assert.Equal(PdfErrorCode.UnsupportedFilter, status.Refusal.Error!.Code);
    }

    [Fact]
    [Trait("Spec", "PO-27")]
    public void ParserLimitsAreCarriedThroughAsLimitRefusals()
    {
        var status = Open(AssemblyFixtures.Read("A.pdf"), options: AssemblyFixtures.Fixed with { ParserLimits = new ParserLimits { MaxFileSize = 1000 } });
        Assert.Equal(AssemblyRefusalReason.LimitExceeded, status.Refusal!.Reason);
        Assert.Equal(PdfLimit.FileSize, status.Refusal.Error!.Limit);
    }

    [Fact]
    [Trait("Spec", "PO-27")]
    [Trait("Spec", "R-39")]
    public void EverySourceIsReportedSoADialogCanListAllThatCannotBeUsedAndTheRestStillAssemble()
    {
        var set = PageAssembler.Open(
            [AssemblyFixtures.Source("C.pdf"), new AssemblerSource("broken.pdf", new byte[] { 1, 2, 3 }), AssemblyFixtures.Source("B.pdf"), new AssemblerSource("locked.pdf", FixtureTests.Read("encrypted_hello_world_r3.pdf"))],
            AssemblyFixtures.Fixed);
        Assert.Equal([true, false, true, false], set.Sources.Select(static s => s.IsUsable));
        Assert.Equal([AssemblyRefusalReason.SourceNotReadable, AssemblyRefusalReason.SourceEncrypted], set.Sources.Where(static s => !s.IsUsable).Select(static s => s.Refusal!.Reason));
        Assert.Equal(["broken.pdf", "locked.pdf"], set.Sources.Where(static s => !s.IsUsable).Select(static s => s.Refusal!.SourceName));
        Assert.Equal([1, 3], set.Sources.Where(static s => !s.IsUsable).Select(static s => s.Refusal!.SourceIndex));

        // A plan that names a refused source is refused, naming it; a plan without it works ("continue without it").
        var refused = set.Assemble(AssemblyPlan.Merge((0, 6), (1, 1)), TestContext.Current.CancellationToken);
        Assert.False(refused.IsSuccess);
        Assert.Equal((AssemblyRefusalReason.SourceRefused, 1, "broken.pdf"), (refused.Refusal.Reason, refused.Refusal.SourceIndex, refused.Refusal.SourceName));
        var without = set.Assemble(AssemblyPlan.Merge((0, 6), (2, 5)), TestContext.Current.CancellationToken);
        Assert.True(without.IsSuccess);
        Assert.Equal(11, without.Output.PageCount);
    }

    [Fact]
    public void ASourceWithADifferentPageCountThanExpectedIsRefused()
    {
        Assert.True(Open(AssemblyFixtures.Read("A.pdf"), expectedPages: 10).IsUsable);
        var status = Open(AssemblyFixtures.Read("A.pdf"), expectedPages: 9);
        Assert.Equal(AssemblyRefusalReason.SourcePageCountMismatch, status.Refusal!.Reason);
    }

    // ---------------------------------------------------------------- page trees two readers could read differently (SP-34, PO-27)

    [Theory]
    [InlineData("/Count 9")]
    [InlineData("/Count /A")]
    [InlineData("/Count -3")]
    [InlineData("")]
    [Trait("Spec", "PO-27")]
    public void APageTreeWhoseCountDisagreesWithItsLeavesIsRefused(string count)
    {
        var document = new TinyDoc(3);
        var bytes = document.Build(tree: $"<< /Type /Pages /Kids [{string.Join(' ', document.Pages.Select(n => $"{n} 0 R"))}] {count} >>");
        var status = Open(bytes);
        Assert.False(status.IsUsable);
        Assert.Equal(AssemblyRefusalReason.SourceInvalidStructure, status.Refusal!.Reason);
    }

    [Fact]
    public void KidsThatAreNotAnArrayAreRefused()
    {
        var bytes = new TinyDoc(3).Build(tree: "<< /Type /Pages /Kids 7 /Count 3 >>");
        Assert.Equal(AssemblyRefusalReason.SourceInvalidStructure, Open(bytes).Refusal!.Reason);
    }

    [Fact]
    [Trait("Spec", "PO-27")]
    public void ACountOfTenMillionOnAThreePageDocumentIsRefusedNotTrusted()
    {
        var document = new TinyDoc(3);
        var bytes = document.Build(tree: $"<< /Type /Pages /Kids [{string.Join(' ', document.Pages.Select(n => $"{n} 0 R"))}] /Count 10000000 >>");
        Assert.Equal(AssemblyRefusalReason.SourceInvalidStructure, Open(bytes).Refusal!.Reason);
    }

    [Fact]
    [Trait("Spec", "PO-27")]
    public void ANodeReachableTwiceIsRefusedWhetherCycleOrSharedPage()
    {
        var document = new TinyDoc(2);
        var cycle = document.Build(tree: $"<< /Type /Pages /Kids [{document.Pages[0]} 0 R {document.Tree} 0 R] /Count 2 >>");
        Assert.Equal(AssemblyRefusalReason.SourceInvalidStructure, Open(cycle).Refusal!.Reason);

        var shared = new TinyDoc(2);
        var twice = shared.Build(tree: $"<< /Type /Pages /Kids [{shared.Pages[0]} 0 R {shared.Pages[0]} 0 R] /Count 2 >>");
        Assert.Equal(AssemblyRefusalReason.SourceInvalidStructure, Open(twice).Refusal!.Reason);
    }

    [Fact]
    [Trait("Spec", "PO-27")]
    public void KidsThatAreNotReferencesToDictionariesAreRefused()
    {
        var direct = new TinyDoc(1).Build(tree: "<< /Type /Pages /Kids [<< /Type /Page /MediaBox [0 0 1 1] >>] /Count 1 >>");
        Assert.Equal(AssemblyRefusalReason.SourceInvalidStructure, Open(direct).Refusal!.Reason);
        var missing = new TinyDoc(1).Build(tree: "<< /Type /Pages /Kids [99 0 R] /Count 1 >>");
        Assert.Equal(AssemblyRefusalReason.SourceInvalidStructure, Open(missing).Refusal!.Reason);
        var noKids = new TinyDoc(1);
        var empty = noKids.Pdf.Add("<< /Type /Pages /Count 0 >>");
        var bytes = noKids.Build(tree: $"<< /Type /Pages /Kids [{empty} 0 R] /Count 0 >>");
        Assert.Equal(AssemblyRefusalReason.SourceInvalidStructure, Open(bytes).Refusal!.Reason);
    }

    [Fact]
    public void ADocumentWithNoCatalogPagesIsRefused()
    {
        var bytes = new TinyDoc(1).Build();
        Assert.Equal(AssemblyRefusalReason.SourceInvalidStructure, Open(PdfBuilder.Patch(bytes, "/Pages 2 0 R", "/Pagex 2 0 R")).Refusal!.Reason);
    }

    // ---------------------------------------------------------------- plans, cancellation

    [Fact]
    public void AnInvalidPlanIsRefusedBeforeAnythingIsWritten()
    {
        var set = PageAssembler.Open([AssemblyFixtures.Source("C.pdf")], AssemblyFixtures.Fixed);
        foreach (var plan in new[]
        {
            new AssemblyPlan([]),
            new AssemblyPlan([new SourcePage(1, 0)]),
            new AssemblyPlan([new SourcePage(-1, 0)]),
            new AssemblyPlan([new SourcePage(0, 6)]),
            new AssemblyPlan([new SourcePage(0, -1)]),
        })
        {
            var result = set.Assemble(plan, TestContext.Current.CancellationToken);
            Assert.False(result.IsSuccess);
            Assert.Equal(AssemblyRefusalReason.InvalidPlan, result.Refusal.Reason);
        }
    }

    [Fact]
    [Trait("Spec", "PO-23")]
    public void ACancelledAssemblyReturnsCancelledAndNoFile()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var result = PageAssembler.Assemble([AssemblyFixtures.Source("A.pdf")], AssemblyPlan.Range(0, 0, 9), AssemblyFixtures.Fixed, cancel.Token);
        Assert.False(result.IsSuccess);
        Assert.Equal(AssemblyRefusalReason.Cancelled, result.Refusal.Reason);
        Assert.Throws<InvalidOperationException>(() => result.Output);
    }

    [Fact]
    public void AssemblingStraightIntoAStreamWritesTheSameFile()
    {
        var set = PageAssembler.Open([AssemblyFixtures.Source("A.pdf")], AssemblyFixtures.Fixed);
        var inMemory = set.Assemble(AssemblyPlan.Range(0, 2, 6), TestContext.Current.CancellationToken).Output;
        using var stream = new MemoryStream();
        var streamed = set.AssembleTo(AssemblyPlan.Range(0, 2, 6), stream, TestContext.Current.CancellationToken);
        Assert.True(streamed.IsSuccess);
        Assert.True(streamed.Output.Bytes.IsEmpty);
        Assert.Equal(inMemory.Bytes.ToArray(), stream.ToArray());
        Assert.Equal(stream.Length, streamed.Output.BytesWritten);
    }

    // ---------------------------------------------------------------- optional structures left out with a warning

    [Fact]
    public void AnOutlineThatCannotBeFollowedIsLeftOutAndTheRestIsStillAssembled()
    {
        var document = new TinyDoc(2);
        var root = document.Pdf.Reserve();
        var first = document.Pdf.Reserve();
        var second = document.Pdf.Reserve();
        document.Pdf.Set(first, $"<< /Title (one) /Parent {root} 0 R /Dest {document.Dest(0)} /Next {second} 0 R >>");
        document.Pdf.Set(second, $"<< /Title (two) /Parent {root} 0 R /Dest {document.Dest(1)} /Next {first} 0 R >>"); // a cycle
        document.Pdf.Set(root, $"<< /Type /Outlines /First {first} 0 R /Last {second} 0 R /Count 2 >>");
        var output = AssemblyFixtures.Assemble([AssemblyFixtures.Source("doc", document.Build($"/Outlines {root} 0 R"))], AssemblyPlan.Range(0, 0, 1));
        Assert.Contains(output.Warnings, static w => w.Code == AssemblyWarningCode.OutlineUnreadable);
        var parsed = AssemblyFixtures.CheckStructure(output);
        Assert.False(AssemblyChecks.Catalog(parsed).ContainsKey("Outlines"));
    }

    [Fact]
    public void PageLabelsThatContradictThemselvesAreLeftOutWithAWarning()
    {
        var document = new TinyDoc(2);
        var output = AssemblyFixtures.Assemble(
            [AssemblyFixtures.Source("doc", document.Build("/PageLabels << /Nums [0 << /S /D >> 0 << /S /r >>] >>"))],
            AssemblyPlan.Range(0, 0, 1));
        Assert.Contains(output.Warnings, static w => w.Code == AssemblyWarningCode.PageLabelsUnreadable);
        Assert.False(AssemblyChecks.Catalog(AssemblyFixtures.CheckStructure(output)).ContainsKey("PageLabels"));
    }

    [Fact]
    public void ANameTheTreeDefinesTwiceWithDifferentTargetsIsDroppedAndTheLinksThatUsedItWithIt()
    {
        var document = new TinyDoc(2);
        var link = document.Pdf.Add("<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest (twice) >>");
        var fine = document.Pdf.Add("<< /Type /Annot /Subtype /Link /Rect [0 60 50 90] /Dest (once) >>");
        document.OnPage(0, $"/Annots [{link} 0 R {fine} 0 R]");
        var tree = document.Pdf.Add($"<< /Names [(once) {document.Dest(1)} (twice) {document.Dest(0)} (twice) {document.Dest(1)}] >>");
        var output = AssemblyFixtures.Assemble([AssemblyFixtures.Source("doc", document.Build($"/Names << /Dests {tree} 0 R >>"))], AssemblyPlan.Range(0, 0, 1));
        var parsed = AssemblyFixtures.CheckStructure(output);
        var annots = (PdfArray)AssemblyChecks.Resolve(parsed, DocInfo.Pages(parsed)[0].Dict["Annots"])!;
        Assert.Single(annots); // the link to the ambiguous name is gone, the other stays
        Assert.Equal(1, output.Statistics.LinksRemoved);
        Assert.Equal(1, output.Statistics.NamedDestinationsDropped);
        Assert.Equal(1, output.Statistics.NamedDestinationsKept);
    }

    [Fact]
    public void ALinkThatLeadsNowhereInItsSourceIsDroppedAndNeverResolvedAgainstAnotherFilesNames()
    {
        // B's link uses the name "shared", which B does not define; A defines it. In the merged file the name exists, so keeping the link would send it to A's page.
        var a = new TinyDoc(1, "A");
        var aNames = a.Pdf.Add($"<< /Names [(shared) {a.Dest(0)}] >>");
        var b = new TinyDoc(1, "B");
        var link = b.Pdf.Add("<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest (shared) >>");
        b.OnPage(0, $"/Annots [{link} 0 R]");
        var output = AssemblyFixtures.Assemble(
            [AssemblyFixtures.Source("a", a.Build($"/Names << /Dests {aNames} 0 R >>")), AssemblyFixtures.Source("b", b.Build())],
            AssemblyPlan.Merge((0, 1), (1, 1)));
        var parsed = AssemblyFixtures.CheckStructure(output);
        Assert.False(DocInfo.Pages(parsed)[1].Dict.ContainsKey("Annots"));
        Assert.Equal(1, output.Statistics.LinksRemoved);
    }

    [Fact]
    [Trait("Spec", "PO-22")]
    public void ASignedFileLosesItsSignaturesWithAWarningTheUiCanShowBeforeTheSave()
    {
        var bytes = FixtureTests.Read("two_signatures.pdf");
        var set = PageAssembler.Open([new AssemblerSource("signed", bytes)], AssemblyFixtures.Fixed);
        Assert.True(set.Sources[0].Info!.HasSignatures);
        Assert.Contains(set.Warnings, static w => w.Code == AssemblyWarningCode.SignaturesNotKept);

        var result = set.Assemble(AssemblyPlan.Range(0, 0, 0), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Contains(result.Output.Warnings, static w => w.Code == AssemblyWarningCode.SignaturesNotKept);
        Assert.True(result.Output.Statistics.SignaturesCleared > 0);
        var parsed = AssemblyFixtures.CheckStructure(result.Output);
        // No signature value is left: nothing with a /ByteRange, and the signature fields (still there) have no /V.
        foreach (var entry in parsed.Latest.EnumerateEffectiveEntries().Where(static e => e.Key > 0 && e.Value.Type != XrefEntryType.Free))
        {
            if (parsed.GetObject(entry.Key).Value is PdfDictionary dictionary)
            {
                Assert.False(dictionary.ContainsKey("ByteRange"));
                Assert.False(dictionary.GetName("FT") == "Sig" && dictionary.ContainsKey("V"));
            }
        }

        Assert.Equal(1, AssemblyChecks.AssertOpensInFreshEngine(result.Output).PageCount);
    }
}
