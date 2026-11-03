namespace MikePDF.PdfWriter.Editing;

/// <summary>
/// Stage 4 of the save (save-pipeline 3.3): turns annotation edits into one incremental update of a strictly parsed document. For each page that has
/// edits it rewrites the page's <c>/Annots</c> array once (the page object, or the array object when the array is indirect), adds new annotations and
/// their appearance streams, replaces updated annotation objects, and takes deleted ones off the list. Every loaded annotation an edit touches is first
/// checked against what the edit expected (subtype and rectangle within 0.01 point); a mismatch fails the whole stage rather than edit the wrong object.
/// The original bytes stay an exact prefix of the output and the update is checked by <see cref="IncrementalUpdate"/>.
/// </summary>
public static class AnnotationAppender
{
    private const float RectTolerance = 0.01f;

    public static PdfResult<AppendResult> Append(StrictDocument baseDocument, int expectedPageCount, IReadOnlyList<AnnotationChange> edits, AppendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseDocument);
        ArgumentNullException.ThrowIfNull(edits);
        return PdfDocument.Guard(() => AppendCore(baseDocument, expectedPageCount, edits, options ?? new AppendOptions()));
    }

    private static AppendResult AppendCore(StrictDocument document, int expectedPageCount, IReadOnlyList<AnnotationChange> edits, AppendOptions options)
    {
        if (document.Trailer["Encrypt"] is not null)
        {
            ParseContext.Fatal(PdfErrorCode.UnsupportedEncryption, "the writer cannot write into an encrypted document");
        }

        if (edits.Count == 0)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "there are no edits to write");
        }

        var pages = PageTree.Enumerate(document);
        if (pages.TryGetError(out var treeError))
        {
            throw new PdfParseException(treeError);
        }

        if (pages.Value.Count != expectedPageCount)
        {
            ParseContext.Fatal(PdfErrorCode.PageCountMismatch, $"the document has {pages.Value.Count} pages; the edits were made against {expectedPageCount}");
        }

        var modified = options.Modified ?? DateTimeOffset.UtcNow;
        var update = new IncrementalUpdate(document);
        var added = 0;
        var removed = 0;
        var deltas = new List<PageDelta>();

        // Edits keep their order within a page; pages are written in ascending order so the output does not depend on the order edits arrived in.
        foreach (var group in edits.GroupBy(static e => e.Page).OrderBy(static g => g.Key))
        {
            if (group.Key < 0 || group.Key >= pages.Value.Count)
            {
                ParseContext.Fatal(PdfErrorCode.InvalidEdit, $"an edit names page {group.Key}, which does not exist");
            }

            var page = pages.Value[group.Key];
            var annots = LoadAnnots(document, page, out var annotsRef);
            var before = annots.Count;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edit in group)
            {
                switch (edit.Kind)
                {
                    case AnnotationEditKind.Add:
                        RequireAdd(edit);
                        if (!names.Add(edit.Name!))
                        {
                            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "two new annotations on a page have the same /NM");
                        }

                        var (appearance, dictionary) = AnnotationBuilder.Build(edit, page.Reference, modified);
                        var apRef = Unwrap(update.Add(appearance));
                        dictionary = dictionary.With("AP", new PdfDictionary([new KeyValuePair<PdfName, PdfObject>(new PdfName("N"), apRef)]));
                        annots.Add(Unwrap(update.Add(dictionary)));
                        added++;
                        break;
                    case AnnotationEditKind.Delete:
                        var deleteIndex = Find(document, annots, edit);
                        annots.RemoveAt(deleteIndex);
                        removed++;
                        break;
                    case AnnotationEditKind.Update:
                        var updateIndex = Find(document, annots, edit);
                        annots[updateIndex] = Patch(document, update, annots[updateIndex], edit, modified);
                        break;
                    default:
                        ParseContext.Fatal(PdfErrorCode.InvalidEdit, "unknown edit");
                        break;
                }
            }

            var array = new PdfArray(annots);
            if (annotsRef is { } indirect)
            {
                Unwrap(update.Replace(indirect.Number, array));
            }
            else
            {
                Unwrap(update.Replace(page.Reference.Number, page.Dictionary.With("Annots", array)));
            }

            deltas.Add(new PageDelta(group.Key, before, annots.Count));
        }

        var output = update.Build(new IncrementalOptions { NewIdSecondElement = options.NewIdSecondElement });
        if (output.TryGetError(out var buildError))
        {
            throw new PdfParseException(buildError);
        }

        return new AppendResult(output.Value, added, removed, update.ChangedObjectNumbers.Count, deltas);
    }

    private static List<PdfObject> LoadAnnots(StrictDocument document, PageEntry page, out PdfReference? annotsRef)
    {
        annotsRef = null;
        var value = page.Dictionary["Annots"];
        if (value is null || value is PdfNull)
        {
            return [];
        }

        if (value is PdfReference reference)
        {
            annotsRef = reference;
            value = document.Latest.LoadReferenced(reference);
        }

        if (value is not PdfArray array)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "/Annots is not an array");
            return [];
        }

        return [.. array];
    }

    private static void RequireAdd(AnnotationChange edit)
    {
        if (edit.Rect is not { Length: 4 } || edit.Color is not { Length: 3 } || string.IsNullOrEmpty(edit.Name))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a new annotation needs a rectangle, a colour and a name");
        }

        var needsQuads = edit.Annotation is NewAnnotationKind.Highlight or NewAnnotationKind.Underline or NewAnnotationKind.StrikeOut;
        if ((needsQuads && edit.QuadPoints is not { Length: > 0 }) || (edit.QuadPoints is { } quads && quads.Length % 8 != 0))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a text markup needs its quadrilaterals");
        }

        if (edit.Annotation == NewAnnotationKind.Ink && edit.Strokes is not { Count: > 0 })
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "an ink annotation needs strokes");
        }
    }

    /// <summary>Finds the annotation an edit targets in the page's list, verifies the expectation, and returns its index.</summary>
    private static int Find(StrictDocument document, List<PdfObject> annots, AnnotationChange edit)
    {
        if (string.IsNullOrEmpty(edit.ExpectSubtype) || edit.ExpectRect is not { Length: 4 })
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "an edit of a loaded annotation must say what it expects to find");
        }

        var found = -1;
        for (var i = 0; i < annots.Count; i++)
        {
            var item = annots[i];
            if (edit.TargetObject != 0)
            {
                if (item is PdfReference r && r.Number == edit.TargetObject)
                {
                    found = i;
                    break;
                }

                continue;
            }

            var dictionary = item is PdfReference reference ? document.Latest.LoadReferenced(reference) as PdfDictionary : item as PdfDictionary;
            if (dictionary?["NM"] is PdfString nm && string.Equals(NameText(nm), edit.Name, StringComparison.Ordinal))
            {
                found = i;
                break;
            }
        }

        if (found < 0)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "the annotation an edit names is not on its page");
        }

        var target = annots[found] is PdfReference reference2 ? document.Latest.LoadReferenced(reference2) as PdfDictionary : annots[found] as PdfDictionary;
        if (target is null)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "the annotation an edit names is not a dictionary");
        }

        if (target!["Subtype"] is not PdfName subtype || subtype.Value != edit.ExpectSubtype)
        {
            ParseContext.Fatal(PdfErrorCode.ExpectationMismatch, "an annotation's subtype is not the one the edit expected");
        }

        var rect = ReadRect(document, target["Rect"]);
        if (rect is null || !Same(rect, edit.ExpectRect!))
        {
            ParseContext.Fatal(PdfErrorCode.ExpectationMismatch, "an annotation's rectangle is not the one the edit expected");
        }

        return found;
    }

    private static PdfObject Patch(StrictDocument document, IncrementalUpdate update, PdfObject item, AnnotationChange edit, DateTimeOffset modified)
    {
        var current = item is PdfReference reference ? (PdfDictionary)document.Latest.LoadReferenced(reference) : (PdfDictionary)item;
        var changed = current;
        if (edit.Rect is { Length: 4 } rect)
        {
            changed = changed.With("Rect", new PdfArray(rect.Select(static v => (PdfObject)new PdfReal(v))));
        }

        if (edit.Contents is not null)
        {
            changed = changed.With("Contents", AnnotationBuilder.Text(edit.Contents));
        }

        changed = changed.With("M", PdfString.FromAscii("D:" + modified.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "Z00'00'"));
        if (item is PdfReference r)
        {
            Unwrap(update.Replace(r.Number, changed));
            return r;
        }

        return changed;
    }

    private static string NameText(PdfString value)
    {
        var bytes = value.Value;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        return System.Text.Encoding.Latin1.GetString(bytes);
    }

    private static float[]? ReadRect(StrictDocument document, PdfObject? value)
    {
        if (document.Latest.ResolveInternal(value) is not PdfArray { Count: 4 } array)
        {
            return null;
        }

        var numbers = new float[4];
        for (var i = 0; i < 4; i++)
        {
            numbers[i] = document.Latest.ResolveInternal(array[i]) switch
            {
                PdfInteger integer => integer.Value,
                PdfReal real => (float)real.Value,
                _ => float.NaN,
            };
            if (float.IsNaN(numbers[i]))
            {
                return null;
            }
        }

        // A rectangle may be given with its corners in either order.
        return [Math.Min(numbers[0], numbers[2]), Math.Min(numbers[1], numbers[3]), Math.Max(numbers[0], numbers[2]), Math.Max(numbers[1], numbers[3])];
    }

    private static bool Same(float[] actual, float[] expected)
    {
        var normalised = new[] { Math.Min(expected[0], expected[2]), Math.Min(expected[1], expected[3]), Math.Max(expected[0], expected[2]), Math.Max(expected[1], expected[3]) };
        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(actual[i] - normalised[i]) > RectTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static PdfReference Unwrap(PdfResult<PdfReference> result)
    {
        if (result.TryGetError(out var error))
        {
            throw new PdfParseException(error);
        }

        return result.Value;
    }
}
