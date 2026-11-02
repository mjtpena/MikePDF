using System.Reflection;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MikePDF.Protocol;

namespace MikePDF.WriterHost;

/// <summary>
/// Loads and exercises everything a request will need, before the signature policy is applied and before any document byte arrives (the
/// same order as the engine: load the fixed set of images, then lock the process). It runs the real code paths on tiny inputs: the
/// frame codec, a strict parse of a valid and of a damaged PDF, and the memory-mapped file path on the host's own executable.
/// </summary>
internal static class WarmUp
{
    public static void Run()
    {
        LoadReferencedAssemblies(Assembly.GetExecutingAssembly(), []);

        // The codec, both directions, with a payload of every shape the host handles.
        var request = ProtocolCodec.EncodeRequestFrame(1, 0, 1000, new ValidateStructure { Handle = 1 });
        _ = ProtocolCodec.DecodeRequestFrame(request.Value);
        var hello = ProtocolCodec.EncodeRequestFrame(2, 0, 1000, new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = ProcessKind.Broker, Pid = 1 });
        _ = ProtocolCodec.DecodeRequestFrame(hello.Value);

        // A valid file with a warning-free report, and a damaged one with errors, so both reply shapes are built.
        _ = Validation.Run(MinimalPdf());
        var damaged = Validation.Run(Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\nstartxref\n99999\n%%EOF\n"));
        var reply = ProtocolCodec.EncodeResponseFrame(1, Status.Ok, MessageType.ValidateStructure, damaged);
        _ = ProtocolCodec.DecodeResponseFrame(reply.Value);
        _ = ProtocolCodec.EncodeResponseFrame(1, Status.InvalidArgument, MessageType.ValidateStructure, null);
        _ = Validation.Bounded("café 😀 " + new string('x', 400));

        // The path every request takes: a file handle, mapped read-only, parsed in place. The host's own executable is a file it may read.
        using var handle = File.OpenHandle(Environment.ProcessPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var mapped = MappedFile.Open(new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false), out _);
        if (mapped is not null)
        {
            _ = Validation.Run(mapped.Data);
        }

        SaveCodePaths();
        GC.Collect();
    }

    /// <summary>
    /// The save messages' code, run on a tiny document: the edit blob codec both ways, a strict parse, the appender, the rewriter with both
    /// flatten flags, the independent annotation count, and the frames of the three new messages. Everything these load happens before the
    /// signature policy is applied.
    /// </summary>
    private static void SaveCodePaths()
    {
        var edit = new MikePDF.Protocol.Edits.AnnotationEdit
        {
            Operation = MikePDF.Protocol.Edits.EditOperation.Add,
            Page = 0,
            Kind = MikePDF.Protocol.Edits.AnnotationKind.Highlight,
            Rect = [10, 10, 60, 30],
            Color = [1, 1, 0],
            Opacity = 0.5f,
            Name = "warmup",
            Contents = "café",
            Author = "warm",
            QuadPoints = [10, 30, 60, 30, 10, 10, 60, 10],
        };
        var blob = MikePDF.Protocol.Edits.EditBlobCodec.Encode(new MikePDF.Protocol.Edits.EditBlob { PageCount = 1, Edits = [edit] });
        if (!MikePDF.Protocol.Edits.EditBlobCodec.TryDecode(blob, out var decoded, out _))
        {
            return;
        }

        var opened = MikePDF.PdfWriter.StrictDocument.Open(MinimalPdf());
        if (!opened.IsSuccess)
        {
            return;
        }

        var appended = MikePDF.PdfWriter.Editing.AnnotationAppender.Append(opened.Value, 1, [.. decoded.Edits.Select(SaveOperations.ToChange)]);
        if (appended.IsSuccess)
        {
            _ = AnnotationCounter.Count(appended.Value.Output, 1, [0]);
            var again = MikePDF.PdfWriter.StrictDocument.Open(appended.Value.Output);
            if (again.IsSuccess)
            {
                _ = MikePDF.PdfWriter.Editing.DocumentRewriter.Rewrite(again.Value, new MikePDF.PdfWriter.Editing.RewriteOptions { FlattenForms = true, FlattenComments = true });
                _ = MikePDF.PdfWriter.Editing.DocumentRewriter.Rewrite(again.Value);
            }
        }

        _ = ProtocolCodec.EncodeRequestFrame(3, 0, 1000, new AppendAnnotations { BaseHandle = 1, EditsHandle = 2, EditsLength = 1, OutputHandle = 3, PageCount = 1 });
        _ = ProtocolCodec.EncodeRequestFrame(4, 0, 1000, new RewriteDocument { BaseHandle = 1, OutputHandle = 2, Flags = 3 });
        _ = ProtocolCodec.EncodeRequestFrame(5, 0, 1000, new VerifyAnnotations { BaseHandle = 1, OutputHandle = 2, PageCount = 1, Pages = [0], Added = [1], Removed = [0] });
        _ = ProtocolCodec.EncodeResponseFrame(3, Status.Ok, MessageType.AppendAnnotations, new AppendAnnotationsReply { Ok = false, IssueCodes = [29], IssueMessages = ["error: warm"] });
        _ = ProtocolCodec.EncodeResponseFrame(4, Status.Ok, MessageType.RewriteDocument, new RewriteDocumentReply { Ok = false, IssueCodes = [29], IssueMessages = ["error: warm"] });
        _ = ProtocolCodec.EncodeResponseFrame(5, Status.Ok, MessageType.VerifyAnnotations, new VerifyAnnotationsReply { Ok = true, Mismatches = [] });
    }

    private static void LoadReferencedAssemblies(Assembly assembly, HashSet<string> seen)
    {
        foreach (var name in assembly.GetReferencedAssemblies())
        {
            if (name.FullName is { } full && seen.Add(full))
            {
                try
                {
                    LoadReferencedAssemblies(Assembly.Load(name), seen);
                }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // Referenced but never needed on any path the host takes.
                }
            }
        }
    }

    private static byte[] MinimalPdf()
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Object(string body)
        {
            offsets.Add(pdf.Length);
            pdf.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        var xref = pdf.Length;
        pdf.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
