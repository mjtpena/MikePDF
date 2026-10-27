namespace Quire.PdfWriter.Tests.Support;

/// <summary>Small helpers that read a document through the public API only (page tree walk, annotation append).</summary>
internal static class DocInfo
{
    public static StrictDocument Strict(byte[] bytes, ParserLimits? limits = null)
    {
        var r = StrictDocument.Open(bytes, limits);
        return r.TryGetValue(out var doc) ? doc : throw new InvalidOperationException("strict open failed: " + r.Error);
    }

    public static PdfError StrictError(byte[] bytes, ParserLimits? limits = null)
    {
        var r = StrictDocument.Open(bytes, limits);
        return r.TryGetError(out var e) ? e : throw new InvalidOperationException("expected the strict open to fail");
    }

    public static LenientDocument Lenient(byte[] bytes, ParserLimits? limits = null)
    {
        var r = LenientDocument.Open(bytes, limits);
        return r.TryGetValue(out var doc) ? doc : throw new InvalidOperationException("lenient open failed: " + r.Error);
    }

    /// <summary>The leaf pages in document order, found by an iterative walk with a visited set (so a cyclic tree terminates).</summary>
    public static List<(PdfReference? Ref, PdfDictionary Dict)> Pages(PdfDocument doc) => Pages(doc.Latest);

    public static List<(PdfReference? Ref, PdfDictionary Dict)> Pages(PdfDocumentRevision doc)
    {
        var pages = new List<(PdfReference?, PdfDictionary)>();
        if (doc.Trailer["Root"] is not PdfReference root || !doc.GetObject(root.Number).TryGetValue(out var rootObj) || rootObj is not PdfDictionary catalog)
            return pages;
        var stack = new Stack<(PdfReference? Ref, PdfObject Node)>();
        stack.Push((null, catalog["Pages"] ?? PdfNull.Instance));
        var visited = new HashSet<int>();
        while (stack.Count > 0)
        {
            var (r, node) = stack.Pop();
            if (node is PdfReference nr)
            {
                if (!visited.Add(nr.Number))
                    continue;
                if (doc.Resolve(nr).TryGetValue(out var resolved))
                    stack.Push((nr, resolved));
                continue;
            }

            if (node is not PdfDictionary d)
                continue;
            if (d["Kids"] is { } kidsObj && doc.Resolve(kidsObj).TryGetValue(out var kidsResolved) && kidsResolved is PdfArray kids)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                    stack.Push((null, kids[i]));
            }
            else
            {
                pages.Add((r, d));
            }
        }

        return pages;
    }

    public static int PageCount(PdfDocument doc) => Pages(doc).Count;

    /// <summary>Number of entries in the page's /Annots array (resolving an indirect array).</summary>
    public static int AnnotCount(PdfDocument doc, int pageIndex) => AnnotCount(doc.Latest, pageIndex);

    public static int AnnotCount(PdfDocumentRevision doc, int pageIndex)
    {
        var pages = Pages(doc);
        if (pageIndex >= pages.Count)
            return 0;
        var (_, page) = pages[pageIndex];
        return page["Annots"] is { } a && doc.Resolve(a).TryGetValue(out var resolved) && resolved is PdfArray arr ? arr.Count : 0;
    }

    /// <summary>
    /// Adds <paramref name="count"/> Square annotations to page 0 through the incremental writer, updating the page's
    /// /Annots whether it is direct, indirect or missing.
    /// </summary>
    public static byte[] AppendAnnotations(StrictDocument doc, int count = 1, IncrementalOptions? options = null, IncrementalUpdate? into = null)
    {
        var update = into ?? new IncrementalUpdate(doc);
        var (pageRef, page) = Pages(doc)[0];
        Assert.NotNull(pageRef);

        var added = new List<PdfObject>();
        for (int i = 0; i < count; i++)
        {
            var annot = new PdfDictionary(
            [
                new(new PdfName("Type"), new PdfName("Annot")),
                new(new PdfName("Subtype"), new PdfName("Square")),
                new(new PdfName("Rect"), new PdfArray([new PdfInteger(10 + i), new PdfInteger(10), new PdfInteger(60), new PdfInteger(60)])),
                new(new PdfName("P"), pageRef),
                new(new PdfName("F"), new PdfInteger(4)),
                new(new PdfName("NM"), PdfString.FromAscii($"quire-test-{i}")),
            ]);
            added.Add(update.Add(annot).Value);
        }

        PdfArray existing = PdfArray.Empty;
        PdfReference? annotsRef = null;
        if (page["Annots"] is PdfReference ar)
        {
            annotsRef = ar;
            existing = (PdfArray)doc.Resolve(ar).Value;
        }
        else if (page["Annots"] is PdfArray direct)
        {
            existing = direct;
        }

        var merged = new PdfArray(existing.Concat(added));
        if (annotsRef is not null)
            Assert.True(update.Replace(annotsRef.Number, merged).IsSuccess);
        else
            Assert.True(update.Replace(pageRef.Number, page.With("Annots", merged)).IsSuccess);

        var built = update.Build(options ?? new IncrementalOptions { NewIdSecondElement = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray() });
        return built.TryGetValue(out var bytes) ? bytes : throw new InvalidOperationException("build failed: " + built.Error);
    }
}
