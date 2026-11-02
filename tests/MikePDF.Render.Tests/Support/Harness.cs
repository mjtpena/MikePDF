using System.Text;
using MikePDF.Broker;
using MikePDF.Render.Cli;
using MikePDF.Render.Engine;
using MikePDF.Render.Reference;
using MikePDF.Render.Reporting;
using MikePDF.Render.Runner;

namespace MikePDF.Render.Tests.Support;

/// <summary>One sandboxed engine, a scratch directory, and the means to run the tool's own <see cref="RenderRun"/> over files, the way the CLI does.</summary>
internal sealed class Harness : IAsyncDisposable
{

    private Harness(string scratch, SandboxedEngineHost host)
    {
        Scratch = scratch;
        Host = host;
        Out = Path.Combine(scratch, "out");
        Directory.CreateDirectory(Out);
    }

    public string Scratch { get; }

    public string Out { get; }

    public SandboxedEngineHost Host { get; }

    public static Harness Create(string purpose)
    {
        var scratch = LiveEngine.NewScratchDirectory(purpose);
        return new Harness(scratch, LiveEngine.NewHost(Path.Combine(scratch, "work")));
    }

    public string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Scratch, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public RenderOptions Options(params string[] args) => RenderOptions.Parse(["unused.pdf", "--out", Out, "--quiet", .. args]);

    public ReferenceRunner RealReference() =>
        new(Locations.ReferenceExecutable(), Locations.PdfiumDll(null, LiveEngine.EngineDirectory), Path.Combine(Scratch, "work"));

    public static WorkItem Item(string path, string? id = null, ExpectedOutcome? expected = null, string pages = "all", string? password = null) =>
        new(id ?? Path.GetFileName(path), path, password, expected, PageSpec.Parse(pages), []);

    public async Task<List<FileReport>> RunAsync(RenderOptions options, IReadOnlyList<WorkItem> items, IReferenceRenderer? reference, CancellationToken cancellationToken = default)
    {
        var run = new RenderRun(options, Host, options.Reference ? reference : null, Out, _ => { });
        return await run.RunAsync(items, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        LiveEngine.CleanUp(Scratch);
    }

    // ---- files that make an engine work hard ----------------------------------------------------

    /// <summary>A one-page PDF of <paramref name="layers"/> full-page translucent, blended rectangles: slow to draw, in proportion to the layer count.</summary>
    public static byte[] SlowPage(int layers)
    {
        var content = new StringBuilder();
        for (var i = 0; i < layers; i++)
        {
            content.Append("q /G gs ").Append((i % 10) / 10.0).Append(" 0.4 0.8 rg 0 0 200 200 re f Q\n");
        }

        return SinglePage(Encoding.Latin1.GetBytes(content.ToString()), "/ExtGState << /G << /ca 0.5 /BM /Multiply >> >>");
    }

    public static byte[] SinglePage(byte[] content, string resources = "")
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
        Object("/Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200]", null);
        Object($"/Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << {resources} >>", null);
        Object("", content);
        var xref = pdf.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }
}

/// <summary>Deletes the AppContainer profile the live tests made, once, when the last test is done.</summary>
public sealed class ProfileCleanup : IDisposable
{
    public void Dispose()
    {
        try
        {
            AppContainerProfile.Delete($"MikePDF.Render.Tests.{Environment.ProcessId}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            // A leftover profile is harmless; the name is per process.
        }
    }
}
