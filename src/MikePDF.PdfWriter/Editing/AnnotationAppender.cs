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
            var addedRefs = new Dictionary<string, PdfReference>(StringComparer.Ordinal);
            var rotation = PageRotation(document, page);
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

                        addedRefs[edit.Name!] = AddAnnotation(document, update, page, annots, addedRefs, edit, modified, rotation);
                        added++;
                        break;
                    case AnnotationEditKind.Delete:
                        var deleteIndex = Find(document, annots, edit);
                        RemoveWithPopup(document, annots, deleteIndex);
                        removed++;
                        break;
                    case AnnotationEditKind.Update:
                        var updateIndex = Find(document, annots, edit);
                        annots[updateIndex] = Patch(document, update, annots[updateIndex], edit, modified);
                        break;
                    case AnnotationEditKind.Replace:
                        RequireAdd(edit, replacing: true);
                        var replaceIndex = Find(document, annots, edit);
                        ReplaceAnnotation(document, update, page, annots, replaceIndex, edit, modified, rotation);
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

    private static void RequireAdd(AnnotationChange edit, bool replacing = false)
    {
        if (edit.Rect is not { Length: 4 } || edit.Color is not { Length: 3 } || (!replacing && string.IsNullOrEmpty(edit.Name)))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a new annotation needs a rectangle, a colour and a name");
        }

        var needsQuads = edit.Annotation is NewAnnotationKind.Highlight or NewAnnotationKind.Underline or NewAnnotationKind.StrikeOut or NewAnnotationKind.Squiggly;
        if ((needsQuads && edit.QuadPoints is not { Length: > 0 }) || (edit.QuadPoints is { } quads && quads.Length % 8 != 0))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a text markup needs its quadrilaterals");
        }

        if (edit.Annotation == NewAnnotationKind.Ink && (edit.Strokes is not { Count: > 0 } || edit.Strokes.Any(static s => s.Length < 2 || s.Length % 2 != 0)))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "an ink annotation needs strokes");
        }

        var vertexCount = (edit.Vertices?.Length ?? 0) / 2;
        var vertexRule = edit.Annotation switch
        {
            NewAnnotationKind.Line => vertexCount == 2,
            NewAnnotationKind.Polygon => vertexCount >= 3,
            NewAnnotationKind.PolyLine => vertexCount >= 2,
            _ => true,
        };
        if (!vertexRule || (edit.Vertices is { } v && v.Length % 2 != 0))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a line, polygon or polyline has too few vertices");
        }

        if (edit.Annotation == NewAnnotationKind.Stamp && string.IsNullOrEmpty(edit.StandardName))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a stamp needs its standard name");
        }

        if (edit.Annotation == NewAnnotationKind.FreeText && (!(edit.FontSize >= 1f) || edit.TextColor is not { Length: 3 } || edit.FontId is < 0 or > 11))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a text box needs a font, a size and a text colour");
        }

        if (edit.Dash is < 0 or > 2 || edit.LineStart is < 0 or > 5 || edit.LineEnd is < 0 or > 5 || edit.ReplyKind is < 0 or > 2 || edit.State is < 0 or > 4 || edit.Align is < 0 or > 2)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a style value is out of range");
        }

        if (edit.ReplyKind != 0 && (edit.Annotation != NewAnnotationKind.Text || string.IsNullOrEmpty(edit.ParentName)))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a reply is a text annotation that names its parent");
        }
    }

    /// <summary>Creates one annotation, its appearance and its popup; returns the annotation's reference.</summary>
    private static PdfReference AddAnnotation(StrictDocument document, IncrementalUpdate update, PageEntry page, List<PdfObject> annots,
        Dictionary<string, PdfReference> addedRefs, AnnotationChange edit, DateTimeOffset modified, int rotation)
    {
        var built = AnnotationBuilder.Build(edit, page.Reference, modified, rotation);
        var apRef = Unwrap(update.Add(built.Appearance));
        var dictionary = built.Annotation.With("AP", new PdfDictionary([new KeyValuePair<PdfName, PdfObject>(new PdfName("N"), apRef)]));
        if (edit.ReplyKind != 0)
        {
            dictionary = dictionary.With("IRT", ResolveParent(document, annots, addedRefs, edit.ParentName!));
        }

        var noteRef = Unwrap(update.Add(dictionary));
        annots.Add(noteRef);
        if (built.Popup is { } popup)
        {
            var popupRef = Unwrap(update.Add(popup.With("Parent", noteRef)));
            Unwrap(update.Replace(noteRef.Number, dictionary.With("Popup", popupRef)));
            annots.Add(popupRef);
        }

        return noteRef;
    }

    /// <summary>Rebuilds a loaded annotation in place: same object number, same place in the list, the popup and the reply link kept.</summary>
    private static void ReplaceAnnotation(StrictDocument document, IncrementalUpdate update, PageEntry page, List<PdfObject> annots, int index,
        AnnotationChange edit, DateTimeOffset modified, int rotation)
    {
        var item = annots[index];
        var old = item is PdfReference reference ? (PdfDictionary)document.Latest.LoadReferenced(reference) : (PdfDictionary)item;
        var name = old["NM"] is PdfString nm ? NameText(nm) : edit.Name;
        var effective = edit with { Name = string.IsNullOrEmpty(name) ? edit.Name ?? "replaced" : name };
        var built = AnnotationBuilder.Build(effective, page.Reference, modified, rotation);
        var apRef = Unwrap(update.Add(built.Appearance));
        var dictionary = built.Annotation.With("AP", new PdfDictionary([new KeyValuePair<PdfName, PdfObject>(new PdfName("N"), apRef)]));
        if (old["IRT"] is { } answered && dictionary["IRT"] is null)
        {
            dictionary = dictionary.With("IRT", answered);
        }

        // The popup stays with an annotation that still has a comment to show, and goes when the comment goes (text-markup 2.6, TM-17).
        if (old["Popup"] is PdfReference oldPopup)
        {
            if (built.Popup is not null)
            {
                dictionary = dictionary.With("Popup", oldPopup);
            }
            else
            {
                annots.RemoveAll(entry => entry is PdfReference r && r.Number == oldPopup.Number);
            }
        }

        if (edit.Created is null && old["CreationDate"] is { } created && dictionary["CreationDate"] is null)
        {
            dictionary = dictionary.With("CreationDate", created);
        }

        if (dictionary["Popup"] is null && built.Popup is { } popup && item is PdfReference self)
        {
            var popupRef = Unwrap(update.Add(popup.With("Parent", self)));
            dictionary = dictionary.With("Popup", popupRef);
            annots.Add(popupRef);
        }

        if (item is PdfReference r)
        {
            Unwrap(update.Replace(r.Number, dictionary));
        }
        else
        {
            annots[index] = dictionary;
        }
    }

    /// <summary>Takes an annotation off the list, and its popup with it (the popup belongs to it and would be left dangling).</summary>
    private static void RemoveWithPopup(StrictDocument document, List<PdfObject> annots, int index)
    {
        var target = annots[index] is PdfReference reference ? document.Latest.LoadReferenced(reference) as PdfDictionary : annots[index] as PdfDictionary;
        annots.RemoveAt(index);
        if (target?["Popup"] is PdfReference popup)
        {
            annots.RemoveAll(item => item is PdfReference r && r.Number == popup.Number);
        }
    }

    private static PdfReference ResolveParent(StrictDocument document, List<PdfObject> annots, Dictionary<string, PdfReference> addedRefs, string name)
    {
        if (addedRefs.TryGetValue(name, out var fresh))
        {
            return fresh;
        }

        var newNumbers = addedRefs.Values.Select(static r => r.Number).ToHashSet();
        foreach (var item in annots)
        {
            if (item is PdfReference r && !newNumbers.Contains(r.Number)
                && document.Latest.LoadReferenced(r) is PdfDictionary d && d["NM"] is PdfString nm && string.Equals(NameText(nm), name, StringComparison.Ordinal))
            {
                return r;
            }
        }

        ParseContext.Fatal(PdfErrorCode.InvalidEdit, "a reply names a parent that is not on its page");
        throw new InvalidOperationException();
    }

    /// <summary>The page's effective /Rotate, which may be inherited from the page tree (7.7.3.3), in degrees; 0 for a value that is not a multiple of 90.</summary>
    internal static int PageRotation(StrictDocument document, PageEntry page)
    {
        PdfDictionary? current = page.Dictionary;
        for (var depth = 0; depth < 64 && current is not null; depth++)
        {
            if (current["Rotate"] is { } value)
            {
                var degrees = document.Latest.ResolveInternal(value) switch
                {
                    PdfInteger i => i.Value,
                    PdfReal r => (long)Math.Round(r.Value),
                    _ => 0L,
                };
                var normal = (int)(((degrees % 360) + 360) % 360);
                return normal % 90 == 0 ? normal : 0;
            }

            current = current["Parent"] is PdfReference parent ? document.Latest.LoadReferenced(parent) as PdfDictionary : null;
        }

        return 0;
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
