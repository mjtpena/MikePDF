using System.IO.Compression;
using System.Text;

namespace MikePDF.Integration.Tests.Support;

/// <summary>Builds small PDFs for tests that need a particular shape (slow to render, hungry for memory).</summary>
internal static class TestPdf
{
    /// <summary>
    /// A one-page PDF. <paramref name="contentDictionary"/> adds entries (a /Filter) to the content stream's dictionary.
    /// </summary>
    public static byte[] Build(byte[] content, string mediaBox = "0 0 200 200", string resources = "", string contentDictionary = "")
    {
        using var pdf = new MemoryStream();
        var offsets = new List<long>();

        void Write(string text) => pdf.Write(Encoding.Latin1.GetBytes(text));

        void Object(string dictionary, byte[]? stream)
        {
            offsets.Add(pdf.Position);
            Write($"{offsets.Count} 0 obj\n");
            if (stream is null)
            {
                Write($"<< {dictionary} >>");
            }
            else
            {
                Write($"<< {dictionary} /Length {stream.Length} >>\nstream\n");
                pdf.Write(stream);
                Write("\nendstream");
            }

            Write("\nendobj\n");
        }

        Write("%PDF-1.7\n");
        Object("/Type /Catalog /Pages 2 0 R", null);
        Object($"/Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [{mediaBox}]", null);
        Object($"/Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << {resources} >>", null);
        Object(contentDictionary, content);

        var xref = pdf.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>
    /// A page that takes PDFium a long time to draw: <paramref name="layers"/> full-page rectangles, each composited with a
    /// blend mode and partial opacity.
    /// </summary>
    public static byte[] SlowPage(int layers)
    {
        var content = new StringBuilder();
        for (var i = 0; i < layers; i++)
        {
            content.Append("q /G gs ").Append((i % 10) / 10.0).Append(" 0.4 0.8 rg 0 0 200 200 re f Q\n");
        }

        return Build(Encoding.Latin1.GetBytes(content.ToString()), "0 0 200 200", "/ExtGState << /G << /ca 0.5 /BM /Multiply >> >>");
    }

    /// <summary>
    /// A small file that makes PDFium commit a lot of memory when the page is drawn: the content stream is
    /// <paramref name="decodedBytes"/> of whitespace, Flate-compressed to a few hundred KB, and PDFium decodes a content stream in one piece.
    /// </summary>
    public static byte[] HungryPage(int decodedBytes)
    {
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var block = new byte[1 << 20];
                Array.Fill(block, (byte)' ');
                for (var written = 0; written < decodedBytes; written += block.Length)
                {
                    zlib.Write(block, 0, Math.Min(block.Length, decodedBytes - written));
                }
            }

            compressed = output.ToArray();
        }

        return Build(compressed, contentDictionary: "/Filter /FlateDecode");
    }

    /// <summary>Writes bytes to a new file under the OS temp directory and returns its path.</summary>
    public static string WriteTemp(string name, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "mikepdf-it-" + Guid.NewGuid().ToString("N")[..8] + "-" + name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>A file in the OS temp directory, removed on dispose. Declare it before whatever opens it, so that is disposed first.</summary>
internal sealed class TempFile : IDisposable
{
    public TempFile(string name, byte[] bytes) => Path = TestPdf.WriteTemp(name, bytes);

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // Still open somewhere: the OS temp directory is cleaned up eventually.
        }
    }
}
