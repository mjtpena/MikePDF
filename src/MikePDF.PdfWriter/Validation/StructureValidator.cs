namespace MikePDF.PdfWriter;

public enum ValidationSeverity
{
    Warning,
    Error,
}

public enum ValidationCode
{
    /// <summary>The strict parser refused the file (see the message for the underlying <see cref="PdfErrorCode"/>).</summary>
    ParseFailed,

    /// <summary>An xref entry does not lead to a well-formed object with the matching number and generation.</summary>
    InvalidObject,

    /// <summary>A compressed entry does not match its object stream.</summary>
    InvalidObjectStream,

    ObjectOverlap,

    /// <summary>Bytes between objects that are neither whitespace, comments nor a startxref marker. Nothing refers to them, so a reader never sees them, which is exactly why a tampering tool might hide data there.</summary>
    UnreferencedData,
    StartXrefMismatch,
    DanglingReference,
    ReferenceGenerationMismatch,
    RootInvalid,
    CatalogIncomplete,

    /// <summary>The page tree walks differently than its /Count claims (PDFium trusts /Count), contains a cycle, or has unreadable nodes.</summary>
    PageTreeInconsistent,
    TrailerInvalid,
    LimitExceeded,
    TooManyIssues,
}

public sealed record ValidationIssue(ValidationCode Code, ValidationSeverity Severity, string Message, long Offset = -1, int ObjectNumber = -1);

public sealed record ValidationOptions
{
    /// <summary>
    /// A reference to a missing object is legal (it reads as null, 7.3.10) and is a warning by default.
    /// Set this when validating output the writer produced, where a dangling reference is a bug.
    /// </summary>
    public bool DanglingReferencesAreErrors { get; init; }

    /// <summary>Stop collecting after this many issues; a final <see cref="ValidationCode.TooManyIssues"/> warning is added.</summary>
    public int MaxIssues { get; init; } = 1000;
}

public sealed class ValidationReport
{
    internal ValidationReport(IReadOnlyList<ValidationIssue> issues, int objectsChecked, long referencesChecked)
    {
        Issues = issues;
        ObjectsChecked = objectsChecked;
        ReferencesChecked = referencesChecked;
    }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>True when there is no issue of severity <see cref="ValidationSeverity.Error"/>.</summary>
    public bool IsValid => !Issues.Any(i => i.Severity == ValidationSeverity.Error);

    public int ObjectsChecked { get; }

    public long ReferencesChecked { get; }
}

/// <summary>
/// Whole-file structural validation (ISO 32000-2 7.5): walks every cross-reference section and checks that each
/// entry leads to the matching "N G obj", that objects do not overlap, that each section's startxref is right, that
/// references resolve and that the trailer is consistent.
///
/// It accepts a <see cref="StrictDocument"/> only. A lenient parse cannot be validated, so a recovered reading can
/// never turn into a "valid" verdict.
/// </summary>
public static class StructureValidator
{
    public static ValidationReport Validate(StrictDocument document, ValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new Run(document, options ?? new ValidationOptions()).Execute();
    }

    /// <summary>Opens <paramref name="data"/> strictly and validates it. A parse failure becomes an error in the report.</summary>
    public static ValidationReport Validate(ReadOnlyMemory<byte> data, ParserLimits? limits = null, ValidationOptions? options = null)
    {
        var opened = StrictDocument.Open(data, limits);
        if (opened.TryGetError(out var error))
        {
            var code = error.Code == PdfErrorCode.LimitExceeded ? ValidationCode.LimitExceeded : ValidationCode.ParseFailed;
            return new ValidationReport([new ValidationIssue(code, ValidationSeverity.Error, error.ToString(), error.Offset)], 0, 0);
        }

        return Validate(opened.Value, options);
    }

    private sealed class Run(StrictDocument doc, ValidationOptions options)
    {
        private readonly List<ValidationIssue> _issues = [];
        private int _objects;
        private long _references;
        private bool _full;

        public ValidationReport Execute()
        {
            CheckEntries();
            CheckTrailerAndCatalog();
            CheckPageTree();
            CheckReferences();
            return new ValidationReport(_issues, _objects, _references);
        }

        private void Report(ValidationCode code, ValidationSeverity severity, string message, long offset = -1, int objectNumber = -1)
        {
            if (_issues.Count >= options.MaxIssues)
            {
                if (!_full)
                {
                    _full = true;
                    _issues.Add(new ValidationIssue(ValidationCode.TooManyIssues, ValidationSeverity.Warning, "further issues were not recorded"));
                }

                return;
            }

            _issues.Add(new ValidationIssue(code, severity, message, offset, objectNumber));
        }

        private void FromError(ValidationCode code, PdfError error, int objectNumber)
        {
            var c = error.Code == PdfErrorCode.LimitExceeded ? ValidationCode.LimitExceeded : code;
            Report(c, ValidationSeverity.Error, error.ToString(), error.Offset, objectNumber);
        }

        /// <summary>Every entry of every section: header match, object-stream consistency, startxref values, extents.</summary>
        private void CheckEntries()
        {
            var extents = new List<(int Start, int End, int Number)>();
            var data = doc.Data.Span;

            foreach (var rev in doc.Revisions)
            {
                foreach (var (number, entry) in rev.Entries.OrderBy(kv => kv.Key))
                {
                    if (entry.Type == XrefEntryType.Free)
                        continue;
                    _objects++;
                    try
                    {
                        if (entry.Type == XrefEntryType.InUse)
                        {
                            var parsed = rev.ParseAtEntry(number, entry);
                            extents.Add((parsed.Start, parsed.End, number));
                        }
                        else
                        {
                            var r = rev.GetObject(number);
                            if (r.TryGetError(out var error))
                                FromError(ValidationCode.InvalidObjectStream, error, number);
                        }
                    }
                    catch (PdfParseException ex)
                    {
                        FromError(ValidationCode.InvalidObject, ex.Error, number);
                    }
                }

                // The xref region itself takes up bytes no object may share.
                extents.Add((rev.ExtentStart, rev.ExtentEnd, -1 - rev.Index));

                // A section's own marker should name the section. Two legitimate exceptions: a linearized file's first-page
                // section says "startxref 0", and the last section of a linearized file carries the document's final marker,
                // which names the first-page section. Either way it is only a warning; the final startxref is checked below.
                long declared = rev.DeclaredStartXref;
                bool matches = declared >= 0 && declared < data.Length && PdfBytes.SkipWhitespace(data, (int)declared) == rev.ExtentStart;
                if (declared >= 0 && !matches && declared != doc.StartXref)
                    Report(ValidationCode.StartXrefMismatch, ValidationSeverity.Warning, $"revision {rev.Index}: startxref {declared} does not point at its xref section at {rev.XrefOffset}", declared);
            }

            if (PdfBytes.SkipWhitespace(data, (int)doc.StartXref) != doc.Latest.ExtentStart)
                Report(ValidationCode.StartXrefMismatch, ValidationSeverity.Error, $"the final startxref {doc.StartXref} does not point at the newest xref section", doc.StartXref);

            CheckOverlaps(extents);
            CheckGaps(extents);
        }

        private void CheckOverlaps(List<(int Start, int End, int Number)> extents)
        {
            extents.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
            int maxEnd = -1;
            int maxEndOwner = 0;
            (int Start, int End, int Number)? previous = null;
            foreach (var cur in extents)
            {
                if (previous is { } p && p.Start == cur.Start && p.End == cur.End)
                    continue; // same bytes listed twice (an xref stream object is both an entry and a section)
                if (cur.Start < maxEnd)
                {
                    Report(
                        ValidationCode.ObjectOverlap,
                        ValidationSeverity.Error,
                        $"{Describe(cur.Number)} at {cur.Start} overlaps {Describe(maxEndOwner)} (ends at {maxEnd})",
                        cur.Start,
                        cur.Number >= 0 ? cur.Number : -1);
                }

                if (cur.End > maxEnd)
                {
                    maxEnd = cur.End;
                    maxEndOwner = cur.Number;
                }

                previous = cur;
            }

            static string Describe(int n) => n >= 0 ? $"object {n}" : $"xref section {-1 - n}";
        }

        /// <summary>
        /// The file body should be objects, xref sections and markers. Anything else between them is data no reader ever
        /// looks at (a warning: legal, but the usual place to hide something). Gaps may hold whitespace, comments and "startxref N".
        /// </summary>
        private void CheckGaps(List<(int Start, int End, int Number)> sortedExtents)
        {
            var data = doc.Data.Span;
            int cursor = 0;
            foreach (var (start, end, _) in sortedExtents)
            {
                if (start > cursor && ScanGap(data, cursor, start))
                    return;
                cursor = Math.Max(cursor, end);
            }

            ScanGap(data, cursor, data.Length);
        }

        /// <summary>Returns true after reporting (one report per file is enough to flag it).</summary>
        private bool ScanGap(ReadOnlySpan<byte> data, int from, int to)
        {
            var gap = data[..to];
            int pos = from;
            while (true)
            {
                pos = PdfBytes.SkipWhitespaceAndComments(gap, pos);
                if (pos >= to)
                    return false;
                if (PdfBytes.KeywordAt(gap, pos, "startxref"u8))
                {
                    pos = PdfBytes.SkipWhitespace(gap, pos + 9);
                    while (pos < to && PdfBytes.IsDigit(gap[pos]))
                        pos++;
                    continue;
                }

                Report(ValidationCode.UnreferencedData, ValidationSeverity.Warning, $"unreferenced data at {pos} between objects (up to offset {to})", pos);
                return true;
            }
        }

        private void CheckTrailerAndCatalog()
        {
            var latest = doc.Latest;
            var trailer = latest.Trailer;
            if (trailer["Root"] is not PdfReference root)
            {
                Report(ValidationCode.RootInvalid, ValidationSeverity.Error, "trailer has no /Root reference");
                return;
            }

            try
            {
                if (latest.ResolveInternal(root) is not PdfDictionary catalog)
                {
                    Report(ValidationCode.RootInvalid, ValidationSeverity.Error, "/Root does not resolve to a dictionary", ObjectNumberOffset(root), root.Number);
                }
                else
                {
                    string? type = catalog.GetName("Type");
                    if (type is not null && type != "Catalog")
                        Report(ValidationCode.RootInvalid, ValidationSeverity.Error, $"/Root has /Type /{type}, not /Catalog", ObjectNumberOffset(root), root.Number);
                    else if (type is null)
                        Report(ValidationCode.CatalogIncomplete, ValidationSeverity.Warning, "catalog has no /Type", ObjectNumberOffset(root), root.Number);
                    if (latest.ResolveInternal(catalog["Pages"]) is not PdfDictionary)
                        Report(ValidationCode.CatalogIncomplete, ValidationSeverity.Warning, "catalog has no usable /Pages", ObjectNumberOffset(root), root.Number);
                }

                if (trailer["Info"] is { } info && latest.ResolveInternal(info) is not PdfDictionary)
                    Report(ValidationCode.TrailerInvalid, ValidationSeverity.Warning, "trailer /Info does not resolve to a dictionary");
            }
            catch (PdfParseException ex)
            {
                FromError(ValidationCode.RootInvalid, ex.Error, root.Number);
            }

            if (trailer["ID"] is { } id && !(id is PdfArray { Count: 2 } a && a[0] is PdfString && a[1] is PdfString))
                Report(ValidationCode.TrailerInvalid, ValidationSeverity.Warning, "trailer /ID is not an array of two strings");
        }

        /// <summary>
        /// Walks the page tree with a visited set and compares the leaf count with the root /Count. PDFium reports the
        /// root /Count without walking the tree, so a file whose /Count lies makes two readers disagree on page indices.
        /// </summary>
        private void CheckPageTree()
        {
            var latest = doc.Latest;
            try
            {
                if (latest.Trailer["Root"] is not PdfReference root
                    || latest.ResolveInternal(root) is not PdfDictionary catalog
                    || catalog["Pages"] is not { } pagesEntry
                    || latest.ResolveInternal(pagesEntry) is not PdfDictionary pagesRoot)
                {
                    return; // already reported as RootInvalid / CatalogIncomplete
                }

                var visited = new HashSet<int>();
                if (pagesEntry is PdfReference pr)
                    visited.Add(pr.Number);
                var stack = new Stack<PdfDictionary>();
                stack.Push(pagesRoot);
                long leaves = 0;
                long nodes = 0;
                while (stack.Count > 0)
                {
                    var node = stack.Pop();
                    if (++nodes > doc.Limits.MaxObjectCount)
                    {
                        Report(ValidationCode.PageTreeInconsistent, ValidationSeverity.Warning, "page tree has more nodes than the object-count limit");
                        return;
                    }

                    if (node["Kids"] is not { } kidsEntry)
                    {
                        leaves++;
                        continue;
                    }

                    if (latest.ResolveInternal(kidsEntry) is not PdfArray kids)
                    {
                        Report(ValidationCode.PageTreeInconsistent, ValidationSeverity.Warning, "a page tree node has a /Kids that is not an array");
                        continue;
                    }

                    foreach (var kid in kids)
                    {
                        if (kid is PdfReference kr && !visited.Add(kr.Number))
                        {
                            Report(ValidationCode.PageTreeInconsistent, ValidationSeverity.Warning, $"page tree reaches object {kr.Number} more than once (a cycle or a shared node)", -1, kr.Number);
                            continue;
                        }

                        if (latest.ResolveInternal(kid) is PdfDictionary child)
                            stack.Push(child);
                        else
                            Report(ValidationCode.PageTreeInconsistent, ValidationSeverity.Warning, "a page tree node is not a dictionary");
                    }
                }

                if (pagesRoot["Count"] is not PdfInteger count || count.Value != leaves)
                {
                    Report(
                        ValidationCode.PageTreeInconsistent,
                        ValidationSeverity.Warning,
                        $"root /Count is {(pagesRoot["Count"] is PdfInteger c ? c.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing")} but the tree has {leaves} pages");
                }
            }
            catch (PdfParseException ex)
            {
                Report(ValidationCode.PageTreeInconsistent, ValidationSeverity.Warning, "page tree could not be read: " + ex.Error);
            }
        }
        private long ObjectNumberOffset(PdfReference r) =>
            doc.Latest.TryGetEntry(r.Number, out var e) && e.Type == XrefEntryType.InUse ? e.Offset : -1;

        /// <summary>Every reference in every live object must lead to a live entry (7.3.10: otherwise it reads as null).</summary>
        private void CheckReferences()
        {
            var latest = doc.Latest;
            var reported = new HashSet<(int, int)>();
            var severity = options.DanglingReferencesAreErrors ? ValidationSeverity.Error : ValidationSeverity.Warning;
            foreach (var (number, entry) in latest.EnumerateEffectiveEntries().OrderBy(kv => kv.Key))
            {
                if (entry.Type == XrefEntryType.Free)
                    continue;
                var loaded = latest.GetObject(number);
                if (!loaded.TryGetValue(out var obj))
                    continue; // already reported by CheckEntries

                foreach (var reference in PdfObjects.EnumerateReferences(obj))
                {
                    _references++;
                    if (reference.Number == 0)
                        continue;
                    if (!latest.TryGetEntry(reference.Number, out var target) || target.Type == XrefEntryType.Free)
                    {
                        if (reported.Add((reference.Number, reference.Generation)))
                            Report(ValidationCode.DanglingReference, severity, $"object {number} refers to {reference}, which is not in the cross-reference data", entry.Type == XrefEntryType.InUse ? entry.Offset : -1, number);
                    }
                    else if (target.Generation != reference.Generation && reported.Add((reference.Number, reference.Generation)))
                    {
                        Report(ValidationCode.ReferenceGenerationMismatch, ValidationSeverity.Warning, $"object {number} refers to {reference}, but the entry has generation {target.Generation}", -1, number);
                    }
                }
            }
        }
    }
}
