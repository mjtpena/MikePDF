#pragma warning disable CA1861 // seed data: the arrays are written inline on purpose

using System.Text;
using MikePDF.Core.Editing;
using MikePDF.Core.Editing.Journal;
using MikePDF.Core.Editing.Recovery;
using MikePDF.Fuzz.Shared;
using MikePDF.PdfWriter.Editing;
using MikePDF.Protocol.Edits;

namespace MikePDF.PdfWriter.Fuzz;

/// <summary>
/// The fuzz targets of the save pipeline (save-pipeline 3.4, 3.3, 11.3): the journal's reader and the recovery code behind it, the edit blob's
/// reader and what stage 4 does with a blob, and stage 4 and the copies on arbitrary files. Like the other targets they catch nothing: an exception
/// that escapes, a hang or a failed invariant is the finding.
/// </summary>
internal static class SaveFuzzTargets
{
    // Small enough that each execution is fast, tight enough that the limit paths are reached.
    private static readonly JournalLimits JournalFuzzLimits = new()
    {
        MaxDocumentBytes = 1 << 20,
        MaxRecordBytes = 64 << 10,
        MaxHeaderBytes = 64 << 10,
        MaxRecords = 20_000,
        MaxJsonElements = 5_000,
        MaxPageIdentities = 1_000,
    };

    private static readonly byte[] TinyPdf = BuildTinyPdf();

    public static void JournalParse(ReadOnlySpan<byte> input) => _ = JournalOutcome(input.ToArray());

    public static void EditBlobDecode(ReadOnlySpan<byte> input) => _ = EditBlobOutcome(input.ToArray());

    public static void EditPdf(ReadOnlySpan<byte> input) => _ = EditOutcome(input.ToArray());

    // ---- the journal ----

    public static long JournalOutcome(byte[] data)
    {
        var read = JournalCodec.Parse(data, JournalFuzzLimits);
        Check(read.IgnoredBytes >= 0 && read.IgnoredBytes <= data.Length, "the bytes ignored are not a part of the file");
        Check(read.Entries.Count <= data.Length, "more records than bytes");
        Check(read.UnknownCommands == read.Entries.Count(static e => e is UnknownEntry), "the unknown-command count is not the number of unknown entries");
        long signature = MutationalFuzzer.Mix(23, (long)read.Status);
        signature = MutationalFuzzer.Mix(signature, System.Numerics.BitOperations.Log2((uint)read.Entries.Count + 1));

        if (read.Status is JournalReadStatus.Empty or JournalReadStatus.Unreadable or JournalReadStatus.TooLarge)
        {
            Check(read.Entries.Count == 0 || read.Status == JournalReadStatus.TooLarge, "a journal without a header gave records");
            return signature;
        }

        Check(read.Header is not null, "a readable journal has a header");
        for (var i = 0; i < read.Entries.Count; i++)
        {
            Check(read.Entries[i].Seq == i + 1, "sequence numbers are not 1, 2, 3");
        }

        // Whatever was read must survive being written and read again, and once normalised stay the same (a fixed point).
        var header = JournalCodec.EncodeHeader(read.Header!, JournalFuzzLimits);
        var rewritten = new List<byte>(header);
        foreach (var entry in read.Entries.Where(static e => e is not UnknownEntry))
        {
            // An unknown entry has no fields to write; dropping it breaks the numbering, so stop the round trip at the first.
            rewritten.AddRange(JournalCodec.EncodeEntry(entry, JournalFuzzLimits));
        }

        var again = JournalCodec.Parse([.. rewritten], JournalFuzzLimits);
        var firstUnknown = read.Entries.ToList().FindIndex(static e => e is UnknownEntry);
        var comparable = firstUnknown < 0 ? read.Entries.Count : firstUnknown;
        Check(again.Status == JournalReadStatus.Ok || again.Status == JournalReadStatus.TooLarge, "a journal that was read does not read again");
        if (again.Status == JournalReadStatus.Ok && firstUnknown < 0)
        {
            Check(again.Entries.Count == comparable, "the records changed in a round trip");
            for (var i = 0; i < comparable; i++)
            {
                Check(Describe(again.Entries[i]) == Describe(read.Entries[i]), "a record changed in a round trip");
            }
        }

        // What recovery does with it. None of this may throw, and a path it accepts must be what the save pipeline names.
        _ = RecoveryService.Summarise(read);
        foreach (var intent in read.Entries.OfType<SaveIntentEntry>())
        {
            if (SaveRecovery.IsPipelineFile(intent.Target, intent.Temp, ".tmp"))
            {
                Check(string.Equals(Path.GetDirectoryName(intent.Target), Path.GetDirectoryName(intent.Temp), StringComparison.OrdinalIgnoreCase), "recovery accepted a temporary file outside the target's directory");
                Check(Path.GetFileName(intent.Temp).StartsWith('.'), "recovery accepted a temporary file that is not hidden-named");
            }

            if (intent.Backup is not null)
            {
                _ = SaveRecovery.IsPipelineFile(intent.Target, intent.Backup, ".bak");
            }
        }

        return MutationalFuzzer.Mix(signature, read.Entries.Count == 0 ? 0 : read.Entries.Select(static e => (long)e.GetType().Name.Length).Sum());
    }

    private static string Describe(JournalEntry entry) => entry switch
    {
        CommandEntry c => $"c:{entry.Seq}:{entry.Time:O}:{c.Command.Cmd}:{c.Command.Payload.GetRawText()}",
        UndoEntry u => $"u:{entry.Seq}:{u.Target}",
        RedoEntry r => $"r:{entry.Seq}:{r.Target}",
        SaveIntentEntry i => $"i:{entry.Seq}:{i.Temp}:{i.Backup}:{i.Target}",
        SaveBeginEntry b => $"b:{entry.Seq}:{b.OutputSha256}:{b.OutputLength}",
        SaveCommittedEntry => $"k:{entry.Seq}",
        DiscardEntry => $"d:{entry.Seq}",
        _ => $"?:{entry.Seq}",
    };

    // ---- the edit blob ----

    public static long EditBlobOutcome(byte[] data)
    {
        if (!EditBlobCodec.TryDecode(data, out var blob, out var error))
        {
            Check(!string.IsNullOrEmpty(error), "a refusal without a reason");
            return MutationalFuzzer.TextSignature(error);
        }

        // Accepted: it must be a blob the writer would write, and writing it must give the same blob back.
        EditBlobCodec.Validate(blob);
        var encoded = EditBlobCodec.Encode(blob);
        Check(EditBlobCodec.TryDecode(encoded, out var again, out _), "a blob that was accepted does not read again");
        Check(EditBlobCodec.Encode(again).AsSpan().SequenceEqual(encoded), "blob encoding is not a fixed point");

        // And what stage 4 does with it, on a document that has the pages it names (or not: then the appender must refuse, not fail).
        var opened = StrictDocument.Open(TinyPdf);
        Check(opened.IsSuccess, "the built-in document does not open");
        var changes = blob.Edits.Select(ToChange).ToList();
        long signature = MutationalFuzzer.Mix(47, blob.Edits.Count == 0 ? 0 : (long)blob.Edits[0].Operation + 1);
        if (changes.Count == 0)
        {
            return signature;
        }

        var result = AnnotationAppender.Append(opened.Value, (int)Math.Min(blob.PageCount, int.MaxValue), changes);
        if (result.TryGetError(out var refused))
        {
            return MutationalFuzzer.Mix(signature, 1 + (long)refused.Code);
        }

        var output = result.Value.Output;
        Check(output.AsSpan(0, TinyPdf.Length).SequenceEqual(TinyPdf), "the original is not a prefix of the output");
        Check(StrictDocument.Open(output).IsSuccess, "the output does not reopen strictly");
        return MutationalFuzzer.Mix(signature, 1000 + result.Value.AnnotationsAdded);
    }

    // ---- stage 4 and the copies on arbitrary files ----

    public static long EditOutcome(byte[] data)
    {
        var opened = StrictDocument.Open(data, FuzzTargets.Limits);
        if (!opened.TryGetValue(out var document))
        {
            return 1 + (100_000 * (long)opened.Error.Code);
        }

        var tree = PageTree.Enumerate(document);
        if (tree.TryGetError(out var treeError))
        {
            return 2 + (100 * (long)treeError.Code);
        }

        var pages = tree.Value;
        long signature = MutationalFuzzer.Mix(3, System.Numerics.BitOperations.Log2((uint)pages.Count + 1));
        if (pages.Count > 0)
        {
            var edits = new List<AnnotationChange>
            {
                new()
                {
                    Kind = AnnotationEditKind.Add,
                    Page = 0,
                    Annotation = NewAnnotationKind.Highlight,
                    Rect = [10, 10, 90, 30],
                    Color = [1, 1, 0],
                    Opacity = 0.5f,
                    Name = "fuzz-add",
                    QuadPoints = [10, 30, 90, 30, 10, 10, 90, 10],
                },
                new()
                {
                    Kind = AnnotationEditKind.Add,
                    Page = pages.Count - 1,
                    Annotation = NewAnnotationKind.Ink,
                    Rect = [0, 0, 50, 50],
                    Color = [0, 0, 1],
                    Width = 2,
                    Name = "fuzz-ink",
                    Strokes = [[0, 0, 25, 25, 50, 10]],
                },
            };
            if (document.Resolve(pages[0].Dictionary["Annots"]).TryGetValue(out var annotsObject) && annotsObject is PdfArray { Count: > 0 } annots && annots[0] is PdfReference target
                && document.GetObject(target.Number).TryGetValue(out var annotationObject) && annotationObject is PdfDictionary annotation && annotation.GetName("Subtype") is { } subtype
                && Numbers(document, annotation["Rect"]) is { } rect)
            {
                // Aimed at what the file really has, so the expectation holds and the update is applied.
                edits.Add(new AnnotationChange { Kind = AnnotationEditKind.Update, Page = 0, TargetObject = target.Number, ExpectSubtype = subtype, ExpectRect = rect, Contents = "x", Rect = [1, 1, 2, 2] });
                edits.Add(new AnnotationChange { Kind = AnnotationEditKind.Delete, Page = 0, TargetObject = target.Number, ExpectSubtype = subtype, ExpectRect = rect });
            }

            var appended = AnnotationAppender.Append(document, pages.Count, edits);
            if (appended.TryGetValue(out var result))
            {
                Check(result.Output.AsSpan(0, data.Length).SequenceEqual(data), "the original is not a prefix of the output");
                Check(StrictDocument.Open(result.Output, FuzzTargets.Limits).IsSuccess, "the output does not reopen strictly");
                signature = MutationalFuzzer.Mix(signature, 10 + result.AnnotationsAdded);
            }
            else
            {
                signature = MutationalFuzzer.Mix(signature, 20 + (long)appended.Error.Code);
            }
        }

        foreach (var (forms, comments) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var rewritten = DocumentRewriter.Rewrite(document, new RewriteOptions { FlattenForms = forms, FlattenComments = comments });
            if (rewritten.TryGetValue(out var rewrite))
            {
                var reopened = StrictDocument.Open(rewrite.Output, FuzzTargets.Limits);
                Check(reopened.IsSuccess, "a rewrite does not reopen strictly");
                var count = PageTree.Enumerate(reopened.Value);
                Check(count.IsSuccess && count.Value.Count == pages.Count, "a rewrite changed the number of pages");
                signature = MutationalFuzzer.Mix(signature, 30 + rewrite.AnnotationsFlattened);
            }
            else
            {
                signature = MutationalFuzzer.Mix(signature, 40 + (long)rewritten.Error.Code);
            }
        }

        return signature;
    }

    private static float[]? Numbers(StrictDocument document, PdfObject? value)
    {
        if (!document.Resolve(value).TryGetValue(out var resolved) || resolved is not PdfArray { Count: 4 } array)
        {
            return null;
        }

        var numbers = new float[4];
        for (var i = 0; i < 4; i++)
        {
            if (!document.Resolve(array[i]).TryGetValue(out var item))
            {
                return null;
            }

            switch (item)
            {
                case PdfInteger integer:
                    numbers[i] = integer.Value;
                    break;
                case PdfReal real:
                    numbers[i] = (float)real.Value;
                    break;
                default:
                    return null;
            }
        }

        return numbers;
    }

    // ---- seeds ----

    /// <summary>Writes the seed files for a target into <paramref name="directory"/>: what the committed seeds under tests/fuzz/seeds were made from.</summary>
    public static void WriteSeeds(string target, string directory)
    {
        Directory.CreateDirectory(directory);
        var n = 0;
        foreach (var seed in target == "journal" ? JournalSeeds() : EditBlobSeeds())
        {
            File.WriteAllBytes(Path.Combine(directory, $"seed-{n++:D2}" + (target == "journal" ? ".jsonl" : ".bin")), seed);
        }
    }

    private static IEnumerable<byte[]> JournalSeeds()
    {
        var time = new DateTimeOffset(2026, 11, 3, 10, 30, 0, 123, TimeSpan.Zero);
        var header = new JournalHeader(@"C:\Users\a\Documents\report.pdf", "0123abcd", 123456, "2026-11-03T10:00:00.000Z", new string('a', 64), "1.0.0.0", ["p1", "p2"]);
        JournalEntry[] commands =
        [
            new CommandEntry(1, time, EditCommand.Create("AddAnnotation", new { page = 0, kind = "Highlight", name = "h1", rect = new[] { 10f, 20f, 110f, 40f }, color = new[] { 1f, 0.9f, 0f }, contents = "caf\u00E9 \u2603", quads = new[] { 10f, 40f, 110f, 40f, 10f, 20f, 110f, 20f } })),
            new CommandEntry(2, time, EditCommand.Create("SetFieldValue", new { field = "name", value = "Ada" })),
            new UndoEntry(3, time, 2),
            new RedoEntry(4, time, 2),
        ];
        var intent = new SaveIntentEntry(5, time, @"C:\Users\a\Documents\.report.pdf.mikepdf-0a1b2c3d.tmp", @"C:\Users\a\Documents\.report.pdf.mikepdf-0a1b2c3d.bak", @"C:\Users\a\Documents\report.pdf");
        JournalEntry[] save = [intent, new SaveBeginEntry(6, time, new string('b', 64), 123999), new SaveCommittedEntry(7, time), new DiscardEntry(8, time)];
        byte[] Join(params JournalEntry[] entries) => [.. JournalCodec.EncodeHeader(header), .. entries.SelectMany(static e => JournalCodec.EncodeEntry(e))];
        yield return JournalCodec.EncodeHeader(header);
        yield return Join(commands[..1]);
        yield return Join(commands);
        yield return Join([.. commands, .. save]);
        var whole = Join([.. commands, .. save]);
        yield return whole[..^20]; // a torn last line
        var damaged = (byte[])whole.Clone();
        damaged[whole.Length / 2] ^= 0x20;
        yield return damaged;
    }

    private static IEnumerable<byte[]> EditBlobSeeds()
    {
        yield return EditBlobCodec.Encode(new EditBlob { PageCount = 0, Edits = [] });
        yield return EditBlobCodec.Encode(new EditBlob
        {
            PageCount = 2,
            Edits =
            [
                new AnnotationEdit { Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.Highlight, Rect = [10, 20, 110, 40], Color = [1, 0.9f, 0], Opacity = 0.5f, Name = "h", Author = "Mike", Contents = "caf\u00E9", Flags = 4, QuadPoints = [10, 40, 110, 40, 10, 20, 110, 20] },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Ink, Rect = [0, 0, 50, 50], Color = [0, 0, 1], Width = 2.5f, Name = "i", Strokes = [[0, 0, 10, 10, 20, 5], [30, 30, 40, 40]] },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Text, Rect = [5, 5, 25, 25], Color = [1, 1, 0], Name = "t", Contents = "note" },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.Square, Rect = [60, 60, 90, 90], Color = [1, 0, 0], Width = 1, Name = "s" },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.Circle, Rect = [60, 100, 90, 130], Color = [1, 0, 0], Width = 1, Name = "c" },
                new AnnotationEdit { Operation = EditOperation.Delete, Page = 0, TargetObject = 12, ExpectSubtype = "Highlight", ExpectRect = [10, 20, 110, 40] },
                new AnnotationEdit { Operation = EditOperation.Update, Page = 1, Name = "nm", ExpectSubtype = "Text", ExpectRect = [5, 5, 25, 25], Rect = [6, 6, 26, 26], Contents = "moved" },
            ],
        });
        yield return EditBlobCodec.Encode(new EditBlob
        {
            PageCount = 1,
            Edits = [new AnnotationEdit { Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.Underline, Rect = [10, 20, 110, 40], Color = [0, 0, 0], Name = "u", QuadPoints = [10, 40, 110, 40, 10, 20, 110, 20, 10, 60, 110, 60, 10, 50, 110, 50] }],
        });

        // Version 2: every kind the annotation tools add, a reply, a review state, a replace and a popup.
        yield return EditBlobCodec.Encode(new EditBlob
        {
            PageCount = 2,
            Edits =
            [
                new AnnotationEdit { Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.Squiggly, Rect = [10, 20, 110, 40], Color = [1, 0, 0], Name = "sq", QuadPoints = [10, 40, 110, 40, 10, 20, 110, 20], Subject = "Squiggly", Created = "D:20261104101530+01'00'", PopupRect = [120, 20, 300, 120], Contents = "c" },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.FreeText, Rect = [10, 60, 190, 100], Color = [0, 0, 1], Name = "ft", Contents = "Hello", InteriorColor = [1, 1, 0.8f], Width = 2, FontId = 5, FontSize = 12, TextColor = [0, 0, 0], Align = 1 },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.Line, Rect = [0, 0, 100, 50], Color = [0, 0, 0], Name = "ln", Vertices = [0, 0, 100, 50], LineEnd = LineEndKind.ClosedArrow, Dash = DashKind.Dashed, InteriorColor = [1, 0, 0] },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Polygon, Rect = [0, 0, 100, 50], Color = [0, 0, 0], Name = "pg", Vertices = [0, 0, 100, 0, 50, 50], InteriorColor = [0, 1, 0] },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.PolyLine, Rect = [0, 0, 100, 50], Color = [0, 0, 0], Name = "pl", Vertices = [0, 0, 100, 0, 100, 40], LineEnd = LineEndKind.Diamond },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Stamp, Rect = [0, 0, 150, 40], Color = [0, 0.5f, 0], Name = "st", StandardName = "Approved", Traits = (EditTraits)(1u << EditTraitBits.TurnsShift) },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Ink, Rect = [0, 0, 50, 50], Color = [0, 0, 1], Name = "ink", Strokes = [[0, 0, 10, 10, 20, 5], [30, 30]], InkWidths = [[1, 2, 1], [3]], Traits = EditTraits.Highlighter },
                new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Text, Rect = [5, 5, 25, 25], Color = [1, 1, 0], Name = "rp", Contents = "Agreed", ParentName = "ink", ReplyKind = 2, State = 1 },
                new AnnotationEdit { Operation = EditOperation.Replace, Page = 0, Name = "ft", ExpectSubtype = "FreeText", ExpectRect = [10, 60, 190, 100], Kind = AnnotationKind.FreeText, Rect = [10, 60, 200, 110], Color = [0, 0, 1], FontSize = 14, TextColor = [0, 0, 0] },
            ],
        });
    }

    private static AnnotationChange ToChange(AnnotationEdit edit) => MikePDF.WriterHost.EditConversion.ToChange(edit);

    private static byte[] BuildTinyPdf()
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Object(string body)
        {
            offsets.Add(pdf.Length);
            pdf.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        var xref = pdf.Length;
        pdf.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvariantViolationException(message);
        }
    }
}
