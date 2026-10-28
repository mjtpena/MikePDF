using System.Reflection;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Quire.Protocol;

namespace Quire.WriterHost;

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

        GC.Collect();
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
