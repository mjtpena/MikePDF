namespace MikePDF.PdfWriter.Editing;

/// <summary>A page of the base document: its object number and dictionary as the newest revision has them.</summary>
public sealed record PageEntry(PdfReference Reference, PdfDictionary Dictionary);

/// <summary>
/// Walks the page tree of a strictly parsed document, in page order. It is iterative (a hostile tree cannot overflow the stack), refuses a node
/// reached twice (a cycle or a shared subtree is a way for two readers to count pages differently), refuses a depth above the parser's nesting limit,
/// and fails closed on anything that is not a dictionary or a reference where the format needs one.
/// </summary>
public static class PageTree
{
    public static PdfResult<IReadOnlyList<PageEntry>> Enumerate(StrictDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PdfDocument.Guard<IReadOnlyList<PageEntry>>(() => EnumerateCore(document));
    }

    private static List<PageEntry> EnumerateCore(StrictDocument document)
    {
        var catalog = document.Root is { } root ? document.Latest.LoadObject(root.Number) as PdfDictionary : null;
        if (catalog is null || catalog["Pages"] is not PdfReference pagesRef)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "the catalog has no /Pages reference");
            return [];
        }

        var pages = new List<PageEntry>();
        var visited = new HashSet<int>();
        var maxDepth = document.Limits.EffectiveMaxNestingDepth;
        // Depth-first, left to right: the stack holds (node, depth) and children are pushed in reverse.
        var stack = new Stack<(PdfReference Node, int Depth)>();
        stack.Push((pagesRef, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > maxDepth)
            {
                ParseContext.ExceedLimit(PdfLimit.NestingDepth, "the page tree is nested too deeply");
            }

            if (!visited.Add(node.Number))
            {
                ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a page tree node is reachable twice");
            }

            if (document.Latest.LoadObject(node.Number) is not PdfDictionary dictionary)
            {
                ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a page tree node is not a dictionary");
                return [];
            }

            if (dictionary["Kids"] is { } kids)
            {
                if (document.Latest.ResolveInternal(kids) is not PdfArray kidArray)
                {
                    ParseContext.Fatal(PdfErrorCode.InvalidEdit, "/Kids is not an array");
                    return [];
                }

                for (var i = kidArray.Count - 1; i >= 0; i--)
                {
                    if (kidArray[i] is not PdfReference child)
                    {
                        ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a /Kids entry is not a reference");
                        return [];
                    }

                    stack.Push((child, depth + 1));
                }
            }
            else
            {
                pages.Add(new PageEntry(node, dictionary));
            }
        }

        return pages;
    }
}
