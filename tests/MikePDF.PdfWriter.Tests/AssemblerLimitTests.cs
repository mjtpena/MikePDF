using MikePDF.PdfWriter.Assembly;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>
/// Every <see cref="AssemblerLimits"/> value has a test: one below the bound passes, one over it is a refusal that names the bound (never a truncated
/// result). Hostile inputs of PO-27 (page tree loop, lying count, ten million pages, a closure that would not fit) end in a refusal or a bounded output.
/// </summary>
public sealed class AssemblerLimitTests
{
    private static AssemblerOptions With(AssemblerLimits limits) => AssemblyFixtures.Fixed with { Limits = limits };

    private static AssemblyResult Run(byte[] source, AssemblyPlan plan, AssemblerLimits limits, ParserLimits? parser = null) =>
        PageAssembler.Assemble([new AssemblerSource("doc", source)], plan, With(limits) with { ParserLimits = parser }, TestContext.Current.CancellationToken);

    private static void AssertLimit(AssemblyResult result, AssemblyLimit limit)
    {
        Assert.False(result.IsSuccess);
        Assert.True(result.Refusal.Limit is not null, result.Refusal.ToString());
        Assert.Equal((AssemblyRefusalReason.LimitExceeded, limit), (result.Refusal.Reason, result.Refusal.Limit!.Value));
    }

    /// <summary>The same document twice: once inside the bound (must succeed), once with the bound lowered by one (must be refused).</summary>
    private static void AtAndOver(byte[] source, AssemblyPlan plan, Func<int, AssemblerLimits> limits, int bound, AssemblyLimit limit)
    {
        var inside = Run(source, plan, limits(bound));
        Assert.True(inside.IsSuccess, inside.IsSuccess ? "" : inside.Refusal.ToString());
        AssertLimit(Run(source, plan, limits(bound - 1)), limit);
    }

    private static AssemblyPlan All(int pages) => AssemblyPlan.Range(0, 0, pages - 1);

    // ---------------------------------------------------------------- sources, pages, bytes, objects

    [Fact]
    public void MaxSources()
    {
        var sources = Enumerable.Range(0, 3).Select(i => new AssemblerSource("s" + i, new TinyDoc(1).Build())).ToArray();
        var set = PageAssembler.Open(sources, With(new AssemblerLimits { MaxSources = 2 }));
        Assert.Equal(AssemblyLimit.Sources, set.Refusal!.Limit);
        AssertLimit(set.Assemble(AssemblyPlan.Range(0, 0, 0), TestContext.Current.CancellationToken), AssemblyLimit.Sources);
        Assert.Null(PageAssembler.Open(sources, With(new AssemblerLimits { MaxSources = 3 })).Refusal);
    }

    [Fact]
    public void MaxTotalSourceBytes()
    {
        var bytes = new TinyDoc(1).Build();
        var sources = new[] { new AssemblerSource("a", bytes), new AssemblerSource("b", bytes) };
        Assert.Null(PageAssembler.Open(sources, With(new AssemblerLimits { MaxTotalSourceBytes = bytes.Length * 2L })).Refusal);
        Assert.Equal(AssemblyLimit.TotalSourceBytes, PageAssembler.Open(sources, With(new AssemblerLimits { MaxTotalSourceBytes = (bytes.Length * 2L) - 1 })).Refusal!.Limit);
    }

    [Fact]
    public void MaxOutputPages()
    {
        var source = new TinyDoc(3).Build();
        AtAndOver(source, new AssemblyPlan([.. Enumerable.Repeat(new SourcePage(0, 1), 40)]), n => new AssemblerLimits { MaxOutputPages = n }, 40, AssemblyLimit.OutputPages);
    }

    [Fact]
    [Trait("Spec", "PO-26")]
    public void TheDefaultOutputCapIs100000PagesAnd100001IsRefused()
    {
        // PO-26: "merge of 100,000 total pages is accepted and 100,001 refused". One page used 100,000 times: every copy is its own page.
        var source = new TinyDoc(1).Build();
        var hundredThousand = new AssemblyPlan([.. Enumerable.Repeat(new SourcePage(0, 0), 100_000)]);
        var accepted = PageAssembler.Assemble([new AssemblerSource("doc", source)], hundredThousand, AssemblyFixtures.Fixed with { VerifyOutput = false }, TestContext.Current.CancellationToken);
        Assert.True(accepted.IsSuccess, accepted.IsSuccess ? "" : accepted.Refusal.ToString());
        Assert.Equal(100_000, accepted.Output.PageCount);

        var refused = PageAssembler.Assemble([new AssemblerSource("doc", source)], new AssemblyPlan([.. hundredThousand.Pages, new SourcePage(0, 0)]), AssemblyFixtures.Fixed, TestContext.Current.CancellationToken);
        AssertLimit(refused, AssemblyLimit.OutputPages);
        Assert.Equal(100_000, new AssemblerLimits().MaxOutputPages);
        Assert.Equal(200, new AssemblerLimits().MaxSources);
    }

    [Fact]
    public void MaxSourcePages()
    {
        var status = PageAssembler.Open([new AssemblerSource("doc", new TinyDoc(6).Build())], With(new AssemblerLimits { MaxSourcePages = 5 })).Sources[0];
        Assert.False(status.IsUsable);
        Assert.Equal((AssemblyRefusalReason.LimitExceeded, AssemblyLimit.SourcePages), (status.Refusal!.Reason, status.Refusal.Limit!.Value));
        Assert.True(PageAssembler.Open([new AssemblerSource("doc", new TinyDoc(6).Build())], With(new AssemblerLimits { MaxSourcePages = 6 })).Sources[0].IsUsable);
    }

    [Fact]
    public void MaxOutputObjects()
    {
        var source = new TinyDoc(4).Build();
        var plan = All(4);
        var needed = AssemblyFixtures.Assemble([new AssemblerSource("doc", source)], plan).ObjectCount + 1; // the table counts the free entry 0
        AtAndOver(source, plan, n => new AssemblerLimits { MaxOutputObjects = n }, needed, AssemblyLimit.OutputObjects);
    }

    [Fact]
    public void MaxOutputBytes()
    {
        var source = new TinyDoc(4).Build();
        var plan = All(4);
        var size = AssemblyFixtures.Assemble([new AssemblerSource("doc", source)], plan).Bytes.Length;
        AtAndOver(source, plan, n => new AssemblerLimits { MaxOutputBytes = n }, size, AssemblyLimit.OutputBytes);
    }

    // ---------------------------------------------------------------- outline, names, labels

    private static string Outline(TinyDoc document, int items)
    {
        var root = document.Pdf.Reserve();
        var ids = document.Pdf.Reserve(items);
        for (var i = 0; i < items; i++)
        {
            var links = (i > 0 ? $" /Prev {ids[i - 1]} 0 R" : "") + (i < items - 1 ? $" /Next {ids[i + 1]} 0 R" : "");
            document.Pdf.Set(ids[i], $"<< /Title (item {i}) /Parent {root} 0 R /Dest {document.Dest(0)}{links} >>");
        }

        document.Pdf.Set(root, $"<< /Type /Outlines /First {ids[0]} 0 R /Last {ids[^1]} 0 R /Count {items} >>");
        return $"/Outlines {root} 0 R";
    }

    private static string DeepOutline(TinyDoc document, int depth)
    {
        var root = document.Pdf.Reserve();
        var ids = document.Pdf.Reserve(depth);
        for (var i = 0; i < depth; i++)
        {
            var parent = i == 0 ? root : ids[i - 1];
            var kids = i < depth - 1 ? $" /First {ids[i + 1]} 0 R /Last {ids[i + 1]} 0 R /Count 1" : "";
            document.Pdf.Set(ids[i], $"<< /Title (level {i}) /Parent {parent} 0 R /Dest {document.Dest(0)}{kids} >>");
        }

        document.Pdf.Set(root, $"<< /Type /Outlines /First {ids[0]} 0 R /Last {ids[0]} 0 R /Count {depth} >>");
        return $"/Outlines {root} 0 R";
    }

    [Fact]
    public void MaxOutlineItems()
    {
        var document = new TinyDoc(1);
        var source = document.Build(Outline(document, 8));
        AtAndOver(source, All(1), n => new AssemblerLimits { MaxOutlineItems = n }, 8, AssemblyLimit.OutlineItems);
    }

    [Fact]
    public void MaxOutlineDepth()
    {
        var document = new TinyDoc(1);
        var source = document.Build(DeepOutline(document, 6));
        // The reader needs one more level than the entries nest, to see that the last has no children.
        AtAndOver(source, All(1), n => new AssemblerLimits { MaxOutlineDepth = n }, 6, AssemblyLimit.OutlineDepth);
    }

    private static string Names(TinyDoc document, int count)
    {
        var pairs = string.Join(' ', Enumerable.Range(0, count).Select(i => $"(name{i:D3}) {document.Dest(0)}"));
        var node = document.Pdf.Add($"<< /Names [{pairs}] >>");
        return $"/Names << /Dests {node} 0 R >>";
    }

    [Fact]
    public void MaxNamedDestinations()
    {
        var document = new TinyDoc(1);
        var source = document.Build(Names(document, 9));
        AtAndOver(source, All(1), n => new AssemblerLimits { MaxNamedDestinations = n }, 9, AssemblyLimit.NamedDestinations);
    }

    [Fact]
    public void MaxTreeNodesAndMaxTreeDepthForNameTrees()
    {
        // A root with 4 leaves: 5 nodes.
        var wide = new TinyDoc(1);
        var leaves = Enumerable.Range(0, 4).Select(i => wide.Pdf.Add($"<< /Names [(n{i}) {wide.Dest(0)}] >>")).ToList();
        var top = wide.Pdf.Add($"<< /Kids [{string.Join(' ', leaves.Select(l => $"{l} 0 R"))}] >>");
        var wideSource = wide.Build($"/Names << /Dests {top} 0 R >>");
        AtAndOver(wideSource, All(1), n => new AssemblerLimits { MaxTreeNodes = n }, 5, AssemblyLimit.TreeNodes);

        // A chain of 6 nodes: the deepest is at depth 5.
        var deep = new TinyDoc(1);
        var chain = deep.Pdf.Reserve(6);
        for (var i = 0; i < 6; i++)
            deep.Pdf.Set(chain[i], i < 5 ? $"<< /Kids [{chain[i + 1]} 0 R] >>" : $"<< /Names [(leaf) {deep.Dest(0)}] >>");
        var deepSource = deep.Build($"/Names << /Dests {chain[0]} 0 R >>");
        AtAndOver(deepSource, All(1), n => new AssemblerLimits { MaxTreeDepth = n }, 5, AssemblyLimit.TreeDepth);
    }

    [Fact]
    public void MaxPageLabelRanges()
    {
        var document = new TinyDoc(1);
        var source = document.Build("/PageLabels << /Nums [" + string.Join(' ', Enumerable.Range(0, 7).Select(i => $"{i} << /S /D >>")) + "] >>");
        AtAndOver(source, All(1), n => new AssemblerLimits { MaxPageLabelRanges = n }, 7, AssemblyLimit.PageLabelRanges);
    }

    // ---------------------------------------------------------------- annotations, forms, actions, optional content

    [Fact]
    public void MaxAnnotationsPerPage()
    {
        var document = new TinyDoc(1);
        var links = Enumerable.Range(0, 6).Select(_ => document.Pdf.Add("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (x) >> >>")).ToList();
        document.OnPage(0, $"/Annots [{string.Join(' ', links.Select(l => $"{l} 0 R"))}]");
        AtAndOver(document.Build(), All(1), n => new AssemblerLimits { MaxAnnotationsPerPage = n }, 6, AssemblyLimit.AnnotationsPerPage);
    }

    private static string Form(TinyDoc document, int widgets, int depth = 1, bool listed = false)
    {
        // `widgets` widgets on page 1, each below a chain of `depth` fields (the chain's root is shared).
        var chain = document.Pdf.Reserve(depth);
        var ids = document.Pdf.Reserve(widgets);
        for (var i = 0; i < depth; i++)
        {
            var parent = i > 0 ? $" /Parent {chain[i - 1]} 0 R" : "";
            var kids = i < depth - 1 ? $"[{chain[i + 1]} 0 R]" : $"[{string.Join(' ', ids.Select(w => $"{w} 0 R"))}]";
            document.Pdf.Set(chain[i], $"<< /T (f{i}) /FT /Tx{parent} /Kids {kids} >>");
        }

        foreach (var id in ids)
            document.Pdf.Set(id, $"<< /Type /Annot /Subtype /Widget /Rect [0 0 20 20] /Parent {chain[^1]} 0 R /P {document.Pages[0]} 0 R >>");
        document.OnPage(0, $"/Annots [{string.Join(' ', ids.Select(w => $"{w} 0 R"))}]");
        // Not listed in /Fields, the form is invisible to the scan of the whole form, so the bound on the widgets of the kept pages is what is reached.
        return $"/AcroForm << /Fields [{(listed ? $"{chain[0]} 0 R" : "")}] >>";
    }

    [Fact]
    public void MaxFieldNodes()
    {
        var document = new TinyDoc(1);
        var source = document.Build(Form(document, 7)); // 7 widgets and their parent: 8 nodes
        AtAndOver(source, All(1), n => new AssemblerLimits { MaxFieldNodes = n }, 8, AssemblyLimit.FieldNodes);
    }

    [Fact]
    public void TheWholeFormScanForSignaturesIsBoundedByMaxFieldNodesToo()
    {
        var document = new TinyDoc(1);
        var source = document.Build(Form(document, 7, listed: true));
        var status = PageAssembler.Open([new AssemblerSource("doc", source)], With(new AssemblerLimits { MaxFieldNodes = 7 })).Sources[0];
        Assert.False(status.IsUsable);
        Assert.Equal((AssemblyRefusalReason.LimitExceeded, AssemblyLimit.FieldNodes), (status.Refusal!.Reason, status.Refusal.Limit!.Value));
        Assert.True(PageAssembler.Open([new AssemblerSource("doc", source)], With(new AssemblerLimits { MaxFieldNodes = 8 })).Sources[0].IsUsable);
        // A plan that names the refused source says which bound it was.
        var result = Run(source, All(1), new AssemblerLimits { MaxFieldNodes = 7 });
        Assert.Equal((AssemblyRefusalReason.SourceRefused, AssemblyLimit.FieldNodes), (result.Refusal.Reason, result.Refusal.Limit!.Value));
    }

    [Fact]
    public void MaxFieldDepth()
    {
        var document = new TinyDoc(1);
        var source = document.Build(Form(document, 1, depth: 6)); // widget plus 6 ancestors: a chain of 7
        AtAndOver(source, All(1), n => new AssemblerLimits { MaxFieldDepth = n }, 7, AssemblyLimit.FieldDepth);
    }

    [Fact]
    public void MaxActionDepth()
    {
        var document = new TinyDoc(1);
        var action = "<< /S /URI /URI (end) >>";
        for (var i = 0; i < 5; i++)
            action = $"<< /S /URI /URI (step {i}) /Next {action} >>";
        var link = document.Pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A {action} >>");
        document.OnPage(0, $"/Annots [{link} 0 R]");
        var source = document.Build();
        // The parser's own nesting limit is higher than this chain (5 actions deep, 6 with the last).
        AtAndOver(source, All(1), n => new AssemblerLimits { MaxActionDepth = n }, 5, AssemblyLimit.ActionDepth);
    }

    [Fact]
    public void MaxOptionalContentGroups()
    {
        var document = new TinyDoc(1);
        var groups = Enumerable.Range(0, 6).Select(i => document.Pdf.Add($"<< /Type /OCG /Name (g{i}) >>")).ToList();
        var source = document.Build($"/OCProperties << /OCGs [{string.Join(' ', groups.Select(g => $"{g} 0 R"))}] /D << /Order [] >> >>");
        AtAndOver(source, All(1), n => new AssemblerLimits { MaxOptionalContentGroups = n }, 6, AssemblyLimit.OptionalContentGroups);
    }

    // ---------------------------------------------------------------- nesting, hostile input

    [Fact]
    [Trait("Spec", "PO-27")]
    public void ADeeplyNestedPageTreeIsRefusedAtTheParsersNestingLimit()
    {
        // A chain of nodes, each the only kid of the one above, ending in one page.
        const int depth = 12;
        var pdf = new MiniPdf();
        var catalog = pdf.Reserve();
        var nodes = pdf.Reserve(depth);
        var page = pdf.Reserve();
        for (var i = 0; i < depth; i++)
        {
            var parent = i > 0 ? $" /Parent {nodes[i - 1]} 0 R" : "";
            pdf.Set(nodes[i], $"<< /Type /Pages /Kids [{(i < depth - 1 ? nodes[i + 1] : page)} 0 R] /Count 1{parent} >>");
        }

        pdf.Set(page, $"<< /Type /Page /Parent {nodes[^1]} 0 R /MediaBox [0 0 10 10] >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {nodes[0]} 0 R >>");
        var source = pdf.WriteClassic(catalog);

        Assert.True(PageAssembler.Open([new AssemblerSource("deep", source)], AssemblyFixtures.Fixed).Sources[0].IsUsable);
        var status = PageAssembler.Open([new AssemblerSource("deep", source)], AssemblyFixtures.Fixed with { ParserLimits = new ParserLimits { MaxNestingDepth = 6 } }).Sources[0];
        Assert.Equal((AssemblyRefusalReason.LimitExceeded, AssemblyLimit.NestingDepth), (status.Refusal!.Reason, status.Refusal.Limit!.Value));
    }

    [Fact]
    [Trait("Spec", "PO-27")]
    public void AClosureTooBigForTheOutputLimitIsRefusedWhileItIsBeingWritten()
    {
        // One page whose resources hold a 3 MB image; the output may be 1 MB.
        var page = new TinyDoc(1);
        var image = page.Pdf.AddStream("/Type /XObject /Subtype /Image /Width 1000 /Height 1000 /ColorSpace /DeviceRGB /BitsPerComponent 8", new byte[3_000_000]);
        page.OnPage(0, $"/Thumb {image} 0 R");
        var source = page.Build();
        AssertLimit(Run(source, All(1), new AssemblerLimits { MaxOutputBytes = 1 << 20 }), AssemblyLimit.OutputBytes);
        Assert.True(Run(source, All(1), new AssemblerLimits { MaxOutputBytes = 4 << 20 }).IsSuccess);
    }

    [Fact]
    [Trait("Spec", "PO-27")]
    public void AnObjectStreamThatInflatesPastTheDecompressionBudgetIsRefused()
    {
        var bytes = AssemblyFixtures.AsXrefStream(AssemblyFixtures.Read("A.pdf"));
        var status = PageAssembler.Open([new AssemblerSource("doc", bytes)], AssemblyFixtures.Fixed with { ParserLimits = new ParserLimits { MaxDecompressedStreamBytes = 100 } }).Sources[0];
        Assert.Equal(AssemblyRefusalReason.LimitExceeded, status.Refusal!.Reason);
        Assert.Equal(PdfLimit.DecompressedStreamBytes, status.Refusal.Error!.Limit);
    }

    [Fact]
    public void EveryLimitHasADefaultThatMatchesThePageOrganiserSpec()
    {
        var limits = new AssemblerLimits();
        Assert.Equal((200, 100_000), (limits.MaxSources, limits.MaxOutputPages));
        Assert.True(limits.MaxOutputObjects < new ParserLimits().MaxObjectNumber, "the output must always reopen");
        Assert.True(limits.MaxOutputBytes <= int.MaxValue);
        // Each limit appears in AssemblyLimit, so none can be exceeded without a name for it.
        var members = typeof(AssemblerLimits).GetProperties().Where(static p => p.Name.StartsWith("Max", StringComparison.Ordinal)).Select(static p => p.Name[3..]).Order().ToList();
        var named = Enum.GetNames<AssemblyLimit>().Where(static n => n != "NestingDepth").Order().ToList();
        Assert.Equal(named, members);
    }
}
