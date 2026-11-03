using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MikePDF.PdfWriter.Editing;

public sealed record RewriteOptions
{
    /// <summary>Draw every widget's appearance into its page's content and drop the widgets and the form (<c>/AcroForm</c>).</summary>
    public bool FlattenForms { get; init; }

    /// <summary>Draw every other visible annotation that is not a link into its page's content and remove it, popups included.</summary>
    public bool FlattenComments { get; init; }

    public ReadOnlyMemory<byte>? NewIdSecondElement { get; init; }
}

/// <param name="Output">A complete new file: only the objects reachable from the catalog and the information dictionary, a classic cross-reference table, no history.</param>
/// <param name="PagesChanged">Pages whose content or annotations were rewritten.</param>
/// <param name="AnnotationsFlattened">Annotations whose appearance was drawn into a page.</param>
/// <param name="AnnotationsRemoved">Annotations taken off their pages, whether or not they had an appearance to draw.</param>
/// <param name="ObjectsWritten">Objects in the output, not counting the free entries.</param>
public sealed record RewriteResult(byte[] Output, int PagesChanged, int AnnotationsFlattened, int AnnotationsRemoved, int ObjectsWritten);

/// <summary>
/// A full rewrite of a strictly parsed document (save-pipeline 3.3: Save As "Rewrite the whole file" and the flattened copy), optionally flattening
/// annotations into the page content first. Flattening follows 12.5.5: the appearance's box, transformed by its matrix, is fitted to the annotation's
/// rectangle, and the stream is drawn as a form XObject. The output holds only what the catalog and the information dictionary can reach, so an
/// annotation that was flattened (and every older revision) is gone from the file, not merely hidden. The base is never modified.
/// </summary>
public static class DocumentRewriter
{
    public static PdfResult<RewriteResult> Rewrite(StrictDocument document, RewriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PdfDocument.Guard(() => new Job(document, options ?? new RewriteOptions()).Run());
    }

    private sealed class Job(StrictDocument document, RewriteOptions options)
    {
        private readonly Dictionary<int, PdfObject> _overrides = [];
        private int _next = document.Size;
        private int _flattened;
        private int _removed;
        private int _pagesChanged;
        private int _objectsWritten;

        public RewriteResult Run()
        {
            if (document.Trailer["Encrypt"] is not null)
            {
                ParseContext.Fatal(PdfErrorCode.UnsupportedEncryption, "the writer cannot rewrite an encrypted document");
            }

            if (document.Root is not { } root || Get(root.Number) is not PdfDictionary catalog)
            {
                ParseContext.Fatal(PdfErrorCode.InvalidTrailer, "no catalog");
                return null!;
            }

            if (options.FlattenForms || options.FlattenComments)
            {
                var pages = PageTree.Enumerate(document);
                if (pages.TryGetError(out var treeError))
                {
                    throw new PdfParseException(treeError);
                }

                foreach (var page in pages.Value)
                {
                    FlattenPage(page);
                }

                if (options.FlattenForms && catalog.ContainsKey("AcroForm"))
                {
                    _overrides[root.Number] = catalog.Without("AcroForm");
                }
            }

            var output = Write(root);
            var reopened = StrictDocument.Open(output, document.Limits);
            if (reopened.TryGetError(out var error))
            {
                ParseContext.Fatal(PdfErrorCode.InvalidUpdate, "the rewritten file does not parse strictly: " + error.Message);
            }

            return new RewriteResult(output, _pagesChanged, _flattened, _removed, _objectsWritten);
        }

        private PdfObject Get(int number) => _overrides.TryGetValue(number, out var value) ? value : document.Latest.LoadObject(number);

        private PdfObject Resolve(PdfObject? value)
        {
            for (var i = 0; i < 32; i++)
            {
                if (value is not PdfReference reference)
                {
                    return value ?? PdfNull.Instance;
                }

                value = Get(reference.Number);
            }

            ParseContext.Fatal(PdfErrorCode.InvalidReference, "indirect reference chain is too long");
            return PdfNull.Instance;
        }

        private PdfReference AddNew(PdfObject value)
        {
            var number = _next++;
            if (number > document.Limits.MaxObjectNumber)
            {
                ParseContext.ExceedLimit(PdfLimit.ObjectNumber, "the rewrite needs an object number above the limit");
            }

            _overrides[number] = value;
            return new PdfReference(number, 0);
        }

        private void FlattenPage(PageEntry page)
        {
            var annots = Resolve(page.Dictionary["Annots"]);
            if (annots is PdfNull)
            {
                return;
            }

            if (annots is not PdfArray list)
            {
                ParseContext.Fatal(PdfErrorCode.InvalidEdit, "/Annots is not an array");
                return;
            }

            var kept = new List<PdfObject>();
            var draws = new List<(PdfReference Form, double[] Matrix)>();
            var removedHere = 0;
            foreach (var item in list)
            {
                var dictionary = Resolve(item) as PdfDictionary;
                var subtype = dictionary?.GetName("Subtype");
                var isWidget = subtype == "Widget";
                var isLink = subtype == "Link";
                var flatten = dictionary is not null && !isLink && (isWidget ? options.FlattenForms : options.FlattenComments);
                if (!flatten)
                {
                    kept.Add(item);
                    continue;
                }

                removedHere++;
                if (subtype != "Popup" && !IsHiddenOrNoView(dictionary!) && TryPlacement(dictionary!, out var form, out var matrix))
                {
                    draws.Add((form, matrix));
                }
            }

            if (removedHere == 0)
            {
                return;
            }

            var changed = page.Dictionary;
            if (draws.Count > 0)
            {
                changed = DrawInto(changed, draws);
            }

            changed = kept.Count == 0 ? changed.Without("Annots") : changed.With("Annots", new PdfArray(kept));
            _overrides[page.Reference.Number] = changed;
            _pagesChanged++;
            _flattened += draws.Count;
            _removed += removedHere;
        }

        private static bool IsHiddenOrNoView(PdfDictionary annotation) =>
            annotation["F"] is PdfInteger { Value: var flags } && (flags & (2 | 32)) != 0;

        /// <summary>The appearance stream to draw, and the matrix that puts it on the annotation's rectangle (12.5.5, algorithm 8.1).</summary>
        private bool TryPlacement(PdfDictionary annotation, out PdfReference form, out double[] matrix)
        {
            form = null!;
            matrix = [];
            if (Resolve(annotation["AP"]) is not PdfDictionary ap || ap["N"] is null)
            {
                return false;
            }

            var normal = ap["N"]!;
            if (Resolve(normal) is PdfDictionary states && normal is not PdfStream)
            {
                // A dictionary of states: the annotation's /AS names the one in force.
                if (annotation["AS"] is not PdfName state || states[state.Value] is not PdfReference stateRef)
                {
                    return false;
                }

                normal = stateRef;
            }

            if (normal is not PdfReference reference || Resolve(reference) is not PdfStream stream || Numbers(stream.Dictionary["BBox"], 4) is not { } bbox
                || Numbers(annotation["Rect"], 4) is not { } rect)
            {
                return false;
            }

            var m = Numbers(stream.Dictionary["Matrix"], 6) ?? [1, 0, 0, 1, 0, 0];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var (x, y) in new[] { (bbox[0], bbox[1]), (bbox[2], bbox[1]), (bbox[0], bbox[3]), (bbox[2], bbox[3]) })
            {
                var tx = (m[0] * x) + (m[2] * y) + m[4];
                var ty = (m[1] * x) + (m[3] * y) + m[5];
                minX = Math.Min(minX, tx);
                maxX = Math.Max(maxX, tx);
                minY = Math.Min(minY, ty);
                maxY = Math.Max(maxY, ty);
            }

            double rl = Math.Min(rect[0], rect[2]), rb = Math.Min(rect[1], rect[3]), rr = Math.Max(rect[0], rect[2]), rt = Math.Max(rect[1], rect[3]);
            var sx = maxX - minX > 1e-9 ? (rr - rl) / (maxX - minX) : 1;
            var sy = maxY - minY > 1e-9 ? (rt - rb) / (maxY - minY) : 1;
            form = reference;
            matrix = [sx, 0, 0, sy, rl - (minX * sx), rb - (minY * sy)];
            return matrix.All(double.IsFinite);
        }

        private double[]? Numbers(PdfObject? value, int count)
        {
            if (Resolve(value) is not PdfArray array || array.Count != count)
            {
                return null;
            }

            var numbers = new double[count];
            for (var i = 0; i < count; i++)
            {
                switch (Resolve(array[i]))
                {
                    case PdfInteger integer:
                        numbers[i] = integer.Value;
                        break;
                    case PdfReal real:
                        numbers[i] = real.Value;
                        break;
                    default:
                        return null;
                }
            }

            return numbers;
        }

        private PdfDictionary DrawInto(PdfDictionary page, List<(PdfReference Form, double[] Matrix)> draws)
        {
            var resources = Resolve(InheritedResources(page)) as PdfDictionary ?? PdfDictionary.Empty;
            var xobjects = Resolve(resources["XObject"]) as PdfDictionary ?? PdfDictionary.Empty;
            var used = new HashSet<string>(xobjects.Keys.Select(static k => k.Value), StringComparer.Ordinal);
            var content = new StringBuilder();
            var index = 0;
            foreach (var (form, matrix) in draws)
            {
                string name;
                do
                {
                    name = "MikeFlat" + index++.ToString(CultureInfo.InvariantCulture);
                }
                while (!used.Add(name));

                xobjects = xobjects.With(name, form);
                content.Append("q ")
                    .Append(PdfSerializer.FormatReal(matrix[0])).Append(" 0 0 ").Append(PdfSerializer.FormatReal(matrix[3])).Append(' ')
                    .Append(PdfSerializer.FormatReal(matrix[4])).Append(' ').Append(PdfSerializer.FormatReal(matrix[5])).Append(" cm /")
                    .Append(name).Append(" Do Q\n");
            }

            var existing = new List<PdfObject>();
            switch (page["Contents"])
            {
                case PdfReference single:
                    if (Resolve(single) is PdfArray indirectArray)
                    {
                        existing.AddRange(indirectArray);
                    }
                    else
                    {
                        existing.Add(single);
                    }

                    break;
                case PdfArray direct:
                    existing.AddRange(direct);
                    break;
            }

            // The original content is wrapped in q ... Q so that whatever graphics state it leaves behind cannot move what is drawn after it.
            var parts = new List<PdfObject> { AddNew(RawStream("q\n"u8)) };
            parts.AddRange(existing);
            parts.Add(AddNew(RawStream("Q\n"u8)));
            parts.Add(AddNew(RawStream(Encoding.ASCII.GetBytes(content.ToString()))));
            return page.With("Resources", resources.With("XObject", xobjects)).With("Contents", new PdfArray(parts));
        }

        private PdfObject? InheritedResources(PdfDictionary page)
        {
            var node = page;
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            for (var depth = 0; depth < 64 && node is not null; depth++)
            {
                if (node["Resources"] is { } resources)
                {
                    return resources;
                }

                if (!seen.Add(node))
                {
                    break;
                }

                node = node["Parent"] is { } parent ? Resolve(parent) as PdfDictionary : null;
            }

            return null;
        }

        private static PdfStream RawStream(ReadOnlySpan<byte> data) => new(PdfDictionary.Empty, data.ToArray());

        private byte[] Write(PdfReference root)
        {
            // Everything reachable from the catalog and the information dictionary, found without recursion.
            var reachable = new SortedSet<int>();
            var stack = new Stack<int>();
            stack.Push(root.Number);
            if (document.Trailer["Info"] is PdfReference info)
            {
                stack.Push(info.Number);
            }

            while (stack.Count > 0)
            {
                var number = stack.Pop();
                if (number < 1 || !reachable.Add(number))
                {
                    continue;
                }

                if (reachable.Count > document.Limits.MaxObjectCount)
                {
                    ParseContext.ExceedLimit(PdfLimit.ObjectCount, "the rewrite reaches too many objects");
                }

                foreach (var reference in PdfObjects.EnumerateReferences(Get(number)))
                {
                    if (_overrides.ContainsKey(reference.Number) || (document.Latest.TryGetEntry(reference.Number, out var entry) && entry.Type != XrefEntryType.Free))
                    {
                        stack.Push(reference.Number);
                    }
                }
            }

            var sink = new ByteSink(document.Limits.EffectiveMaxFileSize);
            sink.Write(document.Data.Span[..Math.Min(8, document.Data.Length)]);
            sink.Write("\n%âãÏÓ\n"u8.Length == 0 ? [] : new byte[] { (byte)'\n', (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
            var offsets = new Dictionary<int, (int Offset, int Generation)>();
            var maxDepth = document.Limits.EffectiveMaxNestingDepth;
            foreach (var number in reachable)
            {
                var value = Get(number);
                var generation = _overrides.ContainsKey(number) || !document.Latest.TryGetEntry(number, out var e) ? 0 : e.Generation;
                offsets[number] = (sink.Length, generation);
                sink.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{number} {generation} obj\n"));
                if (value is PdfStream stream)
                {
                    var dictionary = stream.Dictionary.With("Length", new PdfInteger(stream.RawData.Length));
                    PdfSerializer.WriteDictionary(sink, dictionary, 0, maxDepth);
                    sink.Write("\nstream\n"u8);
                    sink.Write(stream.RawData.Span);
                    sink.Write("\nendstream"u8);
                }
                else
                {
                    PdfSerializer.Write(sink, value, 0, maxDepth);
                }

                sink.Write("\nendobj\n"u8);
            }

            _objectsWritten = reachable.Count;
            var size = reachable.Max + 1;
            var xrefOffset = sink.Length;
            sink.Write("xref\n"u8);
            sink.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"0 {size}\n"));
            var gaps = Enumerable.Range(1, size - 1).Where(n => !offsets.ContainsKey(n)).ToList();
            var nextFree = gaps.Count > 0 ? gaps[0] : 0;
            sink.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{nextFree:D10} 65535 f \n"));
            for (var n = 1; n < size; n++)
            {
                if (offsets.TryGetValue(n, out var at))
                {
                    sink.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{at.Offset:D10} {at.Generation:D5} n \n"));
                }
                else
                {
                    var index = gaps.IndexOf(n);
                    var following = index + 1 < gaps.Count ? gaps[index + 1] : 0;
                    sink.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{following:D10} 00001 f \n"));
                }
            }

            var trailer = new List<KeyValuePair<PdfName, PdfObject>>
            {
                new(new PdfName("Size"), new PdfInteger(size)),
                new(new PdfName("Root"), root),
            };
            if (document.Trailer["Info"] is PdfReference infoRef && reachable.Contains(infoRef.Number))
            {
                trailer.Add(new(new PdfName("Info"), infoRef));
            }

            if (document.Trailer["ID"] is PdfArray { Count: 2 } id && id[0] is PdfString first && id[1] is PdfString)
            {
                var second = options.NewIdSecondElement is { } fixedId ? fixedId.ToArray() : RandomNumberGenerator.GetBytes(16);
                trailer.Add(new(new PdfName("ID"), new PdfArray([first, new PdfString(second, PdfStringFormat.Hex)])));
            }

            sink.Write("trailer\n"u8);
            PdfSerializer.WriteDictionary(sink, new PdfDictionary(trailer), 0, maxDepth);
            sink.Write("\nstartxref\n"u8);
            sink.WriteAscii(xrefOffset.ToString(CultureInfo.InvariantCulture));
            sink.Write("\n%%EOF\n"u8);
            return sink.ToArray();
        }
    }
}
