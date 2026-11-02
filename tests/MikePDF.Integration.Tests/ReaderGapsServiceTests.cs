using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>
/// The reader gaps through the whole stack (docs/features/m2-reader-gaps.md): named destinations (N1, N3, N4), whole-word search (F1) and the
/// properties dialog's GetDocumentDetails (P4), over the real sandboxed engine.
/// </summary>
public sealed class ReaderGapsServiceTests(SandboxFixture sandbox)
{
    private static async Task<SupervisedDocument> OpenAsync(EngineSupervisor supervisor, string path, string? password = null)
    {
        var opened = await supervisor.OpenDocumentAsync(path, password);
        Assert.Equal(Status.Ok, opened.Status);
        return opened.Value!;
    }

    /// <summary>Three pages. Page 1 has links by name (name tree, catalog /Dests, GoTo action, unknown); the outline has bookmarks by name. A line of text for searching.</summary>
    private static byte[] NamedDestinations()
    {
        var pdf = new PdfAssembly();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var a = pdf.Reserve();
        var b = pdf.Reserve();
        var c = pdf.Reserve();
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var content = pdf.AddStream("BT /F1 12 Tf 72 700 Td (the margin of margins and remargin) Tj ET");
        var kid = pdf.Add($"<< /Limits [(alpha) (beta)] /Names [(alpha) [{b} 0 R /XYZ 50 600 0] (beta) << /D [{c} 0 R /FitH 123] >>] >>");
        var root = pdf.Add($"<< /Kids [{kid} 0 R] >>");
        int Link(string rect, string rest) => pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [{rect}] /Border [0 0 0] {rest} >>");
        var l0 = Link("10 10 40 30", "/Dest (alpha)");
        var l1 = Link("50 10 80 30", "/A << /S /GoTo /D (beta) >>");
        var l2 = Link("90 10 120 30", "/Dest /OldName");
        var l3 = Link("130 10 160 30", "/Dest (nonesuch)");
        var l4 = Link("170 10 200 30", "/A << /S /GoToR /F (other.pdf) /D (alpha) >>");
        var outlines = pdf.Reserve();
        var byName = pdf.Reserve();
        var byAction = pdf.Reserve();
        var unknown = pdf.Reserve();
        pdf.Set(outlines, $"<< /Type /Outlines /First {byName} 0 R /Last {unknown} 0 R /Count 3 >>");
        pdf.Set(byName, $"<< /Title (By name) /Parent {outlines} 0 R /Next {byAction} 0 R /Dest (alpha) >>");
        pdf.Set(byAction, $"<< /Title (By action) /Parent {outlines} 0 R /Prev {byName} 0 R /Next {unknown} 0 R /A << /S /GoTo /D (beta) >> >>");
        pdf.Set(unknown, $"<< /Title (Unknown) /Parent {outlines} 0 R /Prev {byAction} 0 R /Dest (nope) >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /Outlines {outlines} 0 R /Names << /Dests {root} 0 R >> /Dests << /OldName [{b} 0 R /Fit] >> >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{a} 0 R {b} 0 R {c} 0 R] /Count 3 >>");
        var resources = $"/Resources << /Font << /F1 {font} 0 R >> >>";
        pdf.Set(a, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] {resources} /Contents {content} 0 R /Annots [{l0} 0 R {l1} 0 R {l2} 0 R {l3} 0 R {l4} 0 R] >>");
        pdf.Set(b, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 300 400] >>");
        pdf.Set(c, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 500 500] >>");
        return pdf.Build(catalog);
    }

    [Fact]
    public async Task N1_N3_N4_NamedDestinationsOfLinksActionsAndBookmarksResolveThroughTheSandbox()
    {
        using var file = new TempFile("named.pdf", NamedDestinations());
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, file.Path);

        var links = await document.GetPageLinksAsync(0, 0, 512);
        Assert.Equal(Status.Ok, links.Status);
        Assert.Equal<ushort[]>([3, 2, 1, 0, 5], links.Value!.Kinds);       // XYZ, FitH in a dictionary value, Fit by an old-style name, unknown, remote
        Assert.Equal<uint[]>([1, 2, 1, 0, 0], links.Value.Pages);
        Assert.Equal(50f, links.Value.Xs[0]);
        Assert.Equal(600f, links.Value.Ys[0]);
        Assert.Equal(123f, links.Value.Ys[1]);

        var outline = await document.GetOutlineAsync([], 0, 64);
        Assert.Equal(Status.Ok, outline.Status);
        Assert.Equal<ushort[]>([3, 2, 0], outline.Value!.Kinds);
        Assert.Equal<uint[]>([1, 2, 0], outline.Value.Pages);
        Assert.Equal(0, supervisor.RestartCount);
    }

    [Fact]
    public async Task F1_WholeWordSkipsAMatchInsideALongerWord()
    {
        using var file = new TempFile("named.pdf", NamedDestinations());
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, file.Path);

        var any = await document.SearchPageAsync(0, "margin", 0);
        Assert.Equal(Status.Ok, any.Status);
        Assert.Equal(3, any.Value!.Starts.Length);                         // margin, margins, remargin

        var whole = await document.SearchPageAsync(0, "margin", 2);
        Assert.Equal(Status.Ok, whole.Status);
        Assert.Equal<uint[]>([4], whole.Value!.Starts);                    // only the standalone word
        Assert.Equal<uint[]>([6], whole.Value.Lengths);

        var wholeAndCase = await document.SearchPageAsync(0, "Margin", 3);
        Assert.Empty(wholeAndCase.Value!.Starts);
        Assert.Single((await document.SearchPageAsync(0, "margins", 2)).Value!.Starts);
    }

    [Fact]
    public async Task P4_DocumentDetailsComeThroughTheSandboxAndSurviveARestart()
    {
        var pdf = new PdfAssembly();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var page = pdf.Reserve();
        var helvetica = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var times = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>");
        var content = pdf.AddStream("BT /F1 12 Tf 10 50 Td (Hello) Tj ET BT /F2 12 Tf 10 30 Td (World) Tj ET");
        var info = pdf.Add("<< /Title (Handbook) /CreationDate (D:20240115103000+10'00') /ModDate (D:20240201) >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 {helvetica} 0 R /F2 {times} 0 R >> >> /Contents {content} 0 R >>");
        using var file = new TempFile("details.pdf", pdf.Build(catalog, $"/Info {info} 0 R"));
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, file.Path);

        var details = await document.GetDocumentDetailsAsync();
        Assert.Equal(Status.Ok, details.Status);
        Assert.Equal("D:20240115103000+10'00'", details.Value!.CreationDate);
        Assert.Equal("D:20240201", details.Value.ModificationDate);
        Assert.Equal(0u, details.Value.SecurityRevision);
        Assert.Equal<string[]>(["Helvetica", "Times-Roman"], details.Value.FontNames);
        Assert.True(details.Value.FontsComplete);
        Assert.Equal(1u, details.Value.FontPagesScanned);

        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Restored += (_, _) => restored.TrySetResult();
        supervisor.CurrentProcess!.Kill();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var again = await document.GetDocumentDetailsAsync();
        Assert.Equal(Status.Ok, again.Status);
        Assert.Equal(details.Value.FontNames, again.Value!.FontNames);
        Assert.Equal(1, supervisor.RestartCount);
    }

    [Fact]
    public async Task P4_AnEncryptedDocumentReportsItsRevisionAndTheOwnerPasswordLiftsTheRestrictions()
    {
        // A document engine holds one document at a time: one engine for the user, one for the owner.
        await using (var userEngine = await sandbox.StartAsync())
        await using (var asUser = await OpenAsync(userEngine, SandboxFixture.FixturePath("encrypted.pdf"), "1234"))
        {
            var info = await asUser.GetInfoAsync();
            var details = await asUser.GetDocumentDetailsAsync();
            Assert.Equal(Status.Ok, details.Status);
            Assert.Equal(4u, details.Value!.SecurityRevision);
            Assert.Equal(0u, info.Value!.Permissions & 0x10);           // copying is not allowed to a user
            Assert.Equal(0u, details.Value.UserPermissions & 0x10);
        }

        await using var ownerEngine = await sandbox.StartAsync();
        await using var asOwner = await OpenAsync(ownerEngine, SandboxFixture.FixturePath("encrypted.pdf"), "5678");
        var ownerInfo = await asOwner.GetInfoAsync();
        Assert.Equal(0x10u, ownerInfo.Value!.Permissions & 0x10);   // the owner may
    }

    [Fact]
    public async Task P4_ThePoolEngineServesDetailsToo()
    {
        await using var supervisor = await sandbox.StartAsync(o => o with { Kind = EngineKind.Pool });
        await using var document = await OpenAsync(supervisor, SandboxFixture.FixturePath("hello_world.pdf"));
        var details = await document.GetDocumentDetailsAsync();
        Assert.Equal(Status.Ok, details.Status);
        Assert.True(details.Value!.FontsComplete);
        Assert.NotEmpty(details.Value.FontNames);
        Assert.Equal(0, supervisor.RestartCount);
    }
}
