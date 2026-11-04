using Microsoft.Win32.SafeHandles;
using MikePDF.PdfWriter;
using MikePDF.PdfWriter.Editing;
using MikePDF.Protocol;
using MikePDF.Protocol.Edits;

namespace MikePDF.WriterHost;

/// <summary>
/// The save messages (schema 6, save-pipeline 3.3): AppendAnnotations, RewriteDocument and VerifyAnnotations. Every file is a handle the broker
/// duplicated into this process; each is consumed on every path, including a request that already ran out of time. A refusal the broker needs the
/// reason for is an <c>Ok</c> reply with <c>ok</c> false and issues (a status other than Ok carries no payload).
/// </summary>
internal static class SaveOperations
{
    private static SafeFileHandle? Own(ulong value) => value > (ulong)nint.MaxValue ? null : new SafeFileHandle((nint)value, ownsHandle: true);

    private static bool Expired(uint deadlineMs, DateTime received) => deadlineMs != 0 && DateTime.UtcNow - received > TimeSpan.FromMilliseconds(deadlineMs);

    public static (Status Status, AppendAnnotationsReply? Reply) Append(AppendAnnotations message, uint deadlineMs, DateTime received)
    {
        using var baseHandle = Own(message.BaseHandle);
        using var editsHandle = Own(message.EditsHandle);
        using var outputHandle = Own(message.OutputHandle);
        if (baseHandle is null || editsHandle is null || outputHandle is null)
        {
            return (Status.InvalidArgument, null);
        }

        if (Expired(deadlineMs, received))
        {
            return (Status.Timeout, null);
        }

        using var edits = MappedFile.Open(editsHandle, out _);
        if (edits is null || edits.Data.Length != (long)message.EditsLength || !EditBlobCodec.TryDecode(edits.Data, out var blob, out _))
        {
            return (Status.InvalidArgument, null);
        }

        using var source = MappedFile.Open(baseHandle, out var failure);
        if (source is null)
        {
            return (failure == MapFailure.TooLarge ? Status.Unsupported : Status.InvalidArgument, null);
        }

        var opened = StrictDocument.Open(source.Data);
        if (opened.TryGetError(out var openError))
        {
            return (Status.Ok, RefusedAppend(openError));
        }

        var appended = AnnotationAppender.Append(opened.Value, checked((int)message.PageCount), [.. blob.Edits.Select(ToChange)]);
        if (appended.TryGetError(out var appendError))
        {
            return (Status.Ok, RefusedAppend(appendError));
        }

        var result = appended.Value;
        if (!WriteOutput(outputHandle, result.Output))
        {
            return (Status.InvalidArgument, null);
        }

        return (Status.Ok, new AppendAnnotationsReply
        {
            Ok = true,
            AnnotationsAdded = (uint)result.AnnotationsAdded,
            AnnotationsRemoved = (uint)result.AnnotationsRemoved,
            ObjectsWritten = (uint)result.ObjectsWritten,
            BytesWritten = (ulong)result.Output.Length,
            IssueCodes = [],
            IssueMessages = [],
        });
    }

    public static (Status Status, RewriteDocumentReply? Reply) Rewrite(RewriteDocument message, uint deadlineMs, DateTime received)
    {
        using var baseHandle = Own(message.BaseHandle);
        using var outputHandle = Own(message.OutputHandle);
        if (baseHandle is null || outputHandle is null)
        {
            return (Status.InvalidArgument, null);
        }

        if (Expired(deadlineMs, received))
        {
            return (Status.Timeout, null);
        }

        using var source = MappedFile.Open(baseHandle, out var failure);
        if (source is null)
        {
            return (failure == MapFailure.TooLarge ? Status.Unsupported : Status.InvalidArgument, null);
        }

        var opened = StrictDocument.Open(source.Data);
        if (opened.TryGetError(out var openError))
        {
            return (Status.Ok, RefusedRewrite(openError));
        }

        var rewritten = DocumentRewriter.Rewrite(opened.Value, new RewriteOptions { FlattenForms = (message.Flags & 1) != 0, FlattenComments = (message.Flags & 2) != 0 });
        if (rewritten.TryGetError(out var rewriteError))
        {
            return (Status.Ok, RefusedRewrite(rewriteError));
        }

        var result = rewritten.Value;
        if (!WriteOutput(outputHandle, result.Output))
        {
            return (Status.InvalidArgument, null);
        }

        return (Status.Ok, new RewriteDocumentReply
        {
            Ok = true,
            PagesChanged = (uint)result.PagesChanged,
            AnnotationsFlattened = (uint)result.AnnotationsFlattened,
            AnnotationsRemoved = (uint)result.AnnotationsRemoved,
            ObjectsWritten = (uint)result.ObjectsWritten,
            BytesWritten = (ulong)result.Output.Length,
            IssueCodes = [],
            IssueMessages = [],
        });
    }

    /// <summary>
    /// Stage 3 of a save (schema 7): the original's bytes, then the objects PDFium appended and a cross-reference section in the original's form (<see cref="TailNormalizer"/>). A file that
    /// does not fit (not PDFium's save of this original, an object the strict parser refuses) is an <c>Ok</c> reply with <c>ok</c> false and the reason.
    /// </summary>
    public static (Status Status, NormalizeTailReply? Reply) NormalizeTail(NormalizeTail message, uint deadlineMs, DateTime received)
    {
        using var originalHandle = Own(message.OriginalHandle);
        using var pdfiumHandle = Own(message.PdfiumHandle);
        using var outputHandle = Own(message.OutputHandle);
        if (originalHandle is null || pdfiumHandle is null || outputHandle is null)
        {
            return (Status.InvalidArgument, null);
        }

        if (Expired(deadlineMs, received))
        {
            return (Status.Timeout, null);
        }

        using var original = MappedFile.Open(originalHandle, out var originalFailure);
        using var pdfium = MappedFile.Open(pdfiumHandle, out var pdfiumFailure);
        if (original is null || pdfium is null)
        {
            return (originalFailure == MapFailure.TooLarge || pdfiumFailure == MapFailure.TooLarge ? Status.Unsupported : Status.InvalidArgument, null);
        }

        var normalized = TailNormalizer.Normalize(
            original.Data,
            pdfium.Data,
            new TailNormalizeOptions { RestoreCheckAndRadioStreams = (message.Flags & 1) != 0, StripPdfiumKeys = (message.Flags & 2) != 0 });
        if (normalized.TryGetError(out var error))
        {
            return (Status.Ok, new NormalizeTailReply { Ok = false, IssueCodes = [(uint)error.Code], IssueMessages = [Validation.Bounded("error: " + error.Message)] });
        }

        var result = normalized.Value;
        if (!WriteOutput(outputHandle, result.Output))
        {
            return (Status.InvalidArgument, null);
        }

        return (Status.Ok, new NormalizeTailReply { Ok = true, BytesWritten = (ulong)result.Output.Length, IssueCodes = [], IssueMessages = [] });
    }

    public static (Status Status, VerifyAnnotationsReply? Reply) Verify(VerifyAnnotations message, uint deadlineMs, DateTime received)
    {
        using var baseHandle = Own(message.BaseHandle);
        using var outputHandle = Own(message.OutputHandle);
        if (baseHandle is null || outputHandle is null)
        {
            return (Status.InvalidArgument, null);
        }

        if (Expired(deadlineMs, received))
        {
            return (Status.Timeout, null);
        }

        using var original = MappedFile.Open(baseHandle, out var baseFailure);
        using var output = MappedFile.Open(outputHandle, out var outputFailure);
        if (original is null || output is null)
        {
            return (baseFailure == MapFailure.TooLarge || outputFailure == MapFailure.TooLarge ? Status.Unsupported : Status.InvalidArgument, null);
        }

        var pages = message.Pages.Select(static p => (int)p).ToArray();
        var before = AnnotationCounter.Count(original.Data, checked((int)message.PageCount), pages);
        var after = AnnotationCounter.Count(output.Data, checked((int)message.PageCount), pages);
        if (before is null || after is null)
        {
            return (Status.Ok, new VerifyAnnotationsReply { Ok = false, Mismatches = [] });
        }

        var mismatches = new List<uint>();
        for (var i = 0; i < pages.Length; i++)
        {
            if (after[i] != (long)before[i] + message.Added[i] - message.Removed[i])
            {
                mismatches.Add(message.Pages[i]);
            }
        }

        return (Status.Ok, new VerifyAnnotationsReply { Ok = true, Mismatches = [.. mismatches] });
    }

    internal static AnnotationChange ToChange(AnnotationEdit edit) => new()
    {
        Kind = (AnnotationEditKind)(int)edit.Operation,
        Page = (int)edit.Page,
        Annotation = (NewAnnotationKind)(int)edit.Kind,
        Rect = edit.Rect,
        Color = edit.Color,
        Opacity = edit.Opacity,
        Width = edit.Width,
        Contents = edit.Contents,
        Author = edit.Author,
        Name = edit.Name,
        Flags = edit.Flags,
        QuadPoints = edit.QuadPoints,
        Strokes = edit.Strokes,
        TargetObject = (int)edit.TargetObject,
        ExpectSubtype = edit.ExpectSubtype,
        ExpectRect = edit.ExpectRect,
    };

    private static AppendAnnotationsReply RefusedAppend(PdfError error) =>
        new() { Ok = false, IssueCodes = [(uint)error.Code], IssueMessages = [Validation.Bounded("error: " + error.Message)] };

    private static RewriteDocumentReply RefusedRewrite(PdfError error) =>
        new() { Ok = false, IssueCodes = [(uint)error.Code], IssueMessages = [Validation.Bounded("error: " + error.Message)] };

    /// <summary>Writes the whole output to the empty file the broker made and forces it to disk. False when the handle is not an empty file we can write.</summary>
    private static bool WriteOutput(SafeFileHandle output, byte[] bytes)
    {
        try
        {
            if (RandomAccess.GetLength(output) != 0)
            {
                return false;
            }

            RandomAccess.Write(output, bytes, 0);
            RandomAccess.FlushToDisk(output);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>Counts the annotations that are not links, popups or widgets on given pages of a file, with a fresh strict parse: the independent half of stage 5's model check.</summary>
internal static class AnnotationCounter
{
    public static int[]? Count(ReadOnlyMemory<byte> data, int pageCount, int[] pages)
    {
        var opened = StrictDocument.Open(data);
        if (opened.TryGetError(out _))
        {
            return null;
        }

        var document = opened.Value;
        var tree = PageTree.Enumerate(document);
        if (tree.TryGetError(out _) || tree.Value.Count != pageCount)
        {
            return null;
        }

        var counts = new int[pages.Length];
        for (var i = 0; i < pages.Length; i++)
        {
            if (pages[i] < 0 || pages[i] >= pageCount)
            {
                return null;
            }

            counts[i] = CountOn(document, tree.Value[pages[i]]);
        }

        return counts;
    }

    private static int CountOn(StrictDocument document, PageEntry page)
    {
        if (page.Dictionary["Annots"] is not { } annots)
        {
            return 0;
        }

        var list = document.Resolve(annots);
        if (list.TryGetError(out _) || list.Value is not PdfArray array)
        {
            return 0;
        }

        var count = 0;
        foreach (var item in array)
        {
            var resolved = document.Resolve(item);
            if (resolved.TryGetValue(out var value) && value is PdfDictionary dictionary && dictionary.GetName("Subtype") is not ("Link" or "Popup" or "Widget"))
            {
                count++;
            }
        }

        return count;
    }
}
